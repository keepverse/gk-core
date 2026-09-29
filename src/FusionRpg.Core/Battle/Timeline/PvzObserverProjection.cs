namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// What the injector can actually observe about a lawn entity. Deliberately coarse — these are the
/// facts PvZ makes visible, not the facts a turn-based mode would like to have.
/// </summary>
public enum ObservedLawnFact
{
    /// <summary>Exists, has not begun acting.</summary>
    Spawned,

    /// <summary>Idle and able to act. Able, not scheduled — we are not claiming it has a turn.</summary>
    Idle,

    /// <summary>Mid-attack or mid-animation.</summary>
    Acting,

    /// <summary>In its post-attack cooldown.</summary>
    CoolingDown,

    /// <summary>Killed.</summary>
    Died,

    /// <summary>Removed from the board without dying — despawned, collected, replaced.</summary>
    Removed
}

/// <summary>
/// **T7 — the PvZ observer.** Describes a live lawn in the same vocabulary an owned-clock battle uses,
/// so telemetry, VFX and the forecast speak one language across modes.
///
/// <para><b>An adapter, not a scheduler.</b> The Unity game owns that clock
/// (`battle-turn-ideal.md` §1) and this module never schedules, never advances a clock, and holds no
/// queue or per-actor machine. It is a pure function: one observed fact in, one vocabulary word out.</para>
///
/// <para><b>Statelessness is the safety argument, not a style choice.</b> A stateful observer on the
/// injector's hot path would need a per-entity map, which is precisely the per-hit scan-shaped cost the
/// 2026-08 perf audit had to remove once already. A pure function cannot acquire that cost by
/// accident.</para>
///
/// <para>It lives in Core rather than the injector because CI never builds injector projects — logic
/// placed there is untested forever, the same reason `EntityWriteGate` and `TimelineDrive` were
/// extracted. It holds no Unity type.</para>
/// </summary>
public static class PvzObserverProjection
{
    /// <summary>
    /// The observed fact, in the kernel's own state vocabulary.
    ///
    /// <para>⛔ <b><see cref="TurnState.Committed"/> is never returned</b>, and that is a finding
    /// rather than a gap. `Committed` means "intent locked, wind-up running" — a turn-based concept.
    /// PvZ has no observable moment between deciding and resolving, so projecting it would invent a
    /// fact the lawn cannot supply and make a forecast over live PvZ look meaningful when it is not.
    /// <b>The vocabulary is shared; the coverage is not.</b></para>
    /// </summary>
    public static TurnState Project(ObservedLawnFact fact) => fact switch
    {
        ObservedLawnFact.Spawned => TurnState.Charging,
        ObservedLawnFact.Idle => TurnState.Ready,
        ObservedLawnFact.Acting => TurnState.Resolving,
        ObservedLawnFact.CoolingDown => TurnState.Recovering,
        ObservedLawnFact.Died => TurnState.Dead,
        ObservedLawnFact.Removed => TurnState.Withdrawn,
        _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, "Unknown lawn fact — the vocabulary is closed.")
    };

    /// <summary>
    /// How far a forecast over a live lawn can be trusted: not at all.
    ///
    /// <para>`ForecastExactness.Absent` already exists for exactly this (T8) — "we do not own the
    /// clock, so there is nothing to project". An observed lawn has a present to describe and no
    /// scheduled future to roll forward.</para>
    /// </summary>
    public static ForecastExactness ForecastExactness => ForecastExactness.Absent;
}
