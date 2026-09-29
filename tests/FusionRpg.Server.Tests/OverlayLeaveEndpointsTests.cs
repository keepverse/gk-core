using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Overlay;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// rift-gate overlay-hide's hard contract: the Leave endpoint asks the host to close the window and
/// touches **no** story state. The failure this exists to prevent is real — the prologue dialog itself
/// treats dismissal as a durable <c>completed|skipped</c> write, so if a close ever rode that path one
/// Esc would burn the prologue.
///
/// The command is asserted through the REAL inbox the injector polls (not a mock), so this proves the
/// actual delivery path, and the story ledger is read back through the normal onboarding endpoint.
/// </summary>
[Trait("VerificationId", "server.overlay")]
public sealed class OverlayLeaveEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    InjectorCommandInbox _inbox = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _inbox = new InjectorCommandInbox();

        // The onboarding state projection reads the progression curve, so it must be configured
        // before the endpoint can answer (same setup as OnboardingEndpointsTests).
        FusionRpg.Core.Progression.RpgXpCurve.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "progression.v3.json"))));

        var port = GetFreeTcpPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_inbox);
        builder.Services.AddSingleton<InjectorCommandSender>();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapOverlay();
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
    public async Task Leave_pushes_the_overlay_hide_command_through_the_real_inbox()
    {
        var response = await _http.PostAsync("/api/overlay/leave", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LeaveAck>();
        Assert.True(body!.Ok);

        var queued = _inbox.Drain();
        var cmd = Assert.Single(queued);
        Assert.Equal(OverlayCommandNames.Hide, cmd.Name);
        Assert.Equal("overlay.hide", cmd.Name); // the literal the injector's drain matches
    }

    /// <summary>
    /// The contract that matters: leaving is not finishing. A fresh player's story must stay unseen and
    /// eligible after a Leave, and its revision must not move.
    /// </summary>
    [Fact]
    public async Task Leave_does_not_acknowledge_the_story()
    {
        var before = await (await _http.GetAsync($"/api/onboarding/{_playerId}"))
            .Content.ReadFromJsonAsync<OnboardingStateDto>();
        var beforeStory = Assert.Single(before!.Stories);
        Assert.Equal("unseen", beforeStory.State);
        Assert.Null(beforeStory.Outcome);
        Assert.True(beforeStory.Eligible);

        var response = await _http.PostAsync("/api/overlay/leave", null);
        response.EnsureSuccessStatusCode();

        var after = await (await _http.GetAsync($"/api/onboarding/{_playerId}"))
            .Content.ReadFromJsonAsync<OnboardingStateDto>();
        var afterStory = Assert.Single(after!.Stories);
        Assert.Equal("unseen", afterStory.State);
        Assert.Null(afterStory.Outcome);
        Assert.True(afterStory.Eligible);
        Assert.Equal(beforeStory.Revision, afterStory.Revision);
        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public async Task Leave_is_idempotent_and_never_errors()
    {
        for (var i = 0; i < 3; i++)
        {
            var response = await _http.PostAsync("/api/overlay/leave", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var queued = _inbox.Drain();
        Assert.Equal(3, queued.Count);
        Assert.All(queued, c => Assert.Equal(OverlayCommandNames.Hide, c.Name));
    }

    sealed record LeaveAck(bool Ok);

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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "collect-class-system-realrun.ps1")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found");
    }
}
