using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

public static class SimEndpoints
{
    public static void MapSimAndProbes(this WebApplication app)
    {
        var sim = app.MapGroup("/api/sim");
        sim.MapPost("/hello", async (SimService svc) =>
        {
            var blocked = svc.Guard();
            if (blocked != null) return blocked;
            await svc.HelloAsync();
            return Results.Ok(new { ok = true });
        });
        sim.MapPost("/board/start", async (SimBoardStartRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.BoardStart(body)));
        sim.MapPost("/board/end", async (SimBoardEndRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.BoardEnd(body)));
        sim.MapPost("/board/snapshot", async (SimSnapshotRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.BoardSnapshot(body)));
        sim.MapPost("/match/result", async (SimMatchResultRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.MatchResult(body)));
        sim.MapPost("/match/win", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.MatchWin()));
        sim.MapPost("/match/lose", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.MatchLose()));
        sim.MapPost("/wave", async (SimWaveRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.WaveChange(body)));
        sim.MapPost("/plant/place", async (SimPlacePlantRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.PlacePlant(body)));
        sim.MapPost("/zombie/place", async (SimPlaceZombieRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.PlaceZombie(body)));
        sim.MapPost("/plant/mix", async (SimMixRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Mix(body)));
        sim.MapPost("/economy", async (SimEconomyRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Economy(body)));
        sim.MapPost("/sun/spend", async (SimSunSpendRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.SunSpend(body)));
        sim.MapPost("/level/name", async (SimLevelNameRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.SetLevelName(body)));
        sim.MapPost("/wave/spawn", async (SimWaveRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.WaveSpawn(body)));
        sim.MapPost("/card/use", async (SimCardUseRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.CardUse(body)));
        sim.MapPost("/pet", async (SimPetRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Pet(body)));
        sim.MapPost("/grid", async (SimGridRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Grid(body)));
        sim.MapPost("/hypno", async (SimDieRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Hypno(body)));
        sim.MapPost("/crash", async (SimDieRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Crash(body)));
        sim.MapPost("/recipes", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.CatalogRecipes()));
        sim.MapPost("/zombies-catalog", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.CatalogZombies()));
        sim.MapPost("/entity/stats", async (SimEntityStatsRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Recapture(body)));
        sim.MapPost("/sun/gain", async (SimSunSpendRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("sun.gain", new Dictionary<string, object> { ["count"] = body?.Count ?? 25 })));
        sim.MapPost("/money/spend", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("money.spend", new Dictionary<string, object> { ["value"] = 50 })));
        sim.MapPost("/money/gain", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("money.gain", new Dictionary<string, object> { ["count"] = 50 })));
        sim.MapPost("/points/gain", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("points.gain", new Dictionary<string, object> { ["count"] = 1 })));
        sim.MapPost("/item/drop", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("item.drop", new Dictionary<string, object> { ["itemType"] = 0 })));
        sim.MapPost("/wave/huge", async (SimWaveRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("wave.huge", new Dictionary<string, object> { ["wave"] = body?.Wave ?? 1 })));
        sim.MapPost("/match/invade", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("match.invade", new Dictionary<string, object> { ["zombiePtr"] = "Z1" })));
        sim.MapPost("/match/restart", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("match.restart")));
        sim.MapPost("/shovel", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("plant.shovel")));
        sim.MapPost("/unfuse", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("plant.unfuse")));
        sim.MapPost("/glove", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("plant.glove")));
        sim.MapPost("/card/pick", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("card.pick")));
        sim.MapPost("/card/place", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("card.place", new Dictionary<string, object> { ["side"] = "plant" })));
        sim.MapPost("/card/bank", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("card.bank", new Dictionary<string, object> { ["side"] = "plant", ["type"] = 0 })));
        sim.MapPost("/bullet/place", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("bullet.place", new Dictionary<string, object> { ["type"] = 0 })));
        sim.MapPost("/travel/start", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("travel.start")));
        sim.MapPost("/travel/buff", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("travel.buff", new Dictionary<string, object> { ["kind"] = "normal" })));
        sim.MapPost("/travel/pick", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("travel.pick")));
        sim.MapPost("/prize/click", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bare("prize.click")));
        sim.MapPost("/plant/spawn", async (SimSpawnPlantRequest? body, SimService svc) =>
            await svc.RunAsync(s => svc.Engine.SpawnPlant(s, body)));
        sim.MapPost("/plant/damage", async (SimDamageRequest body, SimService svc) =>
            await svc.RunAsync(s => svc.Engine.DamagePlant(s, body)));
        sim.MapPost("/plant/die", async (SimDieRequest body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.DiePlant(body)));
        sim.MapPost("/zombie/spawn", async (SimSpawnZombieRequest? body, SimService svc) =>
            await svc.RunAsync(s => svc.Engine.SpawnZombie(s, body)));
        sim.MapPost("/zombie/damage", async (SimDamageRequest body, SimService svc) =>
            await svc.RunAsync(s => svc.Engine.DamageZombie(s, body)));
        sim.MapPost("/zombie/die", async (SimDieRequest body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.DieZombie(body)));
        sim.MapPost("/bullet", async (SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.Bullet()));
        // Server-side shield probe (combat-unification, sim-adoption): grant + damage +
        // /state show absorption with the game closed.
        sim.MapPost("/shield/grant", async (SimShieldGrantRequest body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.GrantShield(body)));
        sim.MapPost("/mower/place", async (SimMowerRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.PlaceMower(body)));
        sim.MapPost("/mower/start", async (SimMowerRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.StartMower(body)));
        sim.MapPost("/mower/die", async (SimMowerRequest? body, SimService svc) =>
            await svc.RunAsync(_ => svc.Engine.DieMower(body)));
        sim.MapGet("/state", (SimService svc) => Results.Ok(svc.Engine.Snapshot()));

        var test = app.MapGroup("/api/test");
        test.MapSoulTestSeed();
        test.MapWebMatchTest();
        test.MapExpeditionTest();
        test.MapFusionTest();
        test.MapContractTest();
        test.MapWorldTest();
        test.MapPost("/reset", async (SimService svc, RpgStore store) =>
        {
            await svc.FullResetAsync();
            // The patron cache is process state — a reset that wipes rpg_patron must clear it
            // too, or later tests/scenarios inherit a phantom designation.
            PatronEndpoints.RefreshRuntimeState(store);
            return Results.Ok(new { ok = true });
        });
        test.MapGet("/snapshot", async (RpgStore store, SimService svc, EventIngest ingest) =>
        {
            await ingest.FlushPendingAsync();
            return Results.Ok(new
            {
                health = ingest.Decorate(store.ToHealth(true)),
                stats = store.GetStats(),
                events = store.ListEvents(100, 0),
                eventCount = store.CountEvents(),
                eventCounts = store.CountByKind(),
                runs = store.ListRuns(),
                metrics = store.ListMetrics(),
                players = store.ListPlayers(),
                currentPlayerId = store.GetCurrentPlayerId(),
                entities = store.ListEntities(),
                mowers = store.ListMowers(),
                types = store.ListTypes(),
                recipes = store.ListRecipes(),
                spawnStats = store.ListSpawnStatsForPlayer(),
                sim = svc.Engine.Snapshot()
            });
        });
        test.MapPost("/probe", (ProbeRequest body, SimService svc, ILoggerFactory log) =>
        {
            log.CreateLogger("probe").LogInformation("test.probe {Name} {Scenario}", body.Name, body.Scenario);
            var env = new EventEnvelope
            {
                T = ServerClock.UtcNowDateTime.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "test.probe",
                Payload = new Dictionary<string, object?>
                {
                    ["name"] = body.Name,
                    ["scenario"] = body.Scenario,
                    ["data"] = body.Data
                }
            };
            svc.Publish(new[] { env });
            return Results.Ok(new { ok = true });
        });
        test.MapPost("/seed-pvz-stats-demo", async (HttpRequest req, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            long? bodyPlayerId = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("playerId", out var p) && p.TryGetInt64(out var parsed))
                    bodyPlayerId = parsed;
            }
            catch { /* empty body ok */ }
            var targetId = bodyPlayerId ?? store.GetCurrentPlayerId();
            try
            {
                var sheet = store.SeedPvzStatsDemo(targetId);
                await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("PvzStatsUpdated", sheet);
                await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("PvzStatsUpdated", new { playerId = targetId, revision = sheet.Revision });
                inbox.Enqueue(new CommandDto
                {
                    Name = "pvz.stats.reload",
                    Payload = new { playerId = targetId, revision = sheet.Revision }
                });
                try
                {
                    await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("Command",
                        new CommandDto { Name = "pvz.stats.reload", Payload = new { playerId = targetId, revision = sheet.Revision } });
                }
                catch { /* inbox poll is reliable */ }
                return Results.Ok(sheet);
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound();
            }
        });
        test.MapPost("/seed-pvz-activity-demo", async (HttpRequest req, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            long? bodyPlayerId = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("playerId", out var p) && p.TryGetInt64(out var parsed))
                    bodyPlayerId = parsed;
            }
            catch { /* empty body ok */ }
            var targetId = bodyPlayerId ?? store.GetCurrentPlayerId();
            try
            {
                var rollup = store.SeedPvzActivityDemo(targetId);
                await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("PvzActivityUpdated", rollup);
                return Results.Ok(rollup);
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound();
            }
        });
        test.MapPost("/seed-rpg-progression-demo", async (HttpRequest req, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            long? bodyPlayerId = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("playerId", out var p) && p.TryGetInt64(out var parsed))
                    bodyPlayerId = parsed;
            }
            catch { /* empty body ok */ }
            var targetId = bodyPlayerId ?? store.GetCurrentPlayerId();
            try
            {
                var summary = store.SeedRpgProgressionDemo(targetId);
                await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("RpgProgressionUpdated", new
                {
                    playerId = targetId,
                    kind = "player",
                    typeId = 0,
                    revision = summary.Player?.Revision ?? 0
                });
                return Results.Ok(summary);
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound();
            }
        });
    }
}
