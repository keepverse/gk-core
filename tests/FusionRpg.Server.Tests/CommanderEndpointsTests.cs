using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>`commander-roster` EP3.3 — `POST /api/commanders/role`, the list projected from
/// `ICommanderRoster.ForEmpire(HumanEmpireOf(save))`, and the default following a revoke
/// (spec-commander-roster.md "Routes", testing 4).
///
/// The route keeps the shape the suite already exercises (`{playerId}` today); the per-`EmpireRef`
/// re-key is `save-identity` SE4.32's, and this file asserts the projection call rather than
/// re-implementing that shape.
/// </summary>
[Collection(CommanderDirectoryCollection.Name)]
public class CommanderEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;
    ICommanderDirectory _previousDirectory = null!;

    static string AuthoredDefault =>
        CommanderDirectoryHub.Current.DefaultFor(EmpireId.Dave).StableId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        // This class composes the ROLE SOURCE into the directory, exactly as the Server does at boot, so
        // the routes and the session cache below resolve a role-holding creature. Restored on dispose.
        _previousDirectory = CommanderDirectoryHub.Current;
        var authored = DataCommanderDirectory.Parse(File.ReadAllText(Path.Combine(
            KeepverseRoots.Content(), "data", "seed", "commanders", "_registry", "default-commanders.v1.json")));
        CommanderDirectoryHub.Configure(authored.WithSource(new UniqueCommanderSource(authored, _store)));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapCommanders();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
        CommanderDirectoryHub.Configure(_previousDirectory);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    async Task<CommanderListResponse> ListAsync()
    {
        var resp = await _http.GetAsync($"/api/commanders/{_playerId}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CommanderListResponse>())!;
    }

    Task<HttpResponseMessage> SetRoleAsync(string instanceId, bool grant) =>
        _http.PostAsJsonAsync("/api/commanders/role", new SetCommanderRoleRequest
        {
            PlayerId = _playerId, InstanceId = instanceId, Grant = grant,
        });

    [Fact]
    public async Task Granting_the_role_puts_the_creature_in_the_list_and_a_revoke_takes_it_out()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", 3);
        var stableId = UniqueCommanderSource.UniquePrefix + actor.InstanceId;

        var before = await ListAsync();
        Assert.Single(before.Commanders);                       // a fresh save: the authored default only
        Assert.DoesNotContain(before.Commanders, r => r.Id == stableId);

        var granted = await SetRoleAsync(actor.InstanceId, grant: true);
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var grantedBody = (await granted.Content.ReadFromJsonAsync<CommanderRoleResponse>())!;
        Assert.Equal(actor.InstanceId, grantedBody.InstanceId);
        Assert.True(grantedBody.Granted);
        Assert.Equal(AuthoredDefault, grantedBody.DefaultLawnCommanderId);

        var after = await ListAsync();
        Assert.Contains(after.Commanders, r => r.Id == stableId);
        Assert.Equal(2, after.Commanders.Count);
        Assert.True(after.Commanders.Single(r => r.Id == AuthoredDefault).IsDefault);

        var revoked = await SetRoleAsync(actor.InstanceId, grant: false);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var end = await ListAsync();
        Assert.DoesNotContain(end.Commanders, r => r.Id == stableId);
        Assert.Single(end.Commanders);
    }

    [Fact]
    public async Task A_refused_grant_is_named_and_writes_nothing()
    {
        var resp = await SetRoleAsync("no-such-instance", grant: true);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal(RpgStore.CommanderRoleUnknown, body!["reason"]);
        Assert.Single((await ListAsync()).Commanders);          // the list is untouched
    }

    [Fact]
    public async Task Revoking_the_seated_default_resets_it_in_the_same_response()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", 7);
        var stableId = UniqueCommanderSource.UniquePrefix + actor.InstanceId;
        Assert.Equal(HttpStatusCode.OK, (await SetRoleAsync(actor.InstanceId, grant: true)).StatusCode);

        // Seat the creature: the seat route resolves and accepts it because the DIRECTORY does — no
        // production edit anywhere for a new commander (the Open/Closed property, at the route).
        var seated = await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest { PlayerId = _playerId, CommanderId = stableId });
        Assert.Equal(HttpStatusCode.OK, seated.StatusCode);
        Assert.Equal(stableId, (await _http.GetFromJsonAsync<DefaultLawnCommanderResponse>(
            $"/api/commanders/{_playerId}/default"))!.DefaultLawnCommanderId);

        var revoked = await SetRoleAsync(actor.InstanceId, grant: false);
        var body = (await revoked.Content.ReadFromJsonAsync<CommanderRoleResponse>())!;

        // The default followed the revoke in the same call — it never points at a non-commander.
        Assert.Equal(AuthoredDefault, body.DefaultLawnCommanderId);
        Assert.Equal(AuthoredDefault, (await _http.GetFromJsonAsync<DefaultLawnCommanderResponse>(
            $"/api/commanders/{_playerId}/default"))!.DefaultLawnCommanderId);
        Assert.Single((await ListAsync()).Commanders);
    }

    [Fact]
    public async Task A_started_match_snapshot_is_unchanged_by_a_revoke()
    {
        // The snapshot a run was started with is the match's, not the roster's: revoking changes what
        // the NEXT run defaults to, never the commander a started match fights under.
        var actor = _store.CreateUniqueActor(_playerId, "plant", 2);
        var stableId = UniqueCommanderSource.UniquePrefix + actor.InstanceId;
        Assert.Equal(HttpStatusCode.OK, (await SetRoleAsync(actor.InstanceId, grant: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest { PlayerId = _playerId, CommanderId = stableId })).StatusCode);

        Assert.True(CommanderDirectoryHub.Current.TryResolve(stableId, out _),
            "the configured directory must resolve the role holder before the cache is exercised");
        MatchCommanderSessionCache.Configure(CommanderDirectoryHub.Current);
        MatchCommanderSessionCache.Apply(stableId, "Creature", "Might", "Might",
            FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty);
        var snapshot = MatchCommanderSessionCache.BuildFromSessionCache();
        Assert.Equal(stableId, snapshot.LeadingCommanderId);

        Assert.Equal(HttpStatusCode.OK, (await SetRoleAsync(actor.InstanceId, grant: false)).StatusCode);

        // The snapshot a started run already holds is unchanged — the run fights under its commander.
        Assert.Equal(stableId, snapshot.LeadingCommanderId);
        // A REBUILD after the revoke is a new snapshot for the NEXT run, and the id genuinely no longer
        // resolves, so the cache reports its neutral fallback. That is not the started match changing.
        Assert.Equal(AuthoredDefault, MatchCommanderSessionCache.BuildFromSessionCache().LeadingCommanderId);
    }
}
