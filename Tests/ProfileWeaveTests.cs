using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using UnityProfileAttribute;

/// <summary>
/// Weaving fails quietly, so a broken weaver just means no markers.
/// These tests fail instead
/// </summary>
[TestFixture]
public class ProfileWeaveTests
{
    private static readonly List<string> Trace = new();

    [Profile]
    private static int EarlyReturn(int x)
    {
        if (x < 0)
        {
            return -1;
        }

        return x + 1;
    }

    [Profile]
    private static int Throws()
    {
        throw new InvalidOperationException("boom");
    }

    [Profile]
    private static int NestedFinally(int x)
    {
        try
        {
            return x + 100;
        }
        finally
        {
            Trace.Add("inner finally");
        }
    }

    [Profile("Profile.CustomName")]
    private static int Named() => 7;

    [Profile]
    private static string GenericMethod<T>(T value) => typeof(T).Name + ":" + value;

    [Profile]
    private static void Burn(int milliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
        }
    }

    [Profile]
    private static void BurnTwice(int milliseconds)
    {
        Burn(milliseconds);
        Burn(milliseconds);
    }

    private static class Nested<T>
    {
        [Profile]
        public static string Describe(T value) => typeof(T).Name + ":" + value;
    }

    // Bad IL throws InvalidProgramException on the first call
    [Test]
    public void WovenMethodsKeepTheirBehaviour()
    {
        Assert.AreEqual(-1, EarlyReturn(-5), "early return");
        Assert.AreEqual(6, EarlyReturn(5), "normal return");
        Assert.AreEqual(7, Named(), "custom marker name");
        Assert.Throws<InvalidOperationException>(() => Throws(), "exception must still propagate");
    }

    [Test]
    public void WovenHandlerNestsOutsideAnExistingFinally()
    {
        Trace.Clear();
        Assert.AreEqual(100, NestedFinally(0));
        CollectionAssert.AreEqual(new[] { "inner finally" }, Trace);
    }

    [Test]
    public void GenericsAreWoven()
    {
        Assert.AreEqual("Int32:3", GenericMethod(3));
        Assert.AreEqual("String:hi", Nested<string>.Describe("hi"));
    }

    /// <summary>
    /// Asks the IL, not the profiler.
    /// Markers survive domain reloads, so the registry lies about a dead weave.
    /// Neither method has a try/finally of its own, so a finally is the weave
    /// </summary>
    [Test]
    public void WeavingActuallyRan()
    {
        AssertWoven(typeof(ProfileWeaveTests)
            .GetMethod(nameof(EarlyReturn), BindingFlags.NonPublic | BindingFlags.Static));

        AssertWoven(typeof(Nested<>)
            .GetMethod(nameof(Nested<int>.Describe), BindingFlags.Public | BindingFlags.Static));
    }

    private static void AssertWoven(MethodBase method)
    {
        bool hasFinally = method
            .GetMethodBody()
            .ExceptionHandlingClauses
            .Any(clause => clause.Flags == ExceptionHandlingClauseOptions.Finally);

        Assert.IsTrue(
            hasFinally,
            $"{method.Name} has no finally clause, so the [Profile] weaver did not run. "
            + "Check that the asmdef is still named Unity.*.CodeGen and look for a "
            + "'[Profile] weaving skipped' warning in the console.");
    }

    [Test]
    public void MarkersAreRegisteredWithTheProfiler()
    {
        // Touch a woven method to run the generated cctor
        EarlyReturn(0);
        Named();
        GenericMethod(0);
        Nested<int>.Describe(0);

        List<ProfilerRecorderHandle> handles = new();
        ProfilerRecorderHandle.GetAvailable(handles);

        List<string> markers = handles
            .Select(handle => ProfilerRecorderHandle.GetDescription(handle).Name)
            .ToList();

        Assert.Contains("ProfileWeaveTests.EarlyReturn", markers, "default marker name");
        Assert.Contains("Profile.CustomName", markers, "explicit marker name");


        // One marker per generic definition, so the name keeps the parameter
        Assert.Contains("ProfileWeaveTests.GenericMethod<T>", markers, "generic method");
        Assert.Contains("ProfileWeaveTests.Nested<T>.Describe", markers, "nested generic type");
    }

    /// <summary>
    /// A marker recording nothing would pass every test above, so measure it.
    /// BurnTwice does twice the work of Burn and must contain it
    /// </summary>
    [UnityTest]
    public IEnumerator MarkersRecordRealTimings()
    {
        Profiler.enabled = true;

        ProfilerRecorder outer = StartRecorder("ProfileWeaveTests.BurnTwice");
        ProfilerRecorder inner = StartRecorder("ProfileWeaveTests.Burn");
        yield return null;

        BurnTwice(20);

        // The sample only lands once the frame it happened in closes
        yield return null;
        yield return null;

        long outerNs = PeakSample(outer);
        long innerNs = PeakSample(inner);
        outer.Dispose();
        inner.Dispose();

        Assert.Greater(innerNs, 0, "Burn recorded no samples at all");
        Assert.Greater(outerNs, 0, "BurnTwice recorded no samples at all");

        // Two 20ms burns, with loose bounds to survive a busy machine
        Assert.Greater(outerNs, 30_000_000, "BurnTwice measured far less than the 40ms it burned");
        Assert.Less(outerNs, 400_000_000, "BurnTwice measured implausibly long");

        // The outer scope encloses both inner ones
        Assert.GreaterOrEqual(outerNs, innerNs, "BurnTwice must contain Burn");
    }

    /// <summary>
    /// End sits in a finally, so a throwing body still closes its scope.
    /// A leak does not crash, the frame boundary closes it.
    /// It just reports a made up duration and misparents the rest of the frame
    /// </summary>
    [UnityTest]
    public IEnumerator MarkerScopeClosesWhenTheBodyThrows()
    {
        Profiler.enabled = true;

        ProfilerRecorder burn = StartRecorder("ProfileWeaveTests.Burn");
        yield return null;

        for (int i = 0; i < 500; i++)
        {
            Assert.Throws<InvalidOperationException>(() => Throws());
        }

        Burn(20);
        yield return null;
        yield return null;

        long burnNs = PeakSample(burn);
        burn.Dispose();

        // A leak would misparent Burn and lose its own 20ms
        Assert.Greater(burnNs, 10_000_000, "Burn lost its timing after throwing methods ran");
        Assert.Less(burnNs, 400_000_000, "Burn measured implausibly long after throwing methods ran");
    }

    private static ProfilerRecorder StartRecorder(string markerName) =>
        ProfilerRecorder.StartNew(
            ProfilerCategory.Scripts,
            markerName,
            64,
            ProfilerRecorderOptions.SumAllSamplesInFrame);

    private static long PeakSample(ProfilerRecorder recorder)
    {
        List<ProfilerRecorderSample> samples = new();
        recorder.CopyTo(samples);
        return samples.Count == 0 ? 0 : samples.Max(sample => sample.Value);
    }
}
