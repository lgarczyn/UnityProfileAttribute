using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Profiling;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityProfileAttribute
{
/// <summary>
/// Whether anything is recording, asked once a frame rather than once a marker.
/// Profiler.enabled is an internalcall, about as costly as the marker itself.
/// The answer is dropped every frame, so it cannot go stale
/// </summary>
public static class ProfileState
{
    private enum State
    {
        // Zero, so Invalidate and static init both mean unknown
        Unknown = 0,
        Off = 1,
        On = 2,
    }

    // Read from worker threads, so int sized and a race just costs a reread
    private static State s_State;

    public static bool Recording
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            State state = s_State;
            if (state == State.Unknown)
            {
                state = Profiler.enabled ? State.On : State.Off;
                s_State = state;
            }

            return state == State.On;
        }
    }

    /// <summary>
    /// Call this once a frame if you drive frames the player loop cannot see.
    /// Markers otherwise freeze in whatever state they last observed
    /// </summary>
    public static void Invalidate()
    {
        s_State = State.Unknown;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Install()
    {
        PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();

        PlayerLoopSystem invalidate = new()
        {
            type = typeof(ProfileState),
            updateDelegate = Invalidate,
        };

        // Last subsystem of the last phase, so no answer outlives its frame
        int last = root.subSystemList.Length - 1;
        PlayerLoopSystem[] phase = root.subSystemList[last].subSystemList;
        PlayerLoopSystem[] extended = new PlayerLoopSystem[phase.Length + 1];
        phase.CopyTo(extended, 0);
        extended[phase.Length] = invalidate;
        root.subSystemList[last].subSystemList = extended;

        PlayerLoop.SetPlayerLoop(root);
        Invalidate();
    }

#if UNITY_EDITOR
    // Editor tools run woven code with no player loop running
    [InitializeOnLoadMethod]
    private static void InstallEditor()
    {
        EditorApplication.update -= Invalidate;
        EditorApplication.update += Invalidate;
    }
#endif
}
}
