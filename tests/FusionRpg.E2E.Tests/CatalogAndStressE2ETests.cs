using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class CatalogAndStressE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly string[] CatalogKinds =
    {
        "injector.hello", "catalog.types", "catalog.recipes", "board.start", "board.snapshot", "match.result", "match.win", "match.lose",
        "board.end", "wave.change", "plant.place", "plant.spawn", "plant.die", "plant.damage", "stat.applied",
        "zombie.spawn", "zombie.die", "zombie.damage", "mower.place", "mower.start", "mower.die",
        "bullet.init", "test.probe", "patch.failed"
    };

    readonly RpgApiFactory _factory;
    readonly HttpClient _http;

    public CatalogAndStressE2ETests(RpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Catalog_every_implemented_kind_one_match()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        // Per-hit telemetry is opt-in (StatsConfig.LogDamage default flipped to false) —
        // this catalog run wants every implemented kind visible, so opt in explicitly.
        (await _http.PutAsJsonAsync("/api/stats", new StatsConfig { ApplyStats = true, LogDamage = true }))
            .EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "Catalog" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/mower/place", new { row = 0 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/place", new { ptr = "P1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/wave", new { wave = 2, maxWave = 10 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/damage", new { ptr = "P1", damage = 10 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/damage", new { ptr = "Z1", damage = 10 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/bullet", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/mower/start", new { ptr = "M1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/mower/die", new { ptr = "M1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/die", new { ptr = "P1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/die", new { ptr = "Z1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/match/win", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/match/lose", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/match/result", new { result = "victory" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/match/result", new { result = "defeat" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/snapshot", new { sun = 50, wave = 2, maxWave = 10 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { summary = new { } })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/test/probe", new { name = "catalog", scenario = "kinds" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/events", new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Game = RpgConstants.GameId,
            Kind = "patch.failed",
            Payload = new { typeName = "Catalog", error = "test" }
        })).EnsureSuccessStatusCode();

        var snap = await Snapshot();
        var counts = Counts(snap);
        foreach (var kind in CatalogKinds)
            Assert.True(counts.ContainsKey(kind), "missing kind " + kind);

        var run = snap.GetProperty("runs")[0];
        Assert.Equal("victory", run.GetProperty("result").GetString());
        Assert.Equal(1, run.GetProperty("mowersUsed").GetInt32());
        Assert.Equal(1, run.GetProperty("plantsPlanted").GetInt32());
        Assert.Equal(1, run.GetProperty("plantsDied").GetInt32());
        Assert.Equal(1, run.GetProperty("zombiesKilled").GetInt32());
        var matchKey = run.GetProperty("matchKey").GetString();
        Assert.False(string.IsNullOrEmpty(matchKey));
        var spawn = Events(snap).First(e => e.GetProperty("kind").GetString() == "plant.spawn");
        Assert.Equal(matchKey, spawn.GetProperty("matchKey").GetString());
        Assert.Equal(1, spawn.GetProperty("playerId").GetInt64());
        Assert.True(spawn.GetProperty("payload").TryGetProperty("hpBase", out _));
        Assert.Equal("Peashooter", spawn.GetProperty("payload").GetProperty("typeName").GetString());

        var plant = snap.GetProperty("entities").EnumerateArray().First(e => e.GetProperty("side").GetString() == "plant");
        Assert.False(string.IsNullOrEmpty(plant.GetProperty("diedUtc").GetString()));
        Assert.Equal("Peashooter", plant.GetProperty("typeName").GetString());
        var mower = snap.GetProperty("mowers")[0];
        Assert.False(string.IsNullOrEmpty(mower.GetProperty("startedUtc").GetString()));
        Assert.False(string.IsNullOrEmpty(mower.GetProperty("diedUtc").GetString()));

        var typeItems = snap.GetProperty("types").EnumerateArray().ToList();
        Assert.Contains(typeItems, t => t.GetProperty("side").GetString() == "plant" && t.GetProperty("typeName").GetString() == "Peashooter");
        Assert.Contains(typeItems, t => t.GetProperty("side").GetString() == "zombie" && t.GetProperty("typeName").GetString() == "Zombie");
        var plantType = typeItems.First(t => t.GetProperty("side").GetString() == "plant");
        Assert.Equal(300, plantType.GetProperty("hpBase").GetInt32());
        Assert.Equal(1, plantType.GetProperty("seenCount").GetInt32());
        Assert.Equal(1, plantType.GetProperty("killedCount").GetInt32());
        Assert.True(snap.GetProperty("recipes").GetArrayLength() >= 1);
        var spawnStats = snap.GetProperty("spawnStats");
        Assert.True(spawnStats.GetArrayLength() >= 2);
        Assert.Equal("Peashooter", spawn.GetProperty("payload").GetProperty("displayName").GetString());
        Assert.Equal("start", spawn.GetProperty("payload").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Enqueue_2000_returns_fast_then_persists()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        var start = await (await _http.PostAsJsonAsync("/api/sim/board/start", new { })).Content.ReadFromJsonAsync<JsonElement>(Json);
        var matchKey = start.GetProperty("matchKey").GetString();
        await Snapshot();

        var events = Enumerable.Range(0, 2000).Select(i => new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Game = RpgConstants.GameId,
            Kind = "plant.damage",
            MatchKey = matchKey,
            Payload = new { ptr = "P1", damage = 1, i }
        }).ToList();

        var sw = Stopwatch.StartNew();
        var post = await _http.PostAsJsonAsync("/api/events", new EventBatch { Events = events });
        var enqueueMs = sw.ElapsedMilliseconds;
        post.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2000, body.GetProperty("accepted").GetInt32());
        Assert.True(enqueueMs < 100, "enqueue ms " + enqueueMs);

        sw.Restart();
        var snap = await Snapshot();
        var persistMs = sw.ElapsedMilliseconds;
        Assert.True(persistMs < 2000, "persist ms " + persistMs);
        Assert.Equal(2000, CountKind(snap, "plant.damage"));
        Assert.Equal(0, snap.GetProperty("health").GetProperty("ingestQueued").GetInt32());
    }

    [Fact]
    public async Task Batch_order_board_start_then_spawn_share_run()
    {
        var key = Guid.NewGuid().ToString();
        var batch = new EventBatch
        {
            Events =
            [
                new EventEnvelope
                {
                    T = DateTime.UtcNow.ToString("o"),
                    Game = RpgConstants.GameId,
                    Kind = "board.start",
                    MatchKey = key,
                    Payload = new { levelName = "Order" }
                },
                new EventEnvelope
                {
                    T = DateTime.UtcNow.ToString("o"),
                    Game = RpgConstants.GameId,
                    Kind = "plant.spawn",
                    MatchKey = key,
                    Payload = new { type = 0, ptr = "PX", hpBase = 300, hp = 300 }
                }
            ]
        };
        (await _http.PostAsJsonAsync("/api/events", batch)).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        var run = snap.GetProperty("runs")[0];
        Assert.Equal(key, run.GetProperty("matchKey").GetString());
        var spawn = Events(snap).First(e => e.GetProperty("kind").GetString() == "plant.spawn");
        Assert.Equal(run.GetProperty("id").GetInt64(), spawn.GetProperty("runId").GetInt64());
        Assert.Equal(key, spawn.GetProperty("matchKey").GetString());
    }

    [Fact]
    public async Task Mixed_fight_5000_hits_and_500_bullets()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        var start = await (await _http.PostAsJsonAsync("/api/sim/board/start", new { })).Content.ReadFromJsonAsync<JsonElement>(Json);
        var matchKey = start.GetProperty("matchKey").GetString();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { })).EnsureSuccessStatusCode();
        await Snapshot();

        var storm = new List<EventEnvelope>(5500);
        for (var i = 0; i < 5000; i++)
        {
            storm.Add(new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = i % 2 == 0 ? "plant.damage" : "zombie.damage",
                MatchKey = matchKey,
                Payload = new { ptr = i % 2 == 0 ? "P1" : "Z1", damage = 1 }
            });
        }
        for (var i = 0; i < 500; i++)
        {
            storm.Add(new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "bullet.init",
                MatchKey = matchKey,
                Payload = new { ptr = "B" + i }
            });
        }

        var sw = Stopwatch.StartNew();
        (await _http.PostAsJsonAsync("/api/events", new EventBatch { Events = storm })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        Assert.True(sw.ElapsedMilliseconds < 5000, "mixed persist ms " + sw.ElapsedMilliseconds);
        Assert.Equal(2500, CountKind(snap, "plant.damage"));
        Assert.Equal(2500, CountKind(snap, "zombie.damage"));
        Assert.Equal(500, CountKind(snap, "bullet.init"));
        Assert.Equal(500, Metric(snap, "bullets_spawned"));
    }

    [Fact]
    public async Task Fps120_second_9600_events()
    {
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { })).EnsureSuccessStatusCode();
        var start = await Snapshot();
        var matchKey = start.GetProperty("runs")[0].GetProperty("matchKey").GetString();
        var before = start.GetProperty("eventCount").GetInt64();

        var sw = Stopwatch.StartNew();
        for (var frame = 0; frame < 120; frame++)
        {
            var batch = Enumerable.Range(0, 80).Select(i => new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "plant.damage",
                MatchKey = matchKey,
                Payload = new { ptr = "P1", frame, i }
            }).ToList();
            (await _http.PostAsJsonAsync("/api/events", new EventBatch { Events = batch })).EnsureSuccessStatusCode();
        }
        var enqueueMs = sw.ElapsedMilliseconds;
        Assert.True(enqueueMs < 500, "120fps enqueue ms " + enqueueMs);

        sw.Restart();
        var snap = await Snapshot();
        Assert.True(sw.ElapsedMilliseconds < 5000, "120fps persist ms " + sw.ElapsedMilliseconds);
        Assert.Equal(before + 9600, snap.GetProperty("eventCount").GetInt64());
        Assert.Equal(0, snap.GetProperty("health").GetProperty("ingestQueued").GetInt32());
    }

    [Fact]
    public async Task SignalR_does_not_live_push_damage()
    {
        var damageLive = 0;
        var lifecycle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = _factory.Server.CreateHandler();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "/hub/rpg"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();
        void Note(EventEnvelope e)
        {
            if (e.Kind == "plant.damage") Interlocked.Increment(ref damageLive);
            if (e.Kind == "board.start") lifecycle.TrySetResult(true);
        }
        hub.On<EventEnvelope>("Event", Note);
        hub.On<EventBatch>("EventBatch", b =>
        {
            foreach (var e in b.Events ?? [])
                Note(e);
        });
        await hub.StartAsync();
        await hub.InvokeAsync("Join", "web");

        var key = Guid.NewGuid().ToString();
        var events = new List<EventEnvelope>
        {
            new()
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "board.start",
                MatchKey = key,
                Payload = new { levelName = "Live" }
            }
        };
        events.AddRange(Enumerable.Range(0, 2000).Select(i => new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Game = RpgConstants.GameId,
            Kind = "plant.damage",
            MatchKey = key,
            Payload = new { ptr = "P1", i }
        }));
        (await _http.PostAsJsonAsync("/api/events", new EventBatch { Events = events })).EnsureSuccessStatusCode();
        await lifecycle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        var snap = await Snapshot();
        Assert.Equal(2000, CountKind(snap, "plant.damage"));
        Assert.Equal(0, Volatile.Read(ref damageLive));
        await hub.DisposeAsync();
    }

    async Task<JsonElement> Snapshot()
    {
        var r = await _http.GetAsync("/api/test/snapshot");
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    static IEnumerable<JsonElement> Events(JsonElement snap) => snap.GetProperty("events").EnumerateArray();

    static Dictionary<string, long> Counts(JsonElement snap)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in snap.GetProperty("eventCounts").EnumerateObject())
            map[p.Name] = p.Value.GetInt64();
        return map;
    }

    static long CountKind(JsonElement snap, string kind) =>
        Counts(snap).TryGetValue(kind, out var n) ? n : 0;

    static double Metric(JsonElement snap, string name) =>
        snap.GetProperty("metrics").EnumerateArray().First(m => m.GetProperty("name").GetString() == name)
            .GetProperty("value").GetDouble();
}
