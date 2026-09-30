using System;
using System.IO;
using System.Linq;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `ai-empire-species` EP4.17 (R23) — the ONE empire-keyed commander-pool read
/// (`RpgStore.CommanderPoolOf`). Dave's explicit pool is unchanged; any other empire gets
/// explicit-else-ladder-default; nothing is written. It is not wired into a seam yet (EP4.18), so no
/// golden moves and these are the whole proof.
/// </summary>
public class ZombossCommanderPoolTests : IDisposable
{
    static readonly AptitudeTuning RealTuning = AptitudeTuningLoader.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "aptitudes.v10.json")));

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ZombossCommanderPoolTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        // The shipped ladder order (active-preset, species-favour, postures, even) — NOT a one-rung
        // ladder: the point of this file's first case is that the commander context SKIPS the rungs an
        // AI cannot reach, which a minimal ladder could not show. This hub is global (this assembly's
        // bootstrap does not configure it, and Data.Tests' own preset tests configure their own).
        AptitudePresetTuningHub.Configure(AptitudePresetTuningLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "aptitude-presets.v2.json"))));
    }

    public void Dispose() => _testStore.Dispose();

    static string PoolKey(EmpireId empire, long saveId)
    {
        var directory = CommanderDirectoryHub.Current;
        return directory.AllocationScopeKey(directory.DefaultFor(empire), saveId);
    }

    [Fact]
    public void A_zomboss_pool_resolves_the_ladders_commander_default_at_his_budget_and_writes_nothing()
    {
        var player = _store.CreatePlayer("ZombossPool");
        var owner = new EmpireRef(new SaveId(player.Id), EmpireId.Zomboss);
        var key = PoolKey(EmpireId.Zomboss, player.Id);
        var before = _store.LoadAllocation(AllocationScope.Commander, key);

        const long theta = 100;
        var pool = _store.CommanderPoolOf(owner, theta, RealTuning);

        // The ladder's own commander-context walk — asserted against the ladder, never a pinned rung id.
        var expected = AssignLadder.Suggest(
            new AssignContext(ActivePresetRows: null, SpeciesFavourPermille: null, SpeciesPosture: null, FavourAllowed: false),
            AptitudePresetTuningHub.Tuning.AssignLadder);
        Assert.True(pool.IsDefault);
        Assert.Equal(expected.RuleId, pool.DefaultRuleId);
        Assert.NotEmpty(pool.Skipped);   // the skips the commander context records are reported

        // Points sum to the budget the caller composed for him (EP4.16's Theta -> this read).
        var budget = PointBudget.PointsFor(AllocationScope.Commander, theta, RealTuning);
        Assert.True(budget > 0);
        Assert.Equal(budget, pool.Allocation.TotalForScope(AllocationScope.Commander));

        // Never persisted: the stored row is byte-identical, and a second read answers the same.
        Assert.Equal(before.TotalForScope(AllocationScope.Commander),
            _store.LoadAllocation(AllocationScope.Commander, key).TotalForScope(AllocationScope.Commander));
        Assert.Equal(0, _store.LoadAllocation(AllocationScope.Commander, key).TotalForScope(AllocationScope.Commander));
        Assert.Equal(pool.Allocation.TotalForScope(AllocationScope.Commander),
            _store.CommanderPoolOf(owner, theta, RealTuning).Allocation.TotalForScope(AllocationScope.Commander));
    }

    [Fact]
    public void A_zero_budget_resolves_Empty()
    {
        var player = _store.CreatePlayer("ZombossPoolZero");
        var pool = _store.CommanderPoolOf(
            new EmpireRef(new SaveId(player.Id), EmpireId.Zomboss), theta: 0, RealTuning);

        Assert.Equal(0, pool.Allocation.TotalForScope(AllocationScope.Commander));
    }

    [Fact]
    public void An_explicit_pool_under_his_key_wins_wholesale()
    {
        var player = _store.CreatePlayer("ZombossPoolExplicit");
        var key = PoolKey(EmpireId.Zomboss, player.Id);
        var explicitPool = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 7);
        _store.SaveAllocation(AllocationScope.Commander, key, explicitPool);

        var pool = _store.CommanderPoolOf(
            new EmpireRef(new SaveId(player.Id), EmpireId.Zomboss), theta: 100, RealTuning);

        Assert.False(pool.IsDefault);
        Assert.Equal(7, pool.Allocation.PointsAt(AllocationScope.Commander, "Might"));
    }

    [Fact]
    public void Save_As_pool_never_reaches_save_B()
    {
        var saveA = _store.CreatePlayer("ZombossPoolA");
        var saveB = _store.CreatePlayer("ZombossPoolB");
        var saveC = _store.CreatePlayer("ZombossPoolC");
        _store.SaveAllocation(AllocationScope.Commander, PoolKey(EmpireId.Zomboss, saveA.Id),
            AptitudeAllocation.Single(AllocationScope.Commander, "Might", 9));

        var a = _store.CommanderPoolOf(new EmpireRef(new SaveId(saveA.Id), EmpireId.Zomboss), 100, RealTuning);
        var b = _store.CommanderPoolOf(new EmpireRef(new SaveId(saveB.Id), EmpireId.Zomboss), 100, RealTuning);
        var c = _store.CommanderPoolOf(new EmpireRef(new SaveId(saveC.Id), EmpireId.Zomboss), 100, RealTuning);

        Assert.False(a.IsDefault);                                  // A's own explicit row
        Assert.Equal(9, a.Allocation.PointsAt(AllocationScope.Commander, "Might"));
        // B and C are unseeded, so both resolve the ladder default and NEITHER sees A's row: the read
        // uses both halves of (SaveId, EmpireId).
        Assert.True(b.IsDefault);
        Assert.True(c.IsDefault);
        Assert.Equal(c.Allocation.PointsAt(AllocationScope.Commander, "Might"),
            b.Allocation.PointsAt(AllocationScope.Commander, "Might"));
    }

    [Fact]
    public void An_empty_Dave_pool_stays_Empty_and_takes_no_silent_default()
    {
        // map D1: the player's own sheet has no silent default — the player is the one who can click.
        var player = _store.CreatePlayer("DavePoolEmpty");
        var pool = _store.CommanderPoolOf(
            new EmpireRef(new SaveId(player.Id), EmpireId.Dave), theta: 100, RealTuning);

        Assert.False(pool.IsDefault);
        Assert.Equal(0, pool.Allocation.TotalForScope(AllocationScope.Commander));
        // ... and the same read DOES return an explicit one, so the empty answer is the D1 rule and not
        // a broken key.
        _store.SaveAllocation(AllocationScope.Commander, PoolKey(EmpireId.Dave, player.Id),
            AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 5));
        Assert.Equal(5, _store.CommanderPoolOf(
            new EmpireRef(new SaveId(player.Id), EmpireId.Dave), 100, RealTuning)
            .Allocation.PointsAt(AllocationScope.Commander, "Vigor"));
    }

    [Fact]
    public void An_empire_the_save_does_not_carry_reads_Empty()
    {
        var player = _store.CreatePlayer("ZombossPoolUnseeded");
        var empire = new EmpireId("no-such-empire");
        var pool = _store.CommanderPoolOf(new EmpireRef(new SaveId(player.Id), empire), 100, RealTuning);

        Assert.False(pool.IsDefault);
        Assert.Equal(0, pool.Allocation.TotalForScope(AllocationScope.Commander));
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
