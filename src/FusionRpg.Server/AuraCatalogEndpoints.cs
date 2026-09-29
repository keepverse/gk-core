using FusionRpg.Core.Actions;
using FusionRpg.Core.Aura;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// aura-skill T18c: the web surface needs the full, authored aura list to render every equipped OR
/// gated slot (spec-aura-surface.md §2.1 — "every locked aura states its real reason"), not just the
/// per-player subset `/api/aura-runtime/{playerId}` already returns. Mostly a stateless read over the
/// already-shipped `AuraContentCatalog` (T16), no per-player scoping.
///
/// <para><b>`upkeep` is real, queried data, never fabricated.</b> `RpgStore.ListCosts(actionId)`
/// (`rpg_action_cost`) has no foreign-key requirement on a real `ActionRow` — an aura id is a legal
/// key today even though auras are never authored as `ActionRow`s (T16's own deliberate separation).
/// No aura has a real upkeep cost authored yet (confirmed: `grep -rn PerTick data/` finds zero), so
/// every aura's `upkeep` is an honestly empty array today — the moment a balance pass calls
/// `UpsertCost` for a real aura id, this same read starts returning it with no code change.</para>
/// </summary>
public static class AuraCatalogEndpoints
{
    public static void MapAuraCatalog(this WebApplication app)
    {
        app.MapGet("/api/auras", (RpgStore store) =>
            Results.Ok(new
            {
                items = AuraContentCatalog.All
                    .Select(a => new
                    {
                        auraId = a.AuraId,
                        aptitudeId = a.AptitudeId,
                        upkeep = store.ListCosts(a.AuraId)
                            .Select(c => new
                            {
                                resourceId = c.ResourceId,
                                amountMin = c.AmountSpec.Min,
                                amountMax = c.AmountSpec.Max,
                                when = c.When.ToString()
                            })
                            .ToList()
                    })
                    .ToList()
            }));
    }
}
