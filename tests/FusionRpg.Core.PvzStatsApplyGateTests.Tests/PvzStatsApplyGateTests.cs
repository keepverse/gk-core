using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Plugins;
using Xunit;

namespace FusionRpg.Core.Tests;

public class PvzStatsApplyGateTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void ShouldWrite_any_bag_or_absolute(bool scale, bool abs, bool pvz, bool expected) =>
        Assert.Equal(expected, PvzStatsApplyGate.ShouldWrite(scale, abs, pvz));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ShouldComposeScales(bool scale, bool pvz, bool expected) =>
        Assert.Equal(expected, PvzStatsApplyGate.ShouldComposeScales(scale, pvz));

    [Fact]
    public void ShouldComposeScales_effect_session_mods_alone()
    {
        Assert.True(PvzStatsApplyGate.ShouldComposeScales(false, false, hasEffectSessionMods: true));
        Assert.False(PvzStatsApplyGate.ShouldComposeScales(false, false, hasEffectSessionMods: false));
    }

    [Fact]
    public void ShouldReapplyPvz_when_revision_advances_including_clear()
    {
        Assert.False(PvzStatsApplyGate.ShouldReapplyPvz(3, 3));
        Assert.True(PvzStatsApplyGate.ShouldReapplyPvz(3, 4));
        Assert.True(PvzStatsApplyGate.ShouldReapplyPvz(5, 0));
        Assert.True(PvzStatsApplyGate.ShouldReapplyPvz(-1, 0));
    }

    [Fact]
    public void ShouldPushOnDirty_true_for_pvz_revision_without_scale_mods()
    {
        Assert.True(PvzStatsApplyGate.ShouldPushOnDirty(
            cheatDocRevision: 1, appliedCheatRevision: 1,
            pvzRevision: 2, appliedPvzRevision: 1,
            hasPlantScale: false, hasZombieScale: false));
    }

    [Fact]
    public void ShouldPushOnDirty_false_when_synced_and_no_scales()
    {
        Assert.False(PvzStatsApplyGate.ShouldPushOnDirty(
            1, 1, 2, 2, false, false));
    }
}

public class PvzStatsPluginOrderTests
{
    [Fact]
    public void Bootstrap_places_pvz_stats_between_200_and_300()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var ordered = sys.Plugins.Ordered().ToList();
        var pvz = Assert.Single(ordered, p => p.PluginId == PvzStatsPlugin.Id);
        Assert.Equal(250, pvz.Order);
        var before = ordered.Last(p => p.Order < 250);
        var after = ordered.First(p => p.Order > 250);
        Assert.Equal(200, before.Order);
        Assert.True(after.Order >= 300);
    }
}

public class PvzStatsResolveGateTests
{
    [Fact]
    public void Resolve_plant_only_PvzStats_Flat_applies_when_compose_on()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var ctx = sys.Contexts.ForPlant(
            "e1",
            new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 },
            applyStats: true,
            pvzStatsMods: new[]
            {
                PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "ring", StatChannels.Hp, "Flat", 10, 0)
            });
        Assert.Equal(110, sys.Resolve(ctx).Hp);
    }

    [Fact]
    public void Resolve_applyStats_false_ignores_Flat_but_keeps_Override()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var ctx = sys.Contexts.ForPlant(
            "e1",
            new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 },
            applyStats: false,
            pvzStatsMods: new[]
            {
                PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "flat", StatChannels.Hp, "Flat", 10, 0),
                PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "ov", StatChannels.Atk, "Override", 42, 0)
            });
        var final = sys.Resolve(ctx);
        Assert.Equal(100, final.Hp);
        Assert.Equal(42, final.Atk);
    }

    [Fact]
    public void ParseOp_unknown_defaults_to_Flat()
    {
        Assert.Equal(ModifierOp.Flat, PvzStatsSheetComposer.ParseOp(null));
        Assert.Equal(ModifierOp.Flat, PvzStatsSheetComposer.ParseOp(""));
        Assert.Equal(ModifierOp.Flat, PvzStatsSheetComposer.ParseOp("nope"));
        Assert.Equal(ModifierOp.Increased, PvzStatsSheetComposer.ParseOp("increased"));
    }

    [Fact]
    public void Sheet_More_Increased_at_Y0_zero()
    {
        var mods = new[]
        {
            PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "a", StatChannels.Atk, "Flat", 10, 0),
            PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "b", StatChannels.Atk, "Increased", 0.5, 0),
            PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "c", StatChannels.Atk, "More", 1.0, 0)
        };
        // Y0=0: (0+10)*(1+0.5)*(1+1) = 30
        var sheet = PvzStatsSheetComposer.Build(mods);
        var atk = Assert.Single(sheet.Channels, c => c.Channel == StatChannels.Atk);
        Assert.Equal(30, atk.Final);
    }

    [Fact]
    public void TryCanonicalizeChannel_case_insensitive()
    {
        Assert.Equal(StatChannels.Hp, PvzStatsSheetComposer.TryCanonicalizeChannel("HP"));
        Assert.Equal(StatChannels.MaxHp, PvzStatsSheetComposer.TryCanonicalizeChannel("maxhp"));
        Assert.Null(PvzStatsSheetComposer.TryCanonicalizeChannel("mana"));
    }
}
