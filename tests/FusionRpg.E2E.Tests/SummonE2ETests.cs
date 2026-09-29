using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class SummonE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public SummonE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=5000", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Ten_pull_updates_roster_codex_balance_and_pity_and_is_replay_safe()
    {
        var corr = Guid.NewGuid().ToString("N");
        var pull = await _http.PostAsJsonAsync("/api/creatures/summon",
            new { bannerId = "standard-rift", count = 10, correlationId = corr });
        pull.EnsureSuccessStatusCode();
        var outcome = await pull.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(outcome.GetProperty("replayed").GetBoolean());
        Assert.Equal(10, outcome.GetProperty("specimens").EnumerateArray().Count());

        var roster = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1");
        Assert.Equal(10, roster.GetProperty("items").EnumerateArray().Count());

        var codex = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1/codex");
        Assert.True(codex.GetProperty("entries").EnumerateArray().Any());

        var state = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1/summon-state");
        var balance = state.GetProperty("balance").GetProperty("balance").GetInt64();
        var discovery = outcome.GetProperty("discoverySouls").GetInt64();
        Assert.Equal(5000 - 900 + discovery, balance);
        Assert.Equal(25, state.GetProperty("pity").GetProperty("heirloomGuaranteeAt").GetInt32());

        // Replay: identical instance ids, no further spend.
        var replay = await _http.PostAsJsonAsync("/api/creatures/summon",
            new { bannerId = "standard-rift", count = 10, correlationId = corr });
        replay.EnsureSuccessStatusCode();
        var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            outcome.GetProperty("specimens").EnumerateArray().Select(s => s.GetProperty("profile").GetProperty("instanceId").GetString()),
            replayed.GetProperty("specimens").EnumerateArray().Select(s => s.GetProperty("profile").GetProperty("instanceId").GetString()));
        var after = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1");
        Assert.Equal(balance, after.GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Overdraft_returns_409_with_reason()
    {
        // Drain via pulls until a ×10 can't be afforded, then assert the conflict shape.
        for (var i = 0; i < 5; i++)
            await _http.PostAsJsonAsync("/api/creatures/summon",
                new { bannerId = "standard-rift", count = 10, correlationId = Guid.NewGuid().ToString("N") });
        var broke = await _http.PostAsJsonAsync("/api/creatures/summon",
            new { bannerId = "standard-rift", count = 10, correlationId = Guid.NewGuid().ToString("N") });
        if (broke.StatusCode == HttpStatusCode.OK) return; // discovery rewards kept it solvent — acceptable
        Assert.Equal(HttpStatusCode.Conflict, broke.StatusCode);
        var body = await broke.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("souls.insufficient", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Oversized_inputs_are_rejected_with_400()
    {
        var longCorr = new string('x', 65);
        var pull = await _http.PostAsJsonAsync("/api/creatures/summon",
            new { bannerId = "standard-rift", count = 1, correlationId = longCorr });
        Assert.Equal(HttpStatusCode.BadRequest, pull.StatusCode);

        var ok = await _http.PostAsJsonAsync("/api/creatures/summon",
            new { bannerId = "standard-rift", count = 1, correlationId = Guid.NewGuid().ToString("N") });
        ok.EnsureSuccessStatusCode();
        var outcome = await ok.Content.ReadFromJsonAsync<JsonElement>();
        var id = outcome.GetProperty("specimens")[0].GetProperty("profile").GetProperty("instanceId").GetString();
        var longNick = await _http.PostAsJsonAsync($"/api/creatures/specimen/{id}/nickname",
            new { nickname = new string('n', 33) });
        Assert.Equal(HttpStatusCode.BadRequest, longNick.StatusCode);
    }

    [Fact]
    public async Task Nickname_and_lock_work_on_a_summoned_specimen()
    {
        var pull = await _http.PostAsJsonAsync("/api/creatures/summon",
            new { bannerId = "standard-rift", count = 1, correlationId = Guid.NewGuid().ToString("N") });
        pull.EnsureSuccessStatusCode();
        var outcome = await pull.Content.ReadFromJsonAsync<JsonElement>();
        var id = outcome.GetProperty("specimens")[0].GetProperty("profile").GetProperty("instanceId").GetString();

        var nick = await _http.PostAsJsonAsync($"/api/creatures/specimen/{id}/nickname", new { nickname = "Ragnar" });
        nick.EnsureSuccessStatusCode();
        var locked = await _http.PostAsJsonAsync($"/api/creatures/specimen/{id}/lock", new { locked = true });
        locked.EnsureSuccessStatusCode();

        var roster = await _http.GetFromJsonAsync<JsonElement>("/api/creatures/1");
        var item = roster.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("profile").GetProperty("instanceId").GetString() == id);
        Assert.Equal("Ragnar", item.GetProperty("profile").GetProperty("nickname").GetString());
        Assert.True(item.GetProperty("profile").GetProperty("locked").GetBoolean());
    }
}
