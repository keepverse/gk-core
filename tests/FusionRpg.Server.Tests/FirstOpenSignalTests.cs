using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// rift-gate first-open-signal: the durable once-per-player "the FE has been opened" fact.
///
/// The load-bearing properties are (a) durability + idempotency keyed on player_id, (b) that the
/// fact is NOT keyed on the embed marker or any route — it is keyed on the FE being opened, and
/// (c) that actionability is the server's own injector-connectivity read rather than a guess.
///
/// Capture is a separate program: nothing here asserts a capture happened.
/// </summary>
public sealed class FirstOpenSignalTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        FusionRpg.Core.Progression.RpgXpCurve.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "progression.v3.json"))));

        var port = GetFreeTcpPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapOnboarding();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task A_fresh_player_has_no_first_open_fact()
    {
        var body = await (await _http.GetAsync($"/api/onboarding/{_playerId}/first-open"))
            .Content.ReadFromJsonAsync<FirstOpenDto>();

        Assert.False(body!.Opened);
        Assert.Null(body.OpenedUtc);
        Assert.Equal(0, body.Revision);
        Assert.False(body.Actionable);
    }

    [Fact]
    public async Task Recording_the_first_open_is_durable_and_idempotent()
    {
        var first = await (await _http.PostAsync($"/api/onboarding/{_playerId}/first-open", null))
            .Content.ReadFromJsonAsync<FirstOpenDto>();
        Assert.True(first!.Opened);
        Assert.NotNull(first.OpenedUtc);
        var openedUtc = first.OpenedUtc;

        // A second open is a no-op: same row, same timestamp, same revision — never a conflict.
        var second = await (await _http.PostAsync($"/api/onboarding/{_playerId}/first-open", null))
            .Content.ReadFromJsonAsync<FirstOpenDto>();
        Assert.True(second!.Opened);
        Assert.Equal(openedUtc, second.OpenedUtc);
        Assert.Equal(first.Revision, second.Revision);

        // And it survives a fresh read (durable, not in-request state).
        var readBack = await (await _http.GetAsync($"/api/onboarding/{_playerId}/first-open"))
            .Content.ReadFromJsonAsync<FirstOpenDto>();
        Assert.True(readBack!.Opened);
        Assert.Equal(openedUtc, readBack.OpenedUtc);
    }

    [Fact]
    public async Task Actionability_flips_only_once_an_injector_connects()
    {
        await _http.PostAsync($"/api/onboarding/{_playerId}/first-open", null);

        // No heartbeat yet: the fact is recorded but not actionable — and that is the honest state,
        // not a failure. It stays pending.
        var pending = await (await _http.GetAsync($"/api/onboarding/{_playerId}/first-open"))
            .Content.ReadFromJsonAsync<FirstOpenDto>();
        Assert.True(pending!.Opened);
        Assert.False(pending.Actionable);

        _store.Heartbeat(RpgConstants.SourceInjector);

        var ready = await (await _http.GetAsync($"/api/onboarding/{_playerId}/first-open"))
            .Content.ReadFromJsonAsync<FirstOpenDto>();
        Assert.True(ready!.Opened);
        Assert.True(ready.Actionable);
    }

    [Fact]
    public async Task An_unopened_player_is_reported_unactionable_even_with_an_injector_live()
    {
        // Connected injector, but the FE has never been opened: nothing to act on.
        _store.Heartbeat(RpgConstants.SourceInjector);

        var body = await (await _http.GetAsync($"/api/onboarding/{_playerId}/first-open"))
            .Content.ReadFromJsonAsync<FirstOpenDto>();
        Assert.False(body!.Opened);
        Assert.False(body.Actionable);
    }

    [Fact]
    public async Task First_open_writes_no_story_state()
    {
        var before = await (await _http.GetAsync($"/api/onboarding/{_playerId}"))
            .Content.ReadFromJsonAsync<OnboardingStateDto>();
        var beforeStory = Assert.Single(before!.Stories);

        await _http.PostAsync($"/api/onboarding/{_playerId}/first-open", null);

        var after = await (await _http.GetAsync($"/api/onboarding/{_playerId}"))
            .Content.ReadFromJsonAsync<OnboardingStateDto>();
        var afterStory = Assert.Single(after!.Stories);
        Assert.Equal(beforeStory.State, afterStory.State);
        Assert.Equal(beforeStory.Outcome, afterStory.Outcome);
        Assert.Equal(beforeStory.Revision, afterStory.Revision);
    }

    [Fact]
    public async Task Unknown_player_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/onboarding/999999/first-open")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PostAsync("/api/onboarding/999999/first-open", null)).StatusCode);
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
