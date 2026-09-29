using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Timeline;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` (spec-siege-ai.md), R1/R2/R5/R6: the pure decision mechanics — stance,
/// signed aggression, additive scoring, ordinal-tie-broken selection. No random-number generator, no
/// non-integer numeric type anywhere in this file's arithmetic, and no `IBattleView` read anywhere in
/// this file (structurally, not just by discipline — nothing here takes one), so R5's determinism is
/// provable from the type signatures alone.
///
/// <para><b>Deliberately does not implement a full board-reading AI.</b> What is built here proves
/// R1 (stance/aggression/score are three distinct things), R2 (additive scoring with XCOM's shipped
/// weights and a subtracting risk term), R5 (integer-only arithmetic, ordinal tie-break) and R6 (a pure
/// top-three trace function) as pure, directly-testable mechanisms. What is named as a real, un-started
/// gap rather than rushed: R3's objective fallback (`BoardPathfinder`'s `TerrainOnlyOccupancy` — this
/// file never references `BoardPathfinder`), a real `IIntentSource` that reads `IBattleView` to compute
/// live `hitChanceMilli`/`incomingThreatMilli`/`objectiveClassMilli` from actual battle state (the
/// AI-side slot on <see cref="SiegeIntentSource"/> below is caller-supplied, not implemented here),
/// §5.20 rule 5's emplacement replacement vocabulary, and enforcing `RetargetLatencyTicks` from a live
/// retarget loop. Every one of those needed a working read of `IBattleView`/`BoardPathfinder`, and the
/// spec's own §5.20 addendum on Relic's five-patch cover-seeking regression was a direct warning
/// against shipping an unverified live decision-maker under time pressure — all four are now built
/// (`SiegeAiIntentSource`, 2026-09-05/07). `CandidateScorer.TopThree`
/// (`Actions/Ai/CandidateScorer.cs`, formerly this file's own `AiScoring.TopThree` before CAI1.1
/// moved it) is wired into a live trace too (R6, 2026-09-07) — correcting the spec's own citation:
/// `Battle/Timeline/DecisionTrace.cs`
/// replays HUMAN input decisions for `(setup, seed, trace)` determinism, a fixed `Player`/`Timeout`
/// shape with no room for a scored candidate list, so the real target is `BattleTrace.AiDecision`,
/// the SAME opt-in/non-null-gated/golden-neutral observability object the spec's own R6 text names as
/// the pattern to follow ("exactly as `BattleTrace` is").</para>
/// </summary>
public enum Stance { Hold, Guard, Engage }

/// <summary>
/// base-defense `siege-ai` 17.9, §5.20 rule 5: the garrisoned emplacement's OWN two-entry vocabulary,
/// replacing R3's objective-fallback movement (meaningless for something that cannot move) rather than
/// reusing <see cref="Stance"/>'s `Hold` value as a "close enough" stand-in — the spec's own §11 title
/// is literally "a replacement vocabulary, not a degraded one." `SiegeAiIntentSource` resolves every
/// garrisoned occupant to <see cref="FireAtWill"/> today (no content authors a `HoldFire` trigger yet);
/// the vocabulary exists and is named ahead of that consumer, the same order 17.7's own `TargetFilter`
/// already shipped in.
/// </summary>
public enum EmplacementFireMode { HoldFire, FireAtWill }

/// <summary>base-defense `siege-ai` §8 (§5.20 rule 2): a named, player-visible validity filter. Named,
/// because the whole thesis is that STATABILITY is the requirement — a filter the player cannot name
/// produces a miss they read as a bug.</summary>
public sealed record TargetFilter
{
    public string DisplayKey { get; init; } = "";
}

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.8, spec-profile-schema.md §7): **narrowed from fourteen
/// members to four**, then to **two** by `stance-wiring` (module 11, CAI3.1). The ten scorer weights
/// moved to `gk-core/data/tuning/combat-ai.v1.json` (`profiles["siege/default"]`) in module 1's
/// `ScoringWeights`/`AiAntiRepeat` shape — `CombatAiProfilePolicy.For(AiPlace.Siege, AiRole.Default)`
/// is their new address; the two DEAD keys (`stanceDefault`, `autoResolveHandicapMilli`) are gone with
/// `siege.v3.json`, nothing having read either. What is left is **siege board geometry** — a candidate
/// INPUT computed while building a candidate, never part of the scorer itself.
/// </summary>
public sealed record AiTuning(
    int ObjectiveReferenceDistanceCells,
    int ThreatRadiusCells);

// SiegeIntentSource (the played-side/AI-side dispatch wrapper) deleted 2026-09-20 (combat-ai
// `intent-router`, module 4, CAI1.10): confirmed zero production callers (DistrictAssaultResolver.cs's
// own doc comment: "there is no human-played side in this auto-resolved path, so SiegeIntentSource's
// own played-vs-AI dispatch wrapper isn't needed here at all" -- only this file's own now-deleted
// tests constructed it directly). Its dispatch/fallthrough contract (steered key set -> player source,
// everything else -> the automated source, a SELECT never a cascade) lives on in
// `Actions/IntentRouter.cs`'s own `steered`/`steeredKeys` chain step, ported test-for-test into
// `Actions/IntentRouterTests.cs`.
