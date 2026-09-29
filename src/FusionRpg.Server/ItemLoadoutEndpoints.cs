using FusionRpg.Core.Items;
using FusionRpg.Data;
using FusionRpg.Server.Gates;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

/// <summary>
/// build-preset BP1.8 (spec-item-loadout-apply.md, contract owned by
/// <c>docs/architecture/item/spec-armoury.md:220</c> — "the library; apply lands with module 4").
/// The five routes over the item loadout library: list, save, delete, preview, apply. Preview and
/// apply are thin callers of <see cref="ItemLoadoutApplyService"/> — no gate logic and no
/// persistence decision lives in this file.
/// </summary>
public static class ItemLoadoutEndpoints
{
    public static void MapItemLoadouts(this WebApplication app)
    {
        var g = app.MapGroup("/api/items/loadouts");

        g.MapGet("/", (string playerId, RpgStore store) =>
        {
            if (string.IsNullOrWhiteSpace(playerId))
                return Results.BadRequest(new { reason = "playerId.missing" });

            var loadouts = store.ListLoadouts(playerId)
                .Select(l => RenderLoadout(store, l, playerId))
                .ToList();
            return Results.Ok(new { loadouts });
        });

        g.MapPut("/{loadoutId}", (string loadoutId, SaveItemLoadoutRequest body, RpgStore store) =>
        {
            if (body.PlayerId is not { Length: > 0 } playerId)
                return Results.BadRequest(new { reason = "playerId.missing" });
            if (body.Entries is null)
                return Results.BadRequest(new { reason = "entries.missing" });

            var loadout = new RpgItemLoadoutRow(
                loadoutId, playerId, body.Name ?? "", body.Frame, ServerClock.UtcNowDateTime.ToString("o"), 0);
            var entries = body.Entries
                .Select(e => new RpgItemLoadoutEntryRow(loadoutId, e.Role ?? "", e.RefKind ?? "", e.RefId ?? ""))
                .ToList();
            store.SaveLoadout(loadout, entries);

            var saved = store.ListLoadouts(playerId).FirstOrDefault(l => l.LoadoutId == loadoutId);
            return saved is null
                ? Results.Ok(new { loadoutId, entries = store.GetLoadoutEntries(loadoutId) })
                : Results.Ok(RenderLoadout(store, saved, playerId));
        });

        g.MapDelete("/{loadoutId}", (string loadoutId, string playerId, RpgStore store) =>
        {
            if (string.IsNullOrWhiteSpace(playerId))
                return Results.BadRequest(new { reason = "playerId.missing" });
            return store.DeleteLoadout(playerId, loadoutId) ? Results.Ok() : Results.NotFound();
        });

        g.MapPost("/{loadoutId}/preview", (string loadoutId, LoadoutPlanRequest body, ItemLoadoutApplyService apply, RpgStore store) =>
        {
            if (body.TargetId is not { Length: > 0 } targetId)
                return Results.BadRequest(new { reason = "targetId.missing" });
            var playerId = body.PlayerId ?? store.GetCurrentPlayerId();

            var plan = apply.Preview(playerId, loadoutId, targetId, body.Force);
            return Results.Ok(RenderPlan(plan));
        });

        g.MapPost("/{loadoutId}/apply", async (string loadoutId, LoadoutPlanRequest body, ItemLoadoutApplyService apply, RpgStore store) =>
        {
            if (body.TargetId is not { Length: > 0 } targetId)
                return Results.BadRequest(new { reason = "targetId.missing" });
            var playerId = body.PlayerId ?? store.GetCurrentPlayerId();

            var outcome = await apply.ApplyAsync(playerId, loadoutId, targetId, body.Force);
            var rendered = new { plan = RenderPlan(outcome.Plan), results = outcome.Results };
            return outcome.Ok
                ? Results.Ok(rendered)
                : Results.Json(rendered, statusCode: StatusCodes.Status409Conflict);
        });
    }

    static object RenderLoadout(RpgStore store, RpgItemLoadoutRow loadout, string playerId) => new
    {
        loadoutId = loadout.LoadoutId,
        playerId = loadout.PlayerId,
        name = loadout.Name,
        frame = loadout.Frame,
        createdUtc = loadout.CreatedUtc,
        revision = loadout.Revision,
        entries = store.GetLoadoutEntriesValidated(loadout.LoadoutId, playerId),
    };

    static object RenderPlan(ItemLoadoutPlan plan) => new
    {
        entries = plan.Loadout.Entries,
        conflicts = plan.Loadout.Conflicts,
        stripped = plan.Loadout.Stripped,
        stockRefusals = plan.StockRefusals,
        refused = plan.Refused,
    };

    public sealed class SaveItemLoadoutRequest
    {
        public string? PlayerId { get; set; }
        public string? Name { get; set; }
        public string? Frame { get; set; }
        public List<SaveItemLoadoutEntry>? Entries { get; set; }
    }

    public sealed class SaveItemLoadoutEntry
    {
        public string? Role { get; set; }
        public string? RefKind { get; set; }
        public string? RefId { get; set; }
    }

    public sealed class LoadoutPlanRequest
    {
        public long? PlayerId { get; set; }
        public string? TargetId { get; set; }
        public bool Force { get; set; }
    }
}
