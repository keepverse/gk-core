using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// C4b: WebMatchService through the SIM trigger — a web match produces a real run + Souls with
/// zero injector involvement, and a replayed correlation adds nothing.
/// </summary>
[Collection("e2e")]
public class WebMatchE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public WebMatchE2ETests(RpgApiFactory factory)
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
    public async Task Web_match_produces_run_and_souls_with_zero_injector()
    {
        var resp = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-e2e-1", waveId = "rift-skirmish" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("replayed").GetBoolean());
        var matchKey = body.GetProperty("matchKey").GetString();
        Assert.False(string.IsNullOrEmpty(matchKey));
        var runId = body.GetProperty("runId").GetInt64();
        Assert.True(runId > 0);

        var runs = await _http.GetFromJsonAsync<JsonElement>("/api/runs");
        var run = runs.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("matchKey").GetString() == matchKey);
        Assert.Equal("webrpg-1", run.GetProperty("game").GetString());
        Assert.NotNull(run.GetProperty("endedUtc").GetString()); // board.end closed the run

        var balance = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1");
        Assert.True(balance.GetProperty("balance").GetInt64() > 0,
            "a web victory must earn Souls through the one economy");
    }

    [Fact]
    public async Task Replayed_correlation_adds_nothing()
    {
        var first = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-replay", waveId = "rift-skirmish" });
        first.EnsureSuccessStatusCode();
        var a = await first.Content.ReadFromJsonAsync<JsonElement>();
        var balanceAfterFirst = (await _http.GetFromJsonAsync<JsonElement>("/api/souls/1"))
            .GetProperty("balance").GetInt64();
        var runsAfterFirst = (await _http.GetFromJsonAsync<JsonElement>("/api/runs"))
            .GetProperty("items").EnumerateArray().Count();

        var second = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-replay", waveId = "rift-skirmish" });
        second.EnsureSuccessStatusCode();
        var b = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(b.GetProperty("replayed").GetBoolean());
        Assert.Equal(a.GetProperty("matchKey").GetString(), b.GetProperty("matchKey").GetString());
        Assert.Equal(a.GetProperty("runId").GetInt64(), b.GetProperty("runId").GetInt64());

        var balanceAfterReplay = (await _http.GetFromJsonAsync<JsonElement>("/api/souls/1"))
            .GetProperty("balance").GetInt64();
        var runsAfterReplay = (await _http.GetFromJsonAsync<JsonElement>("/api/runs"))
            .GetProperty("items").EnumerateArray().Count();
        Assert.Equal(balanceAfterFirst, balanceAfterReplay);
        Assert.Equal(runsAfterFirst, runsAfterReplay);
    }

    [Fact]
    public async Task Replay_with_a_different_wave_is_a_mismatch_not_a_refight()
    {
        var first = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-mismatch", waveId = "rift-skirmish" });
        first.EnsureSuccessStatusCode();
        var runsAfterFirst = (await _http.GetFromJsonAsync<JsonElement>("/api/runs"))
            .GetProperty("items").EnumerateArray().Count();

        var mismatched = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-mismatch", waveId = "rift-warband" });
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        var body = await mismatched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("correlation.mismatch", body.GetProperty("reason").GetString());

        var runsAfterMismatch = (await _http.GetFromJsonAsync<JsonElement>("/api/runs"))
            .GetProperty("items").EnumerateArray().Count();
        Assert.Equal(runsAfterFirst, runsAfterMismatch); // nothing re-resolved, nothing written
    }

    [Fact]
    public async Task Unknown_wave_rejects()
    {
        var resp = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-bad", waveId = "no-such-wave" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
