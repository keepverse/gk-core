using FusionRpg.Contracts;
using FusionRpg.Core.Creatures.Patron;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Effects.Plugins;

/// <summary>
/// Patron aura (spec-patron-creature.md): grants the marker at match start, gated on the
/// player having any patron designated at all. Grant-only Secondary discipline — no overlay, ever.
///
/// <para><b>Scope is PLANT-SIDE, not match-wide</b> (live defect, creature-standalone PT7,
/// 2026-09-20). The aura is for plant-side reads (spec-patron-creature.md:27), but the grant asked for
/// <c>match</c>, so a live probe read the identical <c>combat.power.fire</c> magnitude back on the
/// ZOMBIE side — the aura was buffing the enemy. It now asks for
/// <see cref="EffectOwnerKey.PlantSide"/>, the grammar's "every plant, any type id" key, with the
/// matching ownerKind so the grant-store lookup on the plant path finds it.</para>
///
/// <para>The grant's magnitude (patron-absorption, `seed-to-concrete` T6.2, 2026-09-06) now comes
/// from `fx.patron_aura`'s own compiled atom action rows, resolved fresh per push from the player's
/// live patron row — never a value frozen here. This plugin's only remaining job is the gate: without
/// it, every player (even one with no patron) would carry the grant as a standing +0 no-op for the
/// whole match, which is harmless but pointless.</para>
/// </summary>
public sealed class PatronSecondaryPlugin : IEffectGrantPlugin
{
    public string PluginId => "sec.patron.aura";

    public void OnMatchStart(EffectPluginContext ctx)
    {
        if (!PatronRuntimeState.TryGet(ctx.PlayerId, out _))
            return;

        ctx.Funnel.EnqueueModifier(new EffectGrantDto
        {
            GrantId = "patron:aura",
            EffectId = "fx.patron_aura",
            // The ownerKind must name the SAME side as the key: `GrantedDerivedAtomReader` looks a
            // plant context up as ("plant", "plant:{typeId}"), and `ForOwner` filters on both fields.
            OwnerKind = OwnerScope.Name(OwnerKind.Plant),
            OwnerKey = EffectOwnerKey.PlantSide,
            PluginId = PluginId
        });
    }

    public void OnLoadoutChanged(EffectPluginContext ctx) { }

    public void OnOwnerChanged(EffectPluginContext ctx) { }

    public void OnRemoved(EffectPluginContext ctx)
    {
        EffectPluginGrantOps.WithdrawByPluginId(ctx, PluginId);
    }
}
