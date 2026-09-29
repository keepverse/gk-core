using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Match;

/// <summary>
/// creature-lawn-deploy T2.4 — the per-match run-state holder, plus an end-to-end mirror of
/// <c>MatchHost.CheckLawnDeployTrigger</c>'s own logic (the real method lives in the Injector project
/// and cannot be unit tested directly — same reasoning as `MatchCommanderSnapshotTests.cs`'s own
/// `HostApplyWithSnapshot` helper). Proves the FULL wiring — roster snapshot + tuning + run-state +
/// evaluator — actually fires and records itself when called together, not just each piece in
/// isolation (already covered separately by `LawnDeployRosterSnapshotTests.cs`/
/// `LawnDeployEventEvaluatorTests.cs`).
/// </summary>
public class LawnDeployEventRunStateHolderTests
{
    public LawnDeployEventRunStateHolderTests()
    {
        LawnDeployRosterSnapshotHolder.EndMatch();
        LawnDeployRosterSessionCache.ResetForTests();
        // LawnDeployEventRunStateHolder's own state is static and process-wide, like every other
        // holder here — BeginMatch() doubles as its own reset (both set Fresh), same as the sibling
        // holders' own EndMatch/ResetForTests calls above.
        LawnDeployEventRunStateHolder.BeginMatch();
    }

    [Fact]
    public void BeginMatch_resets_to_Fresh_even_after_a_fire_was_recorded()
    {
        LawnDeployEventRunStateHolder.RecordFired("zombie-swarm");
        Assert.Equal(1, LawnDeployEventRunStateHolder.Current.TotalFired);

        LawnDeployEventRunStateHolder.BeginMatch();
        Assert.Equal(0, LawnDeployEventRunStateHolder.Current.TotalFired);
        Assert.Empty(LawnDeployEventRunStateHolder.Current.FiredCaseIds);
    }

    [Fact]
    public void EndMatch_also_resets_to_Fresh()
    {
        LawnDeployEventRunStateHolder.RecordFired("thin-defense");
        LawnDeployEventRunStateHolder.EndMatch();
        Assert.Equal(0, LawnDeployEventRunStateHolder.Current.TotalFired);
    }

    [Fact]
    public void RecordFired_accumulates_across_multiple_calls()
    {
        LawnDeployEventRunStateHolder.BeginMatch();
        LawnDeployEventRunStateHolder.RecordFired("zombie-swarm");
        LawnDeployEventRunStateHolder.RecordFired("thin-defense");

        Assert.Equal(2, LawnDeployEventRunStateHolder.Current.TotalFired);
        Assert.Contains("zombie-swarm", LawnDeployEventRunStateHolder.Current.FiredCaseIds);
        Assert.Contains("thin-defense", LawnDeployEventRunStateHolder.Current.FiredCaseIds);
    }

    // ---- end-to-end mirror of MatchHost.CheckLawnDeployTrigger --------------------------------------

    /// <summary>Mirrors MatchHost.CheckLawnDeployTrigger's own real logic exactly (roster gate, tuning
    /// read, seed derivation from matchKey, evaluator call, RecordFired on a hit) — the Injector's own
    /// copy cannot be constructed here (net6.0, real game interop).</summary>
    static LawnDeployEventResult SimulateCheckLawnDeployTrigger(
        MatchSnapshot snapshot, string matchKey, LawnDeployEventsTuning tuning)
    {
        var roster = LawnDeployRosterSnapshotHolder.ResolveOrEmpty();
        if (roster.Eligible.Count == 0) return LawnDeployEventResult.NoFire;

        var seed = FusionRpg.Core.Battle.SeededRng.DeriveStream(0, matchKey).NextULong();
        var result = LawnDeployEventEvaluator.Evaluate(
            snapshot, roster, tuning, LawnDeployEventRunStateHolder.Current, seed);
        if (result.Fires) LawnDeployEventRunStateHolder.RecordFired(result.CaseId!);
        return result;
    }

    [Fact]
    public void Full_wiring_fires_and_records_when_the_real_pieces_agree()
    {
        LawnDeployEventRunStateHolder.BeginMatch();
        LawnDeployRosterSessionCache.Apply(new[] { new LawnDeployRosterEntry("specimen-a", "abyssswordstar") });
        LawnDeployRosterSnapshotHolder.BeginMatch(LawnDeployRosterSessionCache.BuildFromSessionCache());

        var tuning = new LawnDeployEventsTuning(1, 1, 1,
            new[] { new LawnDeployEventCaseTuning("zombie-swarm", ZombieCountAtLeast: 5, PlantCountAtMost: null, FireChanceMilli: 1000) });
        var snapshot = new MatchSnapshot { ZombieCount = 5 };

        var result = SimulateCheckLawnDeployTrigger(snapshot, "m-live-1", tuning);

        Assert.True(result.Fires);
        Assert.Equal("zombie-swarm", result.CaseId);
        Assert.Equal(1, LawnDeployEventRunStateHolder.Current.TotalFired);
    }

    [Fact]
    public void Full_wiring_never_fires_with_an_empty_roster_even_when_the_board_condition_is_true()
    {
        LawnDeployEventRunStateHolder.BeginMatch();
        // No LawnDeployRosterSessionCache.Apply -- holder stays Empty.
        LawnDeployRosterSnapshotHolder.BeginMatch(LawnDeployRosterSessionCache.BuildFromSessionCache());

        var tuning = new LawnDeployEventsTuning(1, 1, 1,
            new[] { new LawnDeployEventCaseTuning("zombie-swarm", ZombieCountAtLeast: 1, PlantCountAtMost: null, FireChanceMilli: 1000) });
        var result = SimulateCheckLawnDeployTrigger(new MatchSnapshot { ZombieCount = 99 }, "m-live-2", tuning);

        Assert.False(result.Fires);
        Assert.Equal(0, LawnDeployEventRunStateHolder.Current.TotalFired);
    }

    [Fact]
    public void Full_wiring_a_second_check_in_the_same_match_does_not_refire_the_same_case()
    {
        LawnDeployEventRunStateHolder.BeginMatch();
        LawnDeployRosterSessionCache.Apply(new[] { new LawnDeployRosterEntry("specimen-a", "abyssswordstar") });
        LawnDeployRosterSnapshotHolder.BeginMatch(LawnDeployRosterSessionCache.BuildFromSessionCache());

        var tuning = new LawnDeployEventsTuning(1, 1, 5,
            new[] { new LawnDeployEventCaseTuning("zombie-swarm", ZombieCountAtLeast: 1, PlantCountAtMost: null, FireChanceMilli: 1000) });
        var snapshot = new MatchSnapshot { ZombieCount = 99 };

        var first = SimulateCheckLawnDeployTrigger(snapshot, "m-live-3", tuning);
        var second = SimulateCheckLawnDeployTrigger(snapshot, "m-live-3", tuning);

        Assert.True(first.Fires);
        Assert.False(second.Fires);
    }
}
