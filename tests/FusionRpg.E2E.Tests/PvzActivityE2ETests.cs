using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class PvzActivityE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public PvzActivityE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        _ = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task FlushIngest()
    {
        (await _http.GetAsync("/api/test/snapshot")).EnsureSuccessStatusCode();
    }

    async Task<long> CurrentPlayerId()
    {
        var players = await _http.GetFromJsonAsync<JsonElement>("/api/players", Json);
        return players.GetProperty("currentPlayerId").GetInt64();
    }

    [Fact]
    public async Task Seed_demo_rollups_and_facts()
    {
        var seed = await _http.PostAsJsonAsync("/api/test/seed-pvz-activity-demo", new { });
        seed.EnsureSuccessStatusCode();
        var rollup = await seed.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(1, rollup.GetProperty("matchesStarted").GetInt64());
        Assert.Equal(2, rollup.GetProperty("zombiesKilled").GetInt64());
        Assert.Equal(1, rollup.GetProperty("victories").GetInt64());
        var playerId = rollup.GetProperty("playerId").GetInt64();
        var facts = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-activity/{playerId}/facts", Json);
        Assert.True(facts.GetProperty("items").GetArrayLength() >= 4);
        Assert.Equal(rollup.GetProperty("revision").GetInt64(), facts.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Get_missing_player_404()
    {
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/pvz-activity/999999")).StatusCode);
    }

    [Fact]
    public async Task Capture_projects_match_kill_place_die_and_result()
    {
        var playerId = await CurrentPlayerId();
        var matchKey = Guid.NewGuid().ToString("N");
        (await _http.PostAsJsonAsync("/api/events", new
        {
            events = new object[]
            {
                new { kind = "board.start", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { levelName = "A" } },
                new { kind = "plant.place", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "P1", col = 1, row = 2, type = 0 } },
                new { kind = "zombie.die", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "Z1", type = 1 } },
                new { kind = "zombie.die", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "Z1", type = 1 } },
                new { kind = "plant.die", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "P1", type = 0 } },
                new { kind = "match.result", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { result = "victory" } }
            }
        })).EnsureSuccessStatusCode();
        await FlushIngest();

        var rollup = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-activity/{playerId}", Json);
        Assert.Equal(1, rollup.GetProperty("matchesStarted").GetInt64());
        Assert.Equal(1, rollup.GetProperty("matchesEnded").GetInt64());
        Assert.Equal(1, rollup.GetProperty("victories").GetInt64());
        Assert.Equal(1, rollup.GetProperty("zombiesKilled").GetInt64());
        Assert.Equal(1, rollup.GetProperty("plantsPlaced").GetInt64());
        Assert.Equal(1, rollup.GetProperty("plantsLost").GetInt64());
        Assert.True(rollup.GetProperty("revision").GetInt64() > 0);
    }

    [Fact]
    public async Task Append_auto_dedupe_allows_two_same_kind_unknown_kind_400()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync($"/api/pvz-activity/{playerId}/facts/append", new
        {
            kind = "ZombieKilled",
            payloadJson = """{"n":1}"""
        })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync($"/api/pvz-activity/{playerId}/facts/append", new
        {
            kind = "ZombieKilled",
            payloadJson = """{"n":2}"""
        })).EnsureSuccessStatusCode();
        var rollup = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-activity/{playerId}", Json);
        Assert.Equal(2, rollup.GetProperty("zombiesKilled").GetInt64());

        var bad = await _http.PostAsJsonAsync($"/api/pvz-activity/{playerId}/facts/append", new { kind = "QuestDone" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Spawn_extra_intent_idempotent_one_command()
    {
        var playerId = await CurrentPlayerId();
        var res = await _http.PostAsJsonAsync("/api/pvz-intent/spawn-extra", new
        {
            typeId = 3,
            reason = "luck-low",
            playerId,
            correlationId = "corr-test-1"
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("inserted").GetBoolean());
        Assert.Equal(1, body.GetProperty("rollup").GetProperty("extraSpawnsFired").GetInt64());

        var pending = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);
        var cmds = pending.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("name").GetString() == "pvz.spawn.extra").ToList();
        Assert.Single(cmds);

        var res2 = await _http.PostAsJsonAsync("/api/pvz-intent/spawn-extra", new
        {
            typeId = 3,
            reason = "luck-low",
            playerId,
            correlationId = "corr-test-1"
        });
        res2.EnsureSuccessStatusCode();
        var body2 = await res2.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(body2.GetProperty("inserted").GetBoolean());

        var pending2 = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);
        Assert.DoesNotContain(pending2.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("name").GetString() == "pvz.spawn.extra");

        var rollup = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-activity/{playerId}", Json);
        Assert.Equal(1, rollup.GetProperty("extraSpawnsFired").GetInt64());
    }

    [Fact]
    public async Task Intent_plus_sim_spawn_source_extra()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "IntentExtra" })).EnsureSuccessStatusCode();

        var corr = Guid.NewGuid().ToString("N");
        var res = await _http.PostAsJsonAsync("/api/pvz-intent/spawn-extra", new
        {
            typeId = 2,
            reason = "luck-low",
            playerId,
            correlationId = corr
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("inserted").GetBoolean());
        Assert.Equal(1, body.GetProperty("rollup").GetProperty("extraSpawnsFired").GetInt64());

        await FlushIngest();

        var runs = await _http.GetFromJsonAsync<JsonElement>("/api/runs", Json);
        var run = runs.GetProperty("items").EnumerateArray()
            .First(r => r.GetProperty("levelName").GetString() == "IntentExtra");
        var runId = run.GetProperty("id").GetInt64();
        var spawns = await _http.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/spawns", Json);
        Assert.Contains(spawns.GetProperty("items").EnumerateArray(),
            s => s.GetProperty("source").GetString() == "extra");

        var rollup = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-activity/{playerId}", Json);
        Assert.Equal(1, rollup.GetProperty("extraSpawnsFired").GetInt64());
    }

    [Fact]
    public async Task Capture_zombie_spawn_source_extra_lands_in_spawn_stats()
    {
        var playerId = await CurrentPlayerId();
        var matchKey = Guid.NewGuid().ToString("N");
        (await _http.PostAsJsonAsync("/api/events", new
        {
            events = new object[]
            {
                new { kind = "board.start", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { } },
                new
                {
                    kind = "zombie.spawn",
                    matchKey,
                    t = DateTime.UtcNow.ToString("o"),
                    payload = new { ptr = "ZX", type = 2, typeName = "X", source = "extra", hp = 100, maxHp = 100, attack = 10 }
                }
            }
        })).EnsureSuccessStatusCode();
        await FlushIngest();

        var runs = await _http.GetFromJsonAsync<JsonElement>("/api/runs", Json);
        var run = runs.GetProperty("items").EnumerateArray().First(r => r.GetProperty("matchKey").GetString() == matchKey);
        var runId = run.GetProperty("id").GetInt64();
        var spawns = await _http.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/spawns", Json);
        var item = spawns.GetProperty("items").EnumerateArray().First(s => s.GetProperty("ptr").GetString() == "ZX");
        Assert.Equal("extra", item.GetProperty("source").GetString());
        Assert.Equal(playerId, run.GetProperty("playerId").GetInt64());
    }

    [Fact]
    public async Task Reset_clears_activity_tables()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-pvz-activity-demo", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        var playerId = await CurrentPlayerId();
        var rollup = await _http.GetFromJsonAsync<JsonElement>($"/api/pvz-activity/{playerId}", Json);
        Assert.Equal(0, rollup.GetProperty("revision").GetInt64());
        Assert.Equal(0, rollup.GetProperty("zombiesKilled").GetInt64());
    }
}
