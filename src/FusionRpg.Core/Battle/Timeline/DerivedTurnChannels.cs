namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// Turn-domain derived channel ids. **Flat and non-elemental** — speed is not an element, so these
/// must stay out of the generated combat roster (12 families × 7 elements); adding them there
/// would invent 14 meaningless channels and change the roster count from 84.
///
/// The precedent already exists: `status.power.*` and `progression.*` are plain consts outside
/// that generation. Names inherited from the Chaos combat-core stat families.
/// </summary>
public static class DerivedTurnChannels
{
    /// <summary>Higher acts more often. Its default value is a balance dial and lives in config —
    /// <see cref="Stats.Derived.DerivedStatPolicy.TurnDefaultSpeed"/>, data/tuning/derived-stats.v{n}.json.</summary>
    public const string Speed = "turn.speed";

    /// <summary>Per-mille action-time multiplier: 1000 = normal, 500 = twice as fast.</summary>
    public const string Haste = "turn.haste";

    // W15 (battle-derived-wire T17): `turn.moveSpeed` was DELETED here 2026-09-23. It was declared,
    // never registered (`DerivedStatRegistry` registers only Speed and Haste), and read by nothing:
    // `ActionEnvelope.SpeedChannel` carries the string but no caller reads that field, and the
    // readiness path reads Speed directly (`BattleEngine.ReadyTicks`). A declared-unregistered channel
    // is exactly what makes the next channel census wrong, so the name went rather than gaining a
    // registration with no consumer. Re-add it with its first consumer. The removal note lives here
    // and in `docs/architecture/battle/spec-readiness-model.md`; the task's named home,
    // `battle-timeline-map.md`, is outside this lane's fence.

    // BaseSpeed was REMOVED by T14/B28 because it was two different numbers wearing one name, and a
    // caller could not tell which it meant. The scale unit is TurnReadiness.SpeedScale (structural);
    // the turn.speed channel default is DerivedStatPolicy.TurnDefaultSpeed (config). Every former
    // caller now names which one it wanted.

    /// <summary>The definition of "per-mille nominal" for <see cref="Haste"/>: 1000 = 1.0, the same
    /// 1000 that means unity everywhere else in the repo. <b>Structural, not a balance dial</b>
    /// (tunables-ssot.md §1) — moving it would not rebalance haste, it would redefine the unit every
    /// haste value is quoted in, silently reinterpreting every stored and authored number at once.</summary>
    public const int NominalHasteMilli = 1000;
}
