using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

public class ActorDerivedProfilesTests
{
    static readonly ResistanceEvaluator Eval = new();

    [Theory]
    [InlineData(ActorDerivedProfiles.Neutral)]
    [InlineData(ActorDerivedProfiles.Glass)]
    public void Neutral_and_glass_are_stub_tier(string profile)
    {
        var snap = ActorDerivedProfiles.Get(profile);
        Assert.Equal(1.0, snap.TierPower);
        Assert.Equal(0, snap.Get(DerivedStatChannels.StatusResistOmni));
    }

    [Fact]
    public void Caster_vs_glass_wither_delta_is_large_positive()
    {
        var delta = ResistanceEvaluator.ComputeDelta(
            "wither",
            StatusL2bCategory.Dot,
            ActorDerivedProfiles.Get(ActorDerivedProfiles.Caster),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.Glass));
        Assert.True(delta > 100);
    }

    [Fact]
    public void Iron_dot_floors_wither()
    {
        var result = Eval.Evaluate(
            new StatusApplyRequest("wither", "Z1", "P1", 20, 5000),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.Caster),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.IronDot),
            new FixedStatusRng(0.0));
        Assert.False(result.Applied);
        Assert.Equal(StatusResistReason.PotencyFloor, result.ResistReason);
    }

    [Fact]
    public void Iron_cc_floors_butter()
    {
        var result = Eval.Evaluate(
            new StatusApplyRequest("butter", "Z1", "P1", 1, 4000),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.Caster),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.IronCc),
            new FixedStatusRng(0.0));
        Assert.False(result.Applied);
        Assert.Equal(StatusResistReason.PotencyFloor, result.ResistReason);
    }

    [Fact]
    public void Iron_contagion_floors_blight()
    {
        var result = Eval.Evaluate(
            new StatusApplyRequest("blight", "Z1", "P1", 12, 5000),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.Caster),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.IronContagion),
            new FixedStatusRng(0.0));
        Assert.False(result.Applied);
        Assert.Equal(StatusResistReason.PotencyFloor, result.ResistReason);
    }

    [Fact]
    public void Immune_poison_blocks_before_roll()
    {
        var result = Eval.Evaluate(
            new StatusApplyRequest("poison", "Z1", "P1", 5, 5000, ImmunityTags: new[] { "poison" }),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.Caster),
            ActorDerivedProfiles.Get(ActorDerivedProfiles.ImmunePoison),
            new FixedStatusRng(0.0));
        Assert.False(result.Applied);
        Assert.Equal(StatusResistReason.Immunity, result.ResistReason);
    }

    [Fact]
    public void Attacker_less_profile_has_zero_tier()
    {
        Assert.Equal(0, ActorDerivedProfiles.Get(ActorDerivedProfiles.AttackerLess).TierPower);
    }

    [Fact]
    public void Overlay_channels_replace_profile()
    {
        var snap = ActorDerivedProfiles.Resolve(
            ActorDerivedProfiles.Glass,
            new Dictionary<string, double> { [DerivedStatChannels.StatusResistOmni] = 3 });
        Assert.Equal(3, snap.Get(DerivedStatChannels.StatusResistOmni));
        Assert.Equal(1.0, snap.TierPower);
    }

    [Fact]
    public void Unknown_profile_throws()
    {
        Assert.Throws<ArgumentException>(() => ActorDerivedProfiles.Get("not-a-profile"));
    }

    [Theory]
    [InlineData(ActorDerivedProfiles.CombatFireCaster, 50)]
    [InlineData(ActorDerivedProfiles.CombatIceTank, 30)]
    public void Combat_profiles_expose_typed_channels(string profile, double expected)
    {
        var snap = ActorDerivedProfiles.Get(profile);
        if (profile == ActorDerivedProfiles.CombatFireCaster)
            Assert.Equal(expected, snap.Get(DerivedStatChannels.CombatPowerFire));
        else
            Assert.Equal(expected, snap.Get(DerivedStatChannels.CombatDefenseIce));
    }
}
