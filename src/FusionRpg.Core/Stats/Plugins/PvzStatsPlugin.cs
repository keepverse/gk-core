namespace FusionRpg.Core.Stats.Plugins;

/// <summary>
/// Emits all enabled PvzStats rows from context into the resolve bag.
/// Higher RPG features upsert into PvzStats; this plugin only reads context.
/// </summary>
public sealed class PvzStatsPlugin : IStatModifierPlugin
{
    public const string Id = "pvz.stats";
    public string PluginId => Id;
    public int Order => 250;

    public void Contribute(StatContext ctx, IModifierBagEditor bag)
    {
        bag.WithdrawPlugin(Id);
        var mods = ctx.PvzStatsMods;
        if (mods == null || mods.Count == 0) return;
        foreach (var m in mods)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.Channel)) continue;
            bag.Upsert(new StatModifier
            {
                PluginId = Id,
                SourceKind = m.SourceKind,
                SourceId = m.SourceId,
                Channel = m.Channel,
                Op = m.Op,
                Value = m.Value,
                Priority = m.Priority
            });
        }
    }
}
