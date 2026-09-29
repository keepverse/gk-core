using FusionRpg.Contracts;
using FusionRpg.Data;

namespace FusionRpg.Server;

public static class UniqueActorEndpoints
{
    public static void MapUniqueActors(this WebApplication app)
    {
        var g = app.MapGroup("/api/unique");

        // save-identity SE4.31 — `?empire=<empireId>` names which empire's roster to read. Absent, it is
        // the save's human empire, so every current caller is unchanged. `rpg_unique_actors` is Tier A
        // (`(save_id, empire_id)`), so any empire OF THIS SAVE is legal here — which is why a Zomboss
        // specimen is visible under `?empire=zomboss` while staying absent from the human's roster. An
        // id that is not this save's empire is a 404, never another save's rows.
        g.MapGet("/actors", (RpgStore store, long? playerId, string? empire) =>
        {
            var pid = playerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (!EmpireScopeRequests.TryResolveAny(store, pid, empire, out var owner, out var refusal))
                return refusal!;
            return Results.Ok(store.ListUniqueActors(owner));
        });

        g.MapGet("/actors/{instanceId}", (string instanceId, UniqueActorService ua) =>
        {
            var row = ua.Get(instanceId);
            return row is null ? Results.NotFound() : Results.Ok(row);
        });

        g.MapPost("/actors", (CreateUniqueActorRequest body, RpgStore store, UniqueActorService ua) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            try
            {
                return Results.Ok(ua.Create(pid, body.Side, body.TypeId));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = "typeId" });
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound();
            }
        });

        g.MapPost("/actors/{instanceId}/deploy", async (
            string instanceId,
            DeployUniqueActorRequest? body,
            UniqueActorService ua) =>
        {
            body ??= new DeployUniqueActorRequest();
            var result = await ua.DeployAsync(
                instanceId,
                body.CorrelationId,
                body.Col,
                body.Row,
                body.MatchKey,
                body.LoadoutJson);
            if (!result.Ok && result.Reason == "not_found")
                return Results.NotFound(result);
            if (!result.Ok)
                return Results.Conflict(result);
            return Results.Ok(result);
        });

        g.MapPost("/actors/{instanceId}/fail-deploy", (string instanceId, UniqueActorService ua) =>
        {
            var (ok, reason, actor) = ua.FailDeploy(instanceId);
            if (reason == "not_found") return Results.NotFound();
            if (!ok) return Results.Conflict(new { ok, reason, actor });
            return Results.Ok(actor);
        });

        g.MapPost("/actors/{instanceId}/retire", (string instanceId, UniqueActorService ua) =>
        {
            var (ok, reason, actor) = ua.Retire(instanceId);
            if (reason == "not_found") return Results.NotFound();
            if (!ok) return Results.Conflict(new { ok, reason, actor });
            return Results.Ok(actor);
        });

        g.MapGet("/actors/{instanceId}/equipment", (string instanceId, UniqueActorService ua) =>
        {
            var eq = ua.GetEquipment(instanceId);
            return eq is null ? Results.NotFound() : Results.Ok(eq);
        });

        g.MapPut("/actors/{instanceId}/equipment/{slot}", (
            string instanceId,
            string slot,
            PutUniqueEquipmentRequest? body,
            UniqueActorService ua) =>
        {
            body ??= new PutUniqueEquipmentRequest();
            var (ok, reason, eq) = ua.PutEquipment(instanceId, slot, body.ItemId);
            if (reason == "not_found") return Results.NotFound(new { ok, reason });
            if (!ok && IsValidationReason(reason))
                return Results.BadRequest(new { ok, reason, equipment = eq });
            if (!ok) return Results.Conflict(new { ok, reason, equipment = eq });
            return Results.Ok(eq);
        });

        g.MapDelete("/actors/{instanceId}/equipment/{slot}", (
            string instanceId,
            string slot,
            UniqueActorService ua) =>
        {
            var (ok, reason, eq) = ua.ClearEquipment(instanceId, slot);
            if (reason == "not_found") return Results.NotFound(new { ok, reason });
            if (!ok && IsValidationReason(reason))
                return Results.BadRequest(new { ok, reason, equipment = eq });
            if (!ok) return Results.Conflict(new { ok, reason, equipment = eq });
            return Results.Ok(eq);
        });

        g.MapPost("/actors/{instanceId}/xp", (
            string instanceId,
            AwardUniqueActorXpRequest? body,
            UniqueActorService ua,
            Microsoft.AspNetCore.SignalR.IHubContext<RpgHub> hub) =>
        {
            body ??= new AwardUniqueActorXpRequest();
            var (ok, reason, actor, levelsGained) = ua.AwardXp(instanceId, body.Delta, body.Reason);
            if (reason == "not_found") return Results.NotFound(new { ok, reason });
            if (!ok && IsValidationReason(reason))
                return Results.BadRequest(new { ok, reason, actor });
            if (!ok) return Results.Conflict(new { ok, reason, actor });
            // EP1.15 (spec-default-build.md, cache trigger T3): the debug award route is the RPG
            // Server Debug fixture named as a T3 seam -- it broadcasts exactly like the other two.
            if (levelsGained > 0 && actor is not null)
                _ = AptitudeEndpoints.BroadcastBestEffort(hub,
                    new AptitudeEndpoints.AptitudesUpdatedDto(actor.PlayerId, "unique", instanceId, null));
            return Results.Ok(actor);
        });
    }

    /// <summary>The reasons that mean "the request itself was malformed" and answer 400. Everything
    /// else is a well-formed request the rules say no to, and answers 409 — <c>phase.not_roster</c>
    /// and, since 2026-09-06, <c>slot.claimed_by_item</c>, which is deliberately absent from this
    /// list: a role held by a real item is a conflict, exactly as the item route's mirror refusal
    /// (<c>equip.role-held-by-relic</c>) is a conflict there.</summary>
    static bool IsValidationReason(string reason) =>
        reason is "bad_delta" or "bad_args" or "bad_slot" or "unknown_item" or "slot_mismatch";
}
