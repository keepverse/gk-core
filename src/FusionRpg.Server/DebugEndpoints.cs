using System.Reflection;
using System.Text.Json;
using FusionRpg.CheatCore;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

/// <summary>Server-side mirror of debug session (injector holds the live arms).</summary>
public static class DebugSessionState
{
    public static bool Active;
    public static string ScenarioId = "";
    public static readonly Dictionary<string, object?> Arms = new(StringComparer.OrdinalIgnoreCase);

    public static object Snapshot() => new
    {
        sessionActive = Active,
        scenarioId = ScenarioId,
        arms = Arms
    };
}

public static class DebugEndpoints
{
    /// <summary>E33 (spec-activation-edge.md §2.1): every `public const string` on <paramref name="t"/>,
    /// in declaration order — the source of truth `/effects/contract` publishes from, so the endpoint
    /// cannot drift from the class it names without a code change to <paramref name="t"/> itself.</summary>
    static string[] PublicConstStrings(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    public static void MapDebug(this WebApplication app)
    {
        var g = app.MapGroup("/api/debug");

        // Game Injector Debug
        g.MapPost("/session/start", async (JsonElement? body, EventIngest ingest, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            var scenarioId = b.TryGetProperty("scenarioId", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!
                : Guid.NewGuid().ToString("N")[..12];
            DebugSessionState.Active = true;
            DebugSessionState.ScenarioId = scenarioId;
            DebugSessionState.Arms.Clear();
            ingest.Enqueue(new EventEnvelope
            {
                T = ServerClock.UtcNowDateTime.ToString("o"),
                Kind = "debug.session.start",
                Payload = new Dictionary<string, object> { ["scenarioId"] = scenarioId }
            });
            await Send(hub, inbox, "debug.session", new { op = "start", scenarioId });
            return Results.Ok(new { ok = true, scenarioId });
        });

        g.MapPost("/session/end", async (EventIngest ingest, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var id = DebugSessionState.ScenarioId;
            DebugSessionState.Active = false;
            DebugSessionState.Arms.Clear();
            ingest.Enqueue(new EventEnvelope
            {
                T = ServerClock.UtcNowDateTime.ToString("o"),
                Kind = "debug.session.end",
                Payload = new Dictionary<string, object> { ["scenarioId"] = id }
            });
            await Send(hub, inbox, "debug.session", new { op = "end" });
            DebugSessionState.ScenarioId = "";
            return Results.Ok(new { ok = true });
        });

        // RPG Server Debug
        // (in-memory server mirror only, no injector relay and no RpgStore read -- ambiguous by
        // the guard's own rule, reads DebugSessionState which is never live-game state)
        g.MapGet("/session", () => Results.Ok(DebugSessionState.Snapshot()));

        // RPG Server Debug
        // (spec-budget-calibration-report.md contract 4.) The reading this module exists for: it runs
        // the REAL store read over whatever catalog the server imported (`ListActionPricing`) and prices
        // it through the SAME helper A-G1's catalog check uses (ST4.1), so it calibrates that check
        // rather than a second pricing of its own. RPG Server Debug scope per live-probe-standard.md:
        // it must run the real domain path against real rows, and the answer is only provenance for
        // those rows -- it writes nothing and fabricates nothing.
        g.MapGet("/action-budget-report", (RpgStore store) =>
        {
            var table = RungPolicy.Table;
            var report = BudgetCalibration.Read(store.ListActionPricing(), table);
            return Results.Ok(new
            {
                kind = "action-budget-report",
                cap = table.Cap,
                pricedActionCount = report.PricedActionCount,
                recommendedReferencePower = report.RecommendedReferencePower,
                // ST4.5f: the argmax, not just the max. `aboveP90` is empty on every rung with fewer
                // than ten actions by construction, so on the real corpus it cannot name the action
                // that sets the scalar -- this can, and always does.
                recommendedBy = report.RecommendedBy,
                // ST4.5e: the actions whose price left atoms out, named -- so a 0 in the rungs above can
                // be told apart from "this action is worth nothing". Read-only, same helper.
                unpricedActions = report.UnpricedActions.Select(u => new
                {
                    actionId = u.ActionId,
                    rung = u.AuthoredRung,
                    atomIds = u.AtomIds,
                }),
                rungs = report.Rungs.Select(r => new
                {
                    rung = r.Rung,
                    n = r.Count,
                    min = r.Min,
                    p50 = r.P50,
                    p90 = r.P90,
                    max = r.Max,
                    poolRolls = r.PoolRolls,
                    qPowerMilli = r.QPowerMilli,
                    powerBudgetMilli = r.PowerBudgetMilli,
                    aboveLoadedScalar = r.ActionsAboveLoadedScalar,
                    // ST4.5b (contract 6): the outliers the tuning change description has to name by
                    // id, so an action that drags the scalar up is visible rather than silent.
                    aboveP90 = r.ActionsAboveP90,
                }),
            });
        });
        // Game Injector Debug
        g.MapGet("/snapshot", async (IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            await Send(hub, inbox, "debug.snapshot", new { });
            return Results.Ok(new
            {
                ok = true,
                server = DebugSessionState.Snapshot(),
                note = "injector emits debug.snapshot (nested match = MatchSnapshot observe); poll GET /api/debug/events?kinds=debug.snapshot"
            });
        });

        // Game Injector Debug
        g.MapPost("/screenshot", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            var tag = SanitizeScreenshotTag(StrProp(b, "tag"));
            await Send(hub, inbox, "debug.screenshot", new { tag });
            return Results.Ok(new
            {
                ok = true,
                tag,
                afterId = store.GetMaxEventId(),
                note = "injector emits debug.screenshot.ready (with stored fileName); poll GET /api/debug/events?kinds=debug.screenshot.ready then GET /api/debug/screenshot/latest"
            });
        });

        // Game Injector Debug
        g.MapPost("/census", async (IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            await Send(hub, inbox, "debug.census", new { });
            return Results.Ok(new
            {
                ok = true,
                note = "injector emits debug.census (counts + samples per roster type); poll GET /api/debug/events?kinds=debug.census"
            });
        });

        // Game Injector Debug
        g.MapPost("/scan", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.scan", new
            {
                type = StrProp(b, "type") ?? "",
                limit = IntProp(b, "limit", 200),
                cursor = StrProp(b, "cursor")
            });
            return Results.Ok(new
            {
                ok = true,
                note = "injector emits debug.scan (full dump of one roster type, capped); poll GET /api/debug/events?kinds=debug.scan"
            });
        });

        // Game Injector Debug
        g.MapPost("/evaluate-search", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.evaluate-search", new
            {
                nameContains = StrProp(b, "nameContains") ?? "",
                type = StrProp(b, "type") ?? "",
                pathContains = StrProp(b, "pathContains") ?? "",
                limit = IntProp(b, "limit", 50),
                tag = StrProp(b, "tag") ?? ""
            });
            return Results.Ok(new
            {
                ok = true,
                afterId = store.GetMaxEventId(),
                note = "injector emits debug.evaluate.result (read-only matches with ptrs); poll GET /api/debug/events?kinds=debug.evaluate.result"
            });
        });

        // Game Injector Debug
        g.MapPost("/evaluate-methods", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.evaluate-methods", new { ptr = StrProp(b, "ptr") ?? "", tag = StrProp(b, "tag") ?? "" });
            return Results.Ok(new
            {
                ok = true,
                afterId = store.GetMaxEventId(),
                note = "injector emits debug.evaluate.methods (read-only method tables); poll GET /api/debug/events?kinds=debug.evaluate.methods"
            });
        });

        // Game Injector Debug
        g.MapPost("/evaluate-call", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.evaluate-call", new
            {
                ptr = StrProp(b, "ptr") ?? "",
                type = StrProp(b, "type") ?? "",
                method = StrProp(b, "method") ?? "",
                args = b.TryGetProperty("args", out var ae) && ae.ValueKind == JsonValueKind.Array
                    ? ae : (JsonElement?)null,
                tag = StrProp(b, "tag") ?? ""
            });
            return Results.Ok(new
            {
                ok = true,
                afterId = store.GetMaxEventId(),
                note = "injector invokes one public instance method on the resolved component; poll GET /api/debug/events?kinds=debug.evaluate.called"
            });
        });

        // Game Injector Debug
        g.MapPost("/evaluate-text", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.evaluate-text", new
            {
                text = StrProp(b, "text") ?? "",
                limit = IntProp(b, "limit", 50),
                tag = StrProp(b, "tag") ?? ""
            });
            return Results.Ok(new
            {
                ok = true,
                afterId = store.GetMaxEventId(),
                note = "injector matches engine text content and resolves clickables; poll GET /api/debug/events?kinds=debug.evaluate.text"
            });
        });

        // Game Injector Debug
        // (DISRUPTIVE surface: moves the operator's real cursor. Banner names it. The
        // opt-in lives per command — no persistent armed state anywhere.)
        g.MapPost("/cursor", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.cursor", new
            {
                x = IntProp(b, "x", -1),
                y = IntProp(b, "y", -1),
                click = !(b.TryGetProperty("click", out var cl) && cl.ValueKind == JsonValueKind.False),
                confirmedLiveCursor = b.TryGetProperty("confirmedLiveCursor", out var cf) && cf.ValueKind == JsonValueKind.True,
                tag = StrProp(b, "tag") ?? ""
            });
            return Results.Ok(new
            {
                ok = true,
                afterId = store.GetMaxEventId(),
                note = "injector moves/clicks the real cursor on opt-in, foreground-checked, throttled; poll GET /api/debug/events?kinds=debug.cursor.done"
            });
        });

        // Game Injector Debug
        g.MapPost("/act", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.act", new
            {
                verb = StrProp(b, "verb") ?? "",
                typeId = IntProp(b, "typeId", -1),
                col = IntProp(b, "col", -1),
                row = IntProp(b, "row", -1),
                tag = StrProp(b, "tag") ?? ""
            });
            return Results.Ok(new
            {
                ok = true,
                afterId = store.GetMaxEventId(),
                note = "injector runs the verb and emits debug.act.done with the expected telemetry kind; poll GET /api/debug/events?kinds=debug.act.done"
            });
        });

        // Game Injector Debug
        g.MapPost("/click", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.click", new
            {
                snapshotId = StrProp(b, "snapshotId") ?? "",
                @ref = StrProp(b, "ref") ?? "",
                tag = StrProp(b, "tag") ?? ""
            });
            return Results.Ok(new
            {
                ok = true,
                afterId = store.GetMaxEventId(),
                note = "injector re-verifies the ref and invokes; poll GET /api/debug/events?kinds=debug.click.done"
            });
        });

        // Game Injector Debug
        g.MapPost("/dump-all", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.dump-all", new { tag = StrProp(b, "tag") ?? "probe" });
            return Results.Ok(new
            {
                ok = true,
                note = "injector enumerates EVERY GameObject to a side-store JSON file; poll GET /api/debug/events?kinds=debug.dump.done then GET /api/debug/dump/latest"
            });
        });

        // Game Injector Debug
        // (raw-dump upload half: JSON side-store write, no relay in body, no domain write —
        // guard lists it as ManualReview by design, same as the screenshot upload.)
        g.MapPost("/dump/upload", async (JsonElement? body) =>
        {
            var b = BodyOrEmpty(body);
            var tag = SanitizeScreenshotTag(StrProp(b, "tag"));
            var json = StrProp(b, "json") ?? "";
            // Structural (not tunable): rejects garbage before buffering. Not the balance surface.
            const int dumpMaxChars = 32 * 1024 * 1024;
            if (json.Length == 0 || json.Length > dumpMaxChars)
                return Results.BadRequest(new { ok = false, error = "empty or over size cap" });
            var dir = DumpStoreDir();
            Directory.CreateDirectory(dir);
            var fileName = $"{ServerClock.UtcNowDateTime:yyyyMMdd-HHmmssfff}-{tag}.json";
            await File.WriteAllTextAsync(Path.Combine(dir, fileName), json);
            var info = new { fileName, tag, chars = json.Length, takenAtUtc = ServerClock.UtcNowDateTime.ToString("o") };
            await File.WriteAllTextAsync(Path.Combine(dir, "latest.json"), JsonSerializer.Serialize(info));
            PruneDumps(dir);
            return Results.Ok(new { ok = true, fileName, tag, chars = json.Length });
        });

        // Game Injector Debug
        // (reads of the raw-dump side store. Same ManualReview note as above.)
        g.MapGet("/dump/latest", () =>
        {
            var dir = DumpStoreDir();
            var latestPath = Path.Combine(dir, "latest.json");
            if (!File.Exists(latestPath))
                return Results.NotFound(new { ok = false, error = "no dump stored yet" });
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(latestPath));
                if (!doc.RootElement.TryGetProperty("fileName", out var f) ||
                    f.ValueKind != JsonValueKind.String)
                    return Results.NotFound(new { ok = false, error = "latest pointer is corrupt" });
                var path = Path.Combine(dir, f.GetString()!);
                if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    return Results.NotFound(new { ok = false, error = "latest file is missing" });
                return Results.File(File.ReadAllBytes(path), "application/json");
            }
            catch
            {
                return Results.NotFound(new { ok = false, error = "latest pointer is unreadable" });
            }
        });

        // Game Injector Debug
        // (metadata read for the file above. Same ManualReview note as above.)
        g.MapGet("/dump/info", () =>
        {
            var latestPath = Path.Combine(DumpStoreDir(), "latest.json");
            if (!File.Exists(latestPath))
                return Results.NotFound(new { ok = false, error = "no dump stored yet" });
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(latestPath));
                return Results.Ok(doc.RootElement.Clone());
            }
            catch
            {
                return Results.BadRequest(new { ok = false, error = "latest pointer is unreadable" });
            }
        });

        // Game Injector Debug
        g.MapPost("/inspect", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            var scope = InspectScopes.Normalize(StrProp(b, "scope"));
            await Send(hub, inbox, "debug.inspect", new
            {
                scope,
                limit = IntProp(b, "limit", 50),
                cursor = StrProp(b, "cursor"),
                tag = StrProp(b, "tag")
            });
            return Results.Ok(new
            {
                ok = true,
                scope,
                afterId = store.GetMaxEventId(),
                note = "injector emits debug.inspect (snapshot id = event id); poll GET /api/debug/events?kinds=debug.inspect"
            });
        });

        // Game Injector Debug
        // (upload half of the screenshot pair: no Send() relay call in this body and no
        // RpgStore/domain write — it stores an opaque engine artifact — so the scope guard
        // lists it as ManualReview by design. The scope is declared here for human readers:
        // this proves only live-engine state, never server correctness.)
        g.MapPost("/screenshot/upload", async (HttpRequest request, string? tag) =>
        {
            var body = request.Body;
            if (request.ContentType == null ||
                !request.ContentType.StartsWith("image/png", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { ok = false, error = "expected Content-Type: image/png" });
            byte[] png;
            using (var ms = new MemoryStream())
            {
                await body.CopyToAsync(ms);
                png = ms.ToArray();
            }
            if (png.Length is 0 or > ScreenshotMaxBytes || !HasPngSignature(png))
                return Results.BadRequest(new { ok = false, error = "not a PNG or over size cap" });
            var cleanTag = SanitizeScreenshotTag(tag);
            var dir = ScreenshotStoreDir();
            Directory.CreateDirectory(dir);
            var fileName = $"{ServerClock.UtcNowDateTime:yyyyMMdd-HHmmssfff}-{cleanTag}.png";
            await File.WriteAllBytesAsync(Path.Combine(dir, fileName), png);
            var info = new
            {
                fileName,
                tag = cleanTag,
                bytes = png.Length,
                takenAtUtc = ServerClock.UtcNowDateTime.ToString("o")
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "latest.json"), JsonSerializer.Serialize(info));
            PruneScreenshots(dir);
            return Results.Ok(new { ok = true, fileName, tag = cleanTag, bytes = png.Length });
        });

        // Game Injector Debug
        // (read half: serves the opaque engine artifact above. Same ManualReview note as PUT.)
        g.MapGet("/screenshot/latest", () =>
        {
            var dir = ScreenshotStoreDir();
            var latestPath = Path.Combine(dir, "latest.json");
            if (!File.Exists(latestPath))
                return Results.NotFound(new { ok = false, error = "no screenshot stored yet" });
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(latestPath));
                if (!doc.RootElement.TryGetProperty("fileName", out var f) ||
                    f.ValueKind != JsonValueKind.String)
                    return Results.NotFound(new { ok = false, error = "latest pointer is corrupt" });
                var path = Path.Combine(dir, f.GetString()!);
                if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    return Results.NotFound(new { ok = false, error = "latest file is missing" });
                return Results.File(File.ReadAllBytes(path), "image/png");
            }
            catch
            {
                return Results.NotFound(new { ok = false, error = "latest pointer is unreadable" });
            }
        });

        // Game Injector Debug
        // (metadata read for the file above. Same ManualReview note as PUT.)
        g.MapGet("/screenshot/info", () =>
        {
            var latestPath = Path.Combine(ScreenshotStoreDir(), "latest.json");
            if (!File.Exists(latestPath))
                return Results.NotFound(new { ok = false, error = "no screenshot stored yet" });
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(latestPath));
                return Results.Ok(doc.RootElement.Clone());
            }
            catch
            {
                return Results.BadRequest(new { ok = false, error = "latest pointer is unreadable" });
            }
        });

        // RPG Server Debug
        // Real bug fixed live 2026-09-14 (lawn-combat-wire T0): `kinds` used to filter the result of
        // an already-`limit`-truncated unfiltered read, so a caller relying on this endpoint's own
        // documented usage (`afterId` defaulting to 0, e.g. `/snapshot`'s returned `note`) always saw
        // the events table's OLDEST `limit` rows -- on a long-running dev server (measured live at
        // 300,000+ rows) that is never going to contain a just-emitted `debug.snapshot`. The kind
        // filter now runs inside the SQL query (RpgStore.ListEventsByKinds), so `limit` bounds the
        // matching population, not the whole table.
        g.MapGet("/events", (RpgStore store, int limit = 200, long afterId = 0, string? kinds = null, string? scenarioId = null) =>
        {
            var clampedLimit = Math.Clamp(limit, 1, 500);
            List<EventEnvelope> items;
            if (!string.IsNullOrWhiteSpace(kinds))
            {
                var kindSet = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                items = store.ListEventsByKinds(clampedLimit, afterId, kindSet);
            }
            else
            {
                items = store.ListEvents(clampedLimit, afterId);
            }
            if (!string.IsNullOrWhiteSpace(scenarioId))
            {
                items = items.Where(e => PayloadHasScenario(e.Payload, scenarioId)).ToList();
            }
            return Results.Ok(new { items });
        });

        // Game Injector Debug
        // Game Injector Debug: leave a live lawn the player's way (menu -> main menu -> confirm) and wait for
        // the injector's ack, which fires only once the Board is destroyed. Replaces ui-nav back-to-menu for
        // board exits (that direct call left the Board alive, 2026-09-15).
        g.MapPost("/leave-board", async (RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            if (!store.InjectorConnected)
                return Results.Conflict(new { ok = false, error = "injector not connected — start the game with the FusionRpg injector loaded" });
            const int timeoutSec = 30; // structural acknowledgement wait, not a balance value
            var before = store.GetMaxEventId();
            await Send(hub, inbox, "debug.leave-board", new { });
            var ack = await PollForKind(store, before, "debug.leave-board", TimeSpan.FromSeconds(timeoutSec));
            if (ack is null)
                return Results.Conflict(new { ok = false, error = $"debug.leave-board did not ack within {timeoutSec}s" });
            return PayloadBool(ack.Payload, "ok")
                ? Results.Ok(new { ok = true, acknowledgement = ack.Payload })
                : Results.Conflict(new { ok = false, error = PayloadString(ack.Payload, "error") ?? "leave-board failed", acknowledgement = ack.Payload });
        });

        g.MapPost("/setup/skip", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            var method = (StrProp(b, "method") ?? "button").Trim().ToLowerInvariant();
            if (method is not ("quick" or "button"))
                return Results.BadRequest(new { ok = false, error = "unknown method — expected quick or button" });

            if (!store.InjectorConnected)
                return Results.Conflict(new { ok = false, error = "injector not connected — start the game with the FusionRpg injector loaded" });

            // structural acknowledgement wait, not a balance value: the injector now acks only once the
            // seed-picker has actually been pressed and InitBoard.ready observed (up to 30s + 10s).
            const int defaultTimeoutSec = 45;
            var timeoutSec = IntProp(b, "timeoutSec", defaultTimeoutSec);
            var before = store.GetMaxEventId();
            // Forward the optional seed-packet pick list and probe flag verbatim (injector DebugActions.SkipSetup).
            object? cards = b.TryGetProperty("cards", out var cardsEl) && cardsEl.ValueKind == JsonValueKind.Array
                ? cardsEl.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToArray()
                : null;
            var waitForBoard = !(b.TryGetProperty("waitForBoard", out var wfb) && wfb.ValueKind == JsonValueKind.False);
            await Send(hub, inbox, "debug.skip-setup", cards is null
                ? new { method, waitForBoard }
                : (object)new { method, waitForBoard, cards });
            var ack = await PollForKind(store, before, "debug.setup.skip", TimeSpan.FromSeconds(timeoutSec));
            if (ack is null)
                return Results.Conflict(new { ok = false, method, error = $"debug.setup.skip did not ack within {timeoutSec}s" });

            var ok = PayloadBool(ack.Payload, "ok");
            if (!ok)
                return Results.Conflict(new
                {
                    ok = false,
                    method,
                    error = PayloadString(ack.Payload, "error") ?? "injector refused setup skip"
                });

            return Results.Ok(new { ok = true, method, acknowledgement = ack.Payload });
        });

        // Game Injector Debug
        // The unified "what is current game state, right now" probe (2026-09-14). Unlike
        // /lawn/state (RpgServerDebug, reconstructs a best guess from the event log -- fragile by
        // construction, see lawn-run-state-machine.md), this ACTIVELY asks the game to read its own
        // live objects (Board.Instance / InitBoard.Instance / GameAPP.theBoardType / the injector's own
        // MatchPhase FSM) and reports exactly what it found, synchronously, no history involved. Use
        // this when the injector is connected and you need ground truth; fall back to /lawn/state when
        // it is not (or when you need "since when" duration context this probe does not carry).
        g.MapPost("/game-state", async (RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            if (!store.InjectorConnected)
                return Results.Conflict(new { ok = false, error = "injector not connected — start the game with the FusionRpg injector loaded" });

            const int timeoutSec = 10; // structural acknowledgement wait, not a balance value
            var before = store.GetMaxEventId();
            await Send(hub, inbox, "debug.game-state", new { });
            var ack = await PollForKind(store, before, "debug.game-state", TimeSpan.FromSeconds(timeoutSec));
            if (ack is null)
                return Results.Conflict(new { ok = false, error = $"debug.game-state did not ack within {timeoutSec}s" });

            return Results.Ok(new { ok = true, live = ack.Payload });
        });

        // Game Injector Debug
        // Calls one real UIMgr static navigation method, chosen by name (see DebugActions.UiNav for
        // the full action list -- back-to-menu, enter-main-menu, back-to-game, etc.). Added
        // 2026-09-14 after debug.enter-level(force:true) never acked live against a defeated-but-
        // still-alive Board. The first fix attempt (a single hard-coded BackToMenu call) proved live
        // that this game's menu stack is not flat -- BackToMenu landed on the previous menu layer
        // (Challenge Mode select), not the true main menu -- so the real fix is exposing every real
        // navigation entry point and finding the working sequence live, not guessing one.
        g.MapPost("/ui-nav", async (JsonElement body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            if (!store.InjectorConnected)
                return Results.Conflict(new { ok = false, error = "injector not connected — start the game with the FusionRpg injector loaded" });

            var action = PayloadString(body, "action");
            if (string.IsNullOrWhiteSpace(action))
                return Results.BadRequest(new { ok = false, error = "action is required" });

            const int timeoutSec = 10; // structural acknowledgement wait, not a balance value
            var before = store.GetMaxEventId();
            await Send(hub, inbox, "debug.ui-nav", new { action });
            var ack = await PollForKind(store, before, "debug.ui-nav", TimeSpan.FromSeconds(timeoutSec));
            if (ack is null)
                return Results.Conflict(new { ok = false, error = $"debug.ui-nav did not ack within {timeoutSec}s" });

            return Results.Ok(new { ok = true, result = ack.Payload });
        });

        // RPG Server Debug
        // (in-memory catalog read only, no injector relay and no RpgStore read)
        g.MapGet("/scenarios", () => Results.Ok(new { items = DebugScenarios.AllIds }));

        // Game Injector Debug
        // every route below through /effects/reload relays to the Injector somewhere in its body
        // (some also do real store/session bookkeeping alongside the relay -- legitimate
        // orchestration, not a violation; see spec-debug-scope-guard.md).
        g.MapPost("/scenario/{id}", async (string id, JsonElement? body, EventIngest ingest, IHubContext<RpgHub> hub, InjectorCommandInbox inbox, EffectGrantSession grants) =>
        {
            var b = BodyOrEmpty(body);
            var scenarioId = b.ValueKind == JsonValueKind.Object
                             && b.TryGetProperty("scenarioId", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!
                : (DebugSessionState.Active ? DebugSessionState.ScenarioId : Guid.NewGuid().ToString("N")[..12]);
            try
            {
                var steps = DebugScenarios.Expand(id, scenarioId);
                DebugSessionState.Active = true;
                DebugSessionState.ScenarioId = scenarioId;
                EffectGrantSessionRecorder.ApplyDebugSteps(
                    grants,
                    steps.Select(st => (st.Name, (object?)st.Payload)));
                ingest.Enqueue(new EventEnvelope
                {
                    T = ServerClock.UtcNowDateTime.ToString("o"),
                    Kind = "debug.scenario.start",
                    Payload = new Dictionary<string, object> { ["id"] = id, ["scenarioId"] = scenarioId, ["steps"] = steps.Count }
                });
                // P0: one injector command; steps run sequentially on Unity main-thread drain.
                await Send(hub, inbox, "debug.run-steps", new
                {
                    scenarioId,
                    id,
                    steps = steps.Select(st => new { name = st.Name, payload = st.Payload }).ToList()
                });
                return Results.Ok(new { ok = true, id, scenarioId, steps = steps.Count, command = "debug.run-steps" });
            }
            catch (ArgumentException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // RPG Server Debug
        // Read-only, no injector relay, no side effects.
        // Answers exactly the question a live-probe session must never guess or eyeball: what state
        // is the lawn actually in, and since when. Built 2026-09-14 after a real incident: an agent
        // read a `/lawn/quick-start` { ok: true } response with real ptrs and declared the board
        // recovered, while the operator was looking at a still-showing defeat screen. Both were
        // "right" about different layers -- the simulation had moved on, the screen had not -- and
        // there was no single query that could have said so instead of one side privately eyeballing
        // the game and the other reading an HTTP body. This endpoint is that query.
        // Full model: docs/architecture/live-probe/lawn-run-state-machine.md. Six states by strict
        // precedence (Cycling > Defeated/Victorious > InMatch > LevelEntryPending > Unknown), built
        // from every real lifecycle signal the injector emits -- not the three ad hoc ones the first
        // version of this endpoint shipped with, which is what produced a confusing "Unknown" read
        // against a genuinely defeated board on 2026-09-14. Read that doc before changing this.
        g.MapGet("/lawn/state", (RpgStore store) =>
        {
            var now = DateTime.UtcNow;
            DateTime? ParseT(EventEnvelope? e) =>
                e is not null && DateTime.TryParse(e.T, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

            var recentBoardEnds = CountRecentEventsOfKind(store, "board.end", TimeSpan.FromSeconds(CyclingWindowSec));

            // Real bug found live 2026-09-14: a fresh game process (new injector.hello) sitting idle
            // at the main menu read as "Defeated", because the classifier read a match.result event
            // from a PREVIOUS, already-dead game process -- nothing invalidated it. Same fix
            // FindLatestLiveBoardStart already applies to board.start: any lifecycle signal older
            // than the newest injector.hello belongs to a process that is gone and must not describe
            // the current one. Compare by event Id (monotonic), not by parsed timestamp.
            var latestHello = FindLatestKind(store, "injector.hello");
            EventEnvelope? DiscardIfBeforeHello(EventEnvelope? ev) =>
                ev is not null && latestHello is not null && ev.Id < latestHello.Id ? null : ev;

            var latestMatchResult = DiscardIfBeforeHello(FindLatestKind(store, "match.result"));
            var latestMatchLose = DiscardIfBeforeHello(FindLatestKind(store, "match.lose"));
            var latestMatchWin = DiscardIfBeforeHello(FindLatestKind(store, "match.win"));
            var latestBoardEconomy = DiscardIfBeforeHello(FindLatestKind(store, "board.economy"));
            var latestCatalogZombies = DiscardIfBeforeHello(FindLatestKind(store, "catalog.zombies"));

            var resultTime = ParseT(latestMatchResult);
            var loseTime = ParseT(latestMatchLose);
            var winTime = ParseT(latestMatchWin);
            var economyTime = ParseT(latestBoardEconomy);
            var catalogTime = ParseT(latestCatalogZombies);
            var resultValue = latestMatchResult is not null ? PayloadString(latestMatchResult.Payload, "result") : null;
            var resultIsDefeat = string.Equals(resultValue, "defeat", StringComparison.OrdinalIgnoreCase);

            // match.result's own result string is cross-checked against the two unambiguous pulse
            // events (match.lose/match.win carry no data but cannot be misread) -- whichever real
            // signal is most recent decides, never "does a terminal event exist anywhere in history".
            var terminalCandidates = new List<(DateTime Time, EventEnvelope Ev, string State)>();
            if (resultTime is { } rt) terminalCandidates.Add((rt, latestMatchResult!, resultIsDefeat ? "Defeated" : "Victorious"));
            if (loseTime is { } lt) terminalCandidates.Add((lt, latestMatchLose!, "Defeated"));
            if (winTime is { } wt) terminalCandidates.Add((wt, latestMatchWin!, "Victorious"));
            var terminal = terminalCandidates.Count > 0
                ? terminalCandidates.OrderByDescending(c => c.Time).First()
                : ((DateTime Time, EventEnvelope Ev, string State)?)null;

            string state;
            EventEnvelope? decidingEvent;
            if (recentBoardEnds >= CyclingBoardEndMinCount)
            {
                state = "Cycling";
                decidingEvent = latestBoardEconomy;
            }
            else if (terminal is { } t && (economyTime is null || t.Time > economyTime))
            {
                // The newest board-lifecycle signal is a terminal result with nothing newer proving a
                // fresh board exists since -- the board is in whatever post-match state the game left
                // it in (debug.reset-board can restore API-level spawning, never the game's own visual
                // overlay -- see the note below).
                state = t.State;
                decidingEvent = t.Ev;
            }
            else if (economyTime is not null && now - economyTime < TimeSpan.FromSeconds(30))
            {
                state = "InMatch";
                decidingEvent = latestBoardEconomy;
            }
            else if (catalogTime is not null
                && (economyTime is null || catalogTime > economyTime)
                && (terminal is null || catalogTime > terminal.Value.Time))
            {
                // catalog.zombies fires while the level's zombie list is being initialized, BEFORE
                // Board.Awake -- the earliest real signal a level entry has begun, likely (not
                // certain -- see the doc) the seed-picker screen. Medium confidence, named as such.
                state = "LevelEntryPending";
                decidingEvent = latestCatalogZombies;
            }
            else
            {
                // No recent signal of any kind: could be the main menu, the seed-picker screen, a
                // paused match, or a frozen/crashed injector -- all identical from here. This state is
                // genuinely ambiguous from passive telemetry alone; say so rather than guess.
                state = "Unknown";
                decidingEvent = latestBoardEconomy ?? latestCatalogZombies ?? latestMatchResult;
            }

            var decidingTime = ParseT(decidingEvent);
            return Results.Ok(new
            {
                state,
                asOf = decidingEvent?.T,
                sinceMs = decidingTime is { } dt ? (long?)(now - dt).TotalMilliseconds : null,
                recentBoardEnds,
                latestMatchResult = resultValue,
                injectorConnected = store.InjectorConnected,
                note = "API/simulation state only. Does NOT confirm what is rendered on screen -- a " +
                    "defeat/victory overlay can persist after debug.reset-board clears entities, and " +
                    "state=\"Unknown\" cannot distinguish the main menu, the seed-picker screen, or a " +
                    "paused match (no passive event fires for any of them; only an active POST " +
                    "/api/debug/setup/skip probe can partially disambiguate, and it has a side effect). " +
                    "When the question is what a human sees, ask the human -- this answers what the " +
                    "simulation has recorded, never a substitute for looking at the actual game. Full " +
                    "model: docs/architecture/live-probe/lawn-run-state-machine.md."
            });
        });

        // Game Injector Debug
        g.MapPost("/lawn/quick-start", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox, EffectGrantSession grants) =>
        {
            var b = BodyOrEmpty(body);
            var levelNumber = IntProp(b, "levelNumber", 1);
            var scenarioId = StrProp(b, "scenario") ?? "lab-overlay";
            var timeoutSec = IntProp(b, "timeoutSec", 45);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Reject an unknown scenario before any wait or game command (it used to surface only after
            // the setup-skip wait, and a skip failure now ends the request before the scenario runs).
            try { DebugScenarios.Expand(scenarioId, "validate"); }
            catch (ArgumentException ex) { return Results.NotFound(new { ok = false, error = ex.Message }); }

            if (!store.InjectorConnected)
                return Results.Conflict(new { ok = false, error = "injector not connected — start the game with the FusionRpg injector loaded" });

            // Observability gap found live 2026-09-14: a board stuck in a rapid match-end/retry loop
            // (e.g. a "quick" setup-skip with no real plants placed, so every wave is lost instantly)
            // produces a confusing "debug.level.enter did not ack" timeout with nothing in the response
            // pointing at the real cause -- diagnosing it required manually diffing thousands of raw
            // events by hand. Detect the loop directly and fail fast and loud instead of polling into
            // it blind. Threshold is a structural safety check, not a balance value: real gameplay does
            // not lose 3 matches in 10 seconds.
            var recentBoardEnds = CountRecentEventsOfKind(store, "board.end", TimeSpan.FromSeconds(CyclingWindowSec));
            if (recentBoardEnds >= CyclingBoardEndMinCount)
                return Results.Conflict(new
                {
                    ok = false,
                    error = $"board is cycling ({recentBoardEnds} board.end events in the last {CyclingWindowSec}s) — " +
                        "the match is likely repeatedly ending with no real plants placed; check the game or place real plants before retrying",
                    recentBoardEnds,
                    waitedMs = sw.ElapsedMilliseconds
                });

            // Real bug found live 2026-09-14: after a real defeat (match.result payload
            // result:"defeat", GameHooks.cs's BoardStatistics.GameOver hook), the old plan here was
            // debug.reset-board (DeleteAllPlants+DeleteAllZombies) -- proven live to restore
            // API-level spawn capability, but the operator confirmed the game's own visual "重新开始"
            // (restart) overlay stayed up regardless: reset-board clears entities on the SAME dead
            // board, it never leaves it. So on a detected defeat this now skips the mid-entry probe
            // (we KNOW the old board is dead, not a fresh seed-picker) and forces straight into the
            // enter-level branch instead.
            //
            // Real bug found live 2026-09-14 (third one, same afternoon): forcing debug.enter-level
            // (force:true bypasses EnterLevel's "board already live" guard entirely) STRAIGHT over the
            // dead board never acked, twice (once via this endpoint, once via a direct manual retry) --
            // consistent with the already-documented hazard that forced entry against a live board can
            // destabilize the engine. The fix is not to force through a live board at all: call the
            // real UIMgr.BackToMenu() first (DebugActions.ExitToMenu, the same static entry point the
            // game's own pause/lose menu buttons call, already Harmony-hooked to emit menu.enter) so the
            // board is actually torn down, THEN a normal (non-forced) enter-level lands cleanly on the
            // seed-picker exactly like a first launch.
            // Real bug found live 2026-09-14 (second one, same afternoon): using injector.hello to
            // guard this was WRONG for an active recovery decision -- restarting the SERVER (not the
            // game) mints a fresh hello for the SAME still-running game, which discarded a genuinely
            // current defeat as if it belonged to a dead process, and quick-start then treated the
            // dead board as live instead of recovering it. The right question is not "did the process
            // restart" but "is this defeat still the most recent word on the board" -- the same
            // newest-signal-wins rule /lawn/state's classifier already gets right (see
            // BoardEconomyAfterDefeat_reportsInMatch_defeatIsStale). Only board.economy proves the
            // board moved on since; nothing did here, so the defeat stands.
            var latestResult = FindLatestKind(store, "match.result");
            var latestEconomyForDefeat = FindLatestKind(store, "board.economy");
            var defeatDetected = latestResult is not null
                && string.Equals(PayloadString(latestResult.Payload, "result"), "defeat", StringComparison.OrdinalIgnoreCase)
                && (latestEconomyForDefeat is null || latestResult.Id > latestEconomyForDefeat.Id);
            var defeatReset = defeatDetected; // reported field name kept; meaning is now "forced a fresh entry", not "called reset-board"

            // Some game profiles never emit board.start for a board that already exists behind the
            // seed-picker screen -- confirmed live 2026-09-14 on pvzrh-3.9: several full match cycles
            // (board.end fired repeatedly), zero board.start events, ever. So a board sitting on the
            // seed-picker (Board not yet constructed) is invisible to FindLatestLiveBoardStart, and
            // debug.enter-level's own live-board guard (which checks the Board reference) never
            // triggers either -- calling EnterGame a second time on an already-mid-entry level is
            // undefined by the vanilla game and was observed to time out with NO debug.level.enter
            // event at all, not even a rejection. Probe for this state directly instead of guessing:
            // debug.skip-setup is spec-sanctioned to call repeatedly (spec-setup-skip.md: "no
            // automatic double-call fallback... repeated calls are allowed"), so try it before
            // enter-level. Success (`board:true`) proves a real Board already exists -- either
            // mid-seed-picker or already in a running match -- and lets quick-start skip straight to
            // wave-freeze/scenario instead of re-attempting an entry that will only time out.
            // Skipped entirely when defeatDetected: a defeated board is known-dead, not mid-entry, and
            // probing it would just waste a round trip before the forced re-entry below regardless.
            var alreadyMidEntry = false;
            if (!defeatDetected)
            {
                store.MergeCheatField("DEBUG-SETUP-SKIP", true, null);
                await Send(hub, inbox, "cheat.toggle", new { id = "DEBUG-SETUP-SKIP", enabled = true });
                var probeBeforeSkip = store.GetMaxEventId();
                // waitForBoard:false -- this is a probe: fail fast at the main menu instead of waiting.
                await Send(hub, inbox, "debug.skip-setup", new { method = "button", waitForBoard = false });
                var probeSkipAck = await PollForKind(store, probeBeforeSkip, "debug.setup.skip", TimeSpan.FromSeconds(Math.Min(timeoutSec, 8)));
                if (probeSkipAck is not null && PayloadBool(probeSkipAck.Payload, "ok")) alreadyMidEntry = true;
            }
            var setupSkipOk = false;
            var levelEnterAckMissing = false;

            var entered = false;
            var boardStart = (alreadyMidEntry || defeatDetected) ? null : FindLatestLiveBoardStart(store);
            string? enteredLevelType = null;

            if ((boardStart is null && !alreadyMidEntry) || defeatDetected)
            {
                store.MergeCheatField("DEBUG-LEVEL-ENTRY", true, null);
                await Send(hub, inbox, "cheat.toggle", new { id = "DEBUG-LEVEL-ENTRY", enabled = true });

                if (defeatDetected)
                {
                    var beforeExit = store.GetMaxEventId();
                    await Send(hub, inbox, "debug.ui-nav", new { action = "back-to-menu" });
                    var exitAckTimeoutSec = Math.Min(timeoutSec, 10);
                    var exitAck = await PollForKind(store, beforeExit, "debug.ui-nav", TimeSpan.FromSeconds(exitAckTimeoutSec));
                    if (exitAck is null)
                        return Results.Conflict(new { ok = false, error = $"debug.ui-nav (back-to-menu) did not ack within {exitAckTimeoutSec}s", waitedMs = sw.ElapsedMilliseconds, defeatReset });
                    if (!PayloadBool(exitAck.Payload, "ok"))
                        return Results.Conflict(new { ok = false, error = PayloadString(exitAck.Payload, "error") ?? "debug.ui-nav (back-to-menu) rejected", defeatReset });
                }

                var beforeEnter = store.GetMaxEventId();
                await Send(hub, inbox, "debug.enter-level", new { levelType = 0, levelNumber, id = 0, name = "" });

                var ackTimeoutSec = Math.Min(timeoutSec, 8); // short: the game-state fallback below covers a lost ack
                var enterAck = await PollForKind(store, beforeEnter, "debug.level.enter", TimeSpan.FromSeconds(ackTimeoutSec));
                if (enterAck is null)
                {
                    // 2026-09-15: observed live — the injector logged "debug.enter-level Advanture#1" and the level
                    // loaded, but that frame's debug.level.enter (and board.modifiers) events never reached the
                    // server while neighbouring events did. Confirm entry from the game's own live objects instead
                    // of failing a level that is actually open, and say the ack was missing.
                    var beforeCheck = store.GetMaxEventId();
                    await Send(hub, inbox, "debug.game-state", new { });
                    var check = await PollForKind(store, beforeCheck, "debug.game-state", TimeSpan.FromSeconds(Math.Min(timeoutSec, 10)));
                    if (check is null || !PayloadBool(check.Payload, "hasBoard"))
                        return Results.Conflict(new { ok = false, error = $"debug.level.enter did not ack within {ackTimeoutSec}s and game-state shows no board", waitedMs = sw.ElapsedMilliseconds, defeatReset });
                    levelEnterAckMissing = true;
                    enterAck = check;
                }

                var ackOk = PayloadBool(enterAck.Payload, "ok");
                if (!ackOk)
                {
                    var err = PayloadString(enterAck.Payload, "error") ?? "enter-level rejected";
                    if (!err.Contains("board already live", StringComparison.OrdinalIgnoreCase))
                        return Results.Conflict(new { ok = false, error = err });
                    // "board already live" is an explicit injector assertion. On cold starts the
                    // Board.Awake event can predate the server's current event window, so use the
                    // injector assertion and the latest catalog level metadata rather than inventing
                    // a board lifecycle row. The scenario snapshot below still proves the board.
                    boardStart = FindLatestLiveBoardStart(store, trustInjectorLiveBoard: true);
                    if (boardStart is null)
                        enteredLevelType = PayloadString(FindLatestKind(store, "catalog.zombies")?.Payload, "levelType");
                }
                else
                {
                    // Some game builds create the Board and begin spawning before the Board.Awake
                    // telemetry reaches the server. The injector's successful enter acknowledgement
                    // is still authoritative for the level type; keep waiting for board.start for
                    // lifecycle correlation, but do not reject a usable live board solely because
                    // that optional telemetry edge was missed.
                    enteredLevelType = PayloadString(enterAck.Payload, "levelType")
                        ?? PayloadString(enterAck.Payload, "theBoardTypeName"); // game-state fallback when the ack was lost
                    // Board.Awake telemetry is best-effort on cold starts; bound this optional wait so
                    // quick-start can continue from the authoritative enter acknowledgement instead
                    // of holding the HTTP request for the full scenario timeout.
                    boardStart = await PollForKind(store, beforeEnter, "board.start",
                        TimeSpan.FromSeconds(Math.Min(timeoutSec, 5)));
                    entered = true;
                }

                // The INJECTOR has just said a board is live, and it holds the actual Board object —
                // it outranks this server's event-log heuristic. So drop the session filter here:
                // a SERVER restart makes the injector re-Hello from the SAME game process with the
                // SAME live board, which would otherwise look "stale" to the session rule below and
                // 409 a perfectly good lawn. Found live 2026-08-30, immediately after the session rule
                // itself was added — the fix for one false positive created a false negative.
                //
                // Found live 2026-09-14: on this profile board.start never fires (see the mid-entry
                // probe comment above) AND the catalog.zombies fallback can come up empty too (a long
                // enough server session simply scrolls it out of FindLatestKind's lookback window) --
                // hard-refusing here left a REAL, injector-confirmed live board unusable. The injector
                // already told us the board is live; treat this exactly like the mid-entry probe's own
                // "board exists, levelType unresolvable" case instead of a second, inconsistent refusal
                // for the same underlying situation.
                if (boardStart is null && string.IsNullOrWhiteSpace(enteredLevelType))
                    alreadyMidEntry = true;
            }

            // alreadyMidEntry has no board.start / enter-level ack to read a levelType from (see the
            // probe comment above) -- the successful skip-setup probe is itself the live-board proof
            // in that case, so the Explore/Travel/IZ refusal below is skipped rather than guessed at.
            var levelType = alreadyMidEntry
                ? ""
                : (boardStart is null ? enteredLevelType ?? "" : PayloadString(boardStart.Payload, "levelType") ?? "");
            if (!alreadyMidEntry && BadLevelTypes.Contains(levelType))
                return Results.Conflict(new { ok = false, error = $"refusing lab on levelType={levelType} — open Adventure/Challenge day lawn, not Explore/Travel" });

            // EnterGame opens the level, but the real lawn (waves moving, plants/zombies acting) stays
            // behind the vanilla "Choose Your Plants" seed-picker screen (InitBoard/InGameUI) until that
            // screen is dismissed. debug.skip-setup (InitBoard.QuickInGame) is the sanctioned dismissal —
            // call it before any wave/scenario work so a fresh board is never left sitting on that
            // screen. The proactive probe above already did this when alreadyMidEntry is true; only
            // call it again for a level we just entered ourselves in this same request.
            if (!alreadyMidEntry)
            {
                var beforeSkip = store.GetMaxEventId();
                // 2026-09-15 fix: the old immediate QuickInGame fired before the picker had slid in and left
                // the level blocked. The injector now waits for the picker, presses its start button, and
                // acks only after InitBoard.ready -- so this wait covers level load + picker + start.
                await Send(hub, inbox, "debug.skip-setup", new { method = "button" });
                var skipAck = await PollForKind(store, beforeSkip, "debug.setup.skip", TimeSpan.FromSeconds(Math.Min(timeoutSec, 45)));
                setupSkipOk = skipAck is not null && PayloadBool(skipAck.Payload, "ok");
                // A board left behind the seed-picker has no game time and no usable seed bank -- never report
                // it as a ready lab (the owner-reported every-run failure, 2026-09-15).
                if (!setupSkipOk)
                    return Results.Conflict(new
                    {
                        ok = false,
                        error = skipAck is null
                            ? "debug.setup.skip did not ack — the level may still be on the seed-picker"
                            : PayloadString(skipAck.Payload, "error") ?? "seed-picker was not dismissed",
                        stage = skipAck is null ? null : PayloadString(skipAck.Payload, "stage"),
                        levelEnterAckMissing,
                        defeatReset,
                        waitedMs = sw.ElapsedMilliseconds
                    });
            }

            // 2026-09-15 live: this freeze used to go out before the scenario's own debug.session start, so the
            // injector's pre-session cheat snapshot captured it and "restored" a frozen wave timer after the lab
            // ended -- the next real board never spawned a zombie. Start the session first so every lab-scoped
            // change, including this freeze, is undone when the session ends (the scenario's own start is then a no-op).
            var scenarioCorrelation = Guid.NewGuid().ToString("N")[..12];
            await Send(hub, inbox, "debug.session", new { op = "start", scenarioId = scenarioCorrelation });
            await Send(hub, inbox, "debug.wave-freeze", new { enabled = true });

            IReadOnlyList<DebugScenarioStep> steps;
            try { steps = DebugScenarios.Expand(scenarioId, scenarioCorrelation); }
            catch (ArgumentException ex) { return Results.NotFound(new { ok = false, error = ex.Message }); }

            var beforeScenario = store.GetMaxEventId();
            DebugSessionState.Active = true;
            DebugSessionState.ScenarioId = scenarioCorrelation;
            EffectGrantSessionRecorder.ApplyDebugSteps(grants, steps.Select(st => (st.Name, (object?)st.Payload)));
            await Send(hub, inbox, "debug.run-steps", new
            {
                scenarioId = scenarioCorrelation,
                id = scenarioId,
                steps = steps.Select(st => new { name = st.Name, payload = st.Payload }).ToList()
            });

            var runDone = await PollForKind(store, beforeScenario, "debug.run-steps.done", TimeSpan.FromSeconds(timeoutSec));
            if (runDone is null)
                return Results.Conflict(new { ok = false, error = $"scenario '{scenarioId}' steps did not complete within {timeoutSec}s", waitedMs = sw.ElapsedMilliseconds, defeatReset });

            // 2026-09-15 (live-probe-mcp overview): a caller could read entered:true/ready:true here
            // purely from injector ack events while the real board sat on a stale seed-picker screen
            // or, worse, empty (0 plants/0 zombies) -- the exact ambiguity debug_game_state exists to
            // resolve, but every live probe this session had to make that as a SEPARATE round trip
            // because quick-start never checked. Fold the same real-Unity-object read in here so the
            // response is honest about what is actually alive, not just what the ack chain claims.
            // Best-effort: a missing/failed game-state read degrades to liveEntities:null, never a
            // hard failure -- the ptrs/scenario proof above already stands on their own.
            object? liveEntities = null;
            var beforeGameState = store.GetMaxEventId();
            await Send(hub, inbox, "debug.game-state", new { });
            var gameStateAck = await PollForKind(store, beforeGameState, "debug.game-state", TimeSpan.FromSeconds(Math.Min(timeoutSec, 10)));
            if (gameStateAck is not null && PayloadBool(gameStateAck.Payload, "ok"))
            {
                liveEntities = new
                {
                    plantCount = PayloadInt(gameStateAck.Payload, "plantCount", 0),
                    zombieCount = PayloadInt(gameStateAck.Payload, "zombieCount", 0),
                    liveState = PayloadString(gameStateAck.Payload, "liveState"),
                    phaseMismatch = PayloadBool(gameStateAck.Payload, "phaseMismatch")
                };
            }

            EventEnvelope? snapshot = null;
            var beforeSnapshot = store.GetMaxEventId();
            var snapshotDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < snapshotDeadline && snapshot is null)
            {
                await Send(hub, inbox, "debug.effect.board-snapshot", new { });
                await Task.Delay(400);
                snapshot = FindKindAfter(store, beforeSnapshot, "debug.effect.board-snapshot");
            }

            string? targetPtr = null;
            string? plantPtr = null;
            if (snapshot?.Payload is JsonElement snapEl && snapEl.ValueKind == JsonValueKind.Object
                && snapEl.TryGetProperty("entities", out var entitiesEl) && entitiesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var ent in entitiesEl.EnumerateArray())
                {
                    var living = ent.TryGetProperty("living", out var l) && l.ValueKind == JsonValueKind.True;
                    if (!living || !ent.TryGetProperty("ptr", out var ptrEl) || ptrEl.ValueKind != JsonValueKind.String) continue;
                    var side = ent.TryGetProperty("side", out var s) ? s.GetString() : null;
                    if (side == "zombie" && targetPtr is null) targetPtr = ptrEl.GetString();
                    if (side == "plant" && plantPtr is null) plantPtr = ptrEl.GetString();
                }
            }

            return Results.Ok(new
            {
                ok = true,
                entered,
                levelType,
                scenario = scenarioId,
                targetPtr,
                plantPtr,
                setupSkip = setupSkipOk,
                levelEnterAckMissing,
                defeatReset,
                liveEntities,
                elapsedMs = sw.ElapsedMilliseconds,
                note = snapshot is null ? "no board snapshot arrived — targetPtr/plantPtr unavailable" : null
            });
        });

        MapPost(g, "/reset-board", "debug.reset-board");
        MapPost(g, "/clear-plants", "debug.clear-plants");
        MapPost(g, "/clear-zombies", "debug.clear-zombies");
        MapPost(g, "/spawn-plant", "debug.spawn-plant");
        MapPost(g, "/spawn-zombie", "debug.spawn-zombie");
        MapPost(g, "/spawn-bullet", "debug.spawn-bullet");
        MapPost(g, "/set-mods", "debug.set-mods");
        MapPost(g, "/reset-mods", "debug.reset-mods");
        MapPost(g, "/reapply", "debug.reapply");
        MapPost(g, "/board-stats", "debug.board-stats");
        MapPost(g, "/apply-status", "debug.apply-status");
        MapPost(g, "/apply-status-float", "debug.apply-status-float");
        MapPost(g, "/clear-status", "debug.clear-status");
        MapPost(g, "/kill", "debug.kill");
        MapPost(g, "/kill-plant", "debug.kill-plant");
        MapPost(g, "/wave-freeze", "debug.wave-freeze");
        MapPost(g, "/stress-fill", "debug.stress-fill");
        MapPost(g, "/stress-clear", "debug.stress-clear");
        MapPost(g, "/ensure-sun", "debug.ensure-sun");
        MapPost(g, "/enter-level", "debug.enter-level");
        MapPost(g, "/select", "debug.select");
        MapPost(g, "/spawn-cell", "debug.spawn-cell");
        MapPost(g, "/disarm", "debug.disarm");
        MapPost(g, "/economy", "debug.economy");
        MapPost(g, "/board-config", "debug.board-config");
        MapPost(g, "/board-action", "debug.board-action");
        MapPost(g, "/spawn-grid", "debug.spawn-grid");
        MapPost(g, "/clear-grid", "debug.clear-grid");
        MapPost(g, "/set-box", "debug.set-box");
        MapPost(g, "/grid-query", "debug.grid-query");
        MapPost(g, "/ice-road", "debug.ice-road");
        g.MapPost("/effect/grant", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox, EffectGrantSession grants) =>
        {
            var b = BodyOrEmpty(body);
            var dto = EffectGrantSessionRecorder.TryParseGrant(b);
            if (dto == null || string.IsNullOrWhiteSpace(dto.EffectId))
                return Results.BadRequest(new { error = "effectId required" });
            EffectGrantSessionRecorder.NormalizeGrantDefaults(dto);
            grants.Upsert(dto);
            await Send(hub, inbox, "debug.effect.grant", dto);
            return Results.Ok(new { ok = true, queued = inbox.Count, grantId = dto.GrantId });
        });
        g.MapPost("/effect/withdraw", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox, EffectGrantSession grants) =>
        {
            var b = BodyOrEmpty(body);
            EffectGrantSessionRecorder.ApplyDebugCommand(grants, "debug.effect.withdraw", b);
            await Send(hub, inbox, "debug.effect.withdraw", b);
            return Results.Ok(new { ok = true, queued = inbox.Count });
        });
        g.MapPost("/effect/clear", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox, EffectGrantSession grants) =>
        {
            EffectGrantSessionRecorder.ApplyDebugCommand(grants, "debug.effect.clear", BodyOrEmpty(body));
            await Send(hub, inbox, "debug.effect.clear", BodyOrEmpty(body));
            return Results.Ok(new { ok = true, queued = inbox.Count });
        });
        MapPost(g, "/effect/list", "debug.effect.list");
        MapPost(g, "/effect/fire-synthetic", "debug.effect.fire-synthetic");
        MapPost(g, "/effect/enqueue-delta", "debug.effect.enqueue-delta");
        MapPost(g, "/effect/board-snapshot", "debug.effect.board-snapshot");
        MapPost(g, "/effect/dots", "debug.effect.dots");
        MapPost(g, "/effect/counters", "debug.effect.counters");
        // buff-debuff-scope T11a (2026-08-29) built the injector-side commands
        // (CheatCommandRunner.cs: debug.scope.start-own-side / debug.scope.stop-own-side, wired to
        // DebugScopeRuntime) so T11's own LIVE gate could be executed for the first time -- but never
        // added the HTTP route to actually send them, leaving the commands genuinely unreachable from
        // outside the game process. Found live-qa 2026-09-20 while attempting T11: T11a's own closing
        // note ("T11 could not have been attempted by anyone, owner included, without this") was still
        // literally true after T11a shipped. Same one-line MapPost pattern as every other debug.effect.*
        // route above.
        MapPost(g, "/scope/start-own-side", "debug.scope.start-own-side");
        MapPost(g, "/scope/stop-own-side", "debug.scope.stop-own-side");
        MapPost(g, "/status", "debug.status");
        g.MapGet("/status", async (IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            await Send(hub, inbox, "debug.status", new { });
            return Results.Ok(new
            {
                ok = true,
                note = "injector emits debug.status; poll GET /api/debug/events?kinds=debug.status,debug.status.resisted"
            });
        });
        g.MapGet("/actor-derived", async (string? ptr, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            await Send(hub, inbox, "debug.actor-derived", new { ptr = ptr ?? "" });
            return Results.Ok(new
            {
                ok = true,
                note = "injector emits debug.actor-derived; poll GET /api/debug/events?kinds=debug.actor-derived"
            });
        });
        g.MapPost("/actor-derived", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.actor-derived", b);
            return Results.Ok(new { ok = true, queued = inbox.Count, command = "debug.actor-derived" });
        });
        MapPost(g, "/combat/pin-element", "debug.combat.pin-element");
        MapPost(g, "/combat/silence-vanilla", "debug.combat.silence-vanilla");
        MapPost(g, "/combat/probe", "debug.combat.probe");
        MapPost(g, "/combat/snapshot", "debug.combat.snapshot");
        MapPost(g, "/shield/grant", "debug.shield.grant");
        MapPost(g, "/shield/clear", "debug.shield.clear");
        MapPost(g, "/shield/demo", "debug.shield.demo");
        MapPost(g, "/shield/demo-all", "debug.shield.demo-all");
        MapPost(g, "/shield/snapshot", "debug.shield.snapshot");
        MapPost(g, "/shield/bar-status", "debug.shield.bar-status");
        g.MapPost("/status/apply", async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, "debug.status.apply", b);
            return Results.Ok(new { ok = true, queued = inbox.Count, command = "debug.status.apply" });
        });
        MapPost(g, "/fx/probe-shaders", "debug.fx.probe-shaders");
        MapPost(g, "/fx/world-flash", "debug.fx.world-flash");
        MapPost(g, "/fx/play", "debug.fx.play");
        MapPost(g, "/fx/list", "debug.fx.list");
        MapPost(g, "/fx/mute", "debug.fx.mute");
        MapPost(g, "/fx/unmute", "debug.fx.unmute");
        MapPost(g, "/fx/state", "debug.fx.state");

        g.MapPost("/effects/reload", async (IHubContext<RpgHub> hub, InjectorCommandInbox inbox, EffectGrantSession grants) =>
        {
            EffectGrantSessionRecorder.ApplyDebugCommand(grants, "effects.reload", default);
            await Send(hub, inbox, "effects.reload", new
            {
                contractVersion = FoundationContractVersion.Current
            });
            return Results.Ok(new
            {
                ok = true,
                contractVersion = FoundationContractVersion.Current,
                command = "effects.reload"
            });
        });

        // RPG Server Debug
        // (in-memory session-grant reads only, no injector relay and no RpgStore read)
        g.MapGet("/effects/session-grants", (EffectGrantSession grants) =>
            Results.Ok(new { count = grants.Count, grants = grants.Snapshot() }));

        // E33 (spec-activation-edge.md §2.1, §2.1a): both arrays used to be hand-copied and had
        // drifted from their own source classes — `triggers` was missing OnActivate, `actions` was
        // missing GrantShield and ModifyDerivedStat, all three real (GrantShield has a live executor;
        // ModifyDerivedStat is declarative-by-design but still part of the published vocabulary). A
        // published-but-not-declared or declared-but-not-published constant is exactly "a published
        // list that lies", the defect this endpoint exists to not repeat — so both arrays are now
        // reflected off their own class's public const fields, which is what makes "every constant,
        // and no others" true by construction rather than by someone remembering to edit two lists in
        // sync. E34 grows EffectTriggers to 13 and needs no edit here for that to stay correct; E35/
        // E36/E37 grow EffectActions the same way.
        g.MapGet("/effects/contract", () => Results.Ok(new
        {
            contractVersion = FoundationContractVersion.Current,
            frozen = true,
            triggers = PublicConstStrings(typeof(EffectTriggers)),
            actions = PublicConstStrings(typeof(EffectActions))
        }));

        // `species-progression` SP0.6: `/reforge-world` (T5.7 `dev-reforge`) is REMOVED. Its entire
        // body was the eager, unconditional species reforge (`RpgStore.ReforgePlayerSpecies`) — the
        // RPG Server Debug route's own capability to fabricate layer-1b state
        // (docs/contributing/live-probe-standard.md), which SP0.4's ruling (C2: a save that never
        // fuses gets zero layer-1b rows) makes illegitimate to keep. There is no reforge-world
        // capability left to gate differently; the whole route is gone, not narrowed.

        // Game Injector Debug
        // both this and /fire-spawn-extra delegate to AcceptDebugSpawnExtra, which relays
        g.MapPost("/spawn-extra", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var result = await AcceptDebugSpawnExtra(BodyOrEmpty(body), store, hub, inbox, reasonDefault: "debug");
            return result;
        });

        g.MapPost("/fire-spawn-extra", async (JsonElement? body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            // Ensure typeId default for fire helper
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("typeId", out _))
            {
                b = JsonSerializer.SerializeToElement(new { typeId = DebugScenarios.BasicZombieTypeId });
            }
            return await AcceptDebugSpawnExtra(b, store, hub, inbox, reasonDefault: "debug.fire");
        });

        // RPG Server Debug
        // Derived sheet audit: real UniqueActor → Hub → /sheet (never a synthetic 269 paint).
        g.MapPost("/derived-audit-actor", (JsonElement? body, RpgStore store) =>
        {
            var b = BodyOrEmpty(body);
            long? playerId = null;
            if (b.ValueKind == JsonValueKind.Object
                && b.TryGetProperty("playerId", out var p) && p.TryGetInt64(out var pid))
                playerId = pid;
            try
            {
                return Results.Ok(DerivedAuditActor.Seed(store, playerId));
            }
            catch (Exception ex)
            {
                return Results.Conflict(new { ok = false, error = ex.Message });
            }
        });

        // ── RPG Server Debug (owner ruling 2026-09-22, SSH4.9-P2): the sanctioned GRANT ───────────
        // A pooled live probe needs a save that actually HOLDS a socketable chassis and a gem. These
        // two routes TRIGGER REAL WRITES through the normal store path — a base-type instance whose
        // atom the real `Instantiator` rolls, and stock of a shipped gem — and each reads the changed
        // state back through the normal path (`GetInstance`, `GetItemGeneration`, `ListStock`). They
        // mint nothing gameplay could not produce: the base type and the gem come from the SHIPPED
        // corpus, and the instance carries a real `item_generation` row so the workbench and
        // `SocketHostFor` resolve it exactly as they resolve a drop.
        // ⛔ Scope is RPG Server Debug per docs/contributing/live-probe-standard.md — never the Game
        // Injector scope, and never a second debug surface re-implementing an endpoint.
        g.MapPost("/grant-item", (JsonElement? body, RpgStore store) =>
        {
            var b = BodyOrEmpty(body);
            var baseTypeId = StrProp(b, "baseTypeId");
            if (string.IsNullOrWhiteSpace(baseTypeId))
                return Results.BadRequest(new { error = "baseTypeId required (a shipped id, e.g. item.humanoid-feet-a-003)" });
            var shipped = ShippedBaseTypeFor(baseTypeId!);
            if (shipped is null)
                return Results.Conflict(new { ok = false, error = $"base type '{baseTypeId}' is not in the shipped base-type corpus" });

            var atomId = AtomRow.DeriveId(shipped.Value.Family, "", 1);
            if (store.GetAtom(atomId) is null)
                return Results.Conflict(new { ok = false, error = $"the base type's implicit atom '{atomId}' is not in the loaded atom catalog" });

            var playerId = IntProp(b, "playerId", checked((int)store.GetCurrentPlayerId()));
            var playerKey = playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rungs = store.ListRarities();
            var rungId = StrProp(b, "rungId");
            var rung = string.IsNullOrWhiteSpace(rungId)
                ? rungs.FirstOrDefault(r => string.Equals(r.RarityId, "cultivated", StringComparison.Ordinal)) ?? rungs.First()
                : rungs.FirstOrDefault(r => string.Equals(r.RarityId, rungId, StringComparison.Ordinal));
            if (rung is null) return Results.Conflict(new { ok = false, error = $"unknown rung '{rungId}'" });
            var itemLevel = IntProp(b, "itemLevel", 24);

            // The container is built from the SHIPPED entry and instantiated by the REAL instantiator,
            // so pool rolls and OnInstantiate params are frozen by it, not by this route.
            var container = new ContainerRow
            {
                ContainerId = baseTypeId!, Kind = ContainerKind.Item, Slot = ItemRoles.Id(shipped.Value.Role),
                Rarity = rung.RarityId, Frame = shipped.Value.Frame, BaseTypeId = baseTypeId!,
                Atoms = new[] { new ContainerAtomRow(0, atomId) },
            };
            // The container must EXIST for the consumers: the workbench's TryResolve resolves an
            // instance's container from effect_container (a shipped base-type item has its own row
            // there), so a minted item without one would be refused as item.container-missing. This is
            // the same row a drop's item carries, validated against the real atom catalog.
            var containerRejection = store.UpsertContainer(container);
            if (!containerRejection.IsOk)
                return Results.Conflict(new { ok = false, error = $"the container was refused by the store: {containerRejection}" });

            var minted = Instantiator.TryInstantiate(
                container, store.GetAtom, store.GetAffix,
                rollSeed: unchecked((long)(ServerClock.UtcNowDateTime.Ticks & 0x7fffffff)),
                FusionRpg.Core.Power.PowerTuningHub.Tuning.Curve.PinIndex,
                FusionRpg.Core.Power.PowerTuningHub.Tuning, out var instance,
                InstanceOrigin.Grant, store.GetCatalogRevision());
            if (!minted.IsOk || instance is null)
                return Results.Conflict(new { ok = false, error = $"the real instantiator refused: {minted}" });

            var instanceId = store.SaveInstance(instance);
            var acquired = store.AcquireItem(new RpgItemRow
            {
                InstanceId = instanceId, PlayerId = playerKey,
                AcquiredUtc = ServerClock.UtcNowDateTime.ToString("O"), OriginKind = "debug-grant",
            });
            if (!acquired.IsOk)
                return Results.Conflict(new { ok = false, error = acquired.ToString() });

            store.PersistLoot(playerKey,
                new LootManifest("debug-grant-" + instanceId, "table.debug-grant", 0UL, itemLevel,
                    Array.Empty<LootGrant>(), Array.Empty<string>(), "{}", LootPityState.Empty,
                    LootPityState.Empty, null, false, null),
                "debug", "grant-item", store.GetCatalogRevision(), 1,
                new[]
                {
                    new ItemGenerationRow(instanceId, 0, baseTypeId!, rung.Ordinal, itemLevel,
                        shipped.Value.Frame, ItemRoles.Id(shipped.Value.Role), "debug-grant"),
                });

            var readBack = store.GetInstance(instanceId);
            return Results.Ok(new
            {
                ok = true,
                scope = "RpgServerDebug",
                instanceId,
                baseTypeId,
                containerId = readBack?.ContainerId,
                socketMax = shipped.Value.SocketMax,
                role = ItemRoles.Id(shipped.Value.Role),
                generationRow = store.GetItemGeneration(instanceId) is not null,
            });
        });

        g.MapPost("/grant-materials", (JsonElement? body, RpgStore store) =>
        {
            var b = BodyOrEmpty(body);
            var playerId = IntProp(b, "playerId", checked((int)store.GetCurrentPlayerId()));
            var souls = IntProp(b, "souls", 0);
            if (souls > 0)
                store.AwardSouls(playerId, souls, "debug-grant",
                    "debug-grant-souls-" + Guid.NewGuid().ToString("N"));

            var grants = new List<(string MaterialId, long Qty)>();
            if (b.ValueKind == JsonValueKind.Object &&
                b.TryGetProperty("materials", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in ms.EnumerateArray())
                {
                    var id = m.TryGetProperty("materialId", out var mid) ? mid.GetString() : null;
                    var qty = m.TryGetProperty("qty", out var q) && q.TryGetInt64(out var qv) ? qv : 1;
                    if (!string.IsNullOrWhiteSpace(id)) grants.Add((id!, qty));
                }
            }
            if (grants.Count > 0) store.GrantMaterials(playerId, grants);

            var held = grants.ToDictionary(g => g.MaterialId,
                g => store.GetMaterialQty(playerId, g.MaterialId), StringComparer.Ordinal);
            return Results.Ok(new { ok = true, scope = "RpgServerDebug", souls, granted = grants.Count, held });
        });

        g.MapPost("/grant-gem", (JsonElement? body, RpgStore store) =>
        {
            var b = BodyOrEmpty(body);
            var containerId = StrProp(b, "containerId");
            if (string.IsNullOrWhiteSpace(containerId))
                return Results.BadRequest(new { error = "containerId required (a shipped gem container id)" });
            if (!ShippedGemContains(containerId!))
                return Results.Conflict(new { ok = false, error = $"'{containerId}' is not in the shipped gem corpus" });

            var playerId = IntProp(b, "playerId", checked((int)store.GetCurrentPlayerId()));
            var playerKey = playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var qty = IntProp(b, "qty", 1);
            if (qty <= 0) return Results.BadRequest(new { error = "qty must be positive" });

            store.AdjustStock(playerKey, containerId!, qty);
            var held = store.ListStock(playerKey)
                .Where(s => string.Equals(s.ContainerId, containerId, StringComparison.Ordinal))
                .Sum(s => s.Qty);
            return Results.Ok(new { ok = true, scope = "RpgServerDebug", containerId, granted = qty, held });
        });

        g.MapGet("/derived-audit-coverage", (string? instanceId, bool? writeArtifact, string? artifactPath, RpgStore store) =>
        {
            try
            {
                var write = writeArtifact == true;
                var path = artifactPath;
                if (write && string.IsNullOrWhiteSpace(path))
                    path = Path.Combine(AppContext.BaseDirectory, "derived-audit-coverage.json");
                var json = DerivedAuditActor.CoverageJson(store, instanceId, write, path);
                return Results.Content(json, "application/json");
            }
            catch (Exception ex)
            {
                return Results.Conflict(new { ok = false, error = ex.Message });
            }
        });

        // Game Injector Debug
        g.MapPost("/arm/{kind}", async (string kind, JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var payload = JsonSerializer.SerializeToElement(MergeKind(BodyOrEmpty(body), kind));
            DebugSessionState.Arms[kind] = true;
            await Send(hub, inbox, "debug.arm", payload);
            return Results.Ok(new { ok = true, kind });
        });
    }

    /// <summary>Same accept path as POST /api/pvz-intent/spawn-extra (fact + command when newly inserted).</summary>
    public static async Task<IResult> AcceptDebugSpawnExtra(
        JsonElement body,
        RpgStore store,
        IHubContext<RpgHub> hub,
        InjectorCommandInbox inbox,
        string reasonDefault)
    {
        var playerId = store.GetCurrentPlayerId();
        if (!store.PlayerExists(playerId)) return Results.NotFound(new { error = "no current player" });

        var typeId = body.TryGetProperty("typeId", out var t) && t.TryGetInt32(out var ti) ? ti : DebugScenarios.BasicZombieTypeId;
        var corr = body.TryGetProperty("correlationId", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()!
            : Guid.NewGuid().ToString("N");
        int? row = body.TryGetProperty("row", out var r) && r.TryGetInt32(out var rv) ? rv : null;
        int? col = body.TryGetProperty("col", out var colEl) && colEl.TryGetInt32(out var cv) ? cv : null;
        var reason = body.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
            ? reasonEl.GetString()!
            : reasonDefault;
        var side = body.TryGetProperty("side", out var sideEl) && sideEl.ValueKind == JsonValueKind.String
            ? sideEl.GetString()!.Trim().ToLowerInvariant()
            : "zombie";

        try
        {
            var (rollup, inserted) = store.RecordExtraSpawnIntent(playerId, corr, typeId, reason, side);
            if (inserted)
            {
                var activity = store.GetPvzActivityRollup(playerId);
                if (activity is not null)
                    await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("PvzActivityUpdated", activity);

                await Send(hub, inbox, "pvz.spawn.extra", new
                {
                    typeId,
                    col,
                    row = row ?? 2,
                    reason,
                    correlationId = corr,
                    side,
                    playerId,
                    source = "extra"
                }, cmdId: corr);
            }

            return Results.Ok(new { ok = true, correlationId = corr, inserted, rollup, queued = inbox.Count });
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound();
        }
    }

    static JsonElement BodyOrEmpty(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } b ? b : JsonSerializer.SerializeToElement(new { });

    // ---- lawn/quick-start helpers (centralizes what setup-lab-run.ps1 + tools/live_test/lawn.py
    // each separately hand-rolled — see .claude/skills/live-lawn-quick-start/SKILL.md) ----

    static readonly HashSet<string> BadLevelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Explore", "TravelAdvanture", "Travel", "IZ"
    };

    // Structural safety check, not a balance value: real gameplay does not lose 3 matches in 10
    // seconds. Shared by /lawn/state and /lawn/quick-start so the two never drift apart.
    const int CyclingBoardEndMinCount = 3;
    const int CyclingWindowSec = 10;

    /// <summary>SSH4.9-P2: one entry of the SHIPPED `gk-data/packs/fusion/data/seed/items/base-types/**` corpus — the same
    /// tree the card resolves a base type from. A miss is a named refusal at the route, never a guessed
    /// base type.</summary>
    static (string Frame, ItemRole Role, string Family, int SocketMax)? ShippedBaseTypeFor(string baseTypeId)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "base-types");
        if (!Directory.Exists(root)) return null;
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("entries", out var rows) ||
                rows.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !string.Equals(id.GetString(), baseTypeId, StringComparison.Ordinal)) continue;
                var frame = row.TryGetProperty("frame", out var f) ? f.GetString() ?? "" : "";
                var roleId = row.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                var socketMax = row.TryGetProperty("socketMax", out var s) && s.TryGetInt32(out var sm) ? sm : 0;
                var family = "";
                if (row.TryGetProperty("implicit", out var imp) && imp.ValueKind == JsonValueKind.Object &&
                    imp.TryGetProperty("family", out var fam)) family = fam.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(family) || !ItemRoles.TryParse(roleId, out var role)) continue;
                return (frame, role, family, socketMax);
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="containerId"/> is a shipped gem, so a granted stock row names a
    /// real container the insert path can resolve and mint.</summary>
    static bool ShippedGemContains(string containerId)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "gems");
        if (!Directory.Exists(dir)) return false;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("entries", out var rows) ||
                rows.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in rows.EnumerateArray())
                if (row.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                    string.Equals(id.GetString(), containerId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static int IntProp(JsonElement obj, string name, int fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.TryGetInt32(out var v)
            ? v : fallback;

    static string? StrProp(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    // ---- lawn-screenshot side store (dev artifacts, never RpgStore, never committed) ----

    // Structural (not tunable): rejects garbage/accidental multi-GB posts before buffering.
    const int ScreenshotMaxBytes = 8 * 1024 * 1024;

    // Structural (not tunable): disk bound for dev screenshots.
    const int ScreenshotRetainCount = 20;

    static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    static bool HasPngSignature(byte[] png)
    {
        if (png.Length < PngSignature.Length) return false;
        for (var i = 0; i < PngSignature.Length; i++)
            if (png[i] != PngSignature[i]) return false;
        return true;
    }

    static string SanitizeScreenshotTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "probe";
        var sb = new System.Text.StringBuilder(32);
        foreach (var c in tag.Trim())
        {
            if (sb.Length >= 32) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }
        return sb.Length == 0 ? "probe" : sb.ToString();
    }

    static string ScreenshotStoreDir() =>
        Path.Combine(AppContext.BaseDirectory, "artifacts", "lawn-screenshots");

    static string DumpStoreDir() =>
        Path.Combine(AppContext.BaseDirectory, "artifacts", "lawn-dumps");

    // Structural (not tunable): disk bound for dev dumps. Not the balance surface.
    const int DumpRetainCount = 10;

    static void PruneDumps(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.json")
                .Where(f => !string.Equals(f.Name, "latest.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Name)
                .Skip(DumpRetainCount)
                .ToList();
            foreach (var f in files)
            {
                try { f.Delete(); } catch { /* best-effort prune */ }
            }
        }
        catch { /* best-effort prune */ }
    }

    static void PruneScreenshots(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.png")
                .OrderByDescending(f => f.Name)
                .Skip(ScreenshotRetainCount)
                .ToList();
            foreach (var f in files)
            {
                try { f.Delete(); } catch { /* best-effort prune */ }
            }
        }
        catch { /* best-effort prune */ }
    }

    static bool PayloadBool(object? payload, string name) =>
        payload is JsonElement el && el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string? PayloadString(object? payload, string name) =>
        payload is JsonElement el && el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static int PayloadInt(object? payload, string name, int dflt) =>
        payload is JsonElement el && el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? i : dflt;

    /// <summary>Newest `board.start` with no later `board.end` — in-process port of
    /// `setup-lab-run.ps1`'s `Get-LatestBoardStart`/`Test-BoardStillLive` (external, HTTP-bound,
    /// forced into a paging/binary-search shape) and `lawn.py`'s `latest_board_start`/
    /// `board_still_live` (same idea, Python). One direct scan is sufficient here since the caller
    /// already holds the store in-process — no HTTP round trip to approximate.</summary>
    /// <summary>Internal for <c>FusionRpg.Server.Tests</c> — the stale-board rule below has cost two
    /// sessions and now has a regression test.</summary>
    /// <param name="trustInjectorLiveBoard">
    /// Set only when the injector has just reported "board already live". It holds the real Board and
    /// outranks this event-log heuristic, so the injector-session filter below is skipped — otherwise a
    /// SERVER restart (same game, same board, fresh Hello) would read as stale.
    /// </param>
    internal static EventEnvelope? FindLatestLiveBoardStart(RpgStore store, bool trustInjectorLiveBoard = false)
    {
        var max = store.GetMaxEventId();
        if (max <= 0) return null;
        // How far back to scan the event log for a `board.start`. **Structural, not a balance dial**
        // (tunables-ssot.md §1): it is the page size handed to `ListEvents`, so it bounds a query
        // rather than tuning anything a balance pass would touch.
        //
        // Named `windowCapacity` rather than `window` deliberately (2026-09-04): the magic-number
        // audit recognises structural intent from the NAME, and `window` alone reads as a tunable.
        // Renaming it is better than adding `window` to the audit's exempt list, which would silently
        // excuse every future `window` constant in the repo — precision over coverage, the rule that
        // file's own comments already state.
        const int windowCapacity = 2000;
        var after = Math.Max(0, max - windowCapacity);
        var items = store.ListEventsForServerScan(windowCapacity, after);
        var starts = items.Where(e => e.Kind == "board.start").ToList();
        if (starts.Count == 0) return null;
        var latestStart = starts[^1];
        var endedAfter = items.Any(e => e.Kind == "board.end" && e.Id > latestStart.Id);
        if (endedAfter) return null;

        // A board.start is only "live" if it belongs to the CURRENT injector session.
        //
        // A `board.end` is written on a clean exit. Kill the game mid-match -- a crash, a redeploy, or
        // an assistant tool call whose process tree is reaped -- and none is ever written, so that row
        // stays "live" forever. `quick-start` then reports `entered:false` with null targetPtr/plantPtr
        // and every probe afterwards runs against a board that does not exist.
        //
        // This false positive has now cost two separate sessions (2026-08-30, twice: once mistaken for
        // an `attackDamage` regression, once blocking A5 entirely), which is why it is fixed here rather
        // than documented again. `injector.hello` is emitted once per injector startup, so any
        // board.start older than the newest one belongs to a game process that is gone.
        if (trustInjectorLiveBoard) return latestStart;

        var lastHello = items.LastOrDefault(e => e.Kind == "injector.hello");
        if (lastHello is not null && latestStart.Id < lastHello.Id) return null;

        return latestStart;
    }

    /// <summary>Counts events of `kind` timestamped within the last `window` — the observability check
    /// that catches a rapidly cycling board (match ending and retrying every second or two, e.g. from
    /// a "quick" setup-skip with no real plants placed) before quick-start polls into a confusing
    /// timeout against it. Same bounded-scan shape as <see cref="FindLatestLiveBoardStart"/>; `T` is
    /// the ISO round-trip string every event is stamped with (`DateTime.UtcNow.ToString("o")`).</summary>
    static int CountRecentEventsOfKind(RpgStore store, string kind, TimeSpan window)
    {
        var max = store.GetMaxEventId();
        if (max <= 0) return 0;
        const int windowCapacity = 2000;
        var after = Math.Max(0, max - windowCapacity);
        var cutoff = DateTime.UtcNow - window;
        var count = 0;
        foreach (var e in store.ListEventsForServerScan(windowCapacity, after))
        {
            if (!string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (DateTime.TryParse(e.T, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) && t >= cutoff)
                count++;
        }
        return count;
    }

    static EventEnvelope? FindKindAfter(RpgStore store, long afterId, string kind)
    {
        var items = store.ListEvents(500, afterId);
        return items.LastOrDefault(e => e.Kind == kind);
    }

    static EventEnvelope? FindLatestKind(RpgStore store, string kind)
    {
        var max = store.GetMaxEventId();
        if (max <= 0) return null;
        const int windowCapacity = 2000;
        return store.ListEventsForServerScan(windowCapacity, Math.Max(0, max - windowCapacity))
            .LastOrDefault(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
    }

    static async Task<EventEnvelope?> PollForKind(RpgStore store, long afterId, string kind, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = FindKindAfter(store, afterId, kind);
            if (found is not null) return found;
            await Task.Delay(300);
        }
        return null;
    }

    static object MergeKind(JsonElement body, string kind)
    {
        var dict = new Dictionary<string, object?> { ["kind"] = kind };
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in body.EnumerateObject())
            {
                if (prop.NameEquals("kind")) continue;
                dict[prop.Name] = prop.Value.Clone();
            }
        }
        return dict;
    }

    static void MapPost(RouteGroupBuilder g, string path, string cmdName)
    {
        g.MapPost(path, async (JsonElement? body, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
        {
            var b = BodyOrEmpty(body);
            await Send(hub, inbox, cmdName, b);
            return Results.Ok(new { ok = true, queued = inbox.Count });
        });
    }

    static bool PayloadHasScenario(object? payload, string scenarioId)
    {
        if (payload is JsonElement el && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("scenarioId", out var s) && s.ValueKind == JsonValueKind.String)
            return string.Equals(s.GetString(), scenarioId, StringComparison.Ordinal);
        return false;
    }

    static async Task Send(IHubContext<RpgHub> hub, InjectorCommandInbox inbox, string name, object payload, string? cmdId = null)
    {
        var cmd = new CommandDto { Name = name, Payload = payload, Id = cmdId ?? Guid.NewGuid().ToString("N") };
        inbox.Enqueue(cmd);
        try { await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("Command", cmd); }
        catch { /* inbox poll */ }
    }
}
