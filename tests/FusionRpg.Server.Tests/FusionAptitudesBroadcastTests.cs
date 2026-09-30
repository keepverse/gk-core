using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>species-progression `species-layer-delivery` step 6.2, trigger 5 (SP6.5) — a REAL
/// (non-replayed) <c>/api/fusion/execute</c> appends a 1b ledger row (`species-mod-ledger`, module 4)
/// and must broadcast <c>AptitudesUpdated(scope: "species")</c> to BOTH SignalR groups, the same way
/// `AptitudesInjectorBroadcastTests` already proves for a commander allocation. This is what lets the
/// injector's existing trigger-3 handler (`RpgClient.cs`, "AptitudesUpdated" -> `aptitudes.allocation.reload`
/// -> `RefreshCommanderAllocationAsync()`, extended by SP6.4 to also carry `speciesLayers`) pick up the
/// new 1b row for actors already spawned, without a reconnect.
///
/// <para>Fixture mirrors `FusionInheritancePicksTests`' own real-recipe setup (Data.Tests) — a real
/// `CreatureRecipeCatalog` recipe, two real sacrifice specimens, a real bankroll — driven here through
/// the real HTTP endpoint rather than `RpgStore.ExecuteFusion` directly, since the broadcast under test
/// lives in `FusionEndpoints.cs`, not the store.</para></summary>
public class FusionAptitudesBroadcastTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    string _baseUrl = "";

    CreatureRecipeDef _recipe = null!;
    CreatureSpeciesDef _output = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        string Read(string name) => File.ReadAllText(Path.Combine(RepoTuningDir(), name));
        FusionRpg.Core.Power.PowerTuningHub.Configure(
            FusionRpg.Core.Power.PowerTuningLoader.Parse(Read("power-scale.v2.json")));
        FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Configure(
            FusionRpg.Core.Stats.Aptitudes.AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        // ExecuteFusion's own tuning dependencies (mirroring ProgressionLayerParityTests.cs's own
        // bootstrap for the same reason -- every fusion rule reads one of these, no built-in default).
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(Read("contracts.v1.json")));
        FusionRpg.Core.Creatures.SoulEarnPolicy.Configure(
            FusionRpg.Core.Creatures.SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(Read("fusion.v2.json")));
        FusionRpg.Core.Creatures.SummonRoller.Configure(
            FusionRpg.Core.Creatures.SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        // T8.4/T8.5 (fusion-recipe-runtime): every host must call Configure before touching
        // CreatureRecipeCatalog.All -- there is no built-in default, and BuildDeterministicOnly() is
        // `internal` to FusionRpg.Data.Tests/FusionRpg.E2E.Tests only (InternalsVisibleTo.Fusion.cs) --
        // FusionRpg.Server.Tests has no grant, so this builds ONE minimal recipe by hand from the
        // compiled species default and calls the SAME public Configure(IReadOnlyList<CreatureRecipeDef>)
        // Program.cs itself calls, rather than widening that deliberately narrow internal surface for
        // this test's own convenience.
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        var eligible = CreatureSpeciesCatalog.All
            .Where(s => CreatureRarityLadder.AtLeast(s.BaseRarity, FusionRpg.Core.Creatures.Fusion.CreatureRecipeCatalog.OutputEligibilityFloor))
            .ToList();
        var inputA = eligible[0];
        var inputB = eligible.First(s => s.SpeciesId != inputA.SpeciesId);
        var output = eligible.First(s => s.SpeciesId != inputA.SpeciesId && s.SpeciesId != inputB.SpeciesId);
        _recipe = new CreatureRecipeDef("test.broadcast-recipe", output.SpeciesId, inputA.SpeciesId, inputB.SpeciesId);
        FusionRpg.Core.Creatures.Fusion.CreatureRecipeCatalog.Configure(new[] { _recipe });
        _output = CreatureSpeciesCatalog.Get(_recipe.OutputSpeciesId);

        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>(); // RpgHub ctor dependency (NS1.6)
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapFusion();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    // ---- real recipe/sacrifice fixture (mirrors FusionInheritancePicksTests.cs) --------------------

    /// <summary>A real POOLED species-passive container with roll budget — RpgStore.Fusion.cs only
    /// appends a 1b ledger row when the request carries at least one inheritance pick
    /// (`if (picks.Count > 0)`), and a forced pick needs a nonzero roll budget to land in.</summary>
    void SeedPooledSpecies(string speciesId, out string atomId)
    {
        var familyId = "atom." + speciesId.Replace(".", "-") + "-passive";
        atomId = familyId + ".t1";
        var atomRes = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.modify", FamilyId = familyId, Tier = 1,
            Name = atomId, ParamsJson = """{"channel":"maxHp","op":"flat","amount":7}""",
        });
        Assert.True(atomRes.IsOk, atomRes.ToString());
        var affixId = "affix." + speciesId + ".passive";
        var affixRes = _store.UpsertAffix(
            new AffixRow(affixId, AffixClass.Prefix, new[] { new AffixRefRow(1, atomId) }),
            _store.GetAtom);
        Assert.True(affixRes.IsOk, affixRes.ToString());
        var containerRes = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = $"species-passive.{speciesId}",
            Kind = ContainerKind.SpeciesPassive,
            PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow(affixId, 100) },
        });
        Assert.True(containerRes.IsOk, containerRes.ToString());
    }

    string Mint(string speciesId)
    {
        var species = CreatureSpeciesCatalog.Get(speciesId);
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = species.SpeciesId,
            Side = species.Side,
            GameTypeId = species.GameTypeId,
            Rarity = species.BaseRarity.ToId(),
            Variant = "normal",
            ElementPrimary = species.ElementPrimary.ToElementId(),
            ElementSecondary = species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { species.TraitPool[0] },
            Origin = "summon"
        });
        return specimen.Actor.InstanceId;
    }

    void Bankroll(long souls)
    {
        var cost = FusionCostTable.Recipe(_output.BaseRarity);
        _store.AwardSouls(1, souls, "seed", "broadcast-bank-" + Guid.NewGuid().ToString("N"));
        _store.AddCreatureMaterials(1, new[]
        {
            ("shard." + cost.ShardRarity.ToId(), (long)cost.ShardCount * 5),
            ("essence." + _output.ElementPrimary.ToElementId(), (long)cost.EssenceCount * 5),
        });
    }

    (string a, string b, string atomId) SetUpValidSacrifices()
    {
        SeedPooledSpecies(_recipe.InputSpeciesIdA, out _);
        var a = Mint(_recipe.InputSpeciesIdA);
        var b = Mint(_recipe.InputSpeciesIdB);
        var atomId = _store.PickSourceAtoms(1, _recipe.InputSpeciesIdA).Select(at => at.AtomId).First();
        return (a, b, atomId);
    }

    static readonly FusionRpg.Core.Saves.EmpireRef Owner = new(new FusionRpg.Core.Saves.SaveId(1), EmpireId.Dave);

    [Fact]
    public async Task A_real_fusion_execute_appends_a_ledger_row_and_broadcasts_to_both_groups()
    {
        var (a, b, atomId) = SetUpValidSacrifices();
        var pick = _store.GetCreatureProfile(a)!.TraitIds[0];
        // Seeded AFTER the sacrifices were read (mirrors FusionInheritancePicksTests.cs): the output
        // species' own species-passive container is what InstanceProducer.Compose rolls the forced
        // pick into.
        SeedPooledSpecies(_output.SpeciesId, out _);
        Bankroll(5000);
        Assert.Empty(_store.ListSpeciesMods(Owner)); // no ledger row before the fusion

        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<object>("AptitudesUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var postResp = await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            playerId = 1L,
            mode = FusionModes.Recipe,
            sacrifices = new[] { a, b },
            pickedTraitId = pick,
            picks = new[] { new { sourceInstanceId = a, atomId } },
            correlationId = "broadcast-test-" + Guid.NewGuid().ToString("N"),
        });
        if (!postResp.IsSuccessStatusCode)
            throw new Exception(await postResp.Content.ReadAsStringAsync());

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got, "an injector-group connection never received AptitudesUpdated after a real fusion execute");
        Assert.NotEmpty(_store.ListSpeciesMods(Owner)); // the ledger append this broadcast is FOR
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_replayed_fusion_execute_does_not_re_broadcast()
    {
        // The SAME gate the pre-existing CreaturesUpdated/SoulsUpdated broadcasts already use: a
        // replay (the same correlationId twice) appends no NEW ledger row, so there is nothing new
        // for an already-spawned actor to pick up, and no second broadcast is warranted.
        var (a, b, atomId) = SetUpValidSacrifices();
        var pick = _store.GetCreatureProfile(a)!.TraitIds[0];
        SeedPooledSpecies(_output.SpeciesId, out _);
        Bankroll(5000);
        var correlationId = "broadcast-replay-" + Guid.NewGuid().ToString("N");

        var first = await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            playerId = 1L, mode = FusionModes.Recipe, sacrifices = new[] { a, b },
            pickedTraitId = pick, picks = new[] { new { sourceInstanceId = a, atomId } }, correlationId,
        });
        if (!first.IsSuccessStatusCode)
            throw new Exception(await first.Content.ReadAsStringAsync());

        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<object>("AptitudesUpdated", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var replay = await _http.PostAsJsonAsync("/api/fusion/execute", new
        {
            playerId = 1L, mode = FusionModes.Recipe, sacrifices = new[] { a, b },
            pickedTraitId = pick, picks = new[] { new { sourceInstanceId = a, atomId } },
            correlationId, // SAME correlationId -- a replay
        });
        if (!replay.IsSuccessStatusCode)
            throw new Exception(await replay.Content.ReadAsStringAsync());

        var raced = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(received.Task, raced); // the delay won -- no broadcast arrived
        await hub.DisposeAsync();
    }

    static string RepoTuningDir() => Path.Combine(FindRepoRoot(), "data", "tuning");

    static string LatestAptitudesPath()
    {
        var dir = RepoTuningDir();
        var best = Directory.EnumerateFiles(dir, "aptitudes.v*.json")
            .Select(Path.GetFileName)
            .Select(n => (Name: n!, Match: System.Text.RegularExpressions.Regex.Match(n!, @"^aptitudes\.v(\d+)\.json$")))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value))
            .First();
        return Path.Combine(dir, best.Name);
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
