using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>commander-surface commander-list-api: empire filter, loadout intersect runtime active aura.</summary>
[Collection(CommanderDirectoryCollection.Name)]
public class CommanderListEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    /// <summary>The human empire's default commander, from the directory (SE4.1 removed the enum's
    /// stable-id switch; the registry is the one source).</summary>
    static string DefaultCommanderStableId =>
        CommanderDirectoryHub.Current.DefaultFor(EmpireId.Dave).StableId;

    static string ZombossCommanderStableId =>
        CommanderDirectoryHub.Current.DefaultFor(EmpireId.Zomboss).StableId;

    public async Task InitializeAsync()
    {
        AuraRuntimeEndpoints.ResetForTests();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        AuraTuningHub.Configure(
            AuraTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "aura.v1.json"))));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        // BP3 (spec-aura-binding-producer.md): /enable and /disable now call AuraBindingProducer via
        // UniqueActorService.PushAtomUnionAsync -- both need registering for MapAuraRuntime below.
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapCommanders();
        _app.MapLoadout();
        _app.MapAuraRuntime();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    void Equip(params string[] auraIds) =>
        _store.SetLoadout(
            new OwnerScope(OwnerKind.Player, _playerId.ToString()),
            auraIds,
            isHeld: id => AuraContentCatalog.IsKnown(id),
            isMidRun: () => false);

    [Fact]
    public async Task List_unknown_player_returns_404()
    {
        var resp = await _http.GetAsync("/api/commanders/999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task List_fresh_save_returns_Dave_only_with_default_flag()
    {
        var resp = await _http.GetAsync($"/api/commanders/{_playerId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CommanderListResponse>();
        Assert.NotNull(body);
        Assert.Equal(DefaultCommanderStableId, body!.DefaultLawnCommanderId);
        Assert.Single(body.Commanders);
        var row = body.Commanders[0];
        Assert.Equal(DefaultCommanderStableId, row.Id);
        // SE4.4 + identity-rename T13: the empty save is seeded from the lead-names registry's
        // `lead_summoner` row (the owner's onboarding name), so the expectation is the registry's own
        // value — a lead rename stays one row edit and this test follows it.
        // first commander displays the player's own name — read back through the real endpoint.
        Assert.Equal(
            FusionRpg.Core.Narrative.LeadNamesHub.Current.Display(
                FusionRpg.Core.Narrative.LeadTokens.Summoner),
            _store.GetCurrentPlayer()!.Name);
        Assert.Equal(_store.GetCurrentPlayer()!.Name, row.DisplayName);
        Assert.True(row.IsDefault);
        Assert.Null(row.ActiveAuraId);
        Assert.Null(row.ActiveAuraName);
        Assert.Null(row.LocationStub);
        Assert.Null(row.LegionStub);
        Assert.DoesNotContain(body.Commanders, r => r.Id == ZombossCommanderStableId);
    }

    [Fact]
    public async Task List_equipped_but_not_enabled_aura_has_null_active_fields()
    {
        Equip("Might");

        var resp = await _http.GetAsync($"/api/commanders/{_playerId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CommanderListResponse>();
        Assert.Null(body!.Commanders[0].ActiveAuraId);
        Assert.Null(body.Commanders[0].ActiveAuraName);
    }

    [Fact]
    public async Task List_equipped_and_enabled_Might_shows_active_aura_fields()
    {
        Equip("Might");
        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Might" }))
            .EnsureSuccessStatusCode();

        var resp = await _http.GetAsync($"/api/commanders/{_playerId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CommanderListResponse>();
        var row = body!.Commanders[0];
        Assert.Equal("Might", row.ActiveAuraId);
        Assert.Equal("Might", row.ActiveAuraName);
    }

    [Fact]
    public async Task List_never_includes_Zomboss()
    {
        var resp = await _http.GetAsync($"/api/commanders/{_playerId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CommanderListResponse>();
        Assert.DoesNotContain(body!.Commanders, r =>
            string.Equals(r.Id, ZombossCommanderStableId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_corrupt_default_row_reads_implicit_Dave_in_envelope()
    {
        Assert.True(_store.SetDefaultLawnCommanderId(_playerId, DefaultCommanderStableId).Ok);
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "UPDATE rpg_player_commander SET default_lawn_commander_id='not-a-commander' WHERE player_id=$p;";
            cmd.Parameters.AddWithValue("$p", _playerId);
            cmd.ExecuteNonQuery();
        }

        var resp = await _http.GetAsync($"/api/commanders/{_playerId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CommanderListResponse>();
        Assert.Equal(DefaultCommanderStableId, body!.DefaultLawnCommanderId);
        Assert.True(body.Commanders[0].IsDefault);
    }

    [Fact]
    public async Task List_default_envelope_matches_post_default()
    {
        var stable = DefaultCommanderStableId;
        (await _http.PostAsJsonAsync("/api/commanders/default",
            new SetDefaultLawnCommanderRequest { PlayerId = _playerId, CommanderId = stable }))
            .EnsureSuccessStatusCode();

        var body = await (await _http.GetAsync($"/api/commanders/{_playerId}"))
            .Content.ReadFromJsonAsync<CommanderListResponse>();
        Assert.Equal(stable, body!.DefaultLawnCommanderId);
        Assert.All(body.Commanders, row =>
            Assert.Equal(string.Equals(row.Id, stable, StringComparison.Ordinal), row.IsDefault));
    }

    static string RepoTuningDir() => Path.Combine(KeepverseRoots.Core(), "data", "tuning");

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
