using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Ai;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// `encounter-generator` D2.4 (spec-encounter-generator.md §5) — the boss's kit (pattern → allocation
/// → `ChannelMods`, `signatureAction`) and retinue count. `W` is D2.2's own (`raid.BossW + rung.BossWDelta`,
/// already wired in `Encounter.Build`); this module never re-derives it.
///
/// <para><b>Genuinely deferred, not fabricated: phase GRANTS.</b> §5's own text is explicit that "the
/// grant is one pre-rolled `enemy.` container per phase... from `affix.bossKitTier`" — that roll is
/// `EliteAffix`'s own `Instantiator.TryInstantiate` call (D2.6, unbuilt). <see cref="PhaseGrant"/>'s
/// own shape (`BattleModels.cs:190`) makes this a hard type-level block, not a design choice:
/// `ContainerInstanceId` is a non-nullable `string`, so there is no way to construct a "threshold
/// known, container not yet rolled" placeholder. <see cref="PhaseThresholdsMilli"/> below produces the
/// THRESHOLD list alone (real, complete, testable today); pairing each threshold with a rolled
/// container into a real <c>PhaseGrant</c> list is D2.6's own join, not re-derived here.</para>
///
/// <para><b>Genuinely deferred, not fabricated: `rung.eliteSecondActionRow`.</b> §5's text says
/// "`rung.eliteSecondActionRow` adds a second id from the species' action rows" — read in full
/// 2026-09-06: <c>DifficultyRungTuning</c> (`Dungeon/Tuning/DungeonTuning.cs:41-49`, the
/// already-shipped, already-closed difficulty-ladder module) carries no `EliteSecondActionRow` field
/// at all, and no "species' action rows" data source exists anywhere in `src/` either. This is a real
/// tuning-schema gap in a CLOSED module, not this task's own field to invent — <see cref="ApplyKit"/>
/// wires `signatureAction` alone, exactly what is buildable today.</para>
/// </summary>
public static class BossBuild
{
    /// <summary>
    /// battle-hub-fuse T5 — pattern → `AptitudeAllocation` (via <see cref="PointBudget.PointsFor"/> and
    /// <see cref="ZombossPattern.ToAllocation"/>, `ZombossCommanderAllocation`'s own established
    /// two-step), exposed so encounter builders attach it as <see cref="BattleHubInputs.Aptitude"/>
    /// instead of pre-folded `ChannelMods` (battle-hub-fuse T6 deleted the old `ResolveKit`/
    /// `AptitudeResolver.ResolveForBattle` pair this replaced — zero production callers by then).
    /// <see cref="AllocationScope.Commander"/>, matching `ZombossCommanderAllocation`'s own doc: "the
    /// two differ only in WHERE the allocation comes from... never in the shape the hot path
    /// consumes" — a Zomboss pattern IS this scope, the same one a player's commander build reads.
    /// </summary>
    public static AptitudeAllocation ResolveKitAllocation(string patternId, int thetaBoss, AptitudeTuning aptitudeTuning)
    {
        if (string.IsNullOrEmpty(patternId)) throw new ArgumentException("patternId is required.", nameof(patternId));
        if (aptitudeTuning is null) throw new ArgumentNullException(nameof(aptitudeTuning));

        var commander = new ZombossCommanderAllocation(patternId); // throws if patternId is unknown -- ZombossPatterns.Resolve's own loud-over-silent
        commander.Refresh(AllocationScope.Commander, thetaBoss, aptitudeTuning);
        return commander.Resolve(null!); // the hot-path delegate shape -- the StatContext parameter is never read
    }

    /// <summary>Applies the kit's `signatureAction` (one id, from round 1 — §5: "`signatureAction` is
    /// one action id in `EquippedActionIds`... from round 1") to an already-emitted boss setup. Every
    /// other field on <paramref name="boss"/> is untouched. battle-hub-fuse T6 dropped the
    /// `channelMods` parameter: the kit's aptitude reaches battle via <see cref="ResolveKitAllocation"/>
    /// into <see cref="BattleHubInputs.Aptitude"/> (Encounter.cs), never through this method.</summary>
    public static BattleActorSetup ApplyKit(BattleActorSetup boss, string signatureAction)
    {
        if (boss is null) throw new ArgumentNullException(nameof(boss));
        if (string.IsNullOrEmpty(signatureAction)) throw new ArgumentException("signatureAction is required.", nameof(signatureAction));

        return boss with { EquippedActionIds = new[] { signatureAction } };
    }

    /// <summary>The HP-threshold list alone (per mille of max HP) for a phase kind — `none` is empty,
    /// `breakpoint` is `phase.breakpoint.hpThresholdMilli` (exactly one entry, `EncounterTuningLoader`
    /// already enforces this at parse time), `escalating` is `phase.escalating.hpThresholdMilli`
    /// (exactly two). Never the <see cref="PhaseGrant"/> list itself — see this class's own doc
    /// comment for why that join is D2.6's, not this method's.</summary>
    public static IReadOnlyList<long> PhaseThresholdsMilli(BossPhaseKind kind, EncounterTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        return kind switch
        {
            BossPhaseKind.None => Array.Empty<long>(),
            BossPhaseKind.Breakpoint => tuning.PhaseBreakpointHpThresholdMilli,
            BossPhaseKind.Escalating => tuning.PhaseEscalatingHpThresholdMilli,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>
    /// §5 Retinue: "`boss.retinue.{slotRef, countBand}` is one more §2 slot" — filtered and drawn
    /// through the exact same <see cref="SlotFilter"/>/<see cref="SlotFill"/> machinery every other
    /// slot uses, with one different delta shape: `n = NextInt(countBand) + bossRetinuePerPartyDelta ×
    /// (parties − 1)` (multiplicative in party count, unlike a regular slot's flat rung delta).
    /// <b>Count only</b> — every retinue member's stats are §3's, at `Θ_room`, exactly like any other
    /// drawn enemy; there is no per-party HP anywhere. Throws <see cref="EncounterRefusal"/> exactly
    /// like a regular slot on an unfillable tuple or a sub-1 count, so a caller never needs a second
    /// error-handling shape for the retinue.
    /// </summary>
    public static IReadOnlyList<ConcreteAnchor> DrawRetinue(
        IReadOnlyList<ConcreteAnchor> corpus, EncounterSlot retinueSlot, SlotCountBandTuning countBand, ThreatWindow window,
        ElementTypeId? climate, long offClimateMilli, long sameSpeciesMaxMilli, int bossRetinuePerPartyDelta, int parties,
        ulong seed, string streamName, IReadOnlySet<ElementTypeId> spreadSet)
    {
        if (parties < 1) throw new ArgumentOutOfRangeException(nameof(parties), "a raid always has at least 1 party.");

        var candidates = SlotFilter.Candidates(corpus, retinueSlot, window, spreadSet);

        var delta = checked(bossRetinuePerPartyDelta * (parties - 1));
        var countStream = SeededRng.DeriveStream(seed, $"{streamName}:count");
        var count = SlotFill.Count(countStream, countBand, delta);
        if (count < 1)
            throw new EncounterRefusal(retinueSlot, $"retinue count {count} < 1 after {parties} parties' delta ({delta:+0;-#})");

        try
        {
            return SlotFill.Draw(candidates, count, climate, offClimateMilli, sameSpeciesMaxMilli, seed, streamName);
        }
        catch (EncounterFillExhausted ex)
        {
            throw new EncounterRefusal(retinueSlot, ex.Message);
        }
    }
}
