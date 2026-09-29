using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests;

public class MatchAdmitTests
{
    [Fact]
    public void Phase_reason_constants_match_spec()
    {
        Assert.Equal("phase.idle", GateReasons.PhaseIdle);
        Assert.Equal("phase.starting", GateReasons.PhaseStarting);
        Assert.Equal("phase.paused", GateReasons.PhasePaused);
        Assert.Equal("phase.ending", GateReasons.PhaseEnding);
    }

    [Fact]
    public void ForPhase_maps_all_phases()
    {
        Assert.Equal(GateReasons.PhaseIdle, GateReasons.ForPhase(MatchPhase.Idle));
        Assert.Equal(GateReasons.PhaseStarting, GateReasons.ForPhase(MatchPhase.Starting));
        Assert.Equal(GateReasons.PhasePaused, GateReasons.ForPhase(MatchPhase.Paused));
        Assert.Equal(GateReasons.PhaseEnding, GateReasons.ForPhase(MatchPhase.Ending));
        Assert.Equal("", GateReasons.ForPhase(MatchPhase.InMatch));
    }

    [Fact]
    public void Idle_plant_rejects_phase_idle()
    {
        var rt = new MatchRuntime();
        Assert.False(rt.TryAdmitSpawn("plant", out var g));
        Assert.False(g.Ok);
        Assert.Equal(GateReasons.PhaseIdle, g.Reason);
    }

    [Fact]
    public void Idle_invalid_side_still_phase_idle()
    {
        var rt = new MatchRuntime();
        Assert.False(rt.TryAdmitSpawn("grid", out var g));
        Assert.Equal(GateReasons.PhaseIdle, g.Reason);
        Assert.False(rt.TryAdmitSpawn(null, out var g2));
        Assert.Equal(GateReasons.PhaseIdle, g2.Reason);
    }

    [Fact]
    public void InMatch_under_cap_allows()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.True(rt.TryAdmitSpawn("plant", out var g));
        Assert.True(g.Ok);
        Assert.Equal("", g.Reason);
    }

    [Fact]
    public void InMatch_at_plant_cap_rejects()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 1, MaxLivingZombies = 80, MaxLivingBullets = -1 });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1" });

        Assert.False(rt.TryAdmitSpawn("plant", out var g));
        Assert.Equal(GateReasons.CapPlants, g.Reason);
    }

    [Fact]
    public void InMatch_at_zombie_cap_rejects()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 50, MaxLivingZombies = 1, MaxLivingBullets = -1 });
        rt.Apply("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xZ1" });

        Assert.False(rt.TryAdmitSpawn("zombie", out var g));
        Assert.Equal(GateReasons.CapZombies, g.Reason);
    }

    [Fact]
    public void InMatch_bullet_max_zero_rejects()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 50, MaxLivingZombies = 80, MaxLivingBullets = 0 });

        Assert.False(rt.TryAdmitSpawn("bullet", out var g));
        Assert.Equal(GateReasons.CapBullets, g.Reason);
    }

    [Fact]
    public void Paused_rejects_phase_paused()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.NotifyPaused(true);
        Assert.False(rt.TryAdmitSpawn("zombie", out var g));
        Assert.Equal(GateReasons.PhasePaused, g.Reason);
    }

    [Fact]
    public void Paused_invalid_side_still_phase_paused()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.NotifyPaused(true);
        Assert.False(rt.TryAdmitSpawn("grid", out var g));
        Assert.Equal(GateReasons.PhasePaused, g.Reason);
    }

    [Fact]
    public void Invalid_side_while_InMatch_rejects()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.False(rt.TryAdmitSpawn("grid", out var g));
        Assert.Equal(GateReasons.CapInvalidSide, g.Reason);
    }

    [Fact]
    public void Bullet_default_unlimited_ok()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.True(rt.TryAdmitSpawn("bullet", out var g));
        Assert.True(g.Ok);
    }

    [Fact]
    public void After_board_end_rejects_phase_idle()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.Apply("board.end");
        Assert.False(rt.TryAdmitSpawn("plant", out var g));
        Assert.Equal(GateReasons.PhaseIdle, g.Reason);
    }

    [Fact]
    public void After_end_Snapshot_Caps_reset_to_defaults()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 2, MaxLivingZombies = 3, MaxLivingBullets = 4 });
        rt.Apply("board.end");

        var caps = rt.ToSnapshot().Caps;
        Assert.Equal(50, caps.MaxLivingPlants);
        Assert.Equal(80, caps.MaxLivingZombies);
        Assert.Equal(-1, caps.MaxLivingBullets);
    }

    [Fact]
    public void Admit_does_not_bump_revision_or_add_entity()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var rev = rt.Revision;
        Assert.True(rt.TryAdmitSpawn("plant", out _));
        Assert.Equal(rev, rt.Revision);
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
    }

    [Fact]
    public void Reject_Admit_does_not_mutate()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 0, MaxLivingZombies = 80, MaxLivingBullets = -1 });
        var rev = rt.Revision;

        Assert.False(rt.TryAdmitSpawn("plant", out var g));
        Assert.Equal(GateReasons.CapPlants, g.Reason);
        Assert.Equal(rev, rt.Revision);
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
    }

    [Fact]
    public void BoardSide_overload_plant_ok()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.True(rt.TryAdmitSpawn(BoardSide.Plant, out var g));
        Assert.True(g.Ok);
    }

    [Fact]
    public void BoardSide_zombie_and_bullet_ok_under_defaults()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.True(rt.TryAdmitSpawn(BoardSide.Zombie, out var z));
        Assert.True(z.Ok);
        Assert.True(rt.TryAdmitSpawn(BoardSide.Bullet, out var b));
        Assert.True(b.Ok);
    }

    [Fact]
    public void BoardSide_invalid_enum_InMatch_rejects_cap_invalid_side()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        Assert.False(rt.TryAdmitSpawn((BoardSide)99, out var g));
        Assert.Equal(GateReasons.CapInvalidSide, g.Reason);
    }

    [Fact]
    public void ConfigureCaps_null_is_noop()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var max = rt.ToSnapshot().Caps.MaxLivingPlants;
        rt.ConfigureCaps(null);
        Assert.Equal(max, rt.ToSnapshot().Caps.MaxLivingPlants);
    }

    [Fact]
    public void ConfigureCaps_copies_caller_config()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var cfg = new CapPolicyConfig { MaxLivingPlants = 3, MaxLivingZombies = 80, MaxLivingBullets = -1 };
        rt.ConfigureCaps(cfg);
        cfg.MaxLivingPlants = 50;

        Assert.Equal(3, rt.ToSnapshot().Caps.MaxLivingPlants);
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x1" });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x2" });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0x3" });
        Assert.False(rt.TryAdmitSpawn("plant", out var g));
        Assert.Equal(GateReasons.CapPlants, g.Reason);
    }

    [Fact]
    public void ConfigureCaps_does_not_bump_revision()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        var rev = rt.Revision;
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 10 });
        Assert.Equal(rev, rt.Revision);
    }

    [Fact]
    public void Board_start_resets_caps_to_defaults()
    {
        var rt = new MatchRuntime();
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 2 });
        rt.Apply("board.start");
        Assert.Equal(50, rt.ToSnapshot().Caps.MaxLivingPlants);
    }

    [Fact]
    public void Pause_unpause_then_Admit_allows()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.NotifyPaused(true);
        rt.NotifyPaused(false);
        Assert.True(rt.TryAdmitSpawn("plant", out var g));
        Assert.True(g.Ok);
    }
}
