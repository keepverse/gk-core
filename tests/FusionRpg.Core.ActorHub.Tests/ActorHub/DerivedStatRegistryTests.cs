using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

public class DerivedStatRegistryTests
{
    [Fact]
    public void Combat_channels_are_registered()
    {
        var reg = DerivedStatRegistry.CreateDefault();
        foreach (var channelId in DerivedStatChannels.AllCombatChannelIds)
            Assert.True(reg.IsKnown(channelId), $"Missing combat channel: {channelId}");
    }

    [Fact]
    public void Combat_channel_count_is_28_families_x_roster_plus_omni()
    {
        // 28 families × (omni + ElementRoster.Concrete) — 196 since H.1's 16 new element-typed families
        // landed (derived-stats program, 2026-08-24; was 12 families / 84 before). The literal below IS
        // meant to be here — spec-catalog-extension.md §2.4: this test already computes the formula and
        // asserts what it CURRENTLY equals, so a silent roster change is caught. Only this canary moves;
        // rewriting the formula itself would be re-deriving something already correct.
        var expected = DerivedStatChannels.CombatChannelFamilies.Count * (ElementRoster.Concrete.Count + 1);
        Assert.Equal(196, expected);
        Assert.Equal(expected, DerivedStatChannels.AllCombatChannelIds.Count);
        var reg = DerivedStatRegistry.CreateDefault();
        var combatRegistered = reg.AllRegistered.Count(d => DerivedStatChannels.AllCombatChannelIds.Contains(d.ChannelId));
        Assert.Equal(expected, combatRegistered);
    }

    [Fact]
    public void Legacy_combat_crit_chance_rejects()
    {
        var reg = DerivedStatRegistry.CreateDefault();
        Assert.Throws<UnknownDerivedChannelException>(() => reg.ValidateChannel("combat.crit.chance"));
    }

    [Fact]
    public void Unknown_combat_channel_rejects()
    {
        var reg = DerivedStatRegistry.CreateDefault();
        Assert.Throws<UnknownDerivedChannelException>(() => reg.ValidateChannel("combat.power.water"));
    }

    [Fact]
    public void Unknown_channel_rejects()
    {
        var reg = DerivedStatRegistry.CreateDefault();
        Assert.Throws<UnknownDerivedChannelException>(() => reg.ValidateChannel("not.a.real.channel"));
    }

    [Fact]
    public void Sparse_status_power_resolves()
    {
        var reg = DerivedStatRegistry.CreateDefault();
        Assert.True(reg.TryResolveChannel("status.power.wither", out var def));
        Assert.Equal(DerivedComposeKind.SumIncreased, def.Compose);
    }

    [Fact]
    public void Category_resist_cap_is_095()
    {
        var reg = DerivedStatRegistry.CreateDefault();
        reg.TryGet(DerivedStatChannels.StatusResistDot, out var def);
        Assert.Equal(0.95, def.Cap);
    }

    [Theory]
    [MemberData(nameof(AllCombatChannelIds))]
    public void Combat_channels_use_flat_sum(string channelId)
    {
        var reg = DerivedStatRegistry.CreateDefault();
        reg.TryGet(channelId, out var def);
        Assert.Equal(DerivedComposeKind.FlatSum, def.Compose);
    }

    [Theory]
    [MemberData(nameof(AllCombatChannelIds))]
    public void Combat_channels_default_to_zero_in_neutral_compose(string channelId)
    {
        var composer = new DerivedComposer();
        var snap = composer.Compose();
        Assert.Equal(0, snap.Get(channelId));
    }

    [Fact]
    public void Combat_power_flat_modifier_sums()
    {
        var composer = new DerivedComposer();
        var snap = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.CombatPowerFire, DerivedModifierOp.Flat, 3.0),
            new DerivedModifier(DerivedStatChannels.CombatPowerFire, DerivedModifierOp.Flat, 2.0)
        });
        Assert.Equal(5, snap.Get(DerivedStatChannels.CombatPowerFire));
    }

    [Fact]
    public void Composer_neutral_stub_defaults()
    {
        var composer = new DerivedComposer();
        var snap = composer.Compose();
        Assert.Equal(1.0, snap.Get(DerivedStatChannels.ProgressionPower));
        Assert.Equal(1.0, snap.Get(DerivedStatChannels.ProgressionRealm));
        Assert.Equal(0, snap.Get(DerivedStatChannels.StatusPowerDot));
        Assert.Equal(0, snap.Get(DerivedStatChannels.StatusResistOmni));
        Assert.Equal(0, snap.Get(DerivedStatChannels.CombatPowerOmni));
        Assert.Equal(0, snap.Get(DerivedStatChannels.CombatCritRateFire));
    }

    public static IEnumerable<object[]> AllCombatChannelIds() =>
        DerivedStatChannels.AllCombatChannelIds.Select(id => new object[] { id });
}

public class DerivedComposerTests
{
    [Fact]
    public void Category_resist_capped_before_snapshot()
    {
        var composer = new DerivedComposer();
        var snap = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.StatusResistDot, DerivedModifierOp.Increased, 2.0)
        });
        Assert.Equal(0.95, snap.Get(DerivedStatChannels.StatusResistDot));
    }
}
