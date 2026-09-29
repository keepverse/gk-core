namespace FusionRpg.Core.Effects.Plugins;

public static class EffectPluginHostFactory
{
    public static EffectPluginHost Create(EffectBag bag)
    {
        var host = new EffectPluginHost(bag);
        foreach (var plugin in SecondaryPluginRegistry.CreateDefault())
            host.Register(plugin);
        return host;
    }
}
