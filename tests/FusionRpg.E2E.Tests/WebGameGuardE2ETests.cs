using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// Wave B concurrency guard (audit 2026-08-21): a web-mode battle resolved while a real match
/// is live must not clear the live match's effect-grant session.
/// </summary>
[Collection("e2e")]
public class WebGameGuardE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public WebGameGuardE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task<int> SessionGrantCount()
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>("/api/debug/effects/session-grants");
        // Shape-tolerant count: array root or an items/grants property.
        if (doc.ValueKind == JsonValueKind.Array) return doc.GetArrayLength();
        foreach (var name in new[] { "items", "grants" })
            if (doc.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.GetArrayLength();
        return 0;
    }

    [Fact]
    public async Task Web_board_lifecycle_does_not_wipe_live_match_grant_session()
    {
        // Live PvZ match with one session grant.
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "guard" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/debug/effect/grant", new
        {
            effectId = "fx.guard-test",
            ownerKey = "match",
            grantId = "guard-e2e-1"
        })).EnsureSuccessStatusCode();
        Assert.Equal(1, await SessionGrantCount());

        // A web-mode battle resolves mid-match: board.start + board.end under game=webrpg-1.
        var webKey = Guid.NewGuid().ToString("N");
        var t = DateTime.UtcNow.ToString("o");
        var post = await _http.PostAsJsonAsync("/api/events", new
        {
            events = new object[]
            {
                new { t, game = "webrpg-1", kind = "board.start", matchKey = webKey, payload = new { levelName = "web" } },
                new { t, game = "webrpg-1", kind = "match.result", matchKey = webKey, payload = new { result = "victory" } },
                new { t, game = "webrpg-1", kind = "board.end", matchKey = webKey, payload = new { levelName = "web" } }
            }
        });
        post.EnsureSuccessStatusCode();
        await _http.GetAsync("/api/test/snapshot"); // flush the writer

        // The live match's grant session survives; a pvzrh board.end would have cleared it.
        Assert.Equal(1, await SessionGrantCount());
    }

    /// <summary>
    /// C4c: the same wipe scenario through the REAL service path — a WebMatchService battle
    /// (real creatures, real ingest) during a live PvZ match leaves the grant session, the open
    /// PvZ run, and the specimen actors untouched.
    /// </summary>
    [Fact]
    public async Task Web_match_via_service_leaves_live_pvz_state_untouched()
    {
        // Live PvZ match with one session grant.
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "guard-svc" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/debug/effect/grant", new
        {
            effectId = "fx.guard-test",
            ownerKey = "match",
            grantId = "guard-e2e-svc"
        })).EnsureSuccessStatusCode();
        Assert.Equal(1, await SessionGrantCount());

        // Real creatures on the roster (seed → ×10 pull), snapshot their actor state.
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=2000", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/creatures/summon", new
        {
            count = 10,
            correlationId = "guard-svc-pull"
        })).EnsureSuccessStatusCode();
        var rosterBefore = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1");
        var actorsBefore = rosterBefore.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("actor"))
            .ToDictionary(
                a => a.GetProperty("instanceId").GetString()!,
                a => (Phase: a.GetProperty("phase").GetString(), Revision: a.GetProperty("revision").GetInt64()));
        Assert.NotEmpty(actorsBefore);

        // The real service path: resolve + dedicated ingest with the roster squad.
        var resp = await _http.PostAsJsonAsync("/api/test/web-match", new
        {
            correlationId = "guard-svc-match",
            waveId = "rift-skirmish"
        });
        resp.EnsureSuccessStatusCode();

        // Grant session intact, PvZ run still open, specimens byte-identical.
        Assert.Equal(1, await SessionGrantCount());
        var runs = await _http.GetFromJsonAsync<JsonElement>("/api/runs");
        var pvzRun = runs.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("levelName").GetString() == "guard-svc");
        Assert.True(pvzRun.GetProperty("endedUtc").ValueKind == JsonValueKind.Null,
            "the web match must not close the live PvZ run");

        var rosterAfter = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1");
        foreach (var item in rosterAfter.GetProperty("items").EnumerateArray())
        {
            var actor = item.GetProperty("actor");
            var id = actor.GetProperty("instanceId").GetString()!;
            Assert.Equal(actorsBefore[id].Phase, actor.GetProperty("phase").GetString());
            Assert.Equal(actorsBefore[id].Revision, actor.GetProperty("revision").GetInt64());
        }
    }
}
