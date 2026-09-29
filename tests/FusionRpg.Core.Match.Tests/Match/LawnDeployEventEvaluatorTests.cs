using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Match;

/// <summary>
/// creature-lawn-deploy T2.2/T2.3 — one fact per named case × {fires, does-not-fire-below-threshold,
/// does-not-fire-if-already-used-this-run}, per spec-lawn-deploy-events.md's own testing strategy
/// (mirroring spec-ai-commander.md's own per-rule fire/no-fire matrix), plus an explicit determinism
/// assertion. `FireChanceMilli: 1000`/`0` in test-local tuning removes the RNG as a variable for the
/// condition-gate tests — the determinism test below is what actually exercises the roll.
/// </summary>
public class LawnDeployEventEvaluatorTests
{
    static MatchSnapshot Match(int plantCount = 0, int zombieCount = 0) =>
        new() { PlantCount = plantCount, ZombieCount = zombieCount };

    static LawnDeployRosterSnapshot OneEligible() =>
        new(new[] { new LawnDeployRosterEntry("specimen-a", "abyssswordstar") }, 1);

    static LawnDeployEventsTuning Tuning(int maxFiresPerRun, params LawnDeployEventCaseTuning[] cases) =>
        new(1, 1, maxFiresPerRun, cases);

    // ---- zombie-swarm: fires / does-not-fire-below-threshold / does-not-fire-if-already-used --------

    [Fact]
    public void Zombie_swarm_fires_when_the_threshold_is_met()
    {
        var tuning = Tuning(1, new LawnDeployEventCaseTuning("zombie-swarm", ZombieCountAtLeast: 5, PlantCountAtMost: null, FireChanceMilli: 1000));
        var result = LawnDeployEventEvaluator.Evaluate(
            Match(zombieCount: 5), OneEligible(), tuning, LawnDeployEventRunState.Fresh, matchSeed: 1);

        Assert.True(result.Fires);
        Assert.Equal("zombie-swarm", result.CaseId);
    }

    [Fact]
    public void Zombie_swarm_does_not_fire_below_the_threshold()
    {
        var tuning = Tuning(1, new LawnDeployEventCaseTuning("zombie-swarm", ZombieCountAtLeast: 5, PlantCountAtMost: null, FireChanceMilli: 1000));
        var result = LawnDeployEventEvaluator.Evaluate(
            Match(zombieCount: 4), OneEligible(), tuning, LawnDeployEventRunState.Fresh, matchSeed: 1);

        Assert.False(result.Fires);
    }

    [Fact]
    public void Zombie_swarm_does_not_fire_if_already_used_this_run()
    {
        var tuning = Tuning(5, new LawnDeployEventCaseTuning("zombie-swarm", ZombieCountAtLeast: 5, PlantCountAtMost: null, FireChanceMilli: 1000));
        var runState = LawnDeployEventRunState.Fresh.WithFired("zombie-swarm");
        var result = LawnDeployEventEvaluator.Evaluate(
            Match(zombieCount: 99), OneEligible(), tuning, runState, matchSeed: 1);

        Assert.False(result.Fires);
    }

    // ---- thin-defense: fires / does-not-fire-below-threshold / does-not-fire-if-already-used --------

    [Fact]
    public void Thin_defense_fires_when_plants_are_low_and_zombies_present()
    {
        var tuning = Tuning(1, new LawnDeployEventCaseTuning("thin-defense", ZombieCountAtLeast: 1, PlantCountAtMost: 2, FireChanceMilli: 1000));
        var result = LawnDeployEventEvaluator.Evaluate(
            Match(plantCount: 2, zombieCount: 1), OneEligible(), tuning, LawnDeployEventRunState.Fresh, matchSeed: 1);

        Assert.True(result.Fires);
        Assert.Equal("thin-defense", result.CaseId);
    }

    [Fact]
    public void Thin_defense_does_not_fire_when_plant_count_is_above_the_threshold()
    {
        var tuning = Tuning(1, new LawnDeployEventCaseTuning("thin-defense", ZombieCountAtLeast: 1, PlantCountAtMost: 2, FireChanceMilli: 1000));
        var result = LawnDeployEventEvaluator.Evaluate(
            Match(plantCount: 3, zombieCount: 1), OneEligible(), tuning, LawnDeployEventRunState.Fresh, matchSeed: 1);

        Assert.False(result.Fires);
    }

    [Fact]
    public void Thin_defense_does_not_fire_if_already_used_this_run()
    {
        var tuning = Tuning(5, new LawnDeployEventCaseTuning("thin-defense", ZombieCountAtLeast: 1, PlantCountAtMost: 2, FireChanceMilli: 1000));
        var runState = LawnDeployEventRunState.Fresh.WithFired("thin-defense");
        var result = LawnDeployEventEvaluator.Evaluate(
            Match(plantCount: 0, zombieCount: 1), OneEligible(), tuning, runState, matchSeed: 1);

        Assert.False(result.Fires);
    }

    // ---- cross-cutting gates: eligibility, per-run budget, ordering ---------------------------------

    [Fact]
    public void Never_fires_with_no_eligible_creature_regardless_of_board_state()
    {
        var tuning = Tuning(1, new LawnDeployEventCaseTuning("zombie-swarm", 1, null, 1000));
        var result = LawnDeployEventEvaluator.Evaluate(
            Match(zombieCount: 99), LawnDeployRosterSnapshot.Empty, tuning, LawnDeployEventRunState.Fresh, matchSeed: 1);

        Assert.False(result.Fires);
    }

    [Fact]
    public void Per_run_budget_exhausted_refuses_a_different_cases_condition_too()
    {
        var tuning = Tuning(1,
            new LawnDeployEventCaseTuning("zombie-swarm", 1, null, 1000),
            new LawnDeployEventCaseTuning("thin-defense", 1, 2, 1000));
        // TotalFired already at the 1-per-run budget, even though neither case id is itself "used".
        var runState = new LawnDeployEventRunState(new HashSet<string>(), TotalFired: 1);

        var result = LawnDeployEventEvaluator.Evaluate(
            Match(plantCount: 0, zombieCount: 99), OneEligible(), tuning, runState, matchSeed: 1);

        Assert.False(result.Fires);
    }

    [Fact]
    public void Two_true_conditions_resolve_to_the_first_declared_case_not_arbitrarily()
    {
        var tuning = Tuning(1,
            new LawnDeployEventCaseTuning("thin-defense", 1, 2, 1000),
            new LawnDeployEventCaseTuning("zombie-swarm", 1, null, 1000));
        // Both conditions are true at once (low plants AND a zombie swarm).
        var result = LawnDeployEventEvaluator.Evaluate(
            Match(plantCount: 0, zombieCount: 99), OneEligible(), tuning, LawnDeployEventRunState.Fresh, matchSeed: 1);

        Assert.Equal("thin-defense", result.CaseId);
    }

    // ---- determinism (T2.3) --------------------------------------------------------------------------

    [Fact]
    public void Same_seed_and_case_id_produce_a_byte_identical_decision_every_time()
    {
        var tuning = Tuning(1, new LawnDeployEventCaseTuning("zombie-swarm", ZombieCountAtLeast: 1, PlantCountAtMost: null, FireChanceMilli: 300));
        var match = Match(zombieCount: 99);
        var roster = OneEligible();

        LawnDeployEventResult Run() =>
            LawnDeployEventEvaluator.Evaluate(match, roster, tuning, LawnDeployEventRunState.Fresh, matchSeed: 424242);

        var first = Run();
        for (var i = 0; i < 20; i++)
        {
            var repeat = Run();
            Assert.Equal(first.Fires, repeat.Fires);
            Assert.Equal(first.CaseId, repeat.CaseId);
        }
    }

    [Fact]
    public void Different_seeds_can_produce_different_decisions_for_the_same_case()
    {
        // Not a hard requirement of any one seed's own outcome — just proves the roll is actually a
        // function of matchSeed, not a constant the FireChanceMilli gate alone would also explain.
        var tuning = Tuning(1, new LawnDeployEventCaseTuning("zombie-swarm", ZombieCountAtLeast: 1, PlantCountAtMost: null, FireChanceMilli: 300));
        var match = Match(zombieCount: 99);
        var roster = OneEligible();

        var outcomes = new HashSet<bool>();
        for (ulong seed = 1; seed <= 40; seed++)
            outcomes.Add(LawnDeployEventEvaluator.Evaluate(match, roster, tuning, LawnDeployEventRunState.Fresh, seed).Fires);

        Assert.Equal(2, outcomes.Count);
    }

    // ---- the real committed tuning file loads and is internally consistent --------------------------

    [Fact]
    public void The_real_committed_tuning_file_parses_and_names_both_starting_cases()
    {
        var json = File.ReadAllText(RepoRoot() + "/data/tuning/lawn-deploy-events.v1.json");
        var tuning = LawnDeployEventsTuningLoader.Parse(json);

        Assert.Equal(1, tuning.SchemaVersion);
        Assert.True(tuning.MaxFiresPerRun >= 1);
        Assert.Contains(tuning.Cases, c => c.CaseId == "zombie-swarm");
        Assert.Contains(tuning.Cases, c => c.CaseId == "thin-defense");
        Assert.All(tuning.Cases, c => Assert.InRange(c.FireChanceMilli, 0, 1000));
    }

    [Fact]
    public void Loader_rejects_a_case_with_no_condition_at_all()
    {
        const string json = """
            { "schemaVersion":1, "version":1, "maxFiresPerRun":1,
              "cases": { "empty-case": { "fireChanceMilli": 500 } } }
            """;
        var ex = Assert.Throws<LawnDeployEventsTuningRejection>(() => LawnDeployEventsTuningLoader.Parse(json));
        Assert.Contains("empty-case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_rejects_an_out_of_range_fire_chance()
    {
        const string json = """
            { "schemaVersion":1, "version":1, "maxFiresPerRun":1,
              "cases": { "bad": { "zombieCountAtLeast": 1, "fireChanceMilli": 1001 } } }
            """;
        Assert.Throws<LawnDeployEventsTuningRejection>(() => LawnDeployEventsTuningLoader.Parse(json));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find repo root (no src/FusionRpg.Injector above test bin)");
    }
}
