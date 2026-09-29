using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Plugins;
using Xunit;

namespace FusionRpg.Core.Tests;

public class PvzStatsSheetComposerTests
{
    [Fact]
    public void Sheet_flat_plus10_minus5_equals_5_with_two_contributions()
    {
        var mods = new[]
        {
            PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "demo-ring", StatChannels.Hp, "Flat", 10, 0),
            PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "demo-curse", StatChannels.Hp, "Flat", -5, 0)
        };
        var sheet = PvzStatsSheetComposer.Build(mods);
        var hp = Assert.Single(sheet.Channels, c => c.Channel == StatChannels.Hp);
        Assert.Equal(5, hp.Final);
        Assert.Equal(2, hp.SourceCount);
        Assert.Equal(2, hp.Contributions.Count);
    }

    [Fact]
    public void PvzStatsPlugin_emits_context_mods()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        Assert.Contains(sys.Plugins.Ordered(), p => p.PluginId == PvzStatsPlugin.Id);

        var ctx = sys.Contexts.ForPlant(
            "e1",
            new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 },
            applyStats: true,
            pvzStatsMods: new[]
            {
                PvzStatsSheetComposer.ToStatModifier("rpg.item", "item", "demo-ring", StatChannels.Hp, "Flat", 10, 0)
            });
        var final = sys.Resolve(ctx);
        Assert.Equal(110, final.Hp);
        Assert.Contains(final.Contributions, m => m.SourceId == "demo-ring" && m.PluginId == PvzStatsPlugin.Id);
    }
}
