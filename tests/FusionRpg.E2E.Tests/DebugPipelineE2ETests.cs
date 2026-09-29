using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class DebugPipelineE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public DebugPipelineE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
        _ = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_scenarios_contains_p1_baseline()
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>("/api/debug/scenarios", Json);
        Assert.True(doc.TryGetProperty("items", out var items));
        Assert.Contains(items.EnumerateArray(), e => e.GetString() == "p1-baseline");
    }

    [Fact]
    public async Task Session_start_empty_body_ok()
    {
        var res = await _http.PostAsJsonAsync("/api/debug/session/start", new { });
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.GetProperty("scenarioId").GetString()));
    }

    [Fact]
    public async Task Scenario_p1_baseline_enqueues_single_run_steps()
    {
        _ = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);

        var res = await _http.PostAsJsonAsync("/api/debug/scenario/p1-baseline", new { });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("debug.run-steps", body.GetProperty("command").GetString());
        Assert.True(body.GetProperty("steps").GetInt32() > 1);

        var pending = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/commands/pending", Json);
        var cmds = pending.GetProperty("items").EnumerateArray()
            .Where(e => e.GetProperty("name").GetString() == "debug.run-steps")
            .ToList();
        Assert.Single(cmds);
    }

    [Fact]
    public async Task Get_events_ok()
    {
        var res = await _http.GetAsync("/api/debug/events?limit=10");
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(doc.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task Reset_board_empty_body_ok()
    {
        var res = await _http.PostAsJsonAsync("/api/debug/reset-board", new { });
        res.EnsureSuccessStatusCode();
    }
}
