using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects.Plugins;

/// <summary>Secondary plugin — grants butter-on-hit on match start; withdraws by pluginId on end.</summary>
public sealed class MatchButterSecondaryPlugin : IEffectGrantPlugin
{
    public string PluginId => "sec.match.butter";

    public void OnMatchStart(EffectPluginContext ctx)
    {
        ctx.Funnel.EnqueueModifier(new EffectGrantDto
        {
            GrantId = "golden-butter",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = PluginId,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
    }

    public void OnLoadoutChanged(EffectPluginContext ctx) { }

    public void OnOwnerChanged(EffectPluginContext ctx) { }

    public void OnRemoved(EffectPluginContext ctx) =>
        EffectPluginGrantOps.WithdrawByPluginId(ctx, PluginId);
}
