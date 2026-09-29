using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Creatures;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>PT6: the patron loop in SIM — designation pricing, the match-start aura grant in the
/// effect session, and the kill-earn bonus through the real ingest.</summary>
[Collection("e2e")]
public class PatronE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public PatronE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    static readonly string SpeciesId = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly).SpeciesId;

    async Task<string> MintCreature()
    {
        var resp = await _http.PostAsJsonAsync($"/api/test/mint-creature?speciesId={SpeciesId}", new { });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("actor").GetProperty("instanceId").GetString()!;
    }

    [Fact]
    public async Task Designation_prices_switches_and_serves_the_aura()
    {
        var first = await MintCreature();
        var set = await _http.PostAsJsonAsync("/api/patron/set",
            new { instanceId = first, correlationId = "pt-e2e-1" });
        set.EnsureSuccessStatusCode();

        var state = await _http.GetFromJsonAsync<JsonElement>("/api/patron/1");
        var patron = state.GetProperty("patron");
        Assert.Equal(first, patron.GetProperty("instanceId").GetString());
        Assert.True(patron.GetProperty("aura").GetProperty("powerMilli").GetInt32() > 0);

        // Broke: switching refuses with 409; first set was free.
        var second = await MintCreature();
        var broke = await _http.PostAsJsonAsync("/api/patron/set",
            new { instanceId = second, correlationId = "pt-e2e-2" });
        Assert.Equal(HttpStatusCode.Conflict, broke.StatusCode);

        // Funded: switch succeeds and spends exactly the cost.
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=500", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/patron/set",
            new { instanceId = second, correlationId = "pt-e2e-3" })).EnsureSuccessStatusCode();
        var balance = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1");
        Assert.Equal(400, balance.GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Match_start_grants_the_aura_marker_in_the_session()
    {
        var creature = await MintCreature();
        (await _http.PostAsJsonAsync("/api/patron/set",
            new { instanceId = creature, correlationId = "pt-e2e-grant" })).EnsureSuccessStatusCode();

        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "patron-e2e" })).EnsureSuccessStatusCode();

        var grants = await (await _http.GetAsync("/api/debug/effects/session-grants")).Content.ReadAsStringAsync();
        Assert.Contains("patron:aura", grants);

        // board.end tears the session down — the marker leaves with the match.
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { })).EnsureSuccessStatusCode();
        var after = await (await _http.GetAsync("/api/debug/effects/session-grants")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("patron:aura", after);
    }

    [Fact]
    public async Task Patron_kill_earns_pay_the_tenth_kill_bonus_through_ingest()
    {
        var creature = await MintCreature();
        (await _http.PostAsJsonAsync("/api/patron/set",
            new { instanceId = creature, correlationId = "pt-e2e-earn" })).EnsureSuccessStatusCode();

        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "patron-earn" })).EnsureSuccessStatusCode();
        for (var i = 0; i < 10; i++)
        {
            var ptr = $"PE{i}";
            (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { ptr, type = 0 })).EnsureSuccessStatusCode();
            (await _http.PostAsJsonAsync("/api/sim/zombie/die", new { ptr })).EnsureSuccessStatusCode();
        }

        (await _http.PostAsJsonAsync("/api/sim/match/result", new { result = "victory" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { })).EnsureSuccessStatusCode();
        await _http.GetAsync("/api/test/snapshot"); // flush the writer

        // 10 kills: 9×1 + the 10th ×2 = 11, plus first-victory 100.
        var balance = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1");
        Assert.Equal(111, balance.GetProperty("balance").GetInt64());
    }

    [Fact]
    public async Task Unset_players_keep_the_audited_baseline()
    {
        (await _http.PostAsJsonAsync("/api/sim/hello", new { })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/start", new { levelName = "baseline" })).EnsureSuccessStatusCode();
        for (var i = 0; i < 10; i++)
        {
            var ptr = $"PB{i}";
            (await _http.PostAsJsonAsync("/api/sim/zombie/spawn", new { ptr, type = 0 })).EnsureSuccessStatusCode();
            (await _http.PostAsJsonAsync("/api/sim/zombie/die", new { ptr })).EnsureSuccessStatusCode();
        }

        (await _http.PostAsJsonAsync("/api/sim/match/result", new { result = "victory" })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/sim/board/end", new { })).EnsureSuccessStatusCode();
        await _http.GetAsync("/api/test/snapshot");

        var balance = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1");
        Assert.Equal(110, balance.GetProperty("balance").GetInt64()); // byte-identical earn v2
    }
}
