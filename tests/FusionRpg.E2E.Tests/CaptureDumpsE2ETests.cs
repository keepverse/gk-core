using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class CaptureDumpsE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public CaptureDumpsE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Place_counts_planted_without_spawn()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/place", new { ptr = "PX" })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        Assert.Equal(1, snap.GetProperty("runs")[0].GetProperty("plantsPlanted").GetInt32());
        Assert.Equal(0, snap.GetProperty("entities").GetArrayLength());
        Assert.Equal(0, CountKind(snap, "plant.spawn"));
    }

    [Fact]
    public async Task Two_matches_keep_first_sample_json()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "A" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { ptr = "Z1", type = 0, hp = 270, maxHp = 270 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { summary = new { } })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "B" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { ptr = "Z1", type = 0, hp = 2700, maxHp = 2700 })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        var zombieType = snap.GetProperty("types").EnumerateArray()
            .First(t => t.GetProperty("side").GetString() == "zombie" && t.GetProperty("type").GetInt32() == 0);
        Assert.Equal(270, zombieType.GetProperty("hpBase").GetInt32());
        var sample = zombieType.GetProperty("sampleJson").GetString();
        Assert.Contains("270", sample);
        Assert.DoesNotContain("2700", sample ?? "");
        var dumps = snap.GetProperty("spawnStats").EnumerateArray()
            .Where(s => s.GetProperty("side").GetString() == "zombie")
            .ToList();
        Assert.Equal(2, dumps.Count);
        var hps = dumps.Select(s => s.GetProperty("stats").GetProperty("hp").GetInt32()).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { 270, 2700 }, hps);
    }

    [Fact]
    public async Task Recapture_appends_spawn_stats_same_ptr()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { ptr = "Z1", hp = 270 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/entity/stats", new { ptr = "Z1", side = "zombie", hp = 500, source = "setHealthInTravel" })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        var runId = snap.GetProperty("runs")[0].GetProperty("id").GetInt64();
        var spawns = await _http.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/spawns", Json);
        var items = spawns.GetProperty("items").EnumerateArray()
            .Where(s => s.GetProperty("ptr").GetString() == "Z1")
            .ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("initHealth", items[0].GetProperty("source").GetString());
        Assert.Equal("setHealthInTravel", items[1].GetProperty("source").GetString());
        Assert.Equal(270, items[0].GetProperty("stats").GetProperty("hp").GetInt32());
        Assert.Equal(500, items[1].GetProperty("stats").GetProperty("hp").GetInt32());
        var spawnEvt = Events(snap).First(e => e.GetProperty("kind").GetString() == "zombie.spawn");
        Assert.Equal("initHealth", spawnEvt.GetProperty("payload").GetProperty("source").GetString());
        Assert.True(spawnEvt.GetProperty("payload").TryGetProperty("theHealth", out _));
    }

    [Fact]
    public async Task Snapshot_has_more_than_nine_keys()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelType = "Advanture", boardLevel = 3 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/snapshot", new { sunProduced = 80, totalZombieDamage = 12.5 })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        var summary = snap.GetProperty("runs")[0].GetProperty("summary");
        Assert.True(summary.TryGetProperty("sunProduced", out var produced));
        Assert.Equal(80, produced.GetInt32());
        Assert.True(summary.TryGetProperty("totalZombieDamage", out _));
        Assert.True(summary.EnumerateObject().Count() > 8);
        Assert.Equal("Advanture", snap.GetProperty("runs")[0].GetProperty("levelType").GetString());
        Assert.Equal(3, snap.GetProperty("runs")[0].GetProperty("boardLevel").GetInt32());
        var recipes = await _http.GetFromJsonAsync<JsonElement>("/api/recipes", Json);
        Assert.True(recipes.GetProperty("items").GetArrayLength() >= 1);
    }

    async Task<JsonElement> Snapshot()
    {
        var r = await _http.GetAsync("/api/test/snapshot");
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    static IEnumerable<JsonElement> Events(JsonElement snap) => snap.GetProperty("events").EnumerateArray();

    static long CountKind(JsonElement snap, string kind)
    {
        foreach (var p in snap.GetProperty("eventCounts").EnumerateObject())
            if (p.Name == kind) return p.Value.GetInt64();
        return 0;
    }
}
