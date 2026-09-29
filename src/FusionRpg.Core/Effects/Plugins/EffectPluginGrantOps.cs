namespace FusionRpg.Core.Effects.Plugins;

/// <summary>Shared Grant/Withdraw helpers for Secondary plugins — Funnel only.</summary>
public static class EffectPluginGrantOps
{
    public static void WithdrawByPluginId(EffectPluginContext ctx, string pluginId)
    {
        ctx.Funnel.WithdrawByPluginId(pluginId, ctx.OwnerKey);
    }
}
