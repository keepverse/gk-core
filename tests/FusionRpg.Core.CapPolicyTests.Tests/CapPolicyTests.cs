using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests;

public class CapPolicyTests
{
    [Fact]
    public void Defaults_match_match_runtime_doc()
    {
        var d = CapPolicyConfig.Defaults();
        Assert.Equal(50, d.MaxLivingPlants);
        Assert.Equal(80, d.MaxLivingZombies);
        Assert.Equal(-1, d.MaxLivingBullets);
    }

    [Fact]
    public void Plant_at_max_rejects_cap_plants()
    {
        var cfg = new CapPolicyConfig { MaxLivingPlants = 2 };
        var under = CapPolicy.TryAdmit("plant", new LivingCounts(1, 0), cfg);
        Assert.True(under.Ok);
        var at = CapPolicy.TryAdmit("plant", new LivingCounts(2, 0), cfg);
        Assert.False(at.Ok);
        Assert.Equal(GateReasons.CapPlants, at.Reason);
        var over = CapPolicy.TryAdmit("Plant", new LivingCounts(5, 0), cfg);
        Assert.False(over.Ok);
        Assert.Equal(GateReasons.CapPlants, over.Reason);
    }

    [Fact]
    public void Zombie_at_max_rejects_cap_zombies()
    {
        var cfg = new CapPolicyConfig { MaxLivingZombies = 3 };
        Assert.True(CapPolicy.TryAdmit("zombie", new LivingCounts(0, 2), cfg).Ok);
        var at = CapPolicy.TryAdmit("zombie", new LivingCounts(0, 3), cfg);
        Assert.False(at.Ok);
        Assert.Equal(GateReasons.CapZombies, at.Reason);
    }

    [Fact]
    public void Bullet_unlimited_default_always_ok()
    {
        var cfg = CapPolicyConfig.Defaults();
        Assert.True(CapPolicy.TryAdmit("bullet", new LivingCounts(0, 0, 9999), cfg).Ok);
    }

    [Fact]
    public void Bullet_with_max_rejects_at_cap()
    {
        var cfg = new CapPolicyConfig { MaxLivingBullets = 1 };
        Assert.True(CapPolicy.TryAdmit("bullet", new LivingCounts(0, 0, 0), cfg).Ok);
        var at = CapPolicy.TryAdmit("bullet", new LivingCounts(0, 0, 1), cfg);
        Assert.False(at.Ok);
        Assert.Equal(GateReasons.CapBullets, at.Reason);
    }

    [Fact]
    public void Invalid_side_rejects()
    {
        var g = CapPolicy.TryAdmit("grid", new LivingCounts(0, 0), CapPolicyConfig.Defaults());
        Assert.False(g.Ok);
        Assert.Equal(GateReasons.CapInvalidSide, g.Reason);
        Assert.False(CapPolicy.TryAdmit(null, new LivingCounts(0, 0)).Ok);
        Assert.False(CapPolicy.TryAdmit("", new LivingCounts(0, 0)).Ok);
    }

    [Fact]
    public void Max_negative_one_skips_count()
    {
        var cfg = new CapPolicyConfig
        {
            MaxLivingPlants = -1,
            MaxLivingZombies = -1,
            MaxLivingBullets = -1
        };
        Assert.True(CapPolicy.TryAdmit("plant", new LivingCounts(10_000, 0), cfg).Ok);
        Assert.True(CapPolicy.TryAdmit("zombie", new LivingCounts(0, 10_000), cfg).Ok);
        Assert.True(CapPolicy.TryAdmit("bullet", new LivingCounts(0, 0, 10_000), cfg).Ok);
    }

    [Fact]
    public void Max_zero_rejects_at_living_zero()
    {
        var cfg = new CapPolicyConfig { MaxLivingPlants = 0 };
        var g = CapPolicy.TryAdmit("plant", new LivingCounts(0, 0), cfg);
        Assert.False(g.Ok);
        Assert.Equal(GateReasons.CapPlants, g.Reason);
    }

    [Fact]
    public void Living_just_under_max_ok_at_max_rejects()
    {
        var cfg = new CapPolicyConfig { MaxLivingZombies = 5 };
        Assert.True(CapPolicy.TryAdmit("zombie", new LivingCounts(0, 4), cfg).Ok);
        var at = CapPolicy.TryAdmit("zombie", new LivingCounts(0, 5), cfg);
        Assert.False(at.Ok);
        Assert.Equal(GateReasons.CapZombies, at.Reason);
    }

    [Fact]
    public void Side_whitespace_and_case_classified()
    {
        var cfg = new CapPolicyConfig { MaxLivingPlants = 1, MaxLivingZombies = 1 };
        Assert.True(CapPolicy.TryAdmit(" plant ", new LivingCounts(0, 0), cfg).Ok);
        Assert.False(CapPolicy.TryAdmit(" plant ", new LivingCounts(1, 0), cfg).Ok);
        Assert.True(CapPolicy.TryAdmit("ZOMBIE", new LivingCounts(0, 0), cfg).Ok);
        Assert.False(CapPolicy.TryAdmit("ZOMBIE", new LivingCounts(0, 1), cfg).Ok);
    }

    [Fact]
    public void Null_config_uses_defaults()
    {
        var atDefaultMax = CapPolicy.TryAdmit("plant", new LivingCounts(50, 0), config: null);
        Assert.False(atDefaultMax.Ok);
        Assert.Equal(GateReasons.CapPlants, atDefaultMax.Reason);
        Assert.True(CapPolicy.TryAdmit("plant", new LivingCounts(49, 0), config: null).Ok);
    }

    [Fact]
    public void GateResult_allowed_reason_empty_reject_null_coalesces()
    {
        Assert.Equal("", GateResult.Allowed().Reason);
        Assert.True(GateResult.Allowed().Ok);
        Assert.Equal("", GateResult.Reject(null!).Reason);
        Assert.False(GateResult.Reject(null!).Ok);
    }
}
