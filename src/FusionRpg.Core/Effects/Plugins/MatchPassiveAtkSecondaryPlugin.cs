using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects.Plugins;

/// <summary>Secondary plugin — match-scoped passive ATK flat grant for registry variety.</summary>
public sealed class MatchPassiveAtkSecondaryPlugin : IEffectGrantPlugin
{
    public string PluginId => "sec.match.passive_atk";

    public void OnMatchStart(EffectPluginContext ctx)
    {
        ctx.Funnel.EnqueueModifier(new EffectGrantDto
        {
            GrantId = "sec-passive-atk",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Match,
            PluginId = PluginId
        });
    }

    public void OnLoadoutChanged(EffectPluginContext ctx) { }

    public void OnOwnerChanged(EffectPluginContext ctx) { }

    public void OnRemoved(EffectPluginContext ctx) =>
        EffectPluginGrantOps.WithdrawByPluginId(ctx, PluginId);
}
