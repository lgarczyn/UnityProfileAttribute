using System;
using System.Diagnostics;

namespace UnityProfileAttribute
{
/// <summary>
/// Wraps the method body in a ProfilerMarker at compile time.
/// Conditional on ENABLE_PROFILER, so a release build carries none of this
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
[Conditional("ENABLE_PROFILER")]
public sealed class ProfileAttribute : Attribute
{
    public ProfileAttribute(bool deep = false)
    {
        Deep = deep;
    }

    public ProfileAttribute(string name, bool deep = false)
    {
        Name = name;
        Deep = deep;
    }

    /// <summary>
    /// Defaults to Type.Method.
    /// Reuse a name across methods to aggregate them into one profiler row
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Also wraps every call this method makes into the project's own code.
    /// Engine and BCL calls are left alone, a marker costs more than they do.
    /// Meant for hunting something down, not for leaving in
    /// </summary>
    public bool Deep { get; }
}
}
