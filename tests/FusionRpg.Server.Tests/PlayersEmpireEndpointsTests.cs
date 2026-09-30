using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// `save-identity` SE4.31 — the REST surface of a save's empires, against a REAL, minimal in-process
/// host (`EmpireEndpointsTests`' own pattern; the route mappers are the production ones).
///
/// <para>Four facts, each a contract this module owns: a save's empires are readable as data; a
/// specimen carries the empire that owns it, so a Zomboss specimen is present under
/// <c>?empire=&lt;ai empire&gt;</c> and absent from the human's roster; an id that is not this save's
/// empire is a 404, never another save's rows; and a <b>Tier B</b> route asked for a non-human empire
/// answers <c>409 empire_scope_not_widened</c> rather than serving the human's row under that empire's
/// name.</para>
///
/// <para>Which empires a save has is a registry (a population): no test here asserts a row COUNT or the
/// literal ids <c>dave</c>/<c>zomboss</c>. It asserts exactly-one-human (the contract), that the wire
/// carries the same rows the store has, and that the human row is the one the store resolves.</para>
/// </summary>
[Trait("VerificationId", "server.players-empires")]
public class PlayersEmpireEndpointsTests : IAsyncLifetime
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public PlayersEmpireEndpointsTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        // MintForEmpire -> SummonRoller.RollTraits -> FusionRoller.SlotsFor reads StarPolicy.Tuning,
        // which this assembly's [ModuleInitializer] bootstrap does not cover — configured from the real
        // shipped file, the same gap and the same fix AtomPushServiceOwnersForSaveTests records.
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "fusion.v2.json"))));
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find repo root above " + AppContext.BaseDirectory);
    }

    public async Task InitializeAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.MapEmpireRoutes();
        _app.MapUniqueActors();
        _app.MapSouls();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    long Save => _store.GetCurrentPlayerId();

    string HumanId => _store.HumanEmpireOf(Save).Value;

    string AiId => _store.EmpiresOf(Save).First(e => e.Controller == EmpireController.Ai).Empire.Value;

    CreatureSpecimenDto MintFor(string empireId, ulong seed) =>
        _store.MintForEmpire(new EmpireRef(new SaveId(Save), new FusionRpg.Core.Commanders.EmpireId(empireId)),
            CreatureSpeciesCatalog.All.First(s => s.DeployMode != CreatureDeployMode.HypnoAlly).SpeciesId, seed);

    [Fact]
    public async Task A_saves_empires_read_as_data_through_the_route()
    {
        var items = await _http.GetFromJsonAsync<List<SaveEmpireDto>>($"/api/players/{Save}/empires");

        Assert.NotNull(items);
        // The wire carries the store's own rows — same set, same controllers — never a code list. Each
        // token is parsed back through the registry's own closed-vocabulary parser, so a wire spelling
        // that drifted from the authored `human`/`ai` tokens fails here rather than in a consumer.
        Assert.Equal(
            _store.EmpiresOf(Save).Select(e => (Id: e.Empire.Value, e.Controller)).OrderBy(x => x.Id),
            items!.Select(e => (Id: e.EmpireId, Controller: NewSaveEmpires.Controller(e.Controller)))
                .OrderBy(x => x.Id));
        // The contract, not a population: exactly one human empire, and it is the one the store resolves.
        Assert.Single(items, e => e.Controller == EmpireControllerTokens.Human);
        Assert.Equal(HumanId, items.Single(e => e.Controller == EmpireControllerTokens.Human).EmpireId);
        Assert.All(items, e => Assert.False(string.IsNullOrWhiteSpace(e.EmpireId)));
    }

    [Fact]
    public async Task An_unknown_save_reads_404()
    {
        var resp = await _http.GetAsync("/api/players/999999/empires");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// The classification made observable: one specimen per empire of the SAME save, and the roster read
    /// separates them. A Zomboss specimen is in the human roster's exclusion (its owner is not the human
    /// empire) and is present under its own empire — which is the only way a consumer can see it.
    /// </summary>
    [Fact]
    public async Task A_Zomboss_specimen_is_absent_from_the_human_roster_and_present_under_its_own_empire()
    {
        var ai = MintFor(AiId, seed: 40);
        var human = MintFor(HumanId, seed: 41);

        var humanRoster = await _http.GetFromJsonAsync<UniqueActorListDto>(
            $"/api/unique/actors?playerId={Save}");
        Assert.NotNull(humanRoster);
        Assert.Contains(humanRoster!.Items, i => i.InstanceId == human.Actor.InstanceId);
        Assert.DoesNotContain(humanRoster.Items, i => i.InstanceId == ai.Actor.InstanceId);
        // Each specimen carries its OWN owner on the wire.
        Assert.Equal(HumanId, humanRoster.Items.Single(i => i.InstanceId == human.Actor.InstanceId).EmpireId);

        // The default is the human empire: an explicit `?empire=<human>` is the same read.
        var explicitHuman = await _http.GetFromJsonAsync<UniqueActorListDto>(
            $"/api/unique/actors?playerId={Save}&empire={HumanId}");
        Assert.Equal(humanRoster.Items.Select(i => i.InstanceId).OrderBy(x => x),
            explicitHuman!.Items.Select(i => i.InstanceId).OrderBy(x => x));

        var aiRoster = await _http.GetFromJsonAsync<UniqueActorListDto>(
            $"/api/unique/actors?playerId={Save}&empire={AiId}");
        Assert.NotNull(aiRoster);
        Assert.Contains(aiRoster!.Items, i => i.InstanceId == ai.Actor.InstanceId);
        Assert.DoesNotContain(aiRoster.Items, i => i.InstanceId == human.Actor.InstanceId);
        Assert.Equal(AiId, aiRoster.Items.Single(i => i.InstanceId == ai.Actor.InstanceId).EmpireId);

        // The single-specimen read carries it too (`ReadUniqueActorUnlocked` maps the same column).
        var single = await _http.GetFromJsonAsync<UniqueActorDto>($"/api/unique/actors/{ai.Actor.InstanceId}");
        Assert.Equal(AiId, single!.EmpireId);
    }

    [Fact]
    public async Task An_empire_that_is_not_this_saves_reads_404_never_another_saves_rows()
    {
        var resp = await _http.GetAsync($"/api/unique/actors?playerId={Save}&empire=not-an-empire");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("empire_not_found", body.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// The Tier B contract (spec-save-identity.md §Contracts): `rpg_soul_ledger`/`rpg_soul_balances`
    /// keep the save's key, so a route over them must not answer for another empire. The refusal happens
    /// before the read, and the absent/`human` forms are unchanged.
    /// </summary>
    [Fact]
    public async Task A_Tier_B_route_refuses_a_non_human_empire_with_409()
    {
        var absent = await _http.GetAsync($"/api/souls/{Save}");
        Assert.Equal(HttpStatusCode.OK, absent.StatusCode);

        var explicitHuman = await _http.GetAsync($"/api/souls/{Save}?empire={HumanId}");
        Assert.Equal(HttpStatusCode.OK, explicitHuman.StatusCode);

        var refused = await _http.GetAsync($"/api/souls/{Save}?empire={AiId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("empire_scope_not_widened", body.RootElement.GetProperty("error").GetString());

        // An id that is no empire of this save is still a 404, not a widening refusal.
        var unknown = await _http.GetAsync($"/api/souls/{Save}?empire=not-an-empire");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
