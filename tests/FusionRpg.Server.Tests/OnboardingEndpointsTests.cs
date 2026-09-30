using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Progression;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

public sealed class OnboardingEndpointsTests : IAsyncLifetime
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
        RpgXpCurve.Configure(ProgressionTuningLoader.Parse(
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
    public async Task Fresh_player_returns_empty_durable_state()
    {
        var response = await _http.GetAsync($"/api/onboarding/{_playerId}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<OnboardingStateDto>();

        Assert.NotNull(body);
        Assert.Equal(_playerId, body!.PlayerId);
        Assert.Equal(1, body.PlayerLevel);
        Assert.Equal(1, body.Revision);
        Assert.Empty(body.Checkpoints);
        var story = Assert.Single(body.Stories);
        Assert.Equal("rift-prologue", story.StoryId);
        Assert.Equal(1, story.Version);
        Assert.Equal("unseen", story.State);
        Assert.Null(story.Outcome);
        Assert.True(story.Eligible);
    }

    [Fact]
    public async Task Rift_story_acknowledgement_is_durable_idempotent_and_conflict_safe()
    {
        var path = $"/api/onboarding/{_playerId}/stories/rift-prologue/ack";
        var first = await _http.PostAsJsonAsync(path, new { version = 1, outcome = "completed" });
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<OnboardingStoryAckDto>();
        Assert.True(firstBody!.Ok);
        Assert.Equal("acknowledged", firstBody.Story!.State);
        Assert.Equal("completed", firstBody.Story.Outcome);
        Assert.Equal(2, firstBody.Story.Revision);

        var replay = await _http.PostAsJsonAsync(path, new { version = 1, outcome = "completed" });
        replay.EnsureSuccessStatusCode();
        var replayBody = await replay.Content.ReadFromJsonAsync<OnboardingStoryAckDto>();
        Assert.True(replayBody!.Ok);
        Assert.Equal(2, replayBody.Story!.Revision);

        var conflict = await _http.PostAsJsonAsync(path, new { version = 1, outcome = "skipped" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var conflictBody = await conflict.Content.ReadFromJsonAsync<OnboardingStoryAckDto>();
        Assert.Equal("onboarding.story-conflict", conflictBody!.Reason);

        var state = await (await _http.GetAsync($"/api/onboarding/{_playerId}"))
            .Content.ReadFromJsonAsync<OnboardingStateDto>();
        var story = Assert.Single(state!.Stories);
        Assert.Equal("completed", story.Outcome);
        Assert.False(story.Eligible);
        Assert.Equal(story.Revision, state.Revision);
    }

    [Fact]
    public async Task Unknown_player_returns_404()
    {
        var response = await _http.GetAsync("/api/onboarding/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Locked_checkpoint_returns_named_conflict()
    {
        var response = await _http.PostAsync(
            $"/api/onboarding/{_playerId}/checkpoints/{Uri.EscapeDataString("first-win-dave")}/claim", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OnboardingClaimDto>();
        Assert.Equal("onboarding.checkpoint-locked", body!.Reason);
        Assert.False(body.Ok);
        Assert.Null(body.Checkpoint);
    }

    [Fact]
    public async Task Earned_checkpoint_is_projected_and_claim_is_acknowledgement_only()
    {
        Assert.True(_store.TryEarnOnboardingCheckpoint(
            _playerId, "first-win-dave", 42, "fact:42", "{\"commanderId\":\"commander:dave\"}",
            "2026-01-01T00:00:00.0000000Z"));

        var state = await (await _http.GetAsync($"/api/onboarding/{_playerId}"))
            .Content.ReadFromJsonAsync<OnboardingStateDto>();
        var checkpoint = Assert.Single(state!.Checkpoints);
        Assert.Equal("earned", checkpoint.State);
        Assert.Equal(42, checkpoint.EarnedRunId);
        Assert.Equal(1, checkpoint.Revision);

        var claim = await _http.PostAsync($"/api/onboarding/{_playerId}/checkpoints/first-win-dave/claim", null);
        claim.EnsureSuccessStatusCode();
        var claimed = await claim.Content.ReadFromJsonAsync<OnboardingClaimDto>();
        Assert.True(claimed!.Ok);
        Assert.Equal("claimed", claimed.Checkpoint!.State);
        Assert.Equal(2, claimed.Checkpoint.Revision);

        var replay = await _http.PostAsync($"/api/onboarding/{_playerId}/checkpoints/first-win-dave/claim", null);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<OnboardingClaimDto>();
        Assert.Equal("onboarding.checkpoint-already-claimed", replayBody!.Reason);
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
