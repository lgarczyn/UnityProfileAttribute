using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace UnityProfileAttribute.CodeGen
{
/// <summary>
/// Cecil needs a resolver to find the referenced assemblies on disk.
/// Mirrors the copy shipped in Unity.Collections.CodeGen
/// </summary>
internal static class ILPostProcessorHelper
{
    public static AssemblyDefinition AssemblyDefinitionFor(ICompiledAssembly compiledAssembly)
    {
        PostProcessorAssemblyResolver resolver = new(compiledAssembly);
        ReaderParameters readerParameters = new()
        {
            SymbolStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PdbData),
            SymbolReaderProvider = new PortablePdbReaderProvider(),
            AssemblyResolver = resolver,
            ReflectionImporterProvider = new PostProcessorReflectionImporterProvider(),
            ReadingMode = ReadingMode.Immediate,
        };

        MemoryStream peStream = new(compiledAssembly.InMemoryAssembly.PeData);
        AssemblyDefinition assemblyDefinition = AssemblyDefinition.ReadAssembly(peStream, readerParameters);

        // An assembly is not among its own references
        resolver.AddAssemblyDefinitionBeingOperatedOn(assemblyDefinition);
        return assemblyDefinition;
    }

    private sealed class PostProcessorAssemblyResolver : IAssemblyResolver
    {
        private readonly ICompiledAssembly m_CompiledAssembly;
        private readonly Dictionary<string, HashSet<string>> m_ReferenceToPathMap = new();
        private readonly HashSet<string> m_ReferenceDirectories = new();
        private readonly Dictionary<string, AssemblyDefinition> m_Cache = new();
        private AssemblyDefinition m_SelfAssembly;

        public PostProcessorAssemblyResolver(ICompiledAssembly compiledAssembly)
        {
            m_CompiledAssembly = compiledAssembly;
            foreach (string reference in compiledAssembly.References)
            {
                string assemblyName = Path.GetFileNameWithoutExtension(reference);
                if (!m_ReferenceToPathMap.TryGetValue(assemblyName, out HashSet<string> paths))
                {
                    paths = new HashSet<string>();
                    m_ReferenceToPathMap.Add(assemblyName, paths);
                }

                paths.Add(reference);
                m_ReferenceDirectories.Add(Path.GetDirectoryName(reference));
            }
        }

        public void AddAssemblyDefinitionBeingOperatedOn(AssemblyDefinition assemblyDefinition)
        {
            m_SelfAssembly = assemblyDefinition;
        }

        public void Dispose() { }

        public AssemblyDefinition Resolve(AssemblyNameReference name) =>
            Resolve(name, new ReaderParameters(ReadingMode.Deferred));

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (name.Name == m_CompiledAssembly.Name)
            {
                return m_SelfAssembly;
            }

            string fileName = FindFile(name);
            if (fileName == null)
            {
                return null;
            }

            if (m_Cache.TryGetValue(fileName, out AssemblyDefinition cached))
            {
                return cached;
            }

            parameters.AssemblyResolver = this;

            MemoryStream peStream = MemoryStreamFor(fileName);
            string pdb = fileName + ".pdb";
            if (File.Exists(pdb))
            {
                parameters.SymbolStream = MemoryStreamFor(pdb);
            }

            AssemblyDefinition assemblyDefinition = AssemblyDefinition.ReadAssembly(peStream, parameters);
            m_Cache.Add(fileName, assemblyDefinition);
            return assemblyDefinition;
        }

        private string FindFile(AssemblyNameReference name)
        {
            if (m_ReferenceToPathMap.TryGetValue(name.Name, out HashSet<string> paths))
            {
                foreach (string path in paths)
                {
                    if (paths.Count == 1 || AssemblyName.GetAssemblyName(path).FullName == name.FullName)
                    {
                        return path;
                    }
                }
            }

            // Only direct references are listed.
            // An indirect one sits in the same folder as a direct one
            foreach (string directory in m_ReferenceDirectories)
            {
                string candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static MemoryStream MemoryStreamFor(string fileName) =>
            Retry(10, TimeSpan.FromSeconds(1), () =>
            {
                using FileStream stream =
                    new(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] bytes = new byte[stream.Length];
                if (stream.Read(bytes, 0, (int)stream.Length) != stream.Length)
                {
                    throw new InvalidOperationException($"Could not read all of {fileName}.");
                }

                return new MemoryStream(bytes);
            });

        private static MemoryStream Retry(int retryCount, TimeSpan waitTime, Func<MemoryStream> func)
        {
            try
            {
                return func();
            }
            catch (IOException)
            {
                if (retryCount == 0)
                {
                    throw;
                }

                Thread.Sleep(waitTime);
                return Retry(retryCount - 1, waitTime, func);
            }
        }
    }

    private sealed class PostProcessorReflectionImporterProvider : IReflectionImporterProvider
    {
        public IReflectionImporter GetReflectionImporter(ModuleDefinition module) =>
            new PostProcessorReflectionImporter(module);
    }

    /// <summary>
    /// Reflection hands us System.Private.CoreLib types in a .NET Core runner.
    /// The assemblies we weave expect mscorlib
    /// </summary>
    private sealed class PostProcessorReflectionImporter : DefaultReflectionImporter
    {
        private const string SystemPrivateCoreLib = "System.Private.CoreLib";
        private readonly AssemblyNameReference m_CorrectCorlib;

        public PostProcessorReflectionImporter(ModuleDefinition module) : base(module)
        {
            foreach (AssemblyNameReference reference in module.AssemblyReferences)
            {
                if (reference.Name is "mscorlib" or "netstandard" or SystemPrivateCoreLib)
                {
                    m_CorrectCorlib = reference;
                    break;
                }
            }
        }

        public override AssemblyNameReference ImportReference(AssemblyName reference)
        {
            if (m_CorrectCorlib != null && reference.Name == SystemPrivateCoreLib)
            {
                return m_CorrectCorlib;
            }

            return base.ImportReference(reference);
        }
    }
}
}
