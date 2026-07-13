using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;
using Unity.Profiling;

namespace UnityProfileAttribute.CodeGen
{
/// <summary>
/// Rewrites every method tagged [Profile] to run its body in a marker scope.
/// Roslyn source generators cannot do this.
/// They only add code, and never touch an existing body
/// </summary>
public sealed class ProfileILPostProcessor : ILPostProcessor
{
    // Matched by name, not by type.
    // Referencing it would make Unity build the runtime first
    private const string AttributeAssembly = "UnityProfileAttribute";
    private const string AttributeName = "ProfileAttribute";
    private const string AttributeNamespace = "UnityProfileAttribute";
    private const string RecordingType = "UnityProfileAttribute.ProfileState";
    private const string RecordingGetter = "get_Recording";

    public override ILPostProcessor GetInstance() => this;

    public override bool WillProcess(ICompiledAssembly compiledAssembly)
    {
        // Weaving is off in release builds, so the shipped IL is untouched
        if (!compiledAssembly.Defines.Contains("ENABLE_PROFILER"))
        {
            return false;
        }

        if (compiledAssembly.Name == AttributeAssembly)
        {
            return true;
        }

        return compiledAssembly.References
            .Any(reference => Path.GetFileNameWithoutExtension(reference) == AttributeAssembly);
    }

    public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
    {
        List<DiagnosticMessage> diagnostics = new();

        AssemblyDefinition assembly;
        try
        {
            assembly = ILPostProcessorHelper.AssemblyDefinitionFor(compiledAssembly);
        }
        catch (BadImageFormatException)
        {
            return new ILPostProcessResult(null, diagnostics);
        }

        using (assembly)
        {
            ModuleDefinition module = assembly.MainModule;

            List<(MethodDefinition Method, CustomAttribute Attribute)> targets = new();
            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    CustomAttribute attribute = FindProfileAttribute(method);
                    if (attribute != null)
                    {
                        targets.Add((method, attribute));
                    }
                }
            }

            if (targets.Count == 0)
            {
                return new ILPostProcessResult(null, diagnostics);
            }

            HashSet<string> project = ProjectAssemblies(compiledAssembly);

            try
            {
                MarkerHolder holder = new(module);
                bool wove = false;

                foreach ((MethodDefinition method, CustomAttribute attribute) in targets)
                {
                    if (!CanWeave(method, diagnostics))
                    {
                        continue;
                    }

                    Weave(method, holder, MarkerNameFor(method, attribute), IsDeep(attribute),
                        project);
                    wove = true;
                }

                if (!wove)
                {
                    return new ILPostProcessResult(null, diagnostics);
                }

                holder.Finish();

                MemoryStream pe = new();
                MemoryStream pdb = new();
                WriterParameters writerParameters = new()
                {
                    SymbolWriterProvider = new PortablePdbWriterProvider(),
                    SymbolStream = pdb,
                    WriteSymbols = true,
                };
                assembly.Write(pe, writerParameters);

                InMemoryAssembly result = new(pe.ToArray(), pdb.ToArray());
                return new ILPostProcessResult(result, diagnostics);
            }
            catch (Exception exception)
            {
                // A half woven body is corrupt IL, so drop the whole assembly.
                // Unity keeps the original bytes and the build still succeeds
                diagnostics.Add(new DiagnosticMessage
                {
                    DiagnosticType = DiagnosticType.Warning,
                    MessageData =
                        $"[Profile] weaving skipped for {compiledAssembly.Name}: its profiler "
                        + $"markers will be missing. {exception}",
                });
                return new ILPostProcessResult(null, diagnostics);
            }
        }
    }

    /// <summary>
    /// Matched on name and declaring assembly.
    /// Name alone would weave any type a consumer calls ProfileAttribute
    /// </summary>
    private static CustomAttribute FindProfileAttribute(MethodDefinition method)
    {
        foreach (CustomAttribute attribute in method.CustomAttributes)
        {
            TypeReference type = attribute.AttributeType;
            if (type.Name != AttributeName || type.Namespace != AttributeNamespace)
            {
                continue;
            }

            string assembly = type.Scope is AssemblyNameReference reference
                ? reference.Name
                : method.Module.Assembly.Name.Name;

            if (assembly == AttributeAssembly)
            {
                return attribute;
            }
        }

        return null;
    }

    private static string MarkerNameFor(MethodDefinition method, CustomAttribute attribute)
    {
        string explicitName = attribute.ConstructorArguments
            .Select(argument => argument.Value as string)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));

        return explicitName ?? MarkerNameFor(method);
    }

    private static string MarkerNameFor(MethodReference method)
    {
        string methodName = method.Name;
        if (method.HasGenericParameters)
        {
            methodName += Brackets(method.GenericParameters.Select(parameter => parameter.Name));
        }

        return $"{FormatTypeName(method.DeclaringType)}.{methodName}";
    }

    private static bool IsDeep(CustomAttribute attribute) =>
        attribute.ConstructorArguments.Any(argument => argument.Value is true);

    /// <summary>
    /// Formats a type name the way a human writes it, as Outer.Inner&lt;T&gt;.
    /// One marker covers every instantiation of a generic.
    /// The name therefore keeps the parameter, not an argument
    /// </summary>
    private static string FormatTypeName(TypeReference type)
    {
        string name = type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name.Substring(0, tick);
        }

        if (type is GenericInstanceType instance)
        {
            name += Brackets(instance.GenericArguments.Select(FormatTypeName));
        }
        else if (type.HasGenericParameters)
        {
            name += Brackets(type.GenericParameters.Select(parameter => parameter.Name));
        }

        return type.DeclaringType != null ? $"{FormatTypeName(type.DeclaringType)}.{name}" : name;
    }

    private static string Brackets(IEnumerable<string> names) => $"<{string.Join(", ", names)}>";

    private static bool CanWeave(MethodDefinition method, List<DiagnosticMessage> diagnostics)
    {
        if (!method.HasBody)
        {
            diagnostics.Add(Warning(method, "has no body, so there is nothing to profile."));
            return false;
        }

        // The attribute sits on the stub, not on MoveNext.
        // Weaving it would time the setup and report nothing for the work
        if (method.CustomAttributes.Any(a =>
                a.AttributeType.Name is "AsyncStateMachineAttribute" or "IteratorStateMachineAttribute"))
        {
            diagnostics.Add(Warning(method, "is async or an iterator; only its setup would be measured."));
            return false;
        }

        if (method.ReturnType.IsByReference)
        {
            diagnostics.Add(Warning(method, "returns by ref, which cannot cross a finally block."));
            return false;
        }

        // Burst rejects managed calls.
        // Falling back to Mono is worse than having no marker
        if (method.CustomAttributes.Any(a => a.AttributeType.Name == "BurstCompileAttribute")
            || method.DeclaringType.CustomAttributes.Any(a =>
                a.AttributeType.Name == "BurstCompileAttribute"))
        {
            diagnostics.Add(Warning(method, "is Burst compiled, which cannot call into the profiler."));
            return false;
        }

        return true;
    }

    private static DiagnosticMessage Warning(MethodDefinition method, string reason) => new()
    {
        DiagnosticType = DiagnosticType.Warning,
        MessageData = $"[Profile] skipped: {method.FullName} {reason}",
    };

    /// <summary>
    /// Wraps the body in a marker scope, closed by a finally.
    /// Existing rets are mutated in place rather than replaced.
    /// Any branch or handler aimed at them still lands on the right instruction
    /// </summary>
    private static void Weave(
        MethodDefinition method,
        MarkerHolder holder,
        string markerName,
        bool deep,
        HashSet<string> projectAssemblies)
    {
        FieldReference marker = holder.AddMarker(markerName);

        MethodBody body = method.Body;
        body.SimplifyMacros();

        ILProcessor il = body.GetILProcessor();

        // Read the flag once and reuse it.
        // A flip mid method could otherwise half open a scope
        VariableDefinition recording = null;
        if (holder.Recording != null)
        {
            recording = new VariableDefinition(method.Module.TypeSystem.Boolean);
            body.Variables.Add(recording);
            body.InitLocals = true;
        }

        // Zero means nothing is open, which InitLocals gives us for free
        VariableDefinition openMarker = null;
        if (deep)
        {
            openMarker = new VariableDefinition(method.Module.TypeSystem.Int32);
            body.Variables.Add(openMarker);
            body.InitLocals = true;
            WeaveCallSites(method, holder, openMarker, recording, projectAssemblies);
        }

        Instruction bodyStart = body.Instructions[0];

        // Gate and Begin both sit before the try.
        // The finally then always has a matching Begin
        if (recording != null)
        {
            il.InsertBefore(bodyStart, Instruction.Create(OpCodes.Call, holder.Recording));
            il.InsertBefore(bodyStart, Instruction.Create(OpCodes.Stloc, recording));
            il.InsertBefore(bodyStart, Instruction.Create(OpCodes.Ldloc, recording));
            il.InsertBefore(bodyStart, Instruction.Create(OpCodes.Brfalse, bodyStart));
        }

        il.InsertBefore(bodyStart, Instruction.Create(OpCodes.Ldsflda, marker));
        il.InsertBefore(bodyStart, Instruction.Create(OpCodes.Call, holder.Begin));

        bool returnsValue = method.ReturnType.MetadataType != MetadataType.Void;
        VariableDefinition result = null;
        if (returnsValue)
        {
            result = new VariableDefinition(method.ReturnType);
            body.Variables.Add(result);
            body.InitLocals = true;
        }

        Instruction endFinally = Instruction.Create(OpCodes.Endfinally);
        Instruction exit = returnsValue
            ? Instruction.Create(OpCodes.Ldloc, result)
            : Instruction.Create(OpCodes.Ret);

        // Closing our own marker, gated the same way it was opened
        List<Instruction> closeMethod = new();
        if (recording != null)
        {
            closeMethod.Add(Instruction.Create(OpCodes.Ldloc, recording));
            closeMethod.Add(Instruction.Create(OpCodes.Brfalse, endFinally));
        }

        closeMethod.Add(Instruction.Create(OpCodes.Ldsflda, marker));
        closeMethod.Add(Instruction.Create(OpCodes.Call, holder.End));

        // Arguments are evaluated before the callee's Begin runs.
        // So only one inner scope is ever open, and it closes before ours
        List<Instruction> handler = new();
        if (deep)
        {
            handler.Add(Instruction.Create(OpCodes.Ldloc, openMarker));
            handler.Add(Instruction.Create(OpCodes.Brfalse, closeMethod[0]));
            handler.Add(Instruction.Create(OpCodes.Ldsfld, holder.All));
            handler.Add(Instruction.Create(OpCodes.Ldloc, openMarker));
            handler.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            handler.Add(Instruction.Create(OpCodes.Sub));
            handler.Add(Instruction.Create(OpCodes.Ldelema, holder.MarkerType));
            handler.Add(Instruction.Create(OpCodes.Call, holder.End));
        }

        handler.AddRange(closeMethod);
        Instruction handlerStart = handler[0];

        // A ret cannot sit inside a try.
        // Every one becomes a leave to the single exit
        foreach (Instruction instruction in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            if (returnsValue)
            {
                il.InsertAfter(instruction, Instruction.Create(OpCodes.Leave, exit));
                instruction.OpCode = OpCodes.Stloc;
                instruction.Operand = result;
            }
            else
            {
                instruction.OpCode = OpCodes.Leave;
                instruction.Operand = exit;
            }
        }

        foreach (Instruction instruction in handler)
        {
            il.Append(instruction);
        }

        il.Append(endFinally);
        il.Append(exit);
        if (returnsValue)
        {
            il.Append(Instruction.Create(OpCodes.Ret));
        }

        // Added last so it nests outside any handler the body already had
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = bodyStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = exit,
        });

        body.OptimizeMacros();
    }

    /// <summary>
    /// Begin and End are stack neutral, so they splice around a pushed call.
    /// A per call try/finally cannot.
    /// Entering a protected region needs an empty stack.
    /// Every argument would have to spill to a local first.
    /// openMarker and the outer finally give the same safety for two stlocs
    /// </summary>
    private static void WeaveCallSites(
        MethodDefinition method,
        MarkerHolder holder,
        VariableDefinition openMarker,
        VariableDefinition recording,
        HashSet<string> projectAssemblies)
    {
        MethodBody body = method.Body;
        ILProcessor il = body.GetILProcessor();

        foreach (Instruction call in body.Instructions.ToList())
        {
            if (call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt)
            {
                continue;
            }

            if (call.Operand is not MethodReference callee
                || !IsProjectCode(callee, projectAssemblies))
            {
                continue;
            }

            // A constrained prefix has to stay glued to its call.
            // Splice in front of it
            Instruction anchor = call;
            if (call.Previous?.OpCode == OpCodes.Constrained)
            {
                anchor = call.Previous;
            }

            // Nothing may execute between a tail call and the ret it becomes
            if (anchor.Previous?.OpCode == OpCodes.Tail)
            {
                continue;
            }

            Instruction resume = call.Next;
            if (resume == null)
            {
                continue;
            }

            FieldReference marker = holder.AddMarker(MarkerNameFor(callee));

            // Stack neutral, so both paths reach the call with the same stack
            List<Instruction> before = new();
            if (recording != null)
            {
                before.Add(Instruction.Create(OpCodes.Ldloc, recording));
                before.Add(Instruction.Create(OpCodes.Brfalse, anchor));
            }

            before.Add(Instruction.Create(OpCodes.Ldc_I4, holder.Count));
            before.Add(Instruction.Create(OpCodes.Stloc, openMarker));
            before.Add(Instruction.Create(OpCodes.Ldsflda, marker));
            before.Add(Instruction.Create(OpCodes.Call, holder.Begin));

            // Retarget first, or it would rewrite our own brfalse into a loop
            Retarget(body, anchor, before[0]);

            foreach (Instruction instruction in before)
            {
                il.InsertBefore(anchor, instruction);
            }

            List<Instruction> after = new();
            if (recording != null)
            {
                after.Add(Instruction.Create(OpCodes.Ldloc, recording));
                after.Add(Instruction.Create(OpCodes.Brfalse, resume));
            }

            after.Add(Instruction.Create(OpCodes.Ldsflda, marker));
            after.Add(Instruction.Create(OpCodes.Call, holder.End));
            after.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            after.Add(Instruction.Create(OpCodes.Stloc, openMarker));

            Instruction cursor = call;
            foreach (Instruction instruction in after)
            {
                il.InsertAfter(cursor, instruction);
                cursor = instruction;
            }
        }
    }

    private static void Retarget(MethodBody body, Instruction from, Instruction to)
    {
        foreach (Instruction instruction in body.Instructions)
        {
            if (ReferenceEquals(instruction.Operand, from))
            {
                instruction.Operand = to;
            }
            else if (instruction.Operand is Instruction[] targets)
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    if (ReferenceEquals(targets[i], from))
                    {
                        targets[i] = to;
                    }
                }
            }
        }

        foreach (ExceptionHandler h in body.ExceptionHandlers)
        {
            if (ReferenceEquals(h.TryStart, from)) h.TryStart = to;
            if (ReferenceEquals(h.TryEnd, from)) h.TryEnd = to;
            if (ReferenceEquals(h.HandlerStart, from)) h.HandlerStart = to;
            if (ReferenceEquals(h.HandlerEnd, from)) h.HandlerEnd = to;
            if (ReferenceEquals(h.FilterStart, from)) h.FilterStart = to;
        }
    }

    /// <summary>
    /// Only the project's own code is worth wrapping.
    /// A marker costs more than a List.Add does, so it would measure itself.
    /// Property accessors are skipped for the same reason
    /// </summary>
    private static bool IsProjectCode(MethodReference callee, HashSet<string> projectAssemblies)
    {
        MethodDefinition definition;
        try
        {
            definition = callee.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }

        if (definition == null || !definition.HasBody || definition.IsGetter || definition.IsSetter
            || definition.IsConstructor)
        {
            return false;
        }

        string assembly = definition.Module?.Assembly?.Name?.Name;
        return assembly != null && projectAssemblies.Contains(assembly);
    }

    /// <summary>
    /// Told apart by where Unity says they live, not by a name prefix.
    /// The engine gives absolute paths and packages sit under PackageCache.
    /// Project code stays relative to the project root
    /// </summary>
    private static HashSet<string> ProjectAssemblies(ICompiledAssembly compiledAssembly)
    {
        HashSet<string> names = new() { compiledAssembly.Name };

        foreach (string reference in compiledAssembly.References)
        {
            if (Path.IsPathRooted(reference)
                || reference.Replace('\\', '/').Contains("/PackageCache/"))
            {
                continue;
            }

            names.Add(Path.GetFileNameWithoutExtension(reference));
        }

        return names;
    }

    /// <summary>
    /// One generated static class per module, holding every marker.
    /// A static field on a generic type needs a self instantiated reference
    /// </summary>
    private sealed class MarkerHolder
    {
        private readonly TypeDefinition m_Type;
        private readonly MethodDefinition m_Cctor;
        private readonly MethodReference m_MarkerCtor;
        private readonly List<FieldDefinition> m_Fields = new();

        public MethodReference Begin { get; }
        public MethodReference End { get; }
        public TypeReference MarkerType { get; }

        // Every marker, so a deep finally can close an open inner scope
        public FieldDefinition All { get; }

        /// <summary>
        /// Resolved by name out of the runtime assembly.
        /// Null if it is missing, in which case we weave ungated
        /// </summary>
        public MethodReference Recording { get; }

        public int Count => m_Fields.Count;

        public MarkerHolder(ModuleDefinition module)
        {
            MarkerType = module.ImportReference(typeof(ProfilerMarker));
            Recording = ResolveRecording(module);
            m_MarkerCtor = module.ImportReference(
                typeof(ProfilerMarker).GetConstructor(new[] { typeof(string) }));
            Begin = module.ImportReference(typeof(ProfilerMarker).GetMethod("Begin", Type.EmptyTypes));
            End = module.ImportReference(typeof(ProfilerMarker).GetMethod("End", Type.EmptyTypes));

            // BeforeFieldInit drops the class init check on every ldsflda.
            // Roslyn sets it for free, but a hand written cctor has to opt in
            m_Type = new TypeDefinition(
                string.Empty,
                "<Profile>Markers",
                TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract
                | TypeAttributes.NotPublic | TypeAttributes.BeforeFieldInit,
                module.TypeSystem.Object);
            module.Types.Add(m_Type);

            m_Cctor = new MethodDefinition(
                ".cctor",
                MethodAttributes.Static | MethodAttributes.Private | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            m_Type.Methods.Add(m_Cctor);

            All = new FieldDefinition(
                "All",
                FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.Assembly,
                new ArrayType(MarkerType));
            m_Type.Fields.Add(All);
        }

        public FieldReference AddMarker(string markerName)
        {
            FieldDefinition field = new(
                $"Marker{m_Fields.Count}",
                FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.Assembly,
                MarkerType);
            m_Type.Fields.Add(field);
            m_Fields.Add(field);

            ILProcessor il = m_Cctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldstr, markerName);
            il.Emit(OpCodes.Newobj, m_MarkerCtor);
            il.Emit(OpCodes.Stsfld, field);
            return field;
        }

        private static MethodReference ResolveRecording(ModuleDefinition module)
        {
            TypeDefinition state;
            if (module.Assembly.Name.Name == AttributeAssembly)
            {
                state = module.GetType(RecordingType);
            }
            else
            {
                AssemblyNameReference core = module.AssemblyReferences
                    .FirstOrDefault(reference => reference.Name == AttributeAssembly);

                state = core == null
                    ? null
                    : module.AssemblyResolver.Resolve(core)?.MainModule.GetType(RecordingType);
            }

            MethodDefinition getter = state?.Methods
                .FirstOrDefault(method => method.Name == RecordingGetter);

            return getter == null ? null : module.ImportReference(getter);
        }

        public void Finish()
        {
            ILProcessor il = m_Cctor.Body.GetILProcessor();

            il.Emit(OpCodes.Ldc_I4, m_Fields.Count);
            il.Emit(OpCodes.Newarr, MarkerType);
            il.Emit(OpCodes.Stsfld, All);

            for (int i = 0; i < m_Fields.Count; i++)
            {
                il.Emit(OpCodes.Ldsfld, All);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldsfld, m_Fields[i]);
                il.Emit(OpCodes.Stelem_Any, MarkerType);
            }

            il.Emit(OpCodes.Ret);
        }
    }
}
}
