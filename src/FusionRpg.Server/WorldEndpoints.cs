using FusionRpg.Contracts;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Topology;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// The world map's HTTP surface (spec-world-model.md §Server, spec-turn-engine.md §Server).
///
/// Reads are plain projections. The only two writes a client gets are filing orders and ending its
/// own turn — every actual mutation happens inside the turn engine, behind the barrier.
/// </summary>
public static class WorldEndpoints
{
    /// <summary>Bounded input at the boundary — a turn's orders are a handful, not a firehose.</summary>
    const int MaxCommandsPerSubmit = 200;

    public static void MapWorld(this WebApplication app)
    {
        var g = app.MapGroup("/api/world");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            var header = store.GetActiveWorld(playerId);
            return header is null ? Results.NotFound() : Results.Ok(Project(header));
        });

        // Takes a viewer and refuses an unknown one rather than quietly falling back to omniscience.
        // Not the *only* place fog reaches the wire — the turn report projects its entries and its
        // commands the same way — but this is the one a map view polls.
        g.MapGet("/{worldId}/state", (string worldId, string? asFaction, bool? lifelines, string? forLegion, RpgStore store) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound();

            var viewer = string.IsNullOrWhiteSpace(asFaction)
                ? world.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player)?.FactionId
                : asFaction.Trim();

            if (viewer is null || world.Factions.All(f => !string.Equals(f.FactionId, viewer, StringComparison.Ordinal)))
                return Results.BadRequest(new { reason = "faction.unknown" });

            // Reconnection cost is an O(holdings⁴) sweep and the overlay it feeds is off by
            // default, so it is asked for rather than always paid for. Free at six sectors; not free
            // at the size world-generator will produce, on an endpoint a map view polls.
            var topology = lifelines == true ? Lifelines(world, viewer) : NoLifelines;

            var believedView = new BelievedWorldView(world, viewer);

            // world-stage W26: a new read, not the `ListLoggedWorldCommands` call `/turn/{turn}`
            // already makes — that one answers "what happened", this route answers "what will
            // happen", and it needs this viewer's own pending order for the open turn to predict the
            // same release `LoamPhases.Pressure` (W25) will actually pick.
            var pendingCede = store.ListWorldCommands(worldId, world.CurrentTurn)
                .Where(c => c.Kind == WorldCommandKinds.Cede && c.SectorId != null
                    && string.Equals(c.CommanderId, viewer, StringComparison.Ordinal))
                .Select(c => c.SectorId)
                .LastOrDefault();

            var state = Project(world, believedView, topology, pendingCede);

            // world-stage W9 (re-homed from `world-targeting`): the route preview needs a lane's
            // march cost for the legion the player is planning with, and `LaneCost.For` needs the
            // legion's banner element plus a climate lookup neither TypeScript nor a plain field can
            // supply. Opt-in like `lifelines` above — empty when no legion is named or it is not
            // this viewer's own (§0.13 forbids deriving a curve client-side, but there is nothing to
            // derive for a legion nobody selected).
            if (!string.IsNullOrWhiteSpace(forLegion))
            {
                var legion = believedView.OwnForces
                    .FirstOrDefault(e => string.Equals(e.EntityId, forLegion, StringComparison.Ordinal));
                if (legion is not null)
                {
                    var banner = BannerElement.Of(legion);
                    var costs = believedView.Lanes.ToDictionary(
                        l => l.LaneId,
                        l => LaneCost.For(l, banner, sectorId => believedView.Believed(sectorId)?.Climate),
                        StringComparer.Ordinal);
                    state = state with { MarchCosts = costs };
                }
            }

            return Results.Ok(state);
        });

        // wonder-wire §Design 4: the one new read (no POST, no command kind, no turn effect).
        // Reachable-first picking's planning-time source — which relic instances are reachable HERE
        // for one (entity, sector) pair. Viewer gating identical to /state above: an unknown
        // faction refuses `faction.unknown`, never silent omniscience. Presence-only: the
        // faction/presence gates stay deposit/withdraw's job at move time, and sector ownership is
        // re-validated at resolution by BuildResolver (`build.not-yours`), never by this read.
        g.MapGet("/{worldId}/relic-reachability", (
            string worldId, string? entityId, string? sectorId, string? asFaction, RpgStore store) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound();

            var viewer = string.IsNullOrWhiteSpace(asFaction)
                ? world.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player)?.FactionId
                : asFaction.Trim();

            if (viewer is null || world.Factions.All(f => !string.Equals(f.FactionId, viewer, StringComparison.Ordinal)))
                return Results.BadRequest(new { reason = "faction.unknown" });

            var entity = entityId ?? "";
            var sector = sectorId ?? "";
            return Results.Ok(new WorldRelicReachabilityDto
            {
                WorldId = worldId,
                EntityId = entity,
                SectorId = sector,
                ReachableInstanceIds = store.ListReachableRelics(worldId, entity, sector),
            });
        });

        g.MapPost("/{worldId}/commands", (string worldId, SubmitWorldCommandsRequest body, RpgStore store) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound();
            if (body.Commands is not { Count: > 0 })
                return Results.BadRequest(new { reason = "commands.empty" });

            // An FE almost always means "my orders", so the player faction is the default.
            var commanderId = string.IsNullOrWhiteSpace(body.CommanderId)
                ? world.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player)?.FactionId
                : body.CommanderId!.Trim();
            if (commanderId is null) return Results.BadRequest(new { reason = "commander.unknown" });

            if (body.Commands.Count > MaxCommandsPerSubmit)
                return Results.BadRequest(new { reason = "commands.too-many", max = MaxCommandsPerSubmit });

            var commands = body.Commands.Select(c => new WorldCommand
            {
                CommanderId = commanderId,
                CommandId = (c.CommandId ?? "").Trim(),
                Kind = (c.Kind ?? "").Trim(),
                EntityId = c.EntityId,
                SectorId = c.SectorId,
                SlotIndex = c.SlotIndex,
                Stance = c.Stance,
                LanePath = c.LanePath ?? new List<string>(),
                Amount = c.Amount,
                StructureId = c.StructureId,
                // wonder-rest §Design 2: the picked list copies through opaquely — no count/dup
                // pre-check here (admission owns `relic.count-mismatch` / `relic.duplicate`), no
                // IsKnown Get (admission owns `structure.unknown`), null reads as "spends no relic".
                RelicInstanceIds = c.RelicInstanceIds ?? new List<string>(),
                ProjectId = c.ProjectId,
                // empire-inventory-surfaces `cargo-commands` §Design 2: the cargo/cache fields,
                // copied opaquely — admission owns every malformed shape, never this mapping.
                // Appended, never reordered (merge-additive with the `RelicInstanceIds` line).
                Seq = c.Seq,
                TargetEntityId = c.TargetEntityId,
                CacheId = c.CacheId,
                CargoKind = c.CargoKind,
                InstanceId = c.InstanceId,
                ContainerId = c.ContainerId,
                Qty = c.Qty
            }).ToList();

            // One pass over the batch — the store reads the world once and owns which turn is open.
            // Partial acceptance is reported per command: one stale order must not throw away the
            // rest of a commander's turn.
            var results = store.SubmitWorldCommands(worldId, commands)
                .Select(o => new WorldCommandResultDto
                {
                    CommandId = o.CommandId,
                    Ok = o.Ok,
                    Reason = o.Reason,
                    Replayed = o.Replayed
                })
                .ToList();

            return Results.Ok(new { turn = world.CurrentTurn, commanderId, results });
        });

        // empire-inventory-surfaces `claim-endpoints` §Design 1-4: four reads-or-filers, one
        // legion / one sector per read, never empire-wide (ideal §6 Diablo precedent). Appended
        // after the `/commands` filer whose construction discipline the POST claims filer reuses —
        // never interleaved with the `wonder-rest`/`wonder-wire` sibling hunks above and below.
        //
        // Turn-engine purity holds (`WorldEndpoints.cs:13-17` two-write lock): the POST below is a
        // filer — it constructs a `claim-cache` WorldCommand and delegates to SubmitWorldCommands,
        // exactly the way `POST /{worldId}/commands` does. No route calls a cargo/cache verb
        // directly; every mutation still happens inside the commit transaction via 4A.1's
        // post-Step pass. Request DTOs carry no weight field (`weightEachFor` resolves
        // server-side at commit through `TryResolveCargoWeight` — consumed, never re-decided).
        g.MapGet("/{worldId}/legions/{entityId}/claimable-caches", (
            string worldId, string entityId, string? asFaction, RpgStore store) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound(new { reason = "world.unknown" });

            var viewer = ResolveViewer(world, asFaction);
            if (viewer is null) return Results.BadRequest(new { reason = "faction.unknown" });

            var legion = FindEntity(world, entityId);
            if (legion is null) return Results.NotFound(new { reason = "entity.unknown" });
            if (!string.Equals(legion.OwnerFactionId, viewer, StringComparison.Ordinal))
                return Results.BadRequest(new { reason = "entity.not-yours" });

            // The response IS the fog rule (§Design 2): live-position read, non-void + non-empty
            // only, corrupt rows see nothing — projected 1:1, never client-filtered.
            var caches = store.ListClaimableCaches(worldId, entityId)
                .Select(c => new ClaimableCacheDto
                {
                    CacheId = c.CacheId,
                    PlaceKind = c.PlaceKind,
                    PlaceRef = c.PlaceRef,
                    SourceKind = c.SourceKind,
                    ItemCount = store.ListCorpseCacheItems(c.CacheId).Count,
                    CreatedUtc = c.CreatedUtc,
                })
                .ToList();

            return Results.Ok(new ClaimableCacheListDto
            {
                WorldId = worldId,
                EntityId = entityId,
                AsOfTurn = world.CurrentTurn,
                Caches = caches,
                ClaimCostMilli = FusionRpg.Core.World.WorldTuningHub.Tuning.Movement.ClaimCostMilli,
            });
        });

        g.MapPost("/{worldId}/legions/{entityId}/claims", (
            string worldId, string entityId, FileClaimCacheRequest? body, RpgStore store) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound(new { reason = "world.unknown" });

            // Same commander discipline as `/commands` above: the player faction is the default.
            var commanderId = string.IsNullOrWhiteSpace(body?.CommanderId)
                ? world.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player)?.FactionId
                : body!.CommanderId!.Trim();
            if (commanderId is null) return Results.BadRequest(new { reason = "commander.unknown" });

            // A filer, not a mutator: construct the command, file it, report filing — resolution
            // happens at commit through the cargo-commands pass, never here
            // (`correlationId := CommandId`, spec §Design 3 — no second key on this wire).
            var command = new WorldCommand
            {
                CommanderId = commanderId,
                CommandId = body?.CommandId?.Trim() ?? "",
                Kind = WorldCommandKinds.ClaimCache,   // the converged kind (cargo-commands Locked anchors)
                EntityId = entityId,
                CacheId = body?.CacheId?.Trim(),        // weightEachFor resolves server-side at commit — never on this wire
            };
            var outcome = store.SubmitWorldCommands(worldId, new[] { command }).Single();
            return Results.Ok(new WorldCommandResultDto
            {
                CommandId = outcome.CommandId,
                Ok = outcome.Ok,
                Reason = outcome.Reason,
                Replayed = outcome.Replayed
            });
        });

        g.MapGet("/{worldId}/legions/{entityId}/cargo", (
            string worldId, string entityId, string? asFaction, RpgStore store) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound(new { reason = "world.unknown" });

            var viewer = ResolveViewer(world, asFaction);
            if (viewer is null) return Results.BadRequest(new { reason = "faction.unknown" });

            var legion = FindEntity(world, entityId);
            if (legion is null) return Results.NotFound(new { reason = "entity.unknown" });
            if (!string.Equals(legion.OwnerFactionId, viewer, StringComparison.Ordinal))
                return Results.BadRequest(new { reason = "entity.not-yours" });

            // One legion's rows plus used-vs-capacity (§Design 4). Used is the checked C# sum over
            // the stored `weight_each` snapshot column — the same arithmetic `LegionCargo` applies,
            // repeated as a projection, never a second tally query (guard-dal: no SQL here).
            var rows = store.ListCargo(worldId, entityId);
            var weightUsed = 0L;
            var dtos = new List<CargoRowDto>(rows.Count);
            foreach (var row in rows)
            {
                var rowWeight = string.Equals(row.Kind, "stack", StringComparison.Ordinal)
                    ? checked((row.Qty ?? 0) * row.WeightEach)
                    : row.WeightEach;
                weightUsed = checked(weightUsed + rowWeight);
                dtos.Add(new CargoRowDto
                {
                    Seq = row.Seq,
                    Kind = row.Kind,
                    InstanceId = row.InstanceId,
                    ContainerId = row.ContainerId,
                    Qty = row.Qty,
                    WeightEach = row.WeightEach,
                    RowWeight = rowWeight,
                });
            }

            return Results.Ok(new LegionCargoDto
            {
                WorldId = worldId,
                EntityId = entityId,
                AsOfTurn = world.CurrentTurn,
                Rows = dtos,
                WeightUsed = weightUsed,
                WeightCapacity = store.LegionWeightCapacity(worldId, entityId),
                SlotsUsed = rows.Count,
                SlotCapacity = store.LegionSlotCapacity(worldId, entityId),
            });
        });

        g.MapGet("/{worldId}/sectors/{sectorId}/storage", (
            string worldId, string sectorId, string? asFaction, RpgStore store) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound(new { reason = "world.unknown" });

            var viewer = ResolveViewer(world, asFaction);
            if (viewer is null) return Results.BadRequest(new { reason = "faction.unknown" });

            var sector = world.Sectors.FirstOrDefault(s =>
                string.Equals(s.SectorId, sectorId, StringComparison.Ordinal));
            if (sector is null) return Results.NotFound(new { reason = "sector.unknown" });

            // One sector's rows plus room (§Design 4). Capacity is computed fresh from the live
            // sector shell's built vaults — never cached, never client-derived. Owner is the live
            // read and the capture-header input (§Design 5).
            var rows = store.ListSectorStorage(worldId, sectorId);

            return Results.Ok(new SectorStorageDto
            {
                WorldId = worldId,
                SectorId = sectorId,
                OwnerFactionId = sector.OwnerFactionId,
                AsOfTurn = world.CurrentTurn,
                Rows = rows.Select(r => new SectorStorageRowDto
                {
                    Seq = r.Seq,
                    Kind = r.Kind,
                    InstanceId = r.InstanceId,
                    ContainerId = r.ContainerId,
                    Qty = r.Qty,
                }).ToList(),
                SlotsUsed = rows.Count,
                SlotCapacity = SectorItemCapacity.EffectiveCapacity(sector),
            });
        });

        MapTurns(g);
    }

    /// <summary>
    /// Turn writes. Kept in the same module as the reads but deliberately separate from them: the
    /// only two things a client may do to a world are file orders and end its own turn.
    /// </summary>
    static void MapTurns(RouteGroupBuilder g)
    {
        g.MapPost("/{worldId}/commit", (string worldId, CommitWorldTurnRequest? body, RpgStore store, IHubContext<RpgHub> hub,
            FusionRpg.Server.Notifications.WorldTurnNotificationPump notifyPump) =>
        {
            var world = store.LoadWorldState(worldId);
            if (world is null) return Results.NotFound();

            var commanderId = string.IsNullOrWhiteSpace(body?.CommanderId)
                ? world.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player)?.FactionId
                : body!.CommanderId!.Trim();
            if (commanderId is null) return Results.BadRequest(new { reason = "commander.unknown" });

            // Required, not optional. A commit that does not name its turn is a commit that can be
            // retried into the *next* one once the AI releases the barrier automatically, which
            // costs the player a turn they never played.
            if (body?.Turn is not { } expectedTurn)
                return Results.BadRequest(new { reason = "turn.missing" });

            var result = store.CommitWorldTurn(worldId, commanderId, expectedTurn);
            if (!result.Ok) return Results.BadRequest(new { reason = result.Reason });

            // Only the commit that actually stepped the world is worth waking every client for.
            if (result.Advanced)
            {
                hub.Clients.All.SendAsync("WorldUpdated", new { worldId });
                // notify-service spec §3 Triggers: a pump failure never fails the commit response -
                // the commit is already durable, so this runs after it, outside its transaction.
                try { notifyPump.Run(worldId, FusionRpg.Server.Notifications.WorldTurnTrigger.Commit); }
                catch (Exception ex) { Console.Error.WriteLine($"[notify-pump] commit trigger failed for '{worldId}': {ex.Message}"); }
            }

            return Results.Ok(new WorldTurnCommitDto
            {
                Ok = true,
                Reason = result.Reason,
                Advanced = result.Advanced,
                StateHash = result.StateHash,
                CurrentTurn = store.GetWorldHeader(worldId)?.CurrentTurn ?? world.CurrentTurn
            });
        });

        // Takes a viewer for the same reason /state does. Fog is not a secrecy boundary here —
        // `?asFaction=` already hands any caller any faction's view, and this is a single-player
        // game with a trusted client — it is a *rendering* rule: by default you are shown your own
        // war, and auditing somebody else's is a thing you ask for on purpose.
        //
        // Fog on an old turn's report is filtered by W-F1 (spec-world-wire.md §2,
        // WorldReportVisibility.VisibleTo, Core since ask A2)
        // — but `believed` here is built from the world's *current* state (`BelievedWorldView`
        // reads live intel, not a snapshot pinned to `turn`), so a sector scouted only after this
        // turn was played can retroactively surface a battle line from before that scouting
        // happened. Stated rather than hidden: this errs toward showing a viewer more than they knew
        // at the time, never less — the direction W-F1 itself already prefers when it must be wrong.
        g.MapGet("/{worldId}/turn/{turn:int}", (string worldId, int turn, string? asFaction, RpgStore store) =>
        {
            var log = store.GetWorldTurnLog(worldId, turn);
            if (log is null) return Results.NotFound();

            var world = store.LoadWorldState(worldId);
            var viewer = string.IsNullOrWhiteSpace(asFaction)
                ? world?.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player)?.FactionId
                : asFaction.Trim();

            if (world is not null && (viewer is null
                || world.Factions.All(f => !string.Equals(f.FactionId, viewer, StringComparison.Ordinal))))
                return Results.BadRequest(new { reason = "faction.unknown" });

            var believed = world is null || viewer is null ? null : new BelievedWorldView(world, viewer);

            // Reports outside the hot tail are re-derived by replay, and re-derivation refuses
            // rather than fabricating across an engine version change — so this can legitimately
            // come back empty for an old turn played by an older ruleset.
            var report = store.GetWorldTurnReport(worldId, turn);

            return Results.Ok(new WorldTurnReportDto
            {
                Turn = turn,
                StateHash = log.StateHash,
                Phases = report?.Phases.ToList() ?? new List<string>(),
                // Projected like the commands beside them (W39). Entries carry free text, so the
                // filtering is on the structured `SectorId` rather than on the prose — matching a
                // sector name out of a sentence works until somebody writes a different sentence.
                Entries = report?.Entries
                    .Where(e => WorldReportVisibility.VisibleTo(e, viewer, believed))
                    .Select(e => new WorldTurnEntryDto
                    {
                        Phase = e.Phase, Kind = e.Kind, Subject = e.Subject,
                        Detail = e.Detail, SectorId = e.SectorId
                    })
                    .ToList() ?? new List<WorldTurnEntryDto>(),

                // Read from the command log rather than the report, so a trimmed turn still says
                // what everyone was trying to do — and why, where an AI was the one trying.
                // world-stage W18 (arbitration §C, §8.3): `Reason` for another commander's own
                // order is one of the two channels §8c.3 wants closed alongside the fog fix — an
                // ordinary client never gets it; `FUSIONRPG_SIM=1` (`SimFlags.Enabled`) is the same
                // developer gate `/api/test/*` already uses. Owner decision on granularity was left
                // to this task with a stated fallback ("drop Reason, keep the entry" — the cheaper
                // reading) for when no answer comes in time; no answer came, so that is what shipped.
                // The viewer's own commands always keep their own Reason, gate or no gate.
                Commands = store.ListLoggedWorldCommands(worldId, turn)
                    .Where(l => VisibleTo(l.Command, viewer, believed))
                    .Select(l => new WorldTurnCommandDto
                    {
                        CommanderId = l.Command.CommanderId,
                        CommandId = l.Command.CommandId,
                        Kind = l.Command.Kind,
                        EntityId = l.Command.EntityId,
                        SectorId = l.Command.SectorId,
                        Reason = SimFlags.Enabled || string.Equals(l.Command.CommanderId, viewer, StringComparison.Ordinal)
                            ? l.Reason
                            : null
                    })
                    .ToList()
            });
        });

        // world-stage W17: rules, not state — no world id, no viewer, no fog. `StructureCatalog`,
        // `SlotTypeCatalog`, `StrengthBandCatalog` and `LaneTypeCatalog` are all public Core catalogs
        // with no HTTP caller today, so a UI cannot learn what is buildable, what a slot letter
        // means, or what a strength band is worth.
        g.MapGet("/catalog", () => Results.Ok(new WorldCatalogDto
        {
            Structures = StructureCatalog.All.Select(s => new WorldStructureDto
            {
                StructureId = s.StructureId,
                Name = s.Name,
                Kind = s.Kind.ToString(),
                RequiredSlotKind = s.RequiredSlotKind.ToString(),
                Cost = s.Cost,
                YieldMultiplierMilli = s.YieldMultiplierMilli,
                BuildTurns = s.BuildTurns,
                CapacityBonus = s.CapacityBonus,
                ObstacleKind = s.Obstacle.ToString(),
                MaterialTier = s.MaterialTier,
                // wonder-wire §Design 1: four appended projection lines (never reordered — 4A.1's
                // sibling DTO extension merges additively). Rules, not state: no fog gate.
                WonderScope = s.WonderScope?.ToString(),
                WonderRarity = s.WonderRarity?.ToString(),
                RelicCost = s.RelicCost,
                ExistenceCap = s.WonderScope is { } ws && s.WonderRarity is { } wr
                    ? WonderPolicy.ExistenceCapFor(ws, wr) : long.MaxValue,
            }).ToList(),
            SlotTypes = SlotTypeCatalog.All.Select(s => new WorldSlotTypeDto
            {
                SlotTypeId = s.SlotTypeId,
                Name = s.Name,
                Kind = s.Kind.ToString(),
                Buildable = s.Buildable,
                Yields = s.Yields
            }).ToList(),
            StrengthBands = StrengthBandCatalog.All.Select(b => new WorldStrengthBandDto
            {
                Index = b.Index,
                Name = b.Name,
                Floor = b.Floor,
                Ceiling = b.Ceiling,
                Midpoint = b.Midpoint
            }).ToList(),
            LaneTypes = LaneTypeCatalog.All.Select(l => new WorldLaneTypeDto
            {
                LaneTypeId = l.LaneTypeId,
                Name = l.Name,
                CostMultiplierMilli = l.CostMultiplierMilli,
                CarriesSupply = l.CarriesSupply,
                CarriesPressure = l.CarriesPressure,
                OneWay = l.OneWay,
                Gated = l.Gated,
                Ley = l.Ley
            }).ToList(),
            WaystationRangeHops = LoamPolicy.WaystationRangeHops
        }));
    }

    /// <summary>
    /// Whether one order belongs in this viewer's account of the turn.
    ///
    /// Your own orders always. Somebody else's only where you have some belief about the ground it
    /// names — otherwise a turn report would quietly tell you the name of every sector on the map,
    /// which is the thing the state projection spends its whole existence preventing.
    ///
    /// </summary>
    /// <summary>
    /// Whether one report line belongs in this viewer's account of the turn — W-F1
    /// (spec-world-wire.md §2, world-stage W14, fog defect C), read as three named rules rather than
    /// one boolean expression, because the decision behind each is the thing a future reader needs
    /// to find:
    ///
    /// 1. Audience. A faction-scoped economy line has no sector and never did; it belongs to its
    ///    owner, not to everyone.
    /// 2. Live sight, for a dynamic fact. Battles, marches, halts, arrivals — only shown while this
    ///    viewer is actually watching the ground right now (`StateOf(sectorId) == Watched`).
    /// 3. Remembered sight, for a static fact. A claim, ownership changing — shown on any ground
    ///    this viewer has ever seen (`Believed(sectorId) is not null`), Civ VI's own rule.
    ///
    /// 4. Nowhere in particular, but only the calendar. A `Calendar`-kind line (week/month/season)
    ///    carries no `Audience` and no `SectorId` and reveals no ground — shown to everyone
    ///    (world-map W59: found by playing this rule's own scenario. This branch previously
    ///    unconditionally returned `false` for *any* no-audience/no-sector entry, silently dropping
    ///    every calendar tick from the wire despite <c>TurnEngine.cs</c>'s own doc comment for the
    ///    season line stating "the season is visible in the turn report."
    ///    <see cref="WorldTurnReportFogTests"/> proved rules 1-3 in isolation but had no case for
    ///    this fourth one, which is why nothing caught it).
    ///
    ///    Scoped to `Calendar` specifically, not every sectorless entry: a `CommandAccepted`/
    ///    `CommandDropped` entry is also sectorless by construction (`TurnEngine.cs`'s admission
    ///    loop and every resolver's own `Drop` helper call `report.Add` with no `SectorId`/`Audience`
    ///    argument at all), but that per-commander accept/drop story already reaches its own viewer
    ///    correctly through the separate `Commands` array one level up in this same endpoint (its own
    ///    `VisibleTo(WorldCommand, ...)` overload states "your own orders always") — broadening this
    ///    rule to every kind would leak every other commander's routine stand-fast accept/drop into
    ///    `Entries` too, which is a real, separate, larger fog question this task does not decide.
    /// </summary>
    static bool VisibleTo(WorldCommand command, string? viewer, BelievedWorldView? believed)
    {
        if (viewer is null || believed is null) return true;
        if (string.Equals(command.CommanderId, viewer, StringComparison.Ordinal)) return true;

        // An order that names no ground says only "somebody, somewhere, did nothing".
        if (command.SectorId is not { } sectorId) return false;

        return believed.Believed(sectorId) is not null;
    }

    /// <summary>SIM-only world creation — mapped inside the /api/test group.</summary>
    public static void MapWorldTest(this RouteGroupBuilder test)
    {
        test.MapPost("/world/create", (CreateWorldRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var playerId = body.PlayerId ?? store.GetCurrentPlayerId();
            var worldId = string.IsNullOrWhiteSpace(body.WorldId) ? "world-1" : body.WorldId!.Trim();
            var templateId = string.IsNullOrWhiteSpace(body.TemplateId)
                ? WorldTemplateCatalog.FirstLightId
                : body.TemplateId!.Trim();

            if (!WorldTemplateCatalog.IsKnown(templateId))
                return Results.BadRequest(new { reason = "template.unknown", templateId });
            if (!ulong.TryParse(body.Seed, out var seed))
                seed = 1;

            var built = WorldTemplateCatalog.Build(templateId, seed, worldId);
            var (ok, reason, _) = store.CreateWorld(playerId, built);
            if (!ok)
                return reason == "world.exists"
                    ? Results.Conflict(new { reason })
                    : Results.BadRequest(new { reason });

            hub.Clients.All.SendAsync("WorldUpdated", new { worldId });
            return Results.Ok(new { ok = true, worldId, templateId });
        });
    }

    /// <summary>
    /// Viewer resolution shared by the `claim-endpoints` reads (§Design 1): the same
    /// `faction.unknown`-refuses discipline `/state` applies — an unknown viewer never falls back
    /// to omniscience. Null means "refuse as unknown", never a default.
    /// </summary>
    static string? ResolveViewer(WorldState world, string? asFaction)
    {
        var viewer = string.IsNullOrWhiteSpace(asFaction)
            ? world.Factions.FirstOrDefault(f => f.Kind == WorldFactionKind.Player)?.FactionId
            : asFaction.Trim();
        return viewer is null
            || world.Factions.All(f => !string.Equals(f.FactionId, viewer, StringComparison.Ordinal))
            ? null
            : viewer;
    }

    /// <summary>
    /// One legion by id, live — the ownership root the `claim-endpoints` reads gate on before any
    /// reachability or content is touched (admission's own `entity.unknown` / `entity.not-yours`
    /// order, applied to reads).
    /// </summary>
    static WorldEntity? FindEntity(WorldState world, string entityId) =>
        world.Entities.FirstOrDefault(e =>
            string.Equals(e.EntityId, entityId, StringComparison.Ordinal));

    static WorldSectorDto ProjectSector(
        WorldState world, WorldSector sector, IWorldView view,
        (IReadOnlyDictionary<string, long> Cost, IReadOnlySet<string> Critical) lifelines,
        LoamReading loam)
    {
        var state = view.StateOf(sector.SectorId);
        var believed = view.Believed(sector.SectorId);

        // Never seen: where it is, and that is the whole payload.
        if (state == IntelState.Unknown || believed is null)
            return new WorldSectorDto
            {
                SectorId = sector.SectorId,
                Intel = IntelState.Unknown.ToString(),
                Phase = SectorPhase.Unknown.ToString(),
                LayoutX = sector.LayoutX,
                LayoutY = sector.LayoutY
            };

        // Owner-only: these dictionaries only ever contain the *viewer's own* holdings
        // (ComputeLoamReading is built over `TerritoryComponents.For(world, view.FactionId)`), so a
        // sector this faction does not own simply has no entry — gated structurally, not by a
        // per-field ownership check that could be forgotten on the next field added here.
        var production = loam.ProductionBySector.TryGetValue(sector.SectorId, out var p) ? p : 0;
        var upkeep = loam.UpkeepBySector.TryGetValue(sector.SectorId, out var u) ? u : 0;
        var upkeepBreakdown = loam.UpkeepBreakdownBySector.TryGetValue(sector.SectorId, out var ub) ? ub : default;
        var stock = loam.StockBySector.TryGetValue(sector.SectorId, out var st) ? st : 0;
        var componentId = loam.ComponentIdBySector.TryGetValue(sector.SectorId, out var cid) ? cid : null;
        var componentTotals = componentId is not null && loam.ComponentTotals.TryGetValue(componentId, out var totals)
            ? totals
            : (Production: 0L, Upkeep: 0L, Stock: 0L);

        // wonder-wire §Design 3: owner-only attribution reads. Same structural gating as the loam
        // maps above — a sector the viewer does not own reads zero, never another faction's
        // numbers. Common counts are deliberately absent (CountExisting returns 0 for Common).
        var isOwner = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal);
        var wonderLiveCountSector = isOwner
            ? WonderExistenceScan.CountExisting(world, sector, WonderScope.Sector, WonderRarity.Unique)
            : 0;
        var wonderLiveCountEmpire = isOwner
            ? WonderExistenceScan.CountExisting(world, sector, WonderScope.Empire, WonderRarity.Unique)
            : 0;
        // Attributable "Earns" expansion (GG-49): stored-modifier output minus the identity call.
        // `production` above already carries the stored modifier; a Wonder-free faction reads
        // exactly 0 (stored 1000, byte-identical worlds). `checked`: overflow throws, never wraps.
        var wonderProductionContribution = isOwner
            ? checked(production - LoamProduction.For(sector, 1000))
            : 0;

        return new WorldSectorDto
        {
            SectorId = sector.SectorId,
            TypeId = sector.TypeId,
            Climate = believed.Climate?.ToString(),
            DangerBand = believed.DangerBand,
            DevelopmentLevel = believed.DevelopmentLevel,
            FractureIntensityMilli = believed.FractureIntensityMilli,
            Habitable = Habitability.For(believed.Slots.Select(sl =>
                (sl.SlotTypeId, sl.StructureId, sl.ConstructionTurnsRemaining))),
            Phase = believed.Phase.ToString(),
            OwnerFactionId = believed.OwnerFactionId,

            // Stability, pressure, the warden binding, neglect and effective capacity are all
            // owner-only and read from truth directly — the same pattern `LifelineCost` already
            // uses for an owner-only number computed over the viewer's own holdings.
            //
            // world-stage W6: `PressureMilli` used to stay zero on the reasoning that "nobody
            // glances at a sector and reads its depletion off" — written before fade contagion
            // existed. It is now real, live state written every turn by `LoamPhases.NextPressure`
            // (`LoamPhases.cs:266-283`), so the old comment contradicted the code beneath it.
            // `DepletionMilli` stays zero — nothing in Core writes it; it remains a real gap, not a
            // wiring one, and is left alone here rather than invented.
            StabilityMilli = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                ? sector.StabilityMilli
                : 0,
            PressureMilli = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                ? sector.PressureMilli
                : 0,
            WardenBindingId = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                ? sector.WardenBindingId
                : null,
            NeglectedTurns = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                ? sector.NeglectedTurns
                : 0,
            LoamCapacity = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                ? LoamPhases.EffectiveCapacity(sector)
                : 0,
            RecruitStock = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                ? sector.RecruitStock
                : 0,
            ProjectId = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                ? sector.ProjectId
                : null,
            ProjectTurnsRemaining = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                ? sector.ProjectTurnsRemaining
                : null,
            Intel = state.ToString(),
            IntelAge = view.AgeOf(sector.SectorId),
            LastSeenTurn = believed.LastSeenTurn,
            LayoutX = sector.LayoutX,
            LayoutY = sector.LayoutY,

            Slots = believed.Slots.Select(sl => new WorldSlotDto
            {
                SlotIndex = sl.SlotIndex,
                SlotTypeId = sl.SlotTypeId,
                Element = sl.Element?.ToString(),
                State = sl.State.ToString(),
                // Truth-gated, not belief-gated (spec-world-wire.md §1's "cheap resolution"):
                // `RememberedSlot` has no `OwnerFactionId` of its own, so this reads the live
                // `WorldSlot` for the same index and gates it exactly like `StabilityMilli`
                // (world-stage W7) — present only on ground this viewer owns.
                OwnerFactionId = string.Equals(sector.OwnerFactionId, view.FactionId, StringComparison.Ordinal)
                    ? sector.Slots.FirstOrDefault(truthSlot => truthSlot.SlotIndex == sl.SlotIndex)?.OwnerFactionId
                    : null,
                GuardWaveId = sl.GuardWaveId,
                GuardState = sl.GuardState.ToString(),
                StructureId = sl.StructureId,
                ConstructionTurnsRemaining = sl.ConstructionTurnsRemaining,
                // wonder-wire §Design 2: Wonder facet without a second fetch per slot — catalog
                // join, null unless a known Wonder row. Fog-parity with StructureId: as visible as
                // the slot itself, no owner-gating.
                WonderScope = sl.StructureId is { } sid && StructureCatalog.IsKnown(sid)
                    ? StructureCatalog.Get(sid).WonderScope?.ToString() : null,
                WonderRarity = sl.StructureId is { } sid2 && StructureCatalog.IsKnown(sid2)
                    ? StructureCatalog.Get(sid2).WonderRarity?.ToString() : null,
            }).ToList(),

            LifelineCost = lifelines.Cost.TryGetValue(sector.SectorId, out var cost) ? cost : 0,
            Lifeline = lifelines.Critical.Contains(sector.SectorId),

            // wonder-wire §Design 3: appended (never interleaved — 4A.x sibling projections merge
            // additively). WonderUpkeep travels INSIDE UpkeepBreakdown above, not beside it.
            WonderLiveCountSector = wonderLiveCountSector,
            WonderLiveCountEmpire = wonderLiveCountEmpire,
            WonderProductionContribution = wonderProductionContribution,

            LoamProduction = production,
            LoamUpkeep = upkeep,
            UpkeepBreakdown = new LoamUpkeepBreakdownDto
            {
                Base = upkeepBreakdown.Base,
                Garrison = upkeepBreakdown.Garrison,
                Development = upkeepBreakdown.Development,
                Danger = upkeepBreakdown.Danger,
                IntensityMilli = upkeepBreakdown.IntensityMilli,
                HandicapMilli = upkeepBreakdown.HandicapMilli,
                // wonder-wire §Design 3: the fifth operand, projected from the ALREADY-COMPUTED
                // breakdown map above — never a second formula.
                WonderUpkeep = upkeepBreakdown.WonderUpkeep,
            },
            LoamNet = production - upkeep,
            ComponentId = componentId,
            ComponentProduction = componentTotals.Production,
            ComponentUpkeep = componentTotals.Upkeep,
            ComponentNet = componentTotals.Production - componentTotals.Upkeep,
            LoamStock = stock,
            ComponentStock = componentTotals.Stock,
            WillReleaseNextTurn = loam.ReleaseCandidates.Contains(sector.SectorId),

            Forces = believed.Forces.Select(f => new WorldForceDto
            {
                EntityId = f.EntityId,
                OwnerFactionId = f.OwnerFactionId,
                Kind = f.Kind.ToString(),
                Exact = f.Exact,
                Strength = f.Exact ? f.Strength : 0,
                BandName = StrengthBandCatalog.ByIndex(f.BandIndex).Name,
                BandCeiling = f.Defensive
            }).ToList()
        };
    }

    static WorldHeaderDto Project(WorldHeaderRow h) => new()
    {
        WorldId = h.WorldId,
        TemplateId = h.TemplateId,
        CurrentTurn = h.CurrentTurn,
        State = h.State,
        CreatedUtc = h.CreatedUtc,
        Revision = h.Revision
    };

    /// <summary>
    /// Projects a world through one faction's eyes. Nothing here reads `WorldState` for anything a
    /// viewer has not seen: sectors come from belief, forces come from belief, and the only entities
    /// returned are the viewer's own.
    ///
    /// The sector's layout position is always sent — the graph's shape is public knowledge, and a
    /// map you cannot draw is unusable — but an unseen sector carries nothing else at all.
    /// </summary>
    /// <summary>
    /// How load-bearing each of the viewer's own sectors is. Computed over their holdings alone, so
    /// it leaks nothing — it describes territory they can already see. Withholding it would be the
    /// computer having the fun: the AI reads exactly this to decide what to garrison.
    /// </summary>
    static readonly (IReadOnlyDictionary<string, long> Cost, IReadOnlySet<string> Critical) NoLifelines =
        (new Dictionary<string, long>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    static (IReadOnlyDictionary<string, long> Cost, IReadOnlySet<string> Critical) Lifelines(
        WorldState world, string factionId)
    {
        var holdings = world.Sectors
            .Where(s => string.Equals(s.OwnerFactionId, factionId, StringComparison.Ordinal))
            .Select(s => s.SectorId)
            .ToHashSet(StringComparer.Ordinal);

        if (holdings.Count == 0)
            return (new Dictionary<string, long>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        return (
            ReconnectionCost.For(world, holdings),
            ArticulationPoints.Find(LaneGraph.Build(world, holdings)));
    }

    /// <summary>
    /// Derived loam numbers (spec-loam-fe.md), computed from `loam-calc` at projection time and
    /// never stored. Built the same shape as <see cref="Lifelines"/>: computed once, over the
    /// viewer's own holdings only, so the gating is structural — a sector this faction does not own
    /// simply never appears in any of these maps, rather than being computed and then hidden.
    /// </summary>
    sealed record LoamReading(
        IReadOnlyDictionary<string, string> ComponentIdBySector,
        IReadOnlyDictionary<string, long> ProductionBySector,
        IReadOnlyDictionary<string, long> UpkeepBySector,
        IReadOnlyDictionary<string, LoamUpkeepBreakdown> UpkeepBreakdownBySector,
        IReadOnlyDictionary<string, long> StockBySector,
        IReadOnlyDictionary<string, (long Production, long Upkeep, long Stock)> ComponentTotals,
        IReadOnlySet<string> ReleaseCandidates);

    static readonly LoamReading NoLoamReading = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, long>(StringComparer.Ordinal),
        new Dictionary<string, long>(StringComparer.Ordinal),
        new Dictionary<string, LoamUpkeepBreakdown>(StringComparer.Ordinal),
        new Dictionary<string, long>(StringComparer.Ordinal),
        new Dictionary<string, (long, long, long)>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    static LoamReading ComputeLoamReading(WorldState world, string factionId, string? ceded = null)
    {
        var componentIdBySector = new Dictionary<string, string>(StringComparer.Ordinal);
        var productionBySector = new Dictionary<string, long>(StringComparer.Ordinal);
        var upkeepBySector = new Dictionary<string, long>(StringComparer.Ordinal);
        var upkeepBreakdownBySector = new Dictionary<string, LoamUpkeepBreakdown>(StringComparer.Ordinal);
        var stockBySector = new Dictionary<string, long>(StringComparer.Ordinal);
        var componentTotals = new Dictionary<string, (long, long, long)>(StringComparer.Ordinal);
        var releaseCandidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var component in TerritoryComponents.For(world, factionId))
        {
            var componentId = component[0];   // stable and meaningful on the wire — the spec's own choice
            long totalProduction = 0, totalUpkeep = 0, totalStock = 0;

            foreach (var sectorId in component)
            {
                var sector = world.Sectors.First(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal));
                // wonder-effect-empire §Design 4: the same stored-modifier read as LoamForecast/
                // LoamBalance — the debug view must not under-report a Wondered faction.
                var scopeModifierMilli = world.Factions
                    .FirstOrDefault(f => f.FactionId == sector.OwnerFactionId)?.ScopeModifierMilli ?? 1000;
                var production = LoamProduction.For(sector, scopeModifierMilli);
                var upkeepBreakdown = LoamUpkeep.BreakdownFor(world, sector);
                var upkeep = upkeepBreakdown.Total;

                componentIdBySector[sectorId] = componentId;
                productionBySector[sectorId] = production;
                upkeepBySector[sectorId] = upkeep;
                upkeepBreakdownBySector[sectorId] = upkeepBreakdown;
                stockBySector[sectorId] = sector.LoamStock;
                totalProduction += production;
                totalUpkeep += upkeep;
                totalStock += sector.LoamStock;
            }

            componentTotals[componentId] = (totalProduction, totalUpkeep, totalStock);

            // A turn-early warning for spec-loam-fe.md's abandonment surface, computed by the same
            // static the engine's own Pressure phase uses to pick who takes the fade — reusing it
            // rather than re-deriving the selection is what keeps this from silently disagreeing
            // with what actually happens next turn.
            var willRelease = LoamForecast.WillRelease(world, component, ceded);
            if (willRelease is not null)
                releaseCandidates.Add(willRelease);
        }

        return new LoamReading(componentIdBySector, productionBySector, upkeepBySector, upkeepBreakdownBySector, stockBySector, componentTotals, releaseCandidates);
    }

    static WorldStateDto Project(
        WorldState w, IWorldView view,
        (IReadOnlyDictionary<string, long> Cost, IReadOnlySet<string> Critical) lifelines,
        string? ceded = null)
    {
        var loam = ComputeLoamReading(w, view.FactionId, ceded);
        // world-stage W15: the current turn's roll only — never the seed, never a future roll.
        var calendar = TurnCalendar.Roll(w.CurrentTurn, w.Seed);

        return new WorldStateDto
        {
            WorldId = w.WorldId,
            TemplateId = w.TemplateId,
            CurrentTurn = w.CurrentTurn,
            Calendar = new WorldCalendarDto
            {
                DaysPerWeek = TurnCalendar.DaysPerWeek,
                WeeksPerMonth = TurnCalendar.WeeksPerMonth,
                WeekBoundary = calendar.WeekBoundary,
                MonthBoundary = calendar.MonthBoundary,
                SpecialWeek = calendar.SpecialWeek,
                SpecialMonth = calendar.SpecialMonth,
                Plague = calendar.Plague,
                Season = calendar.Season
            },
            // world-stage W16: separate from `Sectors[].Intel` on purpose — never merged in.
            ProspectedSectorIds = Prospecting.Reveal(w, view.FactionId).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            Factions = w.Factions.Select(f => new WorldFactionDto
            {
                FactionId = f.FactionId,
                Kind = f.Kind.ToString(),
                Name = f.Name
            }).ToList(),
            Sectors = w.Sectors.Select(s => ProjectSector(w, s, view, lifelines, loam)).ToList(),
        Lanes = view.Lanes.Select(l => new WorldLaneDto
        {
            LaneId = l.LaneId,
            FromSectorId = l.FromSectorId,
            ToSectorId = l.ToSectorId,
            TypeId = l.TypeId,
            Length = l.Length,
            Width = l.Width,
            HazardMilli = l.HazardMilli,
            WardLevel = l.WardLevel,
            GateKeyId = l.GateKeyId,
            State = l.State.ToString()
        }).ToList(),
        Entities = view.OwnForces.Select(e => new WorldEntityDto
        {
            EntityId = e.EntityId,
            Kind = e.Kind.ToString(),
            OwnerFactionId = e.OwnerFactionId,
            AtSectorId = e.AtSectorId,
            OnLaneId = e.OnLaneId,
            OnLaneTowardSectorId = e.OnLaneTowardSectorId,
            LaneProgressMilli = e.LaneProgressMilli,
            Stance = e.Stance,
            MovementRemaining = e.MovementRemaining,
            Routed = e.Routed,
            Members = e.Members.Select(m => new WorldEntityMemberDto
            {
                InstanceId = m.InstanceId,
                SpeciesId = m.SpeciesId,
                Level = m.Level,
                Hp = m.Hp,
                Wounds = m.Wounds,
                Role = m.Role.ToString()
            }).ToList(),
            DisplayName = EntityNaming.DisplayName(w, e),
            CarriedLoam = e.CarriedLoam,
            Capacity = LegionSupply.Capacity(e),
            Burn = LegionSupply.Burn(e),
            Runway = LegionSupply.TurnsUntilExhausted(e)
        }).ToList()
        };
    }
}
