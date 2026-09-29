using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Power;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FusionRpg.Server;

/// <summary>
/// Player aptitude allocate surfaces: commander (Mode C), species GET, UniqueCreature GET/POST (Mode A —
/// aptitude-sheet <c>unique-allocate</c>). Commander-only scope decision in historical class-system docs is
/// superseded for UniqueActor sheets; this file owns all three HTTP surfaces.
/// </summary>
public static class AptitudeEndpoints
{
    public static void MapAptitudes(this WebApplication app)
    {
        var g = app.MapGroup("/api/aptitudes");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store, IPowerIndexProvider powerIndex) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(ProjectState(store, powerIndex, playerId));
        });

        g.MapPost("/allocate", (AllocateAptitudesRequest body, RpgStore store, IPowerIndexProvider powerIndex, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (body.Shares is null) return Results.BadRequest(new { reason = "shares.missing" });

            AptitudeAllocation allocation;
            try
            {
                allocation = body.Shares.Aggregate(AptitudeAllocation.Empty,
                    (acc, kv) => acc + AptitudeAllocation.Single(AllocationScope.Commander, kv.Key, kv.Value));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { reason = "aptitudes.unknownid", detail = ex.Message });
            }

            var theta = (long)powerIndex.ActorIndex(new StatContext { PlayerId = pid });
            var check = PointBudget.CheckScope(AllocationScope.Commander, allocation, theta, AptitudeTuningHub.Tuning);
            if (!check.WithinBudget)
                return Results.Conflict(new { reason = "aptitudes.overbudget", spent = check.Spent, budget = check.Budget });

            // EP1.9 (spec-specimen-respec-price.md) — the gate decides free-vs-priced; this route no
            // longer writes the allocation itself.
            var payer = new EmpireRef(new SaveId(pid), store.HumanEmpireOf(pid));
            var outcome = store.TryReallocate(payer, AllocationScope.Commander, ScopeKey(pid), allocation, body.CorrelationId);
            if (!outcome.Ok)
            {
                if (outcome.Reason == "souls.insufficient")
                    return Results.Conflict(new { reason = outcome.Reason, priceAmount = outcome.PriceAmount });
                return Results.BadRequest(new { reason = outcome.Reason });
            }

            _ = BroadcastBestEffort(hub, new AptitudesUpdatedDto(
                pid, "commander", null, null, Kind: LivenessInvalidationWire.CommanderAllocation));
            return Results.Ok(WithReallocation(ProjectState(store, powerIndex, pid), outcome));
        });

        g.MapGet("/unique/{instanceId}", (string instanceId, RpgStore store, IPowerIndexProvider powerIndex) =>
        {
            var actor = store.GetUniqueActor(instanceId);
            if (actor is null) return Results.NotFound();
            return Results.Ok(ProjectUniqueState(store, powerIndex, actor));
        });

        g.MapPost("/unique/allocate", (AllocateUniqueAptitudesRequest body, RpgStore store, IPowerIndexProvider powerIndex, IHubContext<RpgHub> hub) =>
        {
            if (string.IsNullOrWhiteSpace(body.InstanceId))
                return Results.BadRequest(new { reason = "instanceId.missing" });
            if (body.Shares is null) return Results.BadRequest(new { reason = "shares.missing" });

            var actor = store.GetUniqueActor(body.InstanceId);
            if (actor is null) return Results.NotFound();

            AptitudeAllocation allocation;
            try
            {
                allocation = body.Shares.Aggregate(AptitudeAllocation.Empty,
                    (acc, kv) => acc + AptitudeAllocation.Single(AllocationScope.UniqueCreature, kv.Key, kv.Value));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { reason = "aptitudes.unknownid", detail = ex.Message });
            }

            var source = PointBudget.UniqueCreatureSourceFromLevel(actor.Level);
            var check = PointBudget.CheckScope(AllocationScope.UniqueCreature, allocation, source, AptitudeTuningHub.Tuning);
            if (!check.WithinBudget)
                return Results.Conflict(new { reason = "aptitudes.overbudget", spent = check.Spent, budget = check.Budget });

            var payer = new EmpireRef(new SaveId(actor.PlayerId), store.HumanEmpireOf(actor.PlayerId));
            var outcome = store.TryReallocate(payer, AllocationScope.UniqueCreature, actor.InstanceId, allocation, body.CorrelationId);
            if (!outcome.Ok)
            {
                if (outcome.Reason == "souls.insufficient")
                    return Results.Conflict(new { reason = outcome.Reason, priceAmount = outcome.PriceAmount });
                return Results.BadRequest(new { reason = outcome.Reason });
            }

            _ = BroadcastBestEffort(hub, new AptitudesUpdatedDto(
                actor.PlayerId, "unique", actor.InstanceId, null, Kind: LivenessInvalidationWire.UniqueAllocation));
            return Results.Ok(WithReallocation(ProjectUniqueState(store, powerIndex, actor), outcome));
        });

        // EP1.9 (spec-specimen-respec-price.md) — a preview only; never persists, never spends.
        g.MapPost("/respec-quote", (RespecQuoteRequest body, RpgStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Scope))
                return Results.BadRequest(new { reason = "presets.scope.missing" });
            if (body.Shares is null) return Results.BadRequest(new { reason = "shares.missing" });

            AllocationScope scope;
            string scopeKey;
            long payerPlayerId;
            if (string.Equals(body.Scope, "commander", StringComparison.OrdinalIgnoreCase))
            {
                payerPlayerId = body.PlayerId ?? store.GetCurrentPlayerId();
                if (!store.PlayerExists(payerPlayerId)) return Results.NotFound();
                scope = AllocationScope.Commander;
                scopeKey = ScopeKey(payerPlayerId);
            }
            else if (string.Equals(body.Scope, "unique", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(body.InstanceId))
                    return Results.BadRequest(new { reason = "instanceId.missing" });
                var actor = store.GetUniqueActor(body.InstanceId);
                if (actor is null) return Results.NotFound();
                payerPlayerId = actor.PlayerId;
                scope = AllocationScope.UniqueCreature;
                scopeKey = actor.InstanceId;
            }
            else
            {
                return Results.BadRequest(new { reason = "presets.scope.unknown" });
            }

            AptitudeAllocation proposed;
            try
            {
                proposed = body.Shares.Aggregate(AptitudeAllocation.Empty,
                    (acc, kv) => acc + AptitudeAllocation.Single(scope, kv.Key, kv.Value));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { reason = "aptitudes.unknownid", detail = ex.Message });
            }

            var payer = new EmpireRef(new SaveId(payerPlayerId), store.HumanEmpireOf(payerPlayerId));
            var quote = store.QuoteReallocation(payer, scope, scopeKey, proposed, out var isRespec);
            var respecCount = store.GetAllocationRespecCount(scope, scopeKey);
            return Results.Ok(new { isRespec, soulPrice = quote.Souls.Amount, respecCount });
        });

        // species GET — writes go through SpeciesBuildEndpoints respec only.
        g.MapGet("/species/{playerId:long}/{speciesId}", (long playerId, string speciesId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (!CreatureSpeciesCatalog.IsKnown(speciesId))
                return Results.BadRequest(new { reason = "species.unknown" });
            return Results.Ok(ProjectSpeciesState(store, playerId, speciesId));
        });
    }

    /// <summary>Shared AptitudesUpdated emitter (aptitude-sheet live-bus). Sole path for commander /
    /// unique / species / preset-activate broadcasts.</summary>
    public static async Task BroadcastBestEffort(IHubContext<RpgHub> hub, AptitudesUpdatedDto dto)
    {
        // camelCase anonymous shape — FE + injector already parse playerId; scope keys additive (S10/live-bus).
        var payload = new
        {
            playerId = dto.PlayerId,
            scope = dto.Scope,
            instanceId = dto.InstanceId,
            speciesId = dto.SpeciesId,
            // ai-empire-species EP4.15 (T1/T5): which empire's progression moved, additive per the
            // transport spec's own rule. Null for every pre-R1 sender (the human empire implied).
            empire = dto.Empire,
            // lawn LW1.5: what changed, for the injector's one typed invalidation. Null on every
            // pre-LW1.5 sender, so an older injector ignores it and a newer one treats it as "no kind".
            kind = dto.Kind
        };
        try { await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("AptitudesUpdated", payload); }
        catch { /* best-effort */ }
        try { await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("AptitudesUpdated", payload); }
        catch { /* best-effort */ }
    }

    public static string ScopeKey(long playerId) => $"player:{playerId}";

    static object ProjectUniqueState(RpgStore store, IPowerIndexProvider powerIndex, UniqueActorDto actor)
    {
        // EP1.14 (spec-default-build.md) -- the sheet/injector hydrate seam: a levelled specimen the
        // player never built now reads its species' default distribution instead of Empty.
        // EP1.17 -- IsDefault/DefaultRuleId ride along so the sheet can label a default AS a
        // default ("Suggested build (<rule>)"), never re-deriving which rung won on the FE side.
        var effective = store.EffectiveUniqueAllocation(actor.InstanceId, AptitudeTuningHub.Tuning);
        var allocation = effective.Allocation;
        var source = PointBudget.UniqueCreatureSourceFromLevel(actor.Level);
        var check = PointBudget.CheckScope(AllocationScope.UniqueCreature, allocation, source, AptitudeTuningHub.Tuning);
        var leftover = check.Budget - check.Spent;
        if (leftover < 0) leftover = 0;

        // unique-theta-wire (T17): the index the specimen's own sheet Hub composes with — UniqueActorHubCompose resolves
        // it through this provider with the owner's player id. Omitted when the owner has no player row to read it from;
        // never the specimen level.
        long? theta = store.PlayerExists(actor.PlayerId)
            ? powerIndex.ActorIndex(new StatContext { PlayerId = actor.PlayerId, EntityKey = actor.InstanceId, TypeId = actor.TypeId })
            : null;

        return new
        {
            instanceId = actor.InstanceId,
            playerId = actor.PlayerId,
            specimenLevel = actor.Level,
            theta,
            budget = check.Budget,
            spent = check.Spent,
            leftover,
            withinBudget = check.WithinBudget,
            shares = AptitudeCatalog.All.ToDictionary(
                a => a.Id, a => allocation.PointsAt(AllocationScope.UniqueCreature, a.Id), StringComparer.Ordinal),
            isDefault = effective.IsDefault,
            defaultRuleId = effective.DefaultRuleId
        };
    }

    /// <summary>EP1.9 — flattens a priced <see cref="ReallocationOutcome"/>'s new fields
    /// (`priced`/`priceAmount`/`respecCount`/`soulBalance`) onto an already-projected state object,
    /// without changing <see cref="ProjectState"/>/<see cref="ProjectUniqueState"/>'s own shape (still
    /// used unchanged by the GET routes). A JSON-node merge rather than a second DTO type, since both
    /// projections return an anonymous type.</summary>
    static JsonObject WithReallocation(object projected, ReallocationOutcome outcome)
    {
        var node = JsonSerializer.SerializeToNode(projected) as JsonObject ?? new JsonObject();
        node["priced"] = outcome.Priced;
        node["priceAmount"] = outcome.PriceAmount;
        node["respecCount"] = outcome.RespecCount;
        node["soulBalance"] = outcome.Balance?.Balance;
        return node;
    }

    static object ProjectState(RpgStore store, IPowerIndexProvider powerIndex, long playerId)
    {
        var allocation = store.LoadAllocation(AllocationScope.Commander, ScopeKey(playerId));
        var theta = (long)powerIndex.ActorIndex(new StatContext { PlayerId = playerId });
        var check = PointBudget.CheckScope(AllocationScope.Commander, allocation, theta, AptitudeTuningHub.Tuning);

        var species = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var speciesId in store.ListLevelledSpeciesIds(playerId))
        {
            var effective = store.EffectiveSpeciesAllocation(playerId, speciesId, AptitudeTuningHub.Tuning);
            species[speciesId] = AptitudeCatalog.All.ToDictionary(
                a => a.Id, a => effective.PointsAt(AllocationScope.CreatureType, a.Id), StringComparer.Ordinal);
        }

        return new
        {
            theta,
            budget = check.Budget,
            spent = check.Spent,
            withinBudget = check.WithinBudget,
            shares = AptitudeCatalog.All.ToDictionary(a => a.Id, a => allocation.PointsAt(AllocationScope.Commander, a.Id), StringComparer.Ordinal),
            species,
            // ai-empire-species EP4.18 (R23): the per-empire commander pools, keyed by EmpireId, plus the
            // save's own human empire so the injector can route the human's ask to its match-scoped
            // commander cache (unchanged) and every OTHER empire's to this map. `shares` above stays the
            // human empire's pool, byte for byte, and is deliberately not duplicated here.
            humanEmpire = store.HumanEmpireOf(playerId).Value,
            commanderByEmpire = ProjectCommanderPools(store, powerIndex, playerId, store.HumanEmpireOf(playerId)),
            speciesLayers = ProjectSpeciesLayers(store, playerId)
        };
    }

    /// <summary>
    /// `ai-empire-species` EP4.18 (R23) — every NON-human empire's commander pool for this save, through
    /// the ONE empire-keyed pool read (EP4.17) at that empire's own Theta (EP4.16). Computed at read,
    /// never persisted; an empire with no pool contributes an all-zero map rather than being omitted, so a
    /// consumer never has to tell "absent" from "empty" (the same shape `shares` already has).
    /// </summary>
    static object ProjectCommanderPools(
        RpgStore store, IPowerIndexProvider powerIndex, long saveId, FusionRpg.Core.Commanders.EmpireId humanEmpire)
    {
        var byEmpire = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var row in store.EmpiresOf(saveId))
        {
            if (row.Empire == humanEmpire) continue;   // `shares` IS the human empire's pool
            var theta = (long)powerIndex.ActorIndexFor(new SaveId(saveId), row.Empire);
            var pool = store.CommanderPoolOf(
                new EmpireRef(new SaveId(saveId), row.Empire), theta, AptitudeTuningHub.Tuning).Allocation;
            byEmpire[row.Empire.Value] = AptitudeCatalog.All.ToDictionary(
                a => a.Id, a => pool.PointsAt(AllocationScope.Commander, a.Id), StringComparer.Ordinal);
        }
        return byEmpire;
    }

    /// <summary>
    /// species-progression step 6.2, Transport (SP6.3) — `speciesLayers.saveId` (path value = SaveId,
    /// `save-identity`'s own rule), `.base` (1a, global per species), `.mod` (1b, per REAL EmpireId,
    /// never a literal list), `.empire` (2b — stays `{}` until SP6.10's cutover, this task's own scope
    /// boundary). Extends the ONE existing fetch; no second route, no second key.
    /// </summary>
    static object ProjectSpeciesLayers(RpgStore store, long saveId)
    {
        var (baseRows, mod) = store.SpeciesLayerTransport(saveId);
        return new
        {
            saveId,
            @base = baseRows.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(ProjectedLayerRowJson.ToWire).ToList(),
                StringComparer.Ordinal),
            mod = mod.ToDictionary(
                empireKv => empireKv.Key,
                empireKv => empireKv.Value.ToDictionary(
                    speciesKv => speciesKv.Key,
                    speciesKv => speciesKv.Value.Select(ProjectedLayerRowJson.ToWire).ToList(),
                    StringComparer.Ordinal),
                StringComparer.Ordinal),
            empire = new Dictionary<string, object>(StringComparer.Ordinal) // 2b — populated at SP6.10
        };
    }

    static object ProjectSpeciesState(RpgStore store, long playerId, string speciesId)
    {
        var creatureTypeId = CreatureSpeciesCatalog.Get(speciesId).CreatureTypeId;
        var level = store.GetRpgActor(playerId, RpgActorKinds.Species, creatureTypeId)?.Level ?? 1;
        var allocation = store.EffectiveSpeciesAllocation(playerId, speciesId, AptitudeTuningHub.Tuning);
        var source = PointBudget.CreatureTypeSourceFromLevel(level);
        var check = PointBudget.CheckScope(AllocationScope.CreatureType, allocation, source, AptitudeTuningHub.Tuning);

        var baseline = store.SpeciesBaselineAllocation(playerId, speciesId, AptitudeTuningHub.Tuning);
        return new
        {
            speciesId,
            level,
            budget = check.Budget,
            spent = check.Spent,
            withinBudget = check.WithinBudget,
            hasOverride = store.HasSpeciesOverride(playerId, speciesId),
            shares = AptitudeCatalog.All.ToDictionary(
                a => a.Id, a => allocation.PointsAt(AllocationScope.CreatureType, a.Id), StringComparer.Ordinal),
            baseline = AptitudeCatalog.All.ToDictionary(
                a => a.Id, a => baseline.PointsAt(AllocationScope.CreatureType, a.Id), StringComparer.Ordinal)
        };
    }

    public sealed class AllocateAptitudesRequest
    {
        public long? PlayerId { get; set; }
        public Dictionary<string, long>? Shares { get; set; }
        /// <summary>EP1.9 — required only when the reallocation is a respec (takes points back);
        /// a first allocation or a top-up needs none. <see cref="RpgStore.TryReallocate"/> refuses
        /// `correlation.missing` if a respec omits it.</summary>
        public string? CorrelationId { get; set; }
    }

    public sealed class AllocateUniqueAptitudesRequest
    {
        public string? InstanceId { get; set; }
        public Dictionary<string, long>? Shares { get; set; }
        public string? CorrelationId { get; set; }
    }

    public sealed class RespecQuoteRequest
    {
        public string? Scope { get; set; }
        public long? PlayerId { get; set; }
        public string? InstanceId { get; set; }
        public Dictionary<string, long>? Shares { get; set; }
    }

    /// <param name="Empire">`ai-empire-species` EP4.15 (triggers T1/T5) — the empire whose progression
    /// moved, when it is not the save's human empire. Additive (the transport spec's own rule): every
    /// existing sender passes nothing and the field reads as "the human empire", exactly the meaning the
    /// payload had before it existed. A Zomboss species level-up is what first populates it.</param>
    public sealed record AptitudesUpdatedDto(
        long PlayerId,
        string Scope,
        string? InstanceId,
        string? SpeciesId,
        string? Empire = null,
        // lawn LW1.5 (spec-actor-liveness-refresh.md): the additive liveness KIND this notice carries,
        // one of LivenessInvalidationWire's names, or null for every pre-LW1.5 sender (the FE's own
        // display refresh, which is not a liveness invalidation). ADDITIVE by construction: the
        // payload field is new, the message name and the two groups are unchanged, so this is not a
        // second Player-kind channel (hard edge E2) — it is the same one, naming what changed.
        string? Kind = null);
}
