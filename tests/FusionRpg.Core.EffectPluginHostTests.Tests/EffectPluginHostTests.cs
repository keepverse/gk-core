using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Plugins;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectPluginHostTests
{
    [Fact]
    public void Register_rejects_duplicate_plugin_id()
    {
        var h = new FoundationHarness();
        var host = new EffectPluginHost(h.Bag);
        host.Register(new MatchButterSecondaryPlugin());
        Assert.Throws<InvalidOperationException>(() => host.Register(new MatchButterSecondaryPlugin()));
    }

    [Fact]
    public void Default_registry_registers_patron_plugin_only()
    {
        var h = new FoundationHarness();
        var host = EffectPluginHostFactory.Create(h.Bag);
        Assert.Single(host.Plugins);
        Assert.Contains(host.Plugins, p => p.PluginId == "sec.patron.aura");
    }
}
