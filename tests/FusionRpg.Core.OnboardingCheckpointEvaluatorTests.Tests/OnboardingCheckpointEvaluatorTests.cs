using FusionRpg.Core.Onboarding;
using Xunit;

namespace FusionRpg.Core.Tests;

public sealed class OnboardingCheckpointEvaluatorTests
{
    static OnboardingEligibilityInput Input(
        long level = 1,
        bool pvz = true,
        string? result = null,
        bool first = false,
        bool species = false,
        bool equipment = false,
        params OnboardingSourceEvidence[] sources) =>
        new(level, pvz, result, Settled: true, first, species, equipment, sources);

    [Fact]
    public void First_pvz_victory_unlocks_Dave_first()
    {
        var result = OnboardingCheckpointEvaluator.Evaluate(Input(result: "victory"));

        Assert.Equal([OnboardingCheckpointIds.FirstWinDave], result.NewlyEligible);
    }

    [Fact]
    public void Defeat_stalemate_or_web_result_does_not_unlock_first_win()
    {
        Assert.Empty(OnboardingCheckpointEvaluator.Evaluate(Input(result: "defeat")).NewlyEligible);
        Assert.Empty(OnboardingCheckpointEvaluator.Evaluate(Input(result: "stalemate")).NewlyEligible);
        Assert.Empty(OnboardingCheckpointEvaluator.Evaluate(Input(pvz: false, result: "victory")).NewlyEligible);
        Assert.Equal("onboarding.not-pvz", OnboardingCheckpointEvaluator.Evaluate(Input(pvz: false, result: "victory")).Reason);
    }

    [Fact]
    public void Level_three_requires_a_valid_general_source_in_the_same_run()
    {
        var noSource = Input(level: 3, first: true);
        var unique = Input(level: 3, first: true,
            sources: new OnboardingSourceEvidence("ZombieSpawned", "creature.progression.v1", "unique:specimen:1"));
        var valid = Input(level: 3, first: true,
            sources: new OnboardingSourceEvidence("ZombieSpawned", "creature.progression.v1", "general:basic-zombie"));

        Assert.Empty(OnboardingCheckpointEvaluator.Evaluate(noSource).NewlyEligible);
        Assert.Empty(OnboardingCheckpointEvaluator.Evaluate(unique).NewlyEligible);
        Assert.Contains(OnboardingCheckpointIds.Level3GeneralSpecies,
            OnboardingCheckpointEvaluator.Evaluate(valid).NewlyEligible);
    }

    [Fact]
    public void Invalid_source_claims_fail_closed_and_never_fallback_by_type()
    {
        var evidence = new[]
        {
            new OnboardingSourceEvidence("ZombieSpawned", null, null, TypeId: 7),
            new OnboardingSourceEvidence("ZombieSpawned", "opaque", "general:basic-zombie", TypeId: 7),
            new OnboardingSourceEvidence("ZombieSpawned", "creature.progression.v1", "commander:dave", TypeId: 7),
        };

        var result = OnboardingCheckpointEvaluator.Evaluate(Input(level: 3, first: true, sources: evidence));

        Assert.Empty(result.NewlyEligible);
        Assert.Equal("onboarding.general-source-required", result.Reason);
    }

    [Fact]
    public void A_single_result_can_unlock_ordered_checkpoints_but_equipment_waits_for_level_four()
    {
        var levelThree = Input(level: 3, result: "won",
            sources: new OnboardingSourceEvidence("ZombieSpawned", "creature.progression.v1", "general:basic-zombie"));
        var levelFour = Input(level: 4, result: "victory",
            sources: new OnboardingSourceEvidence("ZombieSpawned", "creature.progression.v1", "general:basic-zombie"));

        Assert.Equal(
            [OnboardingCheckpointIds.FirstWinDave, OnboardingCheckpointIds.Level3GeneralSpecies],
            OnboardingCheckpointEvaluator.Evaluate(levelThree).NewlyEligible);
        Assert.Equal(
            [OnboardingCheckpointIds.FirstWinDave, OnboardingCheckpointIds.Level3GeneralSpecies,
             OnboardingCheckpointIds.Level4DaveEquipment],
            OnboardingCheckpointEvaluator.Evaluate(levelFour).NewlyEligible);
    }

    [Fact]
    public void Earned_checkpoints_are_not_reissued_and_equipment_waits_for_both_prerequisites()
    {
        var input = Input(level: 4, result: "victory", first: true, species: true,
            sources: new OnboardingSourceEvidence("ZombieSpawned", "creature.progression.v1", "general:basic-zombie"));

        var result = OnboardingCheckpointEvaluator.Evaluate(input);

        Assert.Equal([OnboardingCheckpointIds.Level4DaveEquipment], result.NewlyEligible);
    }
}
