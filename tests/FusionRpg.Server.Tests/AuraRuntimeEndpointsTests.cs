using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>aura-skill T18c: GET/POST /api/aura-runtime against a REAL in-process host, proving the
/// session-scoped enable/disable surface end to end -- real equip gate (via the real loadout, not a
/// mock), real FIFO eviction at the real shipped `maxActiveAuras`, real typed refusal.</summary>
public class AuraRuntimeEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        // The runtime cache is a bare static dictionary keyed by playerId (matching this codebase's
        // own PatronRuntimeState pattern for single-player, process-local session state) -- every
        // test's fresh SQLite file restarts its own autoincrement id sequence, so without a reset a
        // later test's "player 1" would inherit an earlier test's still-active aura.
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
        builder.Services.AddSingleton(_store);
        // BP3: enable/disable now calls AuraBindingProducer, which pushes via
        // UniqueActorService.PushAtomUnionAsync -- both need registering. AddSignalR() alone is enough
        // for IHubContext<RpgHub> to resolve; RpgHub itself is never constructed unless a client
        // connects, so its own (larger) dependency set is not needed here.
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
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
    public async Task Get_unknownPlayer_returns404()
    {
        var resp = await _http.GetAsync("/api/aura-runtime/999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_freshPlayer_returnsEmptyStateWithTheRealShippedCap()
    {
        var resp = await _http.GetAsync($"/api/aura-runtime/{_playerId}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<RuntimeStateDto>();
        Assert.NotNull(body);
        Assert.Empty(body!.ActiveAuraIds);
        Assert.Empty(body.EquippedAuraIds);
        Assert.Equal(AuraTuningHub.Tuning.MaxActiveAuras, body.MaxActiveAuras);
    }

    [Fact]
    public async Task Enable_unknownAuraId_returns400()
    {
        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "not-a-real-aura" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Enable_aRealAuraThatIsNotEquipped_refusesWithNotEquipped()
    {
        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Might" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("NotEquipped", body!["reason"].ToString());
    }

    [Fact]
    public async Task Enable_anEquippedAura_succeedsAndAppearsActive()
    {
        Equip("Might");

        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Might" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<EnableResultDto>();
        Assert.Equal("Might", body!.EnabledAuraId);
        Assert.Null(body.EvictedAuraId);
        Assert.Contains("Might", body.ActiveAuraIds);

        var get = await (await _http.GetAsync($"/api/aura-runtime/{_playerId}")).Content.ReadFromJsonAsync<RuntimeStateDto>();
        Assert.Contains("Might", get!.ActiveAuraIds);
        Assert.Contains("Might", get.EquippedAuraIds);
    }

    [Fact]
    public async Task Enable_atTheRealShippedCap_evictsTheOldestAndNamesIt()
    {
        // aura.v1.json ships maxActiveAuras=1 -- the second enable must evict the first, by name.
        Assert.Equal(1, AuraTuningHub.Tuning.MaxActiveAuras);
        Equip("Might", "Fortitude");

        var first = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Might" });
        first.EnsureSuccessStatusCode();

        var second = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Fortitude" });
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<EnableResultDto>();
        Assert.Equal("Fortitude", body!.EnabledAuraId);
        Assert.Equal("Might", body.EvictedAuraId); // named, not silently dropped (GG-55)
        Assert.DoesNotContain("Might", body.ActiveAuraIds);
        Assert.Contains("Fortitude", body.ActiveAuraIds);
    }

    [Fact]
    public async Task Enable_alreadyActive_refusesWithAlreadyActive()
    {
        Equip("Might");
        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Might" })).EnsureSuccessStatusCode();

        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Might" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("AlreadyActive", body!["reason"].ToString());
    }

    [Fact]
    public async Task Disable_anActiveAura_removesItAndReflectsInGet()
    {
        Equip("Might");
        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Might" })).EnsureSuccessStatusCode();

        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/disable", new { auraId = "Might" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DisableResultDto>();
        Assert.True(body!.WasActive);
        Assert.DoesNotContain("Might", body.ActiveAuraIds);

        var get = await (await _http.GetAsync($"/api/aura-runtime/{_playerId}")).Content.ReadFromJsonAsync<RuntimeStateDto>();
        Assert.Empty(get!.ActiveAuraIds);
        // Equipped-but-inactive: still equipped, just not active (spec-aura-surface.md §2.1).
        Assert.Contains("Might", get.EquippedAuraIds);
    }

    [Fact]
    public async Task Disable_anAuraThatWasNeverActive_isASafeNoOp()
    {
        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/disable", new { auraId = "Might" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DisableResultDto>();
        Assert.False(body!.WasActive);
    }

    sealed class RuntimeStateDto
    {
        public long PlayerId { get; set; }
        public List<string> ActiveAuraIds { get; set; } = new();
        public List<string> EquippedAuraIds { get; set; } = new();
        public int MaxActiveAuras { get; set; }
    }

    sealed class EnableResultDto
    {
        public string EnabledAuraId { get; set; } = "";
        public string? EvictedAuraId { get; set; }
        public List<string> ActiveAuraIds { get; set; } = new();
    }

    sealed class DisableResultDto
    {
        public string DisabledAuraId { get; set; } = "";
        public bool WasActive { get; set; }
        public List<string> ActiveAuraIds { get; set; } = new();
    }

    static string RepoTuningDir() => Path.Combine(FindRepoRoot(), "data", "tuning");

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
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "collect-class-system-realrun.ps1"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
