using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// species-build-todo.md T4.3 — spec-species-respec.md's own ⛔: "spends are never a generic endpoint,
/// each with its own reason." The single player-facing surface over
/// <see cref="RpgStore.TryRespecSpecies"/> (T4.2) — first override and revert-to-baseline are free,
/// every other change is priced and escalates with that species' own churn count (decision 15). Same
/// budget gate the pre-existing <c>/api/aptitudes/species/allocate</c> route already enforces
/// (`PointBudget.CheckScope`) — a respec still cannot buy more points than the level allows, pricing
/// is an ADDITIONAL friction, never a replacement for the anti-cheat budget cap.
///
/// <para>The sibling bypass this module's own T4.3 evidence once named — <c>AptitudeEndpoints.cs</c>'s
/// <c>POST /api/aptitudes/species/allocate</c> (module 5, `creature-type-allocation`), which wrote a
/// CreatureType override directly via <c>store.SaveAllocation</c> with zero pricing awareness — was
/// RETIRED (owner decision, 2026-09-05: "retire it now"), not just documented. This endpoint is now the
/// only write path for a species aptitude override; that route's GET twin still serves reads.</para>
/// </summary>
public static class SpeciesBuildEndpoints
{
    public static void MapSpeciesBuild(this WebApplication app)
    {
        var g = app.MapGroup("/api/species-build");

        g.MapPost("/respec", (RespecSpeciesRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.SpeciesId) || !CreatureSpeciesCatalog.IsKnown(body.SpeciesId))
                return Results.BadRequest(new { reason = "species.unknown" });
            if (body.Shares is null) return Results.BadRequest(new { reason = "shares.missing" });
            if (string.IsNullOrWhiteSpace(body.CorrelationId))
                return Results.BadRequest(new { reason = "correlation.missing" });

            AptitudeAllocation newOverride;
            try
            {
                newOverride = body.Shares.Aggregate(AptitudeAllocation.Empty,
                    (acc, kv) => acc + AptitudeAllocation.Single(AllocationScope.CreatureType, kv.Key, kv.Value));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { reason = "aptitudes.unknownid", detail = ex.Message });
            }

            var creatureTypeId = CreatureSpeciesCatalog.Get(body.SpeciesId).CreatureTypeId;
            var level = store.GetRpgActor(pid, RpgActorKinds.Species, creatureTypeId)?.Level ?? 1;
            var source = PointBudget.CreatureTypeSourceFromLevel(level);
            var check = PointBudget.CheckScope(AllocationScope.CreatureType, newOverride, source, AptitudeTuningHub.Tuning);
            if (!check.WithinBudget)
                return Results.Conflict(new { reason = "aptitudes.overbudget", spent = check.Spent, budget = check.Budget });

            // respec-free-counter EP4.9/EP4.10 — the player's own payment choice arrives as text and is
            // parsed HERE, at the edge, so an unknown spelling is a 400 rather than a default the store
            // would have to guess. Empty means "the client named no choice", which the store answers with
            // respec.payment.choice-required whenever a free respec is available.
            RespecPayment? payWith = null;
            if (!string.IsNullOrWhiteSpace(body.PayWith))
            {
                if (!RespecPayments.TryParse(body.PayWith, out var parsed))
                    return Results.BadRequest(new { reason = "respec.payment.unknown", payWith = body.PayWith });
                payWith = parsed;
            }

            var outcome = store.TryRespecSpecies(pid, body.SpeciesId, newOverride, body.CorrelationId, payWith: payWith);
            if (!outcome.Ok)
            {
                // The choice is the player's, so a client that named none while holding a free respec gets
                // the two numbers it needs to ask: the soul price it would pay, and the stock it could spend.
                if (outcome.Reason == "respec.payment.choice-required")
                    return Results.Conflict(new
                    {
                        reason = outcome.Reason,
                        soulPrice = outcome.PriceAmount,
                        freeRespecStock = outcome.FreeStock,
                    });
                return Results.Conflict(new
                {
                    reason = outcome.Reason,
                    priceAmount = outcome.PriceAmount,
                    freeRespecStock = outcome.FreeStock,
                });
            }

            _ = BroadcastBestEffort(hub, pid, body.SpeciesId!);
            return Results.Ok(new
            {
                speciesId = body.SpeciesId,
                level,
                priced = outcome.Priced,
                priceAmount = outcome.PriceAmount,
                respecCount = outcome.RespecCount,
                soulBalance = outcome.Balance.Balance,
                replay = outcome.Reason == "replay",
                // What was ACTUALLY paid, and what the stock is now -- read from the outcome rather than
                // echoed from the request, so a replay reports its original payment and a refusal pays
                // nothing.
                paidWith = outcome.PaidWith,
                freeRespecStock = outcome.FreeStock,
                shares = AptitudeCatalog.All.ToDictionary(
                    a => a.Id, a => newOverride.PointsAt(AllocationScope.CreatureType, a.Id), StringComparer.Ordinal)
            });
        });

        // Pricing preview -- lets a client show the cost BEFORE the player commits to spending it,
        // without mutating anything (GetSpeciesRespecCount is read-only, same decay-on-read the spend
        // path itself uses).
        g.MapGet("/respec-price/{playerId:long}/{speciesId}", (long playerId, string speciesId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (!CreatureSpeciesCatalog.IsKnown(speciesId))
                return Results.BadRequest(new { reason = "species.unknown" });

            var count = store.GetSpeciesRespecCount(playerId, speciesId);
            // respec-free-counter EP4.10: ONE quote for the preview, sharing `RespecPolicy.Quote` with the
            // spend path, so the price the client shows is the price the store would charge and the free
            // stock it shows is the stock it would spend. `everRespecced` stays: it distinguishes
            // "never touched" (free) from "decayed back to zero" (priced) in a way respecCount cannot.
            var quote = store.QuoteSpeciesRespec(playerId, speciesId);
            return Results.Ok(new
            {
                speciesId,
                respecCount = count,
                priceResource = quote.Souls.Resource.ToString(),
                priceAmount = quote.Souls.Amount,
                freeRespecStock = quote.FreeStock,
                freeAvailable = quote.FreeAvailable,
                everRespecced = store.HasEverRespecced(playerId, speciesId)
            });
        });
    }

    static Task BroadcastBestEffort(IHubContext<RpgHub> hub, long playerId, string speciesId) =>
        AptitudeEndpoints.BroadcastBestEffort(
            hub, new AptitudeEndpoints.AptitudesUpdatedDto(playerId, "species", null, speciesId));

    public sealed class RespecSpeciesRequest
    {
        public long? PlayerId { get; set; }

        /// <summary>`respec-free-counter` EP4.10 — `"souls"` or `"freeRespec"`, absent when the client
        /// names no choice. Parsed at the edge by <see cref="RespecPayments.TryParse"/>.</summary>
        public string? PayWith { get; set; }
        public string? SpeciesId { get; set; }
        public Dictionary<string, long>? Shares { get; set; }
        public string? CorrelationId { get; set; }
    }
}
