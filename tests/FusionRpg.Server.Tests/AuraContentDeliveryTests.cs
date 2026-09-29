using System.Net.Http.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Data.Seed;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// backlog-clear AU2 (`backlog-clear-todo.md`): "the delivery path proven end-to-end via Phase 1" —
/// the REAL shipped `gk-data/packs/fusion/data/seed/atoms/aura-content.json` + `gk-data/packs/fusion/data/seed/containers/aura.json`, imported
/// the same way a player's first launch imports them (<see cref="SeedImportRunner.RunSelfHealing"/>,
/// matching <c>ContentBootStartupWiringTests</c>'s own precedent), bound through the real
/// <c>POST /api/aura-runtime/{id}/enable</c> endpoint (BP2/BP3), and resolved through the real
/// <see cref="AtomPushService"/> compile path (the same one <c>AtomPushService.BuildExternalRefs</c>'s
/// new `aura.magnitude.*` branch feeds) — not a synthetic fixture standing in for any of the three.
/// </summary>
public class AuraContentDeliveryTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        AuraRuntimeEndpoints.ResetForTests();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var boot = SeedImportRunner.RunSelfHealing(_store, RepoRoot());
        Assert.Equal(SeedImportStatus.Imported, boot.Status);
        _store.LoadContentIntoRuntime();

        _playerId = _store.GetCurrentPlayerId();

        AuraTuningHub.Configure(
            AuraTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "aura.v1.json"))));
        PowerTuningHub.Configure(
            PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
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
            isHeld: AuraContentCatalog.IsKnown,
            isMidRun: () => false);

    async Task<AtomPushDto> PushAfterEnable(string auraId)
    {
        Equip(auraId);
        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId });
        resp.EnsureSuccessStatusCode();

        return new AtomPushService(_store).Build(
            new OwnerScope(OwnerKind.Player, _playerId.ToString()),
            new BindContext(RuntimeId.Lawn), matchSeed: 0);
    }

    static Dictionary<string, object?> FindAction(AtomPushDto push, string icdKeyOrEffectIdContains, string action)
    {
        foreach (var def in push.Defs)
        foreach (var row in def.Actions)
        {
            if (row.Action != action) continue;
            if (def.EffectId.Contains(icdKeyOrEffectIdContains, StringComparison.Ordinal)) return row.Params;
        }
        throw new InvalidOperationException(
            $"no '{action}' action found under an effect naming '{icdKeyOrEffectIdContains}' — defs: " +
            string.Join(", ", push.Defs.Select(d => d.EffectId)));
    }

    static long FlatOf(Dictionary<string, object?> parms) =>
        parms["flat"] switch
        {
            System.Text.Json.JsonElement je => Convert.ToInt64(je.GetDouble()),
            var raw => Convert.ToInt64(raw),
        };

    [Fact]
    public async Task A_single_channel_real_aura_resolves_to_the_reference_value_not_the_raw_externalRef()
    {
        var push = await PushAfterEnable("Might");

        var value = FlatOf(FindAction(push, "aura.might", EffectActions.ModifyDerivedStat));
        var expected = AuraMagnitude.ReferenceChannelValue(
            0, 1, PowerTuningHub.Tuning.Curve.PinValue, AuraTuningHub.Tuning, AptitudeTuningHub.Tuning);

        Assert.True(expected > 0, "the reference formula must produce a real, nonzero buff to be worth testing");
        Assert.Equal(expected, value);
    }

    [Fact]
    public async Task A_two_channel_real_aura_splits_evenly_and_conserves_the_total()
    {
        var push = await PushAfterEnable("Onslaught");

        // Both atoms share the icdKey "aura.onslaught", so AtomCompiler groups them into ONE EffectDef
        // with two ModifyDerivedStat action rows -- inspect both rows, not a single "first match".
        var def = Assert.Single(push.Defs.Where(d => d.EffectId.Contains("aura.onslaught", StringComparison.Ordinal)));
        var rows = def.Actions.Where(a => a.Action == EffectActions.ModifyDerivedStat).ToList();
        Assert.Equal(2, rows.Count);
        var values = rows.Select(r => FlatOf(r.Params)).OrderByDescending(v => v).ToList();

        var total = AuraMagnitude.Compute(AuraTuning.MinRung, 1.0, PowerTuningHub.Tuning.Curve.PinValue,
            AuraTuningHub.Tuning, AptitudeTuningHub.Tuning);

        Assert.Equal(total, values[0] + values[1]);
        Assert.True(values[0] - values[1] <= 1);
    }

    [Fact]
    public async Task Disabling_returns_the_channel_to_its_prior_value_via_the_real_endpoint()
    {
        await PushAfterEnable("Fortitude");
        var beforeDisable = new AtomPushService(_store).Build(
            new OwnerScope(OwnerKind.Player, _playerId.ToString()), new BindContext(RuntimeId.Lawn), matchSeed: 0);
        Assert.Contains(beforeDisable.Defs, d => d.EffectId.Contains("aura.fortitude", StringComparison.Ordinal));

        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/disable", new { auraId = "Fortitude" });
        resp.EnsureSuccessStatusCode();

        var afterDisable = new AtomPushService(_store).Build(
            new OwnerScope(OwnerKind.Player, _playerId.ToString()), new BindContext(RuntimeId.Lawn), matchSeed: 0);
        Assert.DoesNotContain(afterDisable.Defs, d => d.EffectId.Contains("aura.fortitude", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Focus_binds_and_is_honestly_inert_never_crashing()
    {
        // Focus declares zero grant channels (spec-aura-content.md S4.1) -- its container has zero
        // atoms. Binding it must succeed and produce nothing, not throw.
        Equip("Focus");
        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = "Focus" });
        resp.EnsureSuccessStatusCode();

        var bindings = _store.ListBindings(new OwnerScope(OwnerKind.Player, _playerId.ToString()));
        var focusBinding = Assert.Single(bindings.Where(b => b.Source == AuraBindingProducer.Source));
        var instance = _store.GetInstance(focusBinding.InstanceId);
        Assert.NotNull(instance);
        Assert.Empty(instance!.Atoms);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static string RepoTuningDir() => Path.Combine(RepoRoot(), "data", "tuning");

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
}
