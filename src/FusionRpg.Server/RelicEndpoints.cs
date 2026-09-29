using FusionRpg.Contracts;
using FusionRpg.Core.Match;

namespace FusionRpg.Server;

/// <summary>
/// T14 — a small, real, seeded relic catalog. No acquisition system exists yet, so every
/// player holds the full catalog (see game-gui-todo.md's honest scoping note); equipping a
/// relic to an actor goes through the existing `/api/unique/actors/{id}/equipment` routes.
///
/// <para><b>Deliberately unchanged by the 2026-09-06 relic row migration</b>
/// (`decision-d1-durable-ownership.md` §10 M1/M2). The durable store moved from
/// <c>rpg_unique_equipment</c> to module 4's <c>rpg_item_assignment</c>; this endpoint serves the
/// relic <b>definitions</b>, which never lived in that table. D1 §10 M2 requires the output shape
/// to be unchanged across the switch, so the response is byte-identical and
/// <c>web/…/layers/relics/RelicsLayer.tsx</c> needed no edit —
/// <c>RelicEndpointShapeTests</c> pins that field-by-field.</para>
/// </summary>
public static class RelicEndpoints
{
    public static void MapRelics(this WebApplication app)
    {
        app.MapGet("/api/relics", () => Results.Ok(new RelicCatalogListDto
        {
            Items = RelicCatalog.Items.ToList()
        }));
    }
}
