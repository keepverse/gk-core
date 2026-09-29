using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Tools.RpgSim;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class FoundationE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly RpgApiFactory _factory;
    readonly HttpClient _http;

    public FoundationE2ETests(RpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_sim_enabled()
    {
        var h = await _http.GetFromJsonAsync<HealthDto>("/health", Json);
        Assert.NotNull(h);
        Assert.True(h!.SimEnabled);
        Assert.Equal("none", h.Source);
        Assert.Equal(1, h.CurrentPlayerId);
    }

    [Fact]
    public async Task Stats_then_spawn_doubles_hp()
    {
        var stats = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 2f } };
        (await _http.PutAsJsonAsync("/api/stats", stats)).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "Test" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { hp = 300, maxHp = 300 })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        Assert.Equal(600, PlantHp(snap, "P1"));
        Assert.Contains(Events(snap), e => e.GetProperty("kind").GetString() == "stat.applied");
        Assert.Equal(1, Metric(snap, "plants_spawned"));
    }

    [Fact]
    public async Task Match_lifecycle_metrics_and_run()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "Life" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/place", new { ptr = "P1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/die", new { ptr = "P1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/die", new { ptr = "Z1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { summary = new { } })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        Assert.Equal(1, Metric(snap, "plants_spawned"));
        Assert.Equal(1, Metric(snap, "plants_died"));
        Assert.Equal(1, Metric(snap, "zombies_spawned"));
        Assert.Equal(1, Metric(snap, "zombies_killed"));
        Assert.Equal(1, Metric(snap, "runs_started"));
        Assert.Equal(1, Metric(snap, "runs_ended"));
        var runs = snap.GetProperty("runs");
        Assert.True(runs.GetArrayLength() >= 1);
        Assert.False(string.IsNullOrEmpty(runs[0].GetProperty("endedUtc").GetString()));
        Assert.Equal("Life", runs[0].GetProperty("levelName").GetString());
        Assert.Equal(1, runs[0].GetProperty("playerId").GetInt64());
        Assert.False(string.IsNullOrEmpty(runs[0].GetProperty("matchKey").GetString()));
        var start = Events(snap).First(e => e.GetProperty("kind").GetString() == "board.start");
        var spawn = Events(snap).First(e => e.GetProperty("kind").GetString() == "plant.spawn");
        Assert.Equal(start.GetProperty("matchKey").GetString(), spawn.GetProperty("matchKey").GetString());
        Assert.Equal(runs[0].GetProperty("matchKey").GetString(), spawn.GetProperty("matchKey").GetString());
        Assert.Equal(1, spawn.GetProperty("playerId").GetInt64());
        Assert.Equal(1, runs[0].GetProperty("plantsPlanted").GetInt32());
        Assert.Equal(1, runs[0].GetProperty("plantsDied").GetInt32());
        Assert.Equal(1, runs[0].GetProperty("zombiesKilled").GetInt32());
    }

    [Fact]
    public async Task Create_and_switch_player_stamps_next_run()
    {
        var created = await _http.PostAsJsonAsync("/api/players", new { name = "Nene" });
        created.EnsureSuccessStatusCode();
        var player = await created.Content.ReadFromJsonAsync<PlayerDto>(Json);
        Assert.NotNull(player);
        Assert.Equal("Nene", player!.Name);
        (await _http.PutAsJsonAsync("/api/players/current", new { id = player.Id })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "P2" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        Assert.Equal(player.Id, snap.GetProperty("currentPlayerId").GetInt64());
        Assert.Equal(player.Id, snap.GetProperty("runs")[0].GetProperty("playerId").GetInt64());
        Assert.Equal(player.Id, Events(snap).First(e => e.GetProperty("kind").GetString() == "plant.spawn").GetProperty("playerId").GetInt64());
    }

    [Fact]
    public async Task Mid_match_switch_keeps_open_run_player()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "Keep" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/place", new { ptr = "P1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { })).EnsureSuccessStatusCode();
        var created = await (await _http.PostAsJsonAsync("/api/players", new { name = "Other" })).Content.ReadFromJsonAsync<PlayerDto>(Json);
        (await _http.PutAsJsonAsync("/api/players/current", new { id = created!.Id })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { })).EnsureSuccessStatusCode();
        // RS-CF3 case (b): the run's counters are applied by the EventIngest DRAIN
        // (`RpgStore.cs:3394`'s `UPDATE runs SET plants_planted = plants_planted + 1`), not by the route
        // that accepted the plant — so a `/api/runs` read taken the instant the route returns is a reading
        // of a race, and this test flaked once with `plantsPlanted` still 0 (`RS-CF3`, Expected 1 / Actual
        // 0 at this line). Every OTHER test in this file reads through `Snapshot()`, which flushes
        // (`SimEndpoints.cs:147`); these two reads go straight to `/api/runs`, so the settle is said out
        // loud here with the runner's own discipline.
        await Settle();
        var runsP1Resp = await _http.GetAsync("/api/runs?playerId=1");
        runsP1Resp.EnsureSuccessStatusCode();
        var runsP1 = JsonDocument.Parse(await runsP1Resp.Content.ReadAsStringAsync()).RootElement;
        var run = runsP1.GetProperty("items")[0];
        Assert.Equal(1, run.GetProperty("playerId").GetInt64());
        Assert.Equal(1, run.GetProperty("plantsPlanted").GetInt32());
        var snap = await Snapshot();
        var zombie = Events(snap).First(e => e.GetProperty("kind").GetString() == "zombie.spawn");
        Assert.Equal(1, zombie.GetProperty("playerId").GetInt64());
        Assert.Equal(run.GetProperty("matchKey").GetString(), zombie.GetProperty("matchKey").GetString());
        var runsCurrentResp = await _http.GetAsync("/api/runs");
        runsCurrentResp.EnsureSuccessStatusCode();
        var runsCurrent = JsonDocument.Parse(await runsCurrentResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, runsCurrent.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Mower_and_match_result_project()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/mower/place", new { row = 0 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/mower/start", new { ptr = "M1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/mower/die", new { ptr = "M1" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/match/result", new { result = "victory" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/match/result", new { result = "defeat" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { summary = new { } })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        var run = snap.GetProperty("runs")[0];
        Assert.Equal("victory", run.GetProperty("result").GetString());
        Assert.Equal(1, run.GetProperty("mowersUsed").GetInt32());
        Assert.Equal(1, Metric(snap, "mowers_used"));
    }

    [Fact]
    public async Task Damage_log_off_when_disabled()
    {
        var stats = new StatsConfig { ApplyStats = true, LogDamage = false };
        (await _http.PutAsJsonAsync("/api/stats", stats)).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/damage", new { ptr = "P1", damage = 50 })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        Assert.DoesNotContain(Events(snap), e => e.GetProperty("kind").GetString() == "plant.damage");
    }

    [Fact]
    public async Task Damage_log_on_records_before_after()
    {
        var stats = new StatsConfig { ApplyStats = true, LogDamage = true, Plants = { DefensePercent = 2f } };
        (await _http.PutAsJsonAsync("/api/stats", stats)).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/damage", new { ptr = "P1", damage = 50 })).EnsureSuccessStatusCode();
        var dmg = Events(await Snapshot()).Last(e => e.GetProperty("kind").GetString() == "plant.damage");
        var payload = dmg.GetProperty("payload");
        Assert.Equal(50, payload.GetProperty("before").GetInt32());
        Assert.Equal(25, payload.GetProperty("after").GetInt32());
    }

    [Fact]
    public async Task Apply_once_via_sim()
    {
        var stats = new StatsConfig { ApplyStats = true, Plants = { HpPercent = 2f } };
        (await _http.PutAsJsonAsync("/api/stats", stats)).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { ptr = "P1", hp = 300 })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { ptr = "P1", hp = 300 })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        Assert.Equal(1, Events(snap).Count(e => e.GetProperty("kind").GetString() == "stat.applied"));
        Assert.Equal(600, PlantHp(snap, "P1"));
    }

    [Fact]
    public async Task ApplyStats_false_keeps_base_hp()
    {
        var stats = new StatsConfig { ApplyStats = false, Plants = { HpPercent = 2f } };
        (await _http.PutAsJsonAsync("/api/stats", stats)).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { hp = 300 })).EnsureSuccessStatusCode();
        Assert.Equal(300, PlantHp(await Snapshot(), "P1"));
        Assert.DoesNotContain(Events(await Snapshot()), e => e.GetProperty("kind").GetString() == "stat.applied");
    }

    [Fact]
    public async Task Armor_zero_and_positive()
    {
        var stats = new StatsConfig { ApplyStats = true, Zombies = { HpPercent = 2f } };
        (await _http.PutAsJsonAsync("/api/stats", stats)).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { hp = 270, armor = 0 })).EnsureSuccessStatusCode();
        Assert.Equal(0, ZombieArmor(await Snapshot(), "Z1"));
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        (await _http.PutAsJsonAsync("/api/stats", stats)).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { hp = 270, armor = 40, armorMax = 40 })).EnsureSuccessStatusCode();
        Assert.Equal(80, ZombieArmor(await Snapshot(), "Z1"));
    }

    [Fact]
    public async Task Bullet_bumps_metric()
    {
        (await _http.PostAsJsonAsync("/api/sim/bullet", new { })).EnsureSuccessStatusCode();
        Assert.Equal(1, Metric(await Snapshot(), "bullets_spawned"));
    }

    [Fact]
    public async Task Probe_reset_snapshot()
    {
        (await _http.PostAsJsonAsync("/api/test/probe", new { name = "n1", scenario = "s1", data = new { k = 1 } })).EnsureSuccessStatusCode();
        Assert.Contains(Events(await Snapshot()), e => e.GetProperty("kind").GetString() == "test.probe");
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(Events(await Snapshot()), e => e.GetProperty("kind").GetString() == "test.probe");
        Assert.Equal(0, Metric(await Snapshot(), "plants_spawned"));
    }

    [Fact]
    public async Task Http_fallback_events_and_heartbeat()
    {
        var env = new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Game = RpgConstants.GameId,
            Kind = "plant.spawn",
            Payload = new { type = 0, ptr = "HTTP1", hp = 300 }
        };
        (await _http.PostAsJsonAsync("/api/events", env)).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/heartbeat", new { source = "injector" })).EnsureSuccessStatusCode();
        var snap = await Snapshot();
        Assert.Contains(Events(snap), e => e.GetProperty("kind").GetString() == "plant.spawn");
        var health = snap.GetProperty("health");
        Assert.Equal("injector", health.GetProperty("source").GetString());
        Assert.True(health.GetProperty("injectorConnected").GetBoolean());
    }

    [Fact]
    public async Task Sim_409_when_live_injector()
    {
        (await _http.PostAsJsonAsync("/api/heartbeat", new { source = "injector" })).EnsureSuccessStatusCode();
        var r = await _http.PostAsJsonAsync("/api/sim/hello", new { });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task SignalR_web_receives_plant_spawn()
    {
        var received = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = _factory.Server.CreateHandler();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "/hub/rpg"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();
        hub.On<EventEnvelope>("Event", e =>
        {
            if (e.Kind == "plant.spawn") received.TrySetResult(e);
        });
        hub.On<EventBatch>("EventBatch", batch =>
        {
            var hit = batch.Events?.FirstOrDefault(e => e.Kind == "plant.spawn");
            if (hit != null) received.TrySetResult(hit);
        });
        await hub.StartAsync();
        await hub.InvokeAsync("Join", "web");
        (await _http.PostAsJsonAsync("/api/sim/plant/spawn", new { })).EnsureSuccessStatusCode();
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("plant.spawn", got.Kind);
        await hub.DisposeAsync();
    }

    /// <summary>
    /// Wait until the host's own background work has drained — the discipline the runner uses
    /// (`gk-core/tools/RpgSim/SimSettler.cs`): a projection read while an <c>EventIngest</c> drain is mid-flight is
    /// a reading of a race. Most of this file gets that for free as a side effect of <see cref="Snapshot"/>
    /// (which calls <c>ingest.FlushPendingAsync()</c>, `gk-core/src/FusionRpg.Server/SimEndpoints.cs:147`); a test
    /// that reads `/api/runs` directly has to say it out loud. A timeout is a FAILURE, never a silent pass:
    /// "I could not tell" is not "it settled".
    /// </summary>
    async Task Settle()
    {
        var report = await SimSettler.WaitAsync(_http);
        Assert.True(report.Settled, report.Reason);
    }

    async Task<JsonElement> Snapshot()
    {
        var r = await _http.GetAsync("/api/test/snapshot");
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    static IEnumerable<JsonElement> Events(JsonElement snap) => snap.GetProperty("events").EnumerateArray();

    static double Metric(JsonElement snap, string name) =>
        snap.GetProperty("metrics").EnumerateArray().First(m => m.GetProperty("name").GetString() == name)
            .GetProperty("value").GetDouble();

    static int PlantHp(JsonElement snap, string ptr) =>
        snap.GetProperty("sim").GetProperty("plants").EnumerateArray()
            .First(p => p.GetProperty("ptr").GetString() == ptr)
            .GetProperty("hp").GetInt32();

    static int ZombieArmor(JsonElement snap, string ptr) =>
        snap.GetProperty("sim").GetProperty("zombies").EnumerateArray()
            .First(z => z.GetProperty("ptr").GetString() == ptr)
            .GetProperty("armor").GetInt32();
}

[CollectionDefinition("e2e", DisableParallelization = true)]
public class E2ECollection : ICollectionFixture<RpgApiFactory> { }
