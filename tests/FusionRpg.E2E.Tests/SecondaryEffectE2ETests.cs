using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class SecondaryEffectE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public SecondaryEffectE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Secondary_butter_match_scenario_passes_via_http()
    {
        var path = Path.Combine(FindScenariosDir(), "effect-secondary-butter-match.json");
        var res = await _http.PostAsJsonAsync("/api/sim/effect/scenario", new { path });
        res.EnsureSuccessStatusCode();
        var run = await res.Content.ReadFromJsonAsync<EffectScenarioRunResult>(Json);
        Assert.NotNull(run);
        Assert.True(run!.Ok, run.Error);
        Assert.Equal("effect-secondary-butter-match", run.Id);

        (await _http.PostAsJsonAsync("/api/sim/effect/clear", new { })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Secondary_match_cycle_scenario_passes_via_http()
    {
        var path = Path.Combine(FindScenariosDir(), "effect-secondary-match-cycle.json");
        var res = await _http.PostAsJsonAsync("/api/sim/effect/scenario", new { path });
        res.EnsureSuccessStatusCode();
        var run = await res.Content.ReadFromJsonAsync<EffectScenarioRunResult>(Json);
        Assert.NotNull(run);
        Assert.True(run!.Ok, run.Error);
        Assert.Equal("effect-secondary-match-cycle", run.Id);

        (await _http.PostAsJsonAsync("/api/sim/effect/clear", new { })).EnsureSuccessStatusCode();
    }

    static string FindScenariosDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "effects", "scenarios");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures", "effects", "scenarios"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("fixtures/effects/scenarios");
    }
}
