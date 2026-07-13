using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using UnityProfileAttribute;

/// <summary>
/// The gate must not cost a frame of samples.
/// Anything that starts caching the answer has to keep NoFrameIsMissed passing
/// </summary>
[TestFixture]
public class ProfileGateTests
{
    [Profile]
    private static void Burn(int milliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
        }
    }

    /// <summary>
    /// A flag refreshed at the start of the frame drops this one
    /// </summary>
    [UnityTest]
    public IEnumerator NoFrameIsMissedAfterTheProfilerIsEnabled()
    {
        Profiler.enabled = false;
        yield return null;

        ProfilerRecorder recorder = ProfilerRecorder.StartNew(
            ProfilerCategory.Scripts, "ProfileGateTests.Burn", 64,
            ProfilerRecorderOptions.SumAllSamplesInFrame);

        // Every frame from here has to record
        Profiler.enabled = true;

        List<long> perFrame = new();
        for (int frame = 0; frame < 6; frame++)
        {
            Burn(2);
            yield return null;
            perFrame.Add(recorder.LastValue);
        }

        recorder.Dispose();
        Profiler.enabled = false;

        int missed = perFrame.Count(value => value == 0);
        UnityEngine.Debug.Log(
            $"frames after enabling the profiler: [{string.Join(", ", perFrame.Select(v => $"{v / 1e6:F1}ms"))}]"
            + $"  missed={missed}");

        Assert.Zero(
            missed,
            "the cached flag dropped a frame of markers, so it is stale when it matters "
            + "and [Profile] must gate on Profiler.enabled instead");
    }

    /// <summary>
    /// The gate changes what a marker costs, not what it says
    /// </summary>
    [UnityTest]
    public IEnumerator GatedMarkersStillMeasureCorrectly()
    {
        Profiler.enabled = true;

        ProfilerRecorder recorder = ProfilerRecorder.StartNew(
            ProfilerCategory.Scripts, "ProfileGateTests.Burn", 64,
            ProfilerRecorderOptions.SumAllSamplesInFrame);
        yield return null;

        Burn(20);
        yield return null;
        yield return null;

        List<ProfilerRecorderSample> samples = new();
        recorder.CopyTo(samples);
        long peak = samples.Count == 0 ? 0 : samples.Max(s => s.Value);
        recorder.Dispose();
        Profiler.enabled = false;

        Assert.Greater(peak, 10_000_000, "gated marker lost its timing");
        Assert.Less(peak, 400_000_000, "gated marker measured implausibly long");
    }
}
