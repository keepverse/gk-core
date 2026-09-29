using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Duration;

/// <summary>
/// T29 (action-todo.md, spec-duration-resolver.md §4): the real, `turn.speed`-backed
/// <see cref="IDurationResolver"/> — unblocked by P0.5 (battle-timeline `TurnReadiness`, 2026-08-28).
/// Converts an already-clamped victim-turn count into ticks using the victim's OWN cadence, read
/// through <see cref="ActorDerivedSnapshot"/> exactly once per call — never cached, since a buff or
/// debuff can move `turn.speed` between two calls (the same freshness rule
/// <c>ResourceChannelReader</c> already follows for `max`/`ratePerTick`).
/// </summary>
public sealed class BattleDurationResolver : IDurationResolver
{
    readonly Func<string, ActorDerivedSnapshot> _snapshotOf;

    public BattleDurationResolver(Func<string, ActorDerivedSnapshot> snapshotOf) =>
        _snapshotOf = snapshotOf ?? throw new ArgumentNullException(nameof(snapshotOf));

    public long ToTicks(int victimTurns, string victimPtr)
    {
        if (victimTurns < 0)
            throw new ArgumentOutOfRangeException(nameof(victimTurns), victimTurns, "a victim-turn count is never negative");
        if (victimTurns == 0)
            return 0; // no readiness floor applies to an authored zero -- there is nothing to wait out

        var snapshot = _snapshotOf(victimPtr);
        var rawSpeed = (long)snapshot.Get(DerivedTurnChannels.Speed);
        var rawHaste = (long)snapshot.Get(DerivedTurnChannels.Haste);
        // "Speed clamped before division" (spec-readiness-model.md boundaries) -- clamped to the
        // REGISTERED DEFAULT, not an arbitrary 1: BattleStatComposer seeds only the channels its own
        // level formulas compute, so an actor with no explicit turn.speed/turn.haste ChannelMod reads
        // 0 here, not the registry's declared 100/1000 (confirmed directly,
        // BattleStatComposerTests.ATurnDotChannelModThroughTheComposePathDoesNotThrow). The reader
        // supplying the real default is the same established pattern this codebase already uses for
        // other channels the composer does not universally seed.
        var speed = rawSpeed > 0 ? rawSpeed : DerivedStatPolicy.TurnDefaultSpeed;
        var haste = rawHaste > 0 ? rawHaste : DerivedTurnChannels.NominalHasteMilli;

        var rate = TurnReadiness.EffectiveRate(speed, haste);
        var ticksPerTurn = TurnReadiness.TicksPerFullTurn(rate);
        return checked((long)victimTurns * ticksPerTurn);
    }
}
