using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class SoulsE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public SoulsE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sim_match_earns_policy_exact_souls()
    {
        // SIM plays a small match: hello → start → 3 kills → victory → end.
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "souls-e2e" })).EnsureSuccessStatusCode();
        for (var i = 0; i < 3; i++)
        {
            var ptr = $"SZ{i}";
            (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { ptr, type = 0 })).EnsureSuccessStatusCode();
            (await _http.PostAsJsonAsync("/api/sim/zombie/die", new { ptr })).EnsureSuccessStatusCode();
        }

        (await _http.PostAsJsonAsync("/api/sim/match/result", new { result = "victory" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { })).EnsureSuccessStatusCode();
        await _http.GetAsync("/api/test/snapshot"); // flush the writer

        var balance = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1");
        // 3 kills × 1 + victory 100 (first win of the day)
        Assert.Equal(103, balance.GetProperty("balance").GetInt64());

        var ledger = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1/ledger");
        Assert.Equal(4, ledger.GetProperty("items").EnumerateArray().Count());
    }

    [Fact]
    public async Task Seed_route_awards_and_balance_reads_back()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=250", new { })).EnsureSuccessStatusCode();
        var balance = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1");
        Assert.Equal(250, balance.GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Sim_victory_emits_game_driven_result_and_unlocks_ordered_onboarding_reveals()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new
        {
            levelName = "onboarding-sim",
            levelType = "Advanture",
            boardLevel = 1,
            matchKey = "onboarding-sim-victory"
        })).EnsureSuccessStatusCode();
        // 21 kills provide enough player XP to cross the level-3 teaching gate.
        for (var i = 0; i < 21; i++)
        {
            var ptr = $"ONB{i}";
            (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new
            {
                ptr,
                type = 0,
                typeName = "NormalZombie",
                source = "initHealth",
                sourceKind = "creature.progression.v1",
                sourceId = "general:normalzombie"
            })).EnsureSuccessStatusCode();
            (await _http.PostAsJsonAsync("/api/sim/zombie/die", new { ptr })).EnsureSuccessStatusCode();
        }
        var resultResponse = await _http.PostAsJsonAsync("/api/sim/match/result", new { result = "victory" });
        resultResponse.EnsureSuccessStatusCode();
        var resultBody = await resultResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("match.result", resultBody.GetProperty("events")[0].GetProperty("kind").GetString());
        Assert.Equal("victory", resultBody.GetProperty("events")[0].GetProperty("payload").GetProperty("result").GetString());
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { })).EnsureSuccessStatusCode();
        _ = await _http.GetFromJsonAsync<JsonElement>("/api/test/snapshot");

        var onboarding = await _http.GetFromJsonAsync<JsonElement>("/api/onboarding/1");
        var checkpoints = onboarding.GetProperty("checkpoints").EnumerateArray().ToList();
        Assert.Equal(new[] { "first-win-dave", "level-3-general-species" },
            checkpoints.Select(c => c.GetProperty("checkpointId").GetString()));
        Assert.All(checkpoints, checkpoint =>
        {
            Assert.Equal("earned", checkpoint.GetProperty("state").GetString());
            Assert.True(checkpoint.GetProperty("earnedRunId").GetInt64() > 0);
        });
    }
}
