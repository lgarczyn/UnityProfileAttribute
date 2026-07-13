using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using UnityProfileAttribute;

/// <summary>
/// Deep splices markers in with the callee's arguments already on the stack.
/// A mistake corrupts the stack rather than merely mismeasuring
/// </summary>
[TestFixture]
public class ProfileDeepWeaveTests
{
    private static readonly List<string> Trace = new();

    private static void Burn(int milliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
        }
    }

    // In this assembly, so deep should wrap them
    private static int Slow(int x)
    {
        Burn(20);
        return x + 1;
    }

    private static int Fast(int x) => x * 2;

    private static int Boom() => throw new InvalidOperationException("boom");

    [Profile(deep: true)]
    private static int DeepCaller(int x)
    {
        int a = Slow(x);
        int b = Fast(a);
        Trace.Add("done");     // List<string>.Add is BCL, so it must NOT be wrapped
        return b;
    }

    [Profile(deep: true)]
    private static int DeepThrows()
    {
        return Boom();
    }

    [Profile("Deep.Branching", deep: true)]
    private static int Branching(int x)
    {
        // The branch has to land on the prologue, not on the bare call
        if (x > 0)
        {
            return Fast(x);
        }

        return Slow(x);
    }

    [Test]
    public void DeepWovenMethodsKeepTheirBehaviour()
    {
        Trace.Clear();
        Assert.AreEqual(4, DeepCaller(1), "Slow(1)=2 then Fast(2)=4");
        CollectionAssert.AreEqual(new[] { "done" }, Trace);

        Assert.AreEqual(10, Branching(5), "branch taken -> Fast");
        Assert.AreEqual(-8, Branching(-9), "branch not taken -> Slow");

        Assert.Throws<InvalidOperationException>(() => DeepThrows());
    }

    [Test]
    public void CalleesGetTheirOwnMarkers()
    {
        DeepCaller(1);

        List<string> markers = MarkerNames();

        Assert.Contains("ProfileDeepWeaveTests.DeepCaller", markers, "the method itself");
        Assert.Contains("ProfileDeepWeaveTests.Slow", markers, "callee in our assembly");
        Assert.Contains("ProfileDeepWeaveTests.Fast", markers, "callee in our assembly");

        // A marker costs more than a List.Add does
        CollectionAssert.DoesNotContain(markers, "List<T>.Add", "BCL calls must not be wrapped");
        CollectionAssert.DoesNotContain(markers, "List`1.Add", "BCL calls must not be wrapped");
    }

    /// <summary>
    /// The whole point: the profiler has to blame the right callee
    /// </summary>
    [UnityTest]
    public IEnumerator DeepMarkersAttributeTimeToTheRightCallee()
    {
        Profiler.enabled = true;

        ProfilerRecorder caller = Recorder("ProfileDeepWeaveTests.DeepCaller");
        ProfilerRecorder slow = Recorder("ProfileDeepWeaveTests.Slow");
        ProfilerRecorder fast = Recorder("ProfileDeepWeaveTests.Fast");
        yield return null;

        DeepCaller(1);
        yield return null;
        yield return null;

        long callerNs = Peak(caller);
        long slowNs = Peak(slow);
        long fastNs = Peak(fast);
        caller.Dispose();
        slow.Dispose();
        fast.Dispose();

        Assert.Greater(slowNs, 10_000_000, "Slow burns 20ms and must be blamed for it");
        Assert.Less(fastNs, 1_000_000, "Fast does nothing and must not be blamed");
        Assert.GreaterOrEqual(callerNs, slowNs, "the caller's scope contains the callee's");
    }

    /// <summary>
    /// A throw mid call leaves the callee's scope open.
    /// Only the outer finally can close it
    /// </summary>
    [UnityTest]
    public IEnumerator InnerScopeClosesWhenACalleeThrows()
    {
        Profiler.enabled = true;

        ProfilerRecorder slow = Recorder("ProfileDeepWeaveTests.Slow");
        yield return null;

        for (int i = 0; i < 200; i++)
        {
            Assert.Throws<InvalidOperationException>(() => DeepThrows());
        }

        // A leak would misparent Slow and lose its own 20ms
        DeepCaller(1);
        yield return null;
        yield return null;

        long slowNs = Peak(slow);
        slow.Dispose();

        Assert.Greater(slowNs, 10_000_000, "Slow lost its timing after throwing callees ran");
        Assert.Less(slowNs, 400_000_000, "Slow measured implausibly long: the stack drifted");
    }

    private static List<string> MarkerNames()
    {
        List<ProfilerRecorderHandle> handles = new();
        ProfilerRecorderHandle.GetAvailable(handles);
        return handles
            .Select(handle => ProfilerRecorderHandle.GetDescription(handle).Name)
            .ToList();
    }

    private static ProfilerRecorder Recorder(string markerName) =>
        ProfilerRecorder.StartNew(
            ProfilerCategory.Scripts,
            markerName,
            64,
            ProfilerRecorderOptions.SumAllSamplesInFrame);

    private static long Peak(ProfilerRecorder recorder)
    {
        List<ProfilerRecorderSample> samples = new();
        recorder.CopyTo(samples);
        return samples.Count == 0 ? 0 : samples.Max(sample => sample.Value);
    }
}
