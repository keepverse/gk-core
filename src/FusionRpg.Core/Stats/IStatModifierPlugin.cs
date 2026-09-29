namespace FusionRpg.Core.Stats;

public interface IStatModifierPlugin
{
    string PluginId { get; }
    int Order { get; }
    void Contribute(StatContext ctx, IModifierBagEditor bag);
}

public sealed class ModifierPluginRegistry
{
    readonly object _gate = new();
    readonly Dictionary<string, IStatModifierPlugin> _byId = new(StringComparer.Ordinal);

    public void Register(IStatModifierPlugin plugin)
    {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        if (string.IsNullOrEmpty(plugin.PluginId)) throw new ArgumentException("PluginId required");
        lock (_gate) _byId[plugin.PluginId] = plugin;
    }

    public bool Unregister(string pluginId)
    {
        lock (_gate) return _byId.Remove(pluginId);
    }

    public IReadOnlyList<IStatModifierPlugin> Ordered()
    {
        lock (_gate)
            return _byId.Values.OrderBy(p => p.Order).ThenBy(p => p.PluginId, StringComparer.Ordinal).ToList();
    }
}
