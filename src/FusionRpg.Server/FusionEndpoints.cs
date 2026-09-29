using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// Fusion lab (spec-creature-fusion.md): preview is a pure read; execute is the one-transaction
/// store call with a server-minted seed; the recipe browser projects silhouettes — an
/// undiscovered recipe's id and output species never reach the wire (the output IS the
/// discovery), only its rarity band and, once both ingredient species are in the codex, its
/// input hint.
/// </summary>
public static class FusionEndpoints
{
    public static void MapFusion(this WebApplication app)
    {
        var g = app.MapGroup("/api/fusion");

        g.MapPost("/preview", (FusionHttpRequest body, RpgStore store) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            return Results.Ok(BuildPreview(store, pid, body));
        });

        g.MapPost("/execute", async (FusionHttpRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.CorrelationId))
                return Results.BadRequest(new { reason = "correlation.missing" });
            if (body.CorrelationId.Trim().Length > 64)
                return Results.BadRequest(new { reason = "correlation.toolong" });

            var seed = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
            var (ok, reason, outcome) = store.ExecuteFusion(pid, body.CorrelationId!, ToRequest(body), seed);
            if (!ok)
            {
                return reason is "souls.insufficient" or "materials.insufficient"
                    ? Results.Conflict(new { reason })
                    : Results.BadRequest(new { reason });
            }

            if (!outcome!.Replayed)
            {
                try
                {
                    await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("CreaturesUpdated", new { playerId = pid });
                    await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("SoulsUpdated", new { playerId = pid });
                    // creature-lawn-deploy T2.1: fusion changes roster membership (sacrifices consumed, a
                    // new specimen created) — see CreatureEndpoints.cs's own matching comment.
                    await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("CreaturesUpdated", new { playerId = pid });
                    // species-progression `species-layer-delivery` step 6.2, trigger 5 (SP6.5): a
                    // real (non-replayed) fusion appends a 1b ledger row (species-mod-ledger, module
                    // 4) — the SAME shared AptitudesUpdated emitter the level-up trigger (EventIngest)
                    // uses turns this into a full injector re-fetch, so actors already spawned pick up
                    // the new 1b row without a reconnect. Gated on !Replayed for the SAME reason the
                    // three broadcasts above are: a replay appends no new ledger row either
                    // (AppendSpeciesModUnlocked is INSERT OR IGNORE on the correlation id), so there is
                    // nothing new for a live entity to pick up.
                    await AptitudeEndpoints.BroadcastBestEffort(hub, new AptitudeEndpoints.AptitudesUpdatedDto(
                        pid, "species", null, null));
                }
                catch
                {
                    // best-effort; the fusion is durable and replay recovers the outcome
                }
            }

            return Results.Ok(new
            {
                replayed = outcome!.Replayed,
                mode = outcome.Mode,
                @base = outcome.Base,
                minted = outcome.Minted,
                recipeId = outcome.RecipeId,
                newlyDiscovered = outcome.NewlyDiscovered,
                discoverySouls = outcome.DiscoverySouls,
                balance = outcome.Balance
            });
        });

        g.MapGet("/{playerId:long}/recipes", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var discovered = new HashSet<string>(store.ListFusionDiscoveries(playerId), StringComparer.Ordinal);
            var codex = store.ListCreatureCodex(playerId).Entries
                .Where(e => e.State == CreatureCodexStates.Discovered)
                .Select(e => e.SpeciesId)
                .ToHashSet(StringComparer.Ordinal);

            var items = CreatureRecipeCatalog.All
                .OrderBy(r => CreatureSpeciesCatalog.Get(r.OutputSpeciesId).BaseRarity)
                .ThenBy(r => r.RecipeId, StringComparer.Ordinal)
                .Select((r, slot) =>
                {
                    var output = CreatureSpeciesCatalog.Get(r.OutputSpeciesId);
                    var cost = FusionCostTable.Recipe(output.BaseRarity);
                    var isDiscovered = discovered.Contains(r.RecipeId);
                    var hintsUnlocked = codex.Contains(r.InputSpeciesIdA) && codex.Contains(r.InputSpeciesIdB);
                    return isDiscovered
                        ? (object)new
                        {
                            slot,
                            discovered = true,
                            recipeId = r.RecipeId,
                            resultSpeciesId = r.OutputSpeciesId,
                            resultRarity = output.BaseRarity.ToId(),
                            // spec-species-rank.md §5: the output's own rank beside its rarity band,
                            // copied off the catalog entry (band info only — the silhouette rule above
                            // is about the species id, which stays hidden until discovered).
                            resultRank = output.Rank?.ToId(),
                            resultRankDisplayName = output.Rank?.ToDisplayName(),
                            inputs = new[] { r.InputSpeciesIdA, r.InputSpeciesIdB },
                            cost = ProjectCost(cost, output)
                        }
                        : new
                        {
                            slot,
                            discovered = false,
                            resultRarity = output.BaseRarity.ToId(),
                            resultRank = output.Rank?.ToId(),
                            resultRankDisplayName = output.Rank?.ToDisplayName(),
                            // The experiment hint: ingredients show once both species are known.
                            inputs = hintsUnlocked ? new[] { r.InputSpeciesIdA, r.InputSpeciesIdB } : null,
                            cost = ProjectCost(cost, output)
                        };
                });
            return Results.Ok(new { items });
        });
    }

    static object BuildPreview(RpgStore store, long playerId, FusionHttpRequest body)
    {
        var roster = store.ListCreatureRoster(playerId).Items
            .ToDictionary(s => s.Profile.InstanceId, StringComparer.Ordinal);

        switch (body.Mode)
        {
            case FusionModes.StarMerge:
            case FusionModes.Promotion:
            {
                if (body.BaseInstanceId is null || !roster.TryGetValue(body.BaseInstanceId, out var baseSpec))
                    return new { ok = false, reason = "base.missing" };
                if (!CreatureRarityIds.TryParse(baseSpec.Profile.Rarity, out var rarity))
                    return new { ok = false, reason = "base.rarity" };
                if (body.Mode == FusionModes.StarMerge)
                {
                    if (baseSpec.Profile.Star >= StarPolicy.StarCap(rarity))
                        return new { ok = false, reason = "star.maxed" };
                    var target = baseSpec.Profile.Star + 1;
                    return new
                    {
                        ok = true,
                        targetStar = target,
                        sacrificesNeeded = StarPolicy.SacrificesForStar(target),
                        cost = ProjectCost(FusionCostTable.StarMerge(rarity), baseSpec.Profile.ElementPrimary)
                    };
                }

                if (!StarPolicy.CanPromote(rarity, baseSpec.Profile.Star, baseSpec.Profile.Promoted))
                    return new { ok = false, reason = "promotion.not-ready" };
                // spec-species-rank.md §6: preview mirrors PromotionUnlocked's own enforcing gate in
                // RpgStore.Fusion.cs — the SAME Passes call over the base species' own catalog rank —
                // so the FE never offers a promotion execute would refuse with `promotion.rank-floor`.
                if (!CreatureRankFloors.Passes(
                        CreatureRankFloors.FusionPromotion, CreatureSpeciesCatalog.Get(baseSpec.Profile.SpeciesId)?.Rank))
                    return new { ok = false, reason = "promotion.rank-floor" };
                var newRarity = CreatureRarityLadder.OneRungAbove(rarity);
                return new
                {
                    ok = true,
                    newRarity = newRarity.ToId(),
                    newSlots = FusionRoller.SlotsFor(newRarity),
                    cost = ProjectCost(FusionCostTable.Promotion(newRarity), baseSpec.Profile.ElementPrimary)
                };
            }

            case FusionModes.Recipe:
            {
                if (body.Sacrifices is not { Count: 2 }
                    || !roster.TryGetValue(body.Sacrifices[0], out var a)
                    || !roster.TryGetValue(body.Sacrifices[1], out var b))
                    return new { ok = false, reason = "sacrifice.invalid" };
                if (string.Equals(body.Sacrifices[0], body.Sacrifices[1], StringComparison.Ordinal))
                    return new { ok = false, reason = "sacrifice.duplicate" }; // execute would refuse too
                var recipe = CreatureRecipeCatalog.TryMatch(a.Profile.SpeciesId, b.Profile.SpeciesId);
                if (recipe is null)
                    return new { ok = false, reason = "recipe.unknown" };
                var output = CreatureSpeciesCatalog.Get(recipe.OutputSpeciesId);
                var isDiscovered = store.ListFusionDiscoveries(playerId)
                    .Contains(recipe.RecipeId, StringComparer.Ordinal);
                var pickable = a.Profile.TraitIds.Concat(b.Profile.TraitIds)
                    .Distinct(StringComparer.Ordinal).ToList();

                // WAVE F2.5 / SP0.3: each sacrifice's own real rolled atoms, offered for inheritance only
                // when the server would actually accept a pick on them — mirrors RecipeUnlocked's own two
                // gates so the FE never offers a choice that bounces at execute time:
                // `picks.already-materialised` (now: a fusion-pick LEDGER row exists for this save's human
                // empire and the output species) and `picks.source-below-inherit-floor`
                // (InheritCostByRarity only covers OutputEligibilityFloor-and-above).
                var owner = new FusionRpg.Core.Saves.EmpireRef(
                    new FusionRpg.Core.Saves.SaveId(playerId), store.HumanEmpireOf(playerId));
                var alreadyOwnsOutput = store.ListSpeciesMods(owner)
                    .Any(m => string.Equals(m.SpeciesId, output.SpeciesId, StringComparison.Ordinal));
                var pickableAtoms = new List<object>();
                if (!alreadyOwnsOutput)
                    foreach (var (specimen, specimenId) in new[] { (a, body.Sacrifices[0]), (b, body.Sacrifices[1]) })
                    {
                        if (!CreatureRarityIds.TryParse(specimen.Profile.Rarity, out var sourceRarity)
                            || !CreatureRarityLadder.AtLeast(sourceRarity, CreatureRecipeCatalog.OutputEligibilityFloor))
                            continue;
                        // spec-species-rank.md §6: preview mirrors RecipeUnlocked's own
                        // `picks.source-below-rank-floor` gate for the SAME source species — the SAME
                        // Passes call over its own catalog rank — so an atom offered here is exactly
                        // an atom execute accepts.
                        if (!CreatureRankFloors.Passes(
                                CreatureRankFloors.FusionRecipeEligibility, CreatureSpeciesCatalog.Get(specimen.Profile.SpeciesId)?.Rank))
                            continue;
                        // The ONE function the fusion transaction also calls: the empire's ledger instance,
                        // else the delayed preview. The atoms offered are exactly the atoms accepted.
                        var sourceAtoms = store.PickSourceAtoms(playerId, specimen.Profile.SpeciesId);
                        if (sourceAtoms.Count == 0) continue;
                        var pickCostSouls = FusionCostTable.InheritPick(sourceRarity);
                        foreach (var atom in sourceAtoms)
                            pickableAtoms.Add(new
                            {
                                sourceInstanceId = specimenId,
                                sourceSpeciesId = specimen.Profile.SpeciesId,
                                atomId = atom.AtomId,
                                costSouls = pickCostSouls
                            });
                    }

                return new
                {
                    ok = true,
                    discovered = isDiscovered,
                    // Undiscovered outputs stay silhouetted even in preview — band + cost only.
                    resultSpeciesId = isDiscovered ? recipe.OutputSpeciesId : null,
                    resultRarity = output.BaseRarity.ToId(),
                    // spec-species-rank.md §5: the output species' own rank rides beside its rarity,
                    // copied off the catalog entry (never re-derived, never defaulted).
                    resultRank = output.Rank?.ToId(),
                    resultRankDisplayName = output.Rank?.ToDisplayName(),
                    pickableTraits = pickable,
                    pickableAtoms,
                    pickSlotCap = FusionRoller.SlotsFor(output.BaseRarity),
                    cost = ProjectCost(FusionCostTable.Recipe(output.BaseRarity), output)
                };
            }

            default:
                return new { ok = false, reason = "mode.unknown" };
        }
    }

    /// <summary>
    /// DECIDED: the essence line is priced from the (possibly hidden) output's element even for
    /// undiscovered recipes. Yes, element + rarity band can narrow a silhouette — that is the
    /// deliberate breadcrumb: without it a player cannot stock the right essence to afford the
    /// experiment, and the discovery loop would be blind trial-and-refusal.
    /// </summary>
    static object ProjectCost(FusionCost cost, CreatureSpeciesDef outputSpecies) =>
        ProjectCost(cost, outputSpecies.ElementPrimary.ToElementId());

    static object ProjectCost(FusionCost cost, string elementId) => new
    {
        souls = cost.Souls,
        shardMaterialId = "shard." + cost.ShardRarity.ToId(),
        shardCount = cost.ShardCount,
        essenceMaterialId = "essence." + elementId,
        essenceCount = cost.EssenceCount
    };

    static FusionRequest ToRequest(FusionHttpRequest body) => new(
        body.Mode ?? "",
        body.BaseInstanceId,
        body.Sacrifices ?? new List<string>(),
        body.PickedTraitId,
        body.Picks?.Select(p => new FusionPick(p.SourceInstanceId ?? "", p.AtomId ?? "")).ToList());

    public sealed class FusionHttpRequest
    {
        public long? PlayerId { get; set; }
        public string? Mode { get; set; }
        public string? BaseInstanceId { get; set; }
        public List<string>? Sacrifices { get; set; }
        public string? PickedTraitId { get; set; }
        public string? CorrelationId { get; set; }
        /// <summary>WAVE F2.4/F2.5: player-selected inheritance picks (fusion lab). Omitted or empty
        /// reproduces today's exact behavior — nothing forced, remainder rolls normally.</summary>
        public List<FusionPickHttp>? Picks { get; set; }
    }

    public sealed class FusionPickHttp
    {
        public string? SourceInstanceId { get; set; }
        public string? AtomId { get; set; }
    }

    /// <summary>SIM-only fixtures — deterministic species mints and material grants for tests.</summary>
    public static void MapFusionTest(this RouteGroupBuilder test)
    {
        test.MapPost("/seed-materials", (RpgStore store, long? playerId, string? materialId, long? qty) =>
        {
            var pid = playerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (!CreatureMaterialCatalog.IsKnown(materialId))
                return Results.BadRequest(new { reason = "material.unknown" });
            store.AddCreatureMaterials(pid, new[] { (materialId!, Math.Clamp(qty ?? 1, 1, 10_000)) });
            return Results.Ok(new { items = store.ListCreatureMaterials(pid) });
        });

        test.MapPost("/mint-creature", (RpgStore store, long? playerId, string? speciesId) =>
        {
            var pid = playerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (!CreatureSpeciesCatalog.IsKnown(speciesId))
                return Results.BadRequest(new { reason = "species.unknown" });
            var species = CreatureSpeciesCatalog.Get(speciesId!);
            var (specimen, _) = store.MintCreature(pid, new CreatureMintSpec
            {
                SpeciesId = species.SpeciesId,
                Side = species.Side,
                GameTypeId = species.GameTypeId,
                Rarity = species.BaseRarity.ToId(),
                Variant = "normal",
                ElementPrimary = species.ElementPrimary.ToElementId(),
                ElementSecondary = species.ElementSecondary?.ToElementId(),
                TraitIds = species.TraitPool.Count > 0
                    ? new List<string> { species.TraitPool[0] }
                    : new List<string>(),
                Origin = "summon"
            });
            return Results.Ok(specimen);
        });
    }
}
