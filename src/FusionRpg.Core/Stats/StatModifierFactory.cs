namespace FusionRpg.Core.Stats;

public sealed class StatModifierFactory
{
    public StatModifier Flat(string pluginId, string sourceKind, string sourceId, string channel, double value, int priority = 0, string applyOwnerKey = "") =>
        Create(pluginId, sourceKind, sourceId, channel, ModifierOp.Flat, value, priority, applyOwnerKey);

    public StatModifier Increased(string pluginId, string sourceKind, string sourceId, string channel, double value, int priority = 0, string applyOwnerKey = "") =>
        Create(pluginId, sourceKind, sourceId, channel, ModifierOp.Increased, value, priority, applyOwnerKey);

    public StatModifier More(string pluginId, string sourceKind, string sourceId, string channel, double value, int priority = 0, string applyOwnerKey = "") =>
        Create(pluginId, sourceKind, sourceId, channel, ModifierOp.More, value, priority, applyOwnerKey);

    public StatModifier Override(string pluginId, string sourceKind, string sourceId, string channel, double value, int priority = 0, string applyOwnerKey = "") =>
        Create(pluginId, sourceKind, sourceId, channel, ModifierOp.Override, value, priority, applyOwnerKey);

    static StatModifier Create(string pluginId, string sourceKind, string sourceId, string channel, ModifierOp op, double value, int priority, string applyOwnerKey) =>
        new()
        {
            PluginId = pluginId,
            SourceKind = sourceKind,
            SourceId = sourceId,
            Channel = channel,
            Op = op,
            Value = value,
            Priority = priority,
            ApplyOwnerKey = StatApplyScope.Normalize(applyOwnerKey)
        };
}
