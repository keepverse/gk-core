using FusionRpg.Core.Progression;
using Xunit;

namespace FusionRpg.Core.Tests.Progression;

/// <summary>`species-build` T1.1/T1.3 (module 3, `species-xp`) — the species curve/tuning surface.
/// Species levelling itself reuses <see cref="RpgActorState"/>/<see cref="RpgXpApply"/>/
/// <see cref="RpgXpCurve"/> via the <see cref="RpgActorKinds.Species"/> kind (spec-species-xp.md §1
/// Option A) — this file proves that reuse resolves correctly for that kind, plus the tuning loader.
/// The storage half (`rpg_actor_progression` kind='species' rows, the lawn/expedition projections) is
/// covered in `FusionRpg.Data.Tests`.</summary>
public class SpeciesProgressionTests
{
    static SpeciesProgressionTests()
    {
        // Same working set as the shipped gk-core/data/tuning/species-progression.v1.json, per this
        // assembly's own "construct one inline; no fixture files" convention.
        SpeciesProgressionTuningHub.Configure(new SpeciesProgressionTuning(
            CurveFirst: 60, CurveStep: 24, RunCompletionAward: 100, PlacementAward: 4));
    }

    [Fact]
    public void XpToNext_matchesTheArithmeticLadder_forTheSpeciesKind()
    {
        Assert.Equal(60, RpgXpCurve.XpToNext(RpgActorKinds.Species, 1));
        Assert.Equal(84, RpgXpCurve.XpToNext(RpgActorKinds.Species, 2));
        Assert.Equal(108, RpgXpCurve.XpToNext(RpgActorKinds.Species, 3));
    }

    [Fact]
    public void Apply_levelsUpOnceEnoughXpAccrues()
    {
        var state = new RpgActorState();
        var result = RpgXpApply.Apply(RpgActorKinds.Species, state, 60, typeId: 10001, reason: "seed");

        Assert.Equal(2, result.State.Level);
        Assert.Equal(0, result.State.Xp);
        Assert.Single(result.LevelChanges);
        Assert.Equal(1, result.LevelChanges[0].LevelBefore);
        Assert.Equal(2, result.LevelChanges[0].LevelAfter);
    }

    [Fact]
    public void Apply_canLevelUpMultipleTimesInOneAward()
    {
        var state = new RpgActorState();
        // 60 (L1->2) + 84 (L2->3) = 144 exactly.
        var result = RpgXpApply.Apply(RpgActorKinds.Species, state, 144, typeId: 10001);

        Assert.Equal(3, result.State.Level);
        Assert.Equal(0, result.State.Xp);
        Assert.Equal(2, result.LevelChanges.Count);
    }

    [Fact]
    public void Apply_belowThreshold_accumulatesWithoutLevelling()
    {
        var state = new RpgActorState();
        var result = RpgXpApply.Apply(RpgActorKinds.Species, state, 59, typeId: 10001);

        Assert.Equal(1, result.State.Level);
        Assert.Equal(59, result.State.Xp);
        Assert.Empty(result.LevelChanges);
    }

    [Fact]
    public void Apply_unlimitedLevels_noClamp_PS8()
    {
        // A huge single award must level up as many times as the arithmetic allows, never clamped.
        var state = new RpgActorState();
        var result = RpgXpApply.Apply(RpgActorKinds.Species, state, 1_000_000, typeId: 10001);

        Assert.True(result.State.Level > 100, $"expected many levels, got {result.State.Level}");
        Assert.True(result.LevelChanges.Count > 100);
    }

    [Fact]
    public void Apply_overflow_throws_neverWraps()
    {
        var state = new RpgActorState { Xp = long.MaxValue - 10 };
        Assert.Throws<OverflowException>(() => RpgXpApply.Apply(RpgActorKinds.Species, state, 100, typeId: 10001));
    }

    [Fact]
    public void RunAward_outEarnsAPlausibleHeavyMatchOfPlacements()
    {
        // species-progression.v1.json's own documented assumption: 20 placements of the SAME species
        // in one heavy match is "plausible heavy", and runCompletion must still exceed it. This is the
        // test that keeps the grind vector closed -- if a balance pass inverts the ratio, this fails.
        var t = SpeciesProgressionTuningHub.Tuning;
        const int plausibleHeavyMatchPlacements = 20;
        Assert.True(t.RunCompletionAward > t.PlacementAward * plausibleHeavyMatchPlacements,
            $"runCompletion ({t.RunCompletionAward}) must exceed {plausibleHeavyMatchPlacements} placements " +
            $"({t.PlacementAward} each = {t.PlacementAward * plausibleHeavyMatchPlacements})");
    }

    [Fact]
    public void Loader_parsesTheShippedShape()
    {
        var tuning = SpeciesProgressionTuningLoader.Parse("""
            {
              "schemaVersion": 1, "version": 1,
              "xpCurve": { "first": 60, "step": 24 },
              "awards": { "runCompletion": 100, "placement": 4 }
            }
            """);
        Assert.Equal(60, tuning.CurveFirst);
        Assert.Equal(24, tuning.CurveStep);
        Assert.Equal(100, tuning.RunCompletionAward);
        Assert.Equal(4, tuning.PlacementAward);
    }

    [Fact]
    public void Loader_rejectsAFractionalAward()
    {
        Assert.Throws<SpeciesProgressionTuningRejection>(() => SpeciesProgressionTuningLoader.Parse("""
            { "schemaVersion": 1, "version": 1,
              "xpCurve": { "first": 60, "step": 24 },
              "awards": { "runCompletion": 100.5, "placement": 4 } }
            """));
    }

    [Fact]
    public void Loader_rejectsAMissingKey()
    {
        Assert.Throws<SpeciesProgressionTuningRejection>(() => SpeciesProgressionTuningLoader.Parse("""
            { "schemaVersion": 1, "version": 1, "xpCurve": { "first": 60, "step": 24 } }
            """));
    }
}
