namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// B9 (battle-timeline-todo.md, spec-readiness-model.md): the pure readiness function —
/// <c>nextReadyTick = now + max(1, RoundDiv(remainingWork × SpeedScale, rate))</c>. A pure function of
/// <c>(work, rate)</c> — never reads a side budget (the spec's own purity rule: "if a budget ever has
/// to be consulted to compute an arrival time, the abstraction has failed").
///
/// <para><b>Scope, decided against the action program's own narrower need (P0.5)</b>: this file
/// supplies the readiness MATH — the piece <c>BattleDurationResolver</c> (T29) actually consumes —
/// not the full B9 slice. B9's own remaining half (scheduling a live
/// <see cref="TimelineEventKind.Readiness"/> event and wiring <c>Charging → Ready</c> in
/// <c>ActionRunner</c>) is a kernel-FSM change with its own "zero production code rewired" acceptance
/// bar (battle-timeline-todo.md Checkpoint A) and is NOT attempted here — this class is a pure
/// function with no scheduling side effects at all, so it changes no existing kernel behavior.</para>
/// </summary>
public static class TurnReadiness
{
    /// <summary>
    /// The readiness formula's SCALE UNIT — <b>structural, not a balance dial</b>
    /// (tunables-ssot.md §1; battle-timeline T14/B28, spec-timeline-tunables.md §1).
    ///
    /// <para>This is the half of the old <c>DerivedTurnChannels.BaseSpeed</c> that a balance pass must
    /// NOT touch. <see cref="TicksFor"/> computes <c>work × SpeedScale / rate</c>, and both the work
    /// supplied (<see cref="OneTurnWork"/>) and the rate compared against it are expressed in these
    /// same units — so scaling this constant scales numerator and denominator together and cancels.
    /// Doubling it does not make anyone faster; it makes a turn take twice as many ticks to describe
    /// the same thing, i.e. it changes the granularity the timeline is measured at. PS-8 exempt for the
    /// same reason <see cref="CooldownMath.MinTicksFloor"/> is: a unit, not a ceiling.</para>
    ///
    /// <para>The <i>other</i> half — "how fast is a baseline actor", the <c>turn.speed</c> channel's
    /// default — IS a balance dial and moved to config as
    /// <see cref="Stats.Derived.DerivedStatPolicy.TurnDefaultSpeed"/>. Both hold 100 today; the split
    /// exists so the next balance pass can move one without re-scaling the arithmetic.</para>
    /// </summary>
    public const long SpeedScale = 100;

    /// <summary>Half-away-from-zero rounding — the same idiom <c>ShieldMath.RoundDivSigned</c> and
    /// <c>CooldownMath</c>'s own private copy already use (both intentionally local rather than a
    /// shared utility, per <c>CooldownMath</c>'s own comment: "reused rather than reinvented" the
    /// FORMULA, not the function). A third local copy, following the same precedent.</summary>
    static long RoundDivSigned(long num, long div) =>
        num >= 0 ? (num + div / 2) / div : -((-num + div / 2) / div);

    /// <summary>Ticks until <paramref name="remainingWork"/> resolves at <paramref name="rate"/>.
    /// Never zero — spec: "a zero-tick readiness under next-event advance schedules an event at
    /// `now`, which pops immediately and reschedules at `now` — an infinite loop that never advances
    /// the clock."</summary>
    public static long TicksFor(long remainingWork, long rate)
    {
        if (remainingWork < 0) throw new ArgumentOutOfRangeException(nameof(remainingWork), remainingWork, "work remaining is never negative");
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate), rate, "rate must be clamped to > 0 before this call (spec: \"speed clamped before division\")");

        return Math.Max(1, RoundDivSigned(checked(remainingWork * SpeedScale), rate));
    }

    public static long NextReadyTick(long nowTick, long remainingWork, long rate) =>
        nowTick + TicksFor(remainingWork, rate);

    /// <summary>Folds <c>turn.haste</c> into a single effective rate the readiness formula's own
    /// <c>rate</c> parameter takes — <c>turn.haste</c> is per-mille and LOWER is faster (1000 =
    /// normal, 500 = twice as fast), so it scales speed up as haste falls:
    /// <c>effectiveRate = speed × NominalHasteMilli / haste</c>. This is what reproduces the spec's
    /// own worked example exactly: speed 100, haste falling from 1000 to 500 mid-flight doubles the
    /// effective rate (100 → 200), which is what turns a 1000-tick wait, half-elapsed, into arriving
    /// at <c>t+750</c> rather than <c>t+1000</c> — proven directly in
    /// <c>TurnReadinessTests.MidFlightHasteRebaseArrivesAtTPlusSevenFifty</c>.</summary>
    public static long EffectiveRate(long speed, long haste)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed), speed, "speed must be clamped to > 0 before this call");
        if (haste <= 0) throw new ArgumentOutOfRangeException(nameof(haste), haste, "haste must be clamped to > 0 before this call");
        return checked(speed * DerivedTurnChannels.NominalHasteMilli) / haste;
    }

    /// <summary>One whole turn's worth of work. The readiness spec pins the FORMULA, not what "one
    /// turn" costs — this module makes that content decision explicitly, self-consistently, from the
    /// formula's own constant rather than inventing a second one: <see cref="SpeedScale"/> itself, so a
    /// default-rate actor (<c>rate == SpeedScale</c>) takes exactly <c>SpeedScale</c> ticks per turn —
    /// <c>TicksFor(SpeedScale, SpeedScale) == SpeedScale</c>.</summary>
    public const long OneTurnWork = SpeedScale;

    public static long TicksPerFullTurn(long rate) => TicksFor(OneTurnWork, rate);
}
