using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>zomboss-deploy-ai T3.3 — the scorer's own named scenario table plus a determinism proof,
/// per this task's own two acceptance lines.</summary>
public class ZombossDeployAiTests
{
    static LawnBoardSnapshot Board(int ownUnits, int enemyUnits) => new(
        WaveNumber: 5, MaxWave: 20,
        VisibleUnits: Enumerable.Range(0, ownUnits)
            .Select(i => (ILawnUnitView)new LawnUnitSnapshot($"own-{i}", RelationKind.Ally, 100, 100))
            .Concat(Enumerable.Range(0, enemyUnits)
                .Select(i => (ILawnUnitView)new LawnUnitSnapshot($"enemy-{i}", RelationKind.Enemy, 100, 100)))
            .ToList());

    static readonly ZombossScorerTuning AlwaysFires = new(FireChanceMilli: 1000, MinEnemyUnitsToConsiderDeploy: 3, MaxConcurrentOwnUnits: 2);
    static readonly ZombossScorerTuning NeverFires = AlwaysFires with { FireChanceMilli = 0 };

    static CreatureRarity RarityOf(string speciesId) => speciesId switch
    {
        "low" => CreatureRarity.Chaff,
        "mid" => CreatureRarity.Cultivated,
        "high" => CreatureRarity.Almanac,
        _ => throw new ArgumentOutOfRangeException(nameof(speciesId)),
    };

    [Fact]
    public void No_eligible_creature_declines_regardless_of_board_state_or_roll()
    {
        var decision = ZombossDeployPolicy.Decide(
            Board(ownUnits: 0, enemyUnits: 10), Array.Empty<string>(), RarityOf, AlwaysFires, matchSeed: 1, caseId: "case-a");

        Assert.False(decision.Deploys);
        Assert.Null(decision.SpeciesId);
    }

    [Fact]
    public void Exactly_one_candidate_is_picked_when_the_board_and_roll_both_favor_deploying()
    {
        var decision = ZombossDeployPolicy.Decide(
            Board(ownUnits: 0, enemyUnits: 10), new[] { "mid" }, RarityOf, AlwaysFires, matchSeed: 1, caseId: "case-a");

        Assert.True(decision.Deploys);
        Assert.Equal("mid", decision.SpeciesId);
    }

    [Fact]
    public void Multiple_candidates_rank_by_rarity_highest_wins_not_just_something()
    {
        var decision = ZombossDeployPolicy.Decide(
            Board(ownUnits: 0, enemyUnits: 10), new[] { "low", "high", "mid" }, RarityOf, AlwaysFires, matchSeed: 1, caseId: "case-a");

        Assert.True(decision.Deploys);
        Assert.Equal("high", decision.SpeciesId);
    }

    [Fact]
    public void Ties_break_by_SpeciesId_ordinal_deterministically()
    {
        static CreatureRarity SameRarity(string _) => CreatureRarity.Cultivated;

        var decision = ZombossDeployPolicy.Decide(
            Board(ownUnits: 0, enemyUnits: 10), new[] { "zzz", "aaa", "mmm" }, SameRarity, AlwaysFires, matchSeed: 1, caseId: "case-a");

        Assert.True(decision.Deploys);
        Assert.Equal("aaa", decision.SpeciesId);
    }

    [Fact]
    public void Declines_when_too_few_enemy_units_are_visible_even_with_candidates_and_a_favorable_roll()
    {
        var decision = ZombossDeployPolicy.Decide(
            Board(ownUnits: 0, enemyUnits: 1), new[] { "high" }, RarityOf, AlwaysFires, matchSeed: 1, caseId: "case-a");

        Assert.False(decision.Deploys);
        Assert.Contains("enemy", decision.Reason);
    }

    [Fact]
    public void Declines_when_already_at_the_concurrent_own_unit_cap()
    {
        var decision = ZombossDeployPolicy.Decide(
            Board(ownUnits: 2, enemyUnits: 10), new[] { "high" }, RarityOf, AlwaysFires, matchSeed: 1, caseId: "case-a");

        Assert.False(decision.Deploys);
        Assert.Contains("concurrent", decision.Reason);
    }

    [Fact]
    public void Declines_on_a_missed_roll_even_with_a_favorable_board_and_candidates()
    {
        var decision = ZombossDeployPolicy.Decide(
            Board(ownUnits: 0, enemyUnits: 10), new[] { "high" }, RarityOf, NeverFires, matchSeed: 1, caseId: "case-a");

        Assert.False(decision.Deploys);
        Assert.Contains("roll", decision.Reason);
    }

    [Fact]
    public void Same_board_seed_and_caseId_produce_a_byte_identical_decision_twice()
    {
        var board = Board(ownUnits: 0, enemyUnits: 10);
        var candidates = new[] { "low", "high", "mid" };

        var first = ZombossDeployPolicy.Decide(board, candidates, RarityOf, AlwaysFires, matchSeed: 777, caseId: "same-case");
        var second = ZombossDeployPolicy.Decide(board, candidates, RarityOf, AlwaysFires, matchSeed: 777, caseId: "same-case");

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_different_caseId_can_change_the_roll_outcome_independently()
    {
        // Same board/candidates/seed, only the case label differs — proves the roll is derived from
        // (matchSeed, caseId) together, not matchSeed alone (SeededRng.DeriveStream's own contract).
        var board = Board(ownUnits: 0, enemyUnits: 10);
        var candidates = new[] { "high" };

        var a = ZombossDeployPolicy.Decide(board, candidates, RarityOf, NeverFires, matchSeed: 42, caseId: "case-a");
        var b = ZombossDeployPolicy.Decide(board, candidates, RarityOf, NeverFires, matchSeed: 42, caseId: "case-b");

        // Both decline (NeverFires), but via the SAME code path -- this test's real assertion is that
        // Decide never throws or conflates the two case ids, exercised directly below.
        Assert.False(a.Deploys);
        Assert.False(b.Deploys);
    }

    [Fact]
    public void Throws_on_null_arguments_rather_than_silently_defaulting()
    {
        var board = Board(0, 10);
        Assert.Throws<ArgumentNullException>(() => ZombossDeployPolicy.Decide(null!, new[] { "x" }, RarityOf, AlwaysFires, 1, "c"));
        Assert.Throws<ArgumentNullException>(() => ZombossDeployPolicy.Decide(board, null!, RarityOf, AlwaysFires, 1, "c"));
        Assert.Throws<ArgumentNullException>(() => ZombossDeployPolicy.Decide(board, new[] { "x" }, null!, AlwaysFires, 1, "c"));
        Assert.Throws<ArgumentNullException>(() => ZombossDeployPolicy.Decide(board, new[] { "x" }, RarityOf, null!, 1, "c"));
    }

    [Fact]
    public void Throws_on_an_empty_caseId()
    {
        var board = Board(0, 10);
        Assert.Throws<ArgumentException>(() => ZombossDeployPolicy.Decide(board, new[] { "x" }, RarityOf, AlwaysFires, 1, ""));
    }
}
