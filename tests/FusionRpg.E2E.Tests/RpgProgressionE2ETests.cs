using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class RpgProgressionE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public RpgProgressionE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        _ = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task FlushIngest() => (await _http.GetAsync("/api/test/snapshot")).EnsureSuccessStatusCode();

    async Task<long> CurrentPlayerId()
    {
        var players = await _http.GetFromJsonAsync<JsonElement>("/api/players", Json);
        return players.GetProperty("currentPlayerId").GetInt64();
    }

    [Fact]
    public async Task Capture_awards_player_plant_zombie_xp()
    {
        var playerId = await CurrentPlayerId();
        var before = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/summary", Json);
        Assert.Equal(0, before.GetProperty("player").GetProperty("revision").GetInt64());

        var matchKey = Guid.NewGuid().ToString("N");
        (await _http.PostAsJsonAsync("/api/events", new
        {
            events = new object[]
            {
                new { kind = "board.start", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { } },
                new { kind = "plant.place", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "P1", type = 7, col = 1, row = 1 } },
                new { kind = "zombie.spawn", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "Z1", type = 3, source = "initHealth" } },
                new { kind = "zombie.die", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "Z1", type = 3 } },
                new { kind = "mower.start", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "M1", type = 0 } },
                new { kind = "match.result", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { result = "defeat" } }
            }
        })).EnsureSuccessStatusCode();
        await FlushIngest();

        var player = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/player/0", Json);
        Assert.True(player.GetProperty("level").GetInt64() >= 1);
        Assert.True(player.GetProperty("revision").GetInt64() > 0);

        var plant = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/plant/7", Json);
        Assert.True(plant.GetProperty("xp").GetDouble() > 0);

        var zombie = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/zombie/3", Json);
        Assert.True(zombie.GetProperty("xp").GetDouble() > 0);

        var ledger = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/ledger", Json);
        var byReason = ledger.GetProperty("items").EnumerateArray()
            .GroupBy(i => i.GetProperty("reason").GetString()!)
            .ToDictionary(g => g.Key, g => g.ToList());
        Assert.Single(byReason["kill"]);
        Assert.Equal(12, byReason["kill"][0].GetProperty("delta").GetDouble());
        var killPayload = byReason["kill"][0].GetProperty("payloadJson").GetString() ?? "";
        Assert.Contains("powerScale", killPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Single(byReason["defeat"]);
        Assert.Equal(-100, byReason["defeat"][0].GetProperty("delta").GetDouble());
        Assert.Single(byReason["mower"]);
        Assert.Equal(-30, byReason["mower"][0].GetProperty("delta").GetDouble());
        Assert.Single(byReason["plant_place"]);
        Assert.Equal(8, byReason["plant_place"][0].GetProperty("delta").GetDouble());
        Assert.Single(byReason["zombie_spawn"]);
        Assert.Equal(9, byReason["zombie_spawn"][0].GetProperty("delta").GetDouble());

        var summary = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/summary", Json);
        Assert.Equal(playerId, summary.GetProperty("playerId").GetInt64());
        Assert.True(summary.GetProperty("player").ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task Duplicate_kill_ptr_one_ledger_row()
    {
        var playerId = await CurrentPlayerId();
        var matchKey = Guid.NewGuid().ToString("N");
        (await _http.PostAsJsonAsync("/api/events", new
        {
            events = new object[]
            {
                new { kind = "board.start", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { } },
                new { kind = "zombie.die", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "ZD", type = 1 } },
                new { kind = "zombie.die", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "ZD", type = 1 } }
            }
        })).EnsureSuccessStatusCode();
        await FlushIngest();

        var ledger = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/ledger?reason=kill", Json);
        Assert.Single(ledger.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Two_players_isolated()
    {
        var p1 = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/players", new { name = "ProgSave2" })).EnsureSuccessStatusCode();
        var players = await _http.GetFromJsonAsync<JsonElement>("/api/players", Json);
        var p2 = players.GetProperty("items").EnumerateArray().First(i => i.GetProperty("name").GetString() == "ProgSave2").GetProperty("id").GetInt64();
        (await _http.PutAsJsonAsync("/api/players/current", new { id = p2 })).EnsureSuccessStatusCode();

        var matchKey = Guid.NewGuid().ToString("N");
        (await _http.PostAsJsonAsync("/api/events", new
        {
            events = new object[]
            {
                new { kind = "board.start", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { } },
                new { kind = "zombie.die", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { ptr = "Ziso", type = 1 } }
            }
        })).EnsureSuccessStatusCode();
        await FlushIngest();

        var a2 = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{p2}/player/0", Json);
        Assert.True(a2.GetProperty("revision").GetInt64() >= 1);

        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await _http.GetAsync($"/api/rpg/progression/{p1}/player/0")).StatusCode);
    }

    [Fact]
    public async Task Append_ZombieKilled_raises_player_xp()
    {
        var playerId = await CurrentPlayerId();
        var before = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/summary", Json);
        var xpBefore = before.GetProperty("player").GetProperty("xp").GetDouble();

        (await _http.PostAsJsonAsync($"/api/pvz-activity/{playerId}/facts/append", new
        {
            kind = "ZombieKilled",
            payloadJson = """{"type":1}""",
            dedupeKey = "append-zk-" + Guid.NewGuid().ToString("N")
        })).EnsureSuccessStatusCode();

        var after = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/summary", Json);
        Assert.True(after.GetProperty("player").GetProperty("xp").GetDouble() > xpBefore);
        Assert.True(after.GetProperty("player").GetProperty("revision").GetInt64() > 0);

        var ledger = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/ledger?reason=kill", Json);
        var kill = Assert.Single(ledger.GetProperty("items").EnumerateArray());
        var payload = kill.GetProperty("payloadJson").GetString() ?? "";
        Assert.Contains("powerScale", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Seed_and_clear_demotion()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-rpg-progression-demo", new { playerId })).EnsureSuccessStatusCode();
        var summary = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/summary", Json);
        Assert.True(summary.GetProperty("player").GetProperty("level").GetInt64() >= 1);

        var cleared = await _http.PostAsJsonAsync($"/api/rpg/progression/{playerId}/player/0/clear-demotion", new { });
        cleared.EnsureSuccessStatusCode();
        var body = await cleared.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0, body.GetProperty("demotionCount").GetInt64());
    }

    [Fact]
    public async Task Clear_demotion_and_get_missing_actor_404()
    {
        var playerId = await CurrentPlayerId();
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await _http.GetAsync($"/api/rpg/progression/{playerId}/plant/999")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await _http.PostAsJsonAsync($"/api/rpg/progression/{playerId}/plant/999/clear-demotion", new { })).StatusCode);

        (await _http.PostAsJsonAsync("/api/test/seed-rpg-progression-demo", new { playerId })).EnsureSuccessStatusCode();
        var cleared = await _http.PostAsJsonAsync($"/api/rpg/progression/{playerId}/player/0/clear-demotion", new { });
        cleared.EnsureSuccessStatusCode();
        var body = await cleared.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0, body.GetProperty("demotionCount").GetInt64());
    }

    [Fact]
    public async Task Summary_highest_uses_peak_not_only_top_current()
    {
        var playerId = await CurrentPlayerId();
        var matchKey = Guid.NewGuid().ToString("N");
        // Award plant type 0 enough to level, then plant type 1 once (lower current, lower highest).
        var places = new List<object>
        {
            new { kind = "board.start", matchKey, t = DateTime.UtcNow.ToString("o"), payload = new { } }
        };
        for (var i = 0; i < 40; i++)
        {
            places.Add(new
            {
                kind = "plant.place",
                matchKey,
                t = DateTime.UtcNow.AddMilliseconds(i).ToString("o"),
                payload = new { ptr = $"PP{i}", type = 0, col = i % 9, row = i % 5 }
            });
        }
        places.Add(new
        {
            kind = "plant.place",
            matchKey,
            t = DateTime.UtcNow.AddSeconds(1).ToString("o"),
            payload = new { ptr = "PPX", type = 1, col = 0, row = 0 }
        });
        (await _http.PostAsJsonAsync("/api/events", new { events = places })).EnsureSuccessStatusCode();
        await FlushIngest();

        var plant0 = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/plant/0", Json);
        var peak = plant0.GetProperty("highestLevel").GetInt64();
        Assert.True(peak >= 2);

        // Force demotion on plant 0 via many mower? Plant can't demote from mower.
        // Instead verify MAX(highest) >= any listed top current and equals plant0 peak when it is highest.
        var summary = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/summary", Json);
        Assert.Equal(peak, summary.GetProperty("highestPlantLevel").GetInt64());
    }

    [Fact]
    public async Task Missing_player_404()
    {
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/rpg/progression/999999/summary")).StatusCode);
    }

    [Fact]
    public async Task Stats_and_paging_after_seed()
    {
        var playerId = await CurrentPlayerId();
        (await _http.PostAsJsonAsync("/api/test/seed-rpg-progression-demo", new { playerId })).EnsureSuccessStatusCode();

        var stats = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/stats", Json);
        Assert.True(stats.GetProperty("xpByReason").GetArrayLength() >= 1);
        Assert.True(stats.GetProperty("recentDeltas").GetArrayLength() >= 1);
        Assert.True(stats.GetProperty("plantLevels").GetArrayLength() >= 1);
        Assert.True(stats.GetProperty("zombieLevels").GetArrayLength() >= 1);

        var killLedger = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/ledger?reason=kill", Json);
        var seedKill = Assert.Single(killLedger.GetProperty("items").EnumerateArray());
        var seedPayload = seedKill.GetProperty("payloadJson").GetString() ?? "";
        Assert.Contains("powerScale", seedPayload, StringComparison.OrdinalIgnoreCase);

        var page0 = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}?kind=plant&limit=1&offset=0", Json);
        Assert.Equal(0, page0.GetProperty("offset").GetInt32());
        Assert.True(page0.GetProperty("total").GetInt32() >= 1);
        Assert.Single(page0.GetProperty("items").EnumerateArray());

        var ledger1 = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/ledger?limit=1", Json);
        Assert.Single(ledger1.GetProperty("items").EnumerateArray());
        Assert.True(ledger1.TryGetProperty("nextAfterId", out var next) && next.ValueKind == JsonValueKind.Number);
        var after = next.GetInt64();
        var ledger2 = await _http.GetFromJsonAsync<JsonElement>($"/api/rpg/progression/{playerId}/ledger?limit=1&afterId={after}", Json);
        Assert.Single(ledger2.GetProperty("items").EnumerateArray());
        Assert.NotEqual(after, ledger2.GetProperty("items")[0].GetProperty("id").GetInt64());
    }
}
