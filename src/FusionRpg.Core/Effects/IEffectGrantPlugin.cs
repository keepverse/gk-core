using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects;

/// <summary>
/// Secondary grant plugin — enqueue via Funnel only. Never Unity / Status / Intent / Bag.Grant.
/// </summary>
public interface IEffectGrantPlugin
{
    string PluginId { get; }
    void OnMatchStart(EffectPluginContext ctx);
    void OnLoadoutChanged(EffectPluginContext ctx);
    void OnOwnerChanged(EffectPluginContext ctx);
    void OnRemoved(EffectPluginContext ctx);
}

public sealed class EffectPluginContext
{
    public EffectBag Bag { get; init; } = null!;
    public EffectFunnel Funnel { get; init; } = null!;
    public string MatchKey { get; init; } = "";
    public string? OwnerKey { get; init; }
    public long PlayerId { get; init; }
}

/// <summary>Registers Secondary plugins and fans out lifecycle hooks.</summary>
public sealed class EffectPluginHost
{
    readonly List<IEffectGrantPlugin> _plugins = new();
    readonly EffectBag _bag;
    readonly EffectFunnel _funnel;

    public EffectPluginHost(EffectBag bag)
    {
        _bag = bag ?? throw new ArgumentNullException(nameof(bag));
        _funnel = bag.Funnel ?? new EffectFunnel(bag);
    }

    public void Register(IEffectGrantPlugin plugin)
    {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        if (_plugins.Any(p => string.Equals(p.PluginId, plugin.PluginId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("duplicate plugin " + plugin.PluginId);
        _plugins.Add(plugin);
    }

    public IReadOnlyList<IEffectGrantPlugin> Plugins => _plugins;

    public void NotifyMatchStart(string matchKey, long playerId = 0)
    {
        FanOut(p => p.OnMatchStart(MakeCtx(matchKey, null, playerId)));
        _funnel.Flush();
    }

    public void NotifyLoadoutChanged(string matchKey, string? ownerKey = null, long playerId = 0)
    {
        FanOut(p => p.OnLoadoutChanged(MakeCtx(matchKey, ownerKey, playerId)));
        _funnel.Flush();
    }

    public void NotifyOwnerChanged(string matchKey, string ownerKey, long playerId = 0)
    {
        FanOut(p => p.OnOwnerChanged(MakeCtx(matchKey, ownerKey, playerId)));
        _funnel.Flush();
    }

    public void NotifyRemoved(string matchKey, string? ownerKey = null, long playerId = 0)
    {
        FanOut(p => p.OnRemoved(MakeCtx(matchKey, ownerKey, playerId)));
        _funnel.Flush();
    }

    EffectPluginContext MakeCtx(string matchKey, string? ownerKey, long playerId) => new()
    {
        Bag = _bag,
        Funnel = _funnel,
        MatchKey = matchKey,
        OwnerKey = ownerKey,
        PlayerId = playerId
    };

    void FanOut(Action<IEffectGrantPlugin> hook)
    {
        foreach (var p in _plugins)
        {
            try { hook(p); }
            catch { /* isolate: remaining plugins still run */ }
        }
    }
}
