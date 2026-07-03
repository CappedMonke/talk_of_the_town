using UnityEngine;

/// <summary>
/// Provides a sim-tick counter derived from Time.time.
/// Zero overhead — no Update/FixedUpdate, just a computed property.
/// When Time.timeScale == 0 (paused during LLM), ticks freeze automatically.
/// </summary>
public static class SimTickTracker
{
    /// <summary>Seconds of game time per tick. 0.5 = 2 ticks per game-second.</summary>
    public const float TickQuantum = 0.5f;

    /// <summary>Current sim-tick, derived from Time.time.</summary>
    public static long CurrentTick => (long)(Time.time / TickQuantum);
}
