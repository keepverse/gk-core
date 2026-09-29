namespace FusionRpg.Core.Stats;

public interface IModifierBagEditor
{
    void Upsert(StatModifier mod);
    void Withdraw(string sourceKind, string sourceId);
    void WithdrawPlugin(string pluginId);
}

public interface IModifierBagReader
{
    IReadOnlyList<StatModifier> All { get; }
}

public sealed class ModifierBag : IModifierBagEditor, IModifierBagReader
{
    readonly Dictionary<string, StatModifier> _byKey = new(StringComparer.Ordinal);

    public IReadOnlyList<StatModifier> All => _byKey.Values.ToList();

    public void Upsert(StatModifier mod)
    {
        if (mod == null) throw new ArgumentNullException(nameof(mod));
        if (string.IsNullOrEmpty(mod.Channel)) throw new ArgumentException("Channel required", nameof(mod));
        _byKey[mod.Key] = mod;
    }

    public void Withdraw(string sourceKind, string sourceId)
    {
        var remove = _byKey.Values
            .Where(m => m.SourceKind == sourceKind && m.SourceId == sourceId)
            .Select(m => m.Key)
            .ToList();
        foreach (var k in remove) _byKey.Remove(k);
    }

    public void WithdrawPlugin(string pluginId)
    {
        var remove = _byKey.Values
            .Where(m => m.PluginId == pluginId)
            .Select(m => m.Key)
            .ToList();
        foreach (var k in remove) _byKey.Remove(k);
    }

    public void RemoveKey(string key) => _byKey.Remove(key);

    public void Clear() => _byKey.Clear();
}
