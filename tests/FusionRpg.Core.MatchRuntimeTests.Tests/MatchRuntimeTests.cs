using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests;

public class MatchRuntimeTests
{
    [Fact]
    public void ContractVersion_is_1()
    {
        Assert.Equal(1, MatchRuntime.ContractVersion);
        var snap = new MatchRuntime().ToSnapshot();
        Assert.Equal(1, snap.ContractVersion);
    }

    [Fact]
    public void Board_start_Idle_to_InMatch_sets_MatchKey_and_revision()
    {
        var rt = new MatchRuntime();
        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Null(rt.MatchKey);
        var rev0 = rt.Revision;

        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-test-1" });

        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.Equal("m-test-1", rt.MatchKey);
        Assert.True(rt.Revision > rev0);
        Assert.True(rt.ToSnapshot().EffectSessionActive);
    }

    [Fact]
    public void Board_start_mints_MatchKey_when_missing()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.False(string.IsNullOrWhiteSpace(rt.MatchKey));
        Assert.StartsWith("m-", rt.MatchKey);
    }

    [Fact]
    public void Board_end_InMatch_to_Idle_clears_MatchKey()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-x" });
        rt.Apply("board.end");

        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Null(rt.MatchKey);
        Assert.False(rt.ToSnapshot().EffectSessionActive);
    }

    [Fact]
    public void Match_result_also_ends_match()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("match.result");
        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Null(rt.MatchKey);
    }

    [Fact]
    public void NotifyPaused_InMatch_Paused_InMatch()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.NotifyPaused(true);
        Assert.Equal(MatchPhase.Paused, rt.Phase);
        rt.NotifyPaused(false);
        Assert.Equal(MatchPhase.InMatch, rt.Phase);
    }

    [Fact]
    public void NotifyPaused_while_Idle_is_noop()
    {
        var rt = new MatchRuntime();
        var rev = rt.Revision;
        rt.NotifyPaused(true);
        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Unknown_kind_is_noop_no_throw()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var phase = rt.Phase;
        var key = rt.MatchKey;
        var rev = rt.Revision;
        rt.Apply("foo.bar", new Dictionary<string, object> { ["ptr"] = "0x1" });
        Assert.Equal(phase, rt.Phase);
        Assert.Equal(key, rt.MatchKey);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Board_end_while_Idle_is_noop()
    {
        var rt = new MatchRuntime();
        var rev = rt.Revision;
        rt.Apply("board.end");
        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Board_start_while_InMatch_is_ignored()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-keep" });
        var phase = rt.Phase;
        var key = rt.MatchKey;
        var rev = rt.Revision;

        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-other" });

        Assert.Equal(phase, rt.Phase);
        Assert.Equal(key, rt.MatchKey);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Board_start_while_Paused_is_ignored()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-p" });
        rt.NotifyPaused(true);
        var phase = rt.Phase;
        var key = rt.MatchKey;
        var rev = rt.Revision;

        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-restart" });

        Assert.Equal(MatchPhase.Paused, phase);
        Assert.Equal(phase, rt.Phase);
        Assert.Equal(key, rt.MatchKey);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Board_end_while_Paused_goes_Idle()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-pe" });
        rt.NotifyPaused(true);
        var rev = rt.Revision;

        rt.Apply("board.end");

        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Null(rt.MatchKey);
        Assert.False(rt.ToSnapshot().EffectSessionActive);
        Assert.True(rt.Revision > rev);
    }

    [Fact]
    public void Match_result_while_Paused_goes_Idle()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.NotifyPaused(true);
        rt.Apply("match.result");
        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Null(rt.MatchKey);
    }

    [Fact]
    public void Caps_snapshot_copy_is_isolated()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var snap = rt.ToSnapshot();
        var defaults = CapPolicyConfig.Defaults();
        Assert.Equal(defaults.MaxLivingPlants, snap.Caps.MaxLivingPlants);

        snap.Caps.MaxLivingPlants = 1;

        var again = rt.ToSnapshot();
        Assert.Equal(defaults.MaxLivingPlants, again.Caps.MaxLivingPlants);
    }

    [Fact]
    public void NotifyPaused_enter_and_exit_bump_revision()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var rev0 = rt.Revision;

        rt.NotifyPaused(true);
        Assert.Equal(MatchPhase.Paused, rt.Phase);
        Assert.True(rt.Revision > rev0);
        var rev1 = rt.Revision;

        rt.NotifyPaused(false);
        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.True(rt.Revision > rev1);
    }

    [Fact]
    public void NotifyPaused_true_twice_second_no_bump()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.NotifyPaused(true);
        var rev = rt.Revision;

        rt.NotifyPaused(true);

        Assert.Equal(MatchPhase.Paused, rt.Phase);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void NotifyPaused_false_while_InMatch_is_noop()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var rev = rt.Revision;

        rt.NotifyPaused(false);

        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Whitespace_or_null_kind_Apply_is_noop()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-w" });
        var phase = rt.Phase;
        var key = rt.MatchKey;
        var rev = rt.Revision;

        rt.Apply("   ");
        rt.Apply(null!);

        Assert.Equal(phase, rt.Phase);
        Assert.Equal(key, rt.MatchKey);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Board_end_bumps_revision()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var rev = rt.Revision;

        rt.Apply("board.end");

        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.True(rt.Revision > rev);
    }

    [Fact]
    public void Spawn_plant_then_zombie_updates_counts_and_entities()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1", ["type"] = 7 });
        rt.Apply("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xZ1", ["typeId"] = 3 });

        var snap = rt.ToSnapshot();
        Assert.Equal(1, snap.PlantCount);
        Assert.Equal(1, snap.ZombieCount);
        Assert.Equal(0, snap.BulletCount);
        Assert.Equal(2, snap.Entities.Length);
        Assert.Contains(snap.Entities, e => e.Ptr == "0xP1" && e.Side == BoardSide.Plant && e.TypeId == 7);
        Assert.Contains(snap.Entities, e => e.Ptr == "0xZ1" && e.Side == BoardSide.Zombie && e.TypeId == 3);
    }

    [Fact]
    public void Duplicate_spawn_same_ptr_upserts_typeId()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xA", ["type"] = 1 });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xa", ["type"] = 9 });

        var snap = rt.ToSnapshot();
        Assert.Equal(1, snap.PlantCount);
        Assert.Single(snap.Entities);
        Assert.Equal(9, snap.Entities[0].TypeId);
    }

    [Fact]
    public void Die_removes_missing_die_no_revision_bump()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xD1" });
        rt.Apply("plant.die", new Dictionary<string, object> { ["ptr"] = "0xD1" });
        Assert.Equal(0, rt.ToSnapshot().PlantCount);

        var rev = rt.Revision;
        rt.Apply("plant.die", new Dictionary<string, object> { ["ptr"] = "0xD1" });
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Place_does_not_change_living_counts()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP" });
        var rev = rt.Revision;
        var snap0 = rt.ToSnapshot();

        rt.Apply("plant.place", new Dictionary<string, object> { ["ptr"] = "0xNEW", ["type"] = 2 });
        rt.Apply("zombie.place", new Dictionary<string, object> { ["ptr"] = "0xZNEW" });

        var snap = rt.ToSnapshot();
        Assert.Equal(snap0.PlantCount, snap.PlantCount);
        Assert.Equal(snap0.ZombieCount, snap.ZombieCount);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Spawn_while_Idle_is_ignored()
    {
        var rt = new MatchRuntime();
        var rev = rt.Revision;
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1" });
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Start_spawn_end_clears_board()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1" });
        rt.Apply("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0x2" });
        rt.Apply("board.end");

        var snap = rt.ToSnapshot();
        Assert.Equal(MatchPhase.Idle, snap.Phase);
        Assert.Equal(0, snap.PlantCount);
        Assert.Equal(0, snap.ZombieCount);
        Assert.Empty(snap.Entities);
    }

    [Fact]
    public void Spawn_while_Paused_is_allowed()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.NotifyPaused(true);
        rt.Apply("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xZ" });

        Assert.Equal(MatchPhase.Paused, rt.Phase);
        Assert.Equal(1, rt.ToSnapshot().ZombieCount);
    }

    [Fact]
    public void Spawn_missing_ptr_is_noop()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var rev = rt.Revision;
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["type"] = 1 });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "  " });
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Identical_respawn_does_not_bump_revision()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1", ["type"] = 4 });
        var rev = rt.Revision;

        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1", ["type"] = 4 });

        Assert.Equal(rev, rt.Revision);
        Assert.Equal(1, rt.ToSnapshot().PlantCount);
    }

    [Fact]
    public void Upsert_typeId_change_bumps_revision()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1", ["type"] = 1 });
        var rev = rt.Revision;

        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1", ["type"] = 8 });

        Assert.True(rt.Revision > rev);
        Assert.Equal(8, rt.ToSnapshot().Entities[0].TypeId);
    }

    [Fact]
    public void Wrong_side_die_is_noop()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP" });
        var rev = rt.Revision;

        rt.Apply("zombie.die", new Dictionary<string, object> { ["ptr"] = "0xP" });

        Assert.Equal(1, rt.ToSnapshot().PlantCount);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Zombie_die_removes_and_bumps()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xZ" });
        var rev = rt.Revision;

        rt.Apply("zombie.die", new Dictionary<string, object> { ["ptr"] = "0xZ" });

        Assert.Equal(0, rt.ToSnapshot().ZombieCount);
        Assert.True(rt.Revision > rev);
    }

    [Fact]
    public void Die_while_Idle_is_noop()
    {
        var rt = new MatchRuntime();
        var rev = rt.Revision;
        rt.Apply("plant.die", new Dictionary<string, object> { ["ptr"] = "0x1" });
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Spawn_null_payload_is_noop()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var rev = rt.Revision;
        rt.Apply("plant.spawn");
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void TypeId_wins_over_type_bad_type_defaults_minus_one()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object>
        {
            ["ptr"] = "0xT",
            ["typeId"] = 11,
            ["type"] = 99
        });
        Assert.Equal(11, rt.ToSnapshot().Entities[0].TypeId);

        rt.Apply("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xB", ["type"] = "nope" });
        Assert.Equal(-1, rt.ToSnapshot().Entities.Single(e => e.Ptr == "0xB").TypeId);
    }

    [Fact]
    public void Kind_case_insensitive_Plant_Spawn_folds()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("Plant.Spawn", new Dictionary<string, object> { ["ptr"] = "0xC", ["type"] = 2 });
        Assert.Equal(1, rt.ToSnapshot().PlantCount);
    }

    [Fact]
    public void Snapshot_Entities_copy_is_isolated()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xE", ["type"] = 5 });
        var snap = rt.ToSnapshot();
        snap.Entities[0].TypeId = 999;

        Assert.Equal(5, rt.ToSnapshot().Entities[0].TypeId);
    }

    [Fact]
    public void Match_result_after_spawn_clears_board()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1" });
        rt.Apply("match.result");

        var snap = rt.ToSnapshot();
        Assert.Equal(MatchPhase.Idle, snap.Phase);
        Assert.Equal(0, snap.PlantCount);
        Assert.Empty(snap.Entities);
    }

    [Fact]
    public void Blank_ptr_die_no_bump()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1" });
        var rev = rt.Revision;

        rt.Apply("plant.die", new Dictionary<string, object> { ["ptr"] = "  " });
        rt.Apply("plant.die");

        Assert.Equal(1, rt.ToSnapshot().PlantCount);
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void TryGetBindingByPtr_returns_bound_row()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var id = "inst-ptr";
        var corr = "corr-ptr";
        Assert.True(rt.TryBeginPending(id, corr, "plant", 1));
        rt.Apply("plant.spawn", new Dictionary<string, object>
        {
            ["ptr"] = "0xBEEF",
            ["correlationId"] = corr,
            ["instanceId"] = id,
        });

        Assert.True(rt.TryGetBindingByPtr("BEEF", out var binding));
        Assert.NotNull(binding);
        Assert.Equal(UniqueBindingPhase.Bound, binding!.Phase);
        Assert.Equal("BEEF", binding.Ptr);
    }

    [Fact]
    public void TryGetBindingByPtr_miss_after_clear()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var id = "inst-clear";
        var corr = "corr-clear";
        Assert.True(rt.TryBeginPending(id, corr, "zombie", 2));
        rt.Apply("zombie.spawn", new Dictionary<string, object>
        {
            ["ptr"] = "0xCAFE",
            ["correlationId"] = corr,
            ["instanceId"] = id,
        });
        rt.Apply("zombie.die", new Dictionary<string, object> { ["ptr"] = "0xCAFE" });

        Assert.False(rt.TryGetBindingByPtr("CAFE", out _));
    }
}
