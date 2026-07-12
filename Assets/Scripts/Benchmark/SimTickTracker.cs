using UnityEngine;

/// <summary>
/// Provides a sim-tick counter derived from Time.time.
/// Zero overhead — no Update/FixedUpdate, just a computed property.
/// When Time.timeScale == 0 (paused during LLM), ticks freeze automatically.
/// Call ResetEpoch() at the start of each benchmark run to zero the counter.
/// </summary>
public static class SimTickTracker
{
    /// <summary>Seconds of game time per tick. 0.5 = 2 ticks per game-second.</summary>
    public const float TickQuantum = 0.5f;

    private static float _epochTime;

    /// <summary>Current sim-tick, relative to the last ResetEpoch() call.</summary>
    public static long CurrentTick => (long)((Time.time - _epochTime) / TickQuantum);

    /// <summary>Reset the tick counter to 0 by recording the current Time.time as epoch.</summary>
    public static void ResetEpoch()
    {
        _epochTime = Time.time;
    }
}
