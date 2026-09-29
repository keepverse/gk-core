using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FusionRpg.Contracts;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// lawn `LW1.5` (<c>lawn-playable/spec-actor-liveness-refresh.md</c>): each server verb that changes a
/// tracked input sends <b>exactly one</b> invalidation naming the right <c>kind</c>, riding the ONE
/// shared <c>AptitudesUpdated</c> transport that `SP6.6` already owns — never a second
/// <c>Player</c>-kind channel (hard edge E2).
///
/// <para><b>How it is asserted.</b> The app is a real host with the real endpoints and a real
/// <see cref="RpgStore"/>, but the hub is a capturing double: the endpoint's own
/// <c>IHubContext&lt;RpgHub&gt;</c> dependency is replaced, so every send is recorded with its group,
/// method and payload. That makes "exactly one" a count rather than a timing window, and it needs no
/// SignalR client. The double is deliberately NOT a mock of the endpoint's decision — the request
/// really travels over HTTP into the real handler.</para>
///
/// <para><b>Equip is covered at its call sites, and that is stated rather than implied.</b> A
/// successful equip needs a rolled item instance in the player's inventory, which no Server test
/// creates today (checked); building that fixture would test the item pipeline, not this
/// vocabulary. So the equip half is a source-text guard over the two handlers
/// (<c>ItemEquipEndpoints.MapItemEquip</c>) plus the shared emitter's own kind — the same
/// soundness caveat <c>DarkCarrierGuardTests</c> documents: a source-text guard is weaker than a
/// behavioural one, and it is labelled so nobody mistakes it for one.</para>
/// </summary>
[Trait("VerificationId", "server.liveness-invalidation")]
public class LivenessInvalidationTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    CapturingHubContext _hub = null!;
    string _baseUrl = "";
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        PowerTuningHub.Configure(PowerTuningLoader.Parse(
            File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));
        FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Configure(
            FusionRpg.Core.Stats.Aptitudes.AptitudeTuningLoader.Parse(File.ReadAllText(LatestAptitudesPath())));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "progression.v3.json"))));
        // The same boot-time tuning Program.cs configures, for the verbs this file exercises: the tree
        // projection needs its own hub, and a unique allocation's respec/effective-allocation gates read
        // the species-build and preset hubs (the identical list AptitudeEndpointsTests records).
        FusionRpg.Core.PassiveTree.State.PassiveTreeTuningHub.Configure(
            FusionRpg.Core.PassiveTree.State.PassiveTreeTuningLoader.Parse(
                File.ReadAllText(Path.Combine(RepoTuningDir(), "passive-tree.v1.json"))));
        FusionRpg.Core.Creatures.Generation.SpeciesBuildTuningHub.Configure(new FusionRpg.Core.Creatures.Generation.SpeciesBuildTuning(
            SchemaVersion: 1, Version: 1,
            ParityFloorPermille: 50, ParityCeilingPermille: 200,
            LeanMinPermille: 350, LeanMaxPermille: 600,
            CrowdingFactor: 633, SecondarySharePermille: 300,
            MaxAptitudesPerSpecies: 5, MinAptitudesPerSpecies: 2,
            RespecBasePrice: 50, RespecEscalationPermille: 500, RespecDecayDays: 3,
            UniqueRespecBasePrice: 50, UniqueRespecEscalationPermille: 500, UniqueRespecDecayDays: 3,
            LeanSignalWeights: FusionRpg.Core.Creatures.Generation.LeanSignalWeights.Zero));
        FusionRpg.Core.Stats.Aptitudes.AptitudePresetTuningHub.Configure(new FusionRpg.Core.Stats.Aptitudes.AptitudePresetTuning(
            1, 1, SoftMaxPresets: 32, DefaultRowAbsMax: 1000,
            AssignLadder: new FusionRpg.Core.Stats.Aptitudes.AssignLadderTuning(new[]
            {
                FusionRpg.Core.Stats.Aptitudes.AptitudeAutoAssignRules.ActivePreset,
                FusionRpg.Core.Stats.Aptitudes.AptitudeAutoAssignRules.SpeciesFavour,
                FusionRpg.Core.Stats.Aptitudes.AssignLadder.PostureRung,
                FusionRpg.Core.Stats.Aptitudes.AptitudeAutoAssignRules.Even
            })));

        _hub = new CapturingHubContext();
        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IHubContext<RpgHub>>(_hub);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.MapAptitudes();
        _app.MapPassiveTree();
        _app.MapItemEquip(new FusionRpg.Server.ItemEquipService(_store));
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Commander_allocate_sends_exactly_one_invalidation_naming_the_commander_kind()
    {
        var resp = await _http.PostAsJsonAsync("/api/aptitudes/allocate",
            new { playerId = _playerId, shares = new Dictionary<string, long> { ["Might"] = 1 } });
        resp.EnsureSuccessStatusCode();

        var sent = _hub.LivenessInvalidations();
        Assert.Single(sent);
        Assert.Equal(LivenessInvalidationWire.CommanderAllocation, sent[0].Kind);
        Assert.Equal("commander", sent[0].Scope);
        Assert.Equal(_playerId, sent[0].PlayerId);
        // One logical invalidation, delivered to the injector's group (and the web's) exactly once each.
        Assert.Equal(1, _hub.SendCount(RpgConstants.InjectorGroup, "AptitudesUpdated"));
        Assert.Equal(1, _hub.SendCount(RpgConstants.WebGroup, "AptitudesUpdated"));
    }

    [Fact]
    public async Task Unique_allocate_sends_exactly_one_invalidation_naming_the_specimen()
    {
        var specimen = _store.EnsureUniqueActorForAudit(_playerId, "ua-lw15", "plant", typeId: 5, level: 10);

        var resp = await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = specimen.InstanceId, shares = new Dictionary<string, long> { ["Might"] = 1 } });
        resp.EnsureSuccessStatusCode();

        var sent = _hub.LivenessInvalidations();
        Assert.Single(sent);
        Assert.Equal(LivenessInvalidationWire.UniqueAllocation, sent[0].Kind);
        Assert.Equal("unique", sent[0].Scope);
        Assert.Equal(specimen.InstanceId, sent[0].InstanceId);
        Assert.Equal(1, _hub.SendCount(RpgConstants.InjectorGroup, "AptitudesUpdated"));
    }

    [Fact]
    public async Task Tree_spend_sends_exactly_one_invalidation_naming_the_tree_kind()
    {
        var resp = await _http.PostAsJsonAsync("/api/passive-tree/allocate",
            new { playerId = _playerId, nodes = new Dictionary<string, int>() });
        resp.EnsureSuccessStatusCode();

        var sent = _hub.LivenessInvalidations();
        Assert.Single(sent);
        Assert.Equal(LivenessInvalidationWire.Tree, sent[0].Kind);

        // The tree's own pre-existing PassiveTreeUpdated signal is a different concern (the FE/HUD
        // refresh) and stays exactly as it was — it is not a liveness invalidation, and this test does
        // not count it as one.
        Assert.Equal(1, _hub.SendCount(RpgConstants.InjectorGroup, "PassiveTreeUpdated"));
    }

    [Fact]
    public async Task A_refused_verb_sends_no_invalidation_at_all()
    {
        var resp = await _http.PostAsJsonAsync("/api/aptitudes/unique/allocate",
            new { instanceId = "no-such-specimen", shares = new Dictionary<string, long> { ["Might"] = 1 } });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(_hub.LivenessInvalidations());
    }

    [Fact]
    public void The_equip_and_unequip_handlers_announce_the_equip_kind_on_success_only()
    {
        // A source-text guard, labelled as one: it proves the two handlers call the shared emitter
        // after a successful outcome, and that the emitter names the Equip kind — not that a real
        // equip produces it (that needs a rolled item fixture this project does not have).
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "FusionRpg.Server", "ItemEquipEndpoints.cs"));

        Assert.Equal(2, CountOccurrences(text, "await BroadcastLivenessAsync(hub, playerId, specimenId)"));
        Assert.Equal(2, CountOccurrences(text, "if (outcome.Ok)"));
        Assert.Contains("LivenessInvalidationWire.Equip", text, StringComparison.Ordinal);

        // ...and the emitter itself is the shared transport, not a private send.
        Assert.Contains("AptitudeEndpoints.BroadcastBestEffort", text, StringComparison.Ordinal);
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "collect-class-system-realrun.ps1"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}

/// <summary>One captured liveness invalidation, read off the anonymous payload
/// <c>AptitudeEndpoints.BroadcastBestEffort</c> sends.</summary>
public sealed record CapturedInvalidation(string Kind, string Scope, long PlayerId, string? InstanceId);

/// <summary>An <see cref="IHubContext{RpgHub}"/> that records every send instead of delivering it.</summary>
public sealed class CapturingHubContext : IHubContext<RpgHub>
{
    readonly List<(string Group, string Method, object? Payload)> _sends = new();

    public CapturingHubContext() => Clients = new CapturingClients(this);

    public IHubClients Clients { get; }

    public IGroupManager Groups { get; } = new NoopGroupManager();

    public int SendCount(string group, string method) =>
        _sends.Count(s => s.Group == group && s.Method == method);

    /// <summary>Every <c>AptitudesUpdated</c> send that carried a liveness <c>kind</c>, deduplicated to
    /// ONE entry per logical invalidation (the transport fans one out to two groups, which is one
    /// invalidation, not two).</summary>
    public IReadOnlyList<CapturedInvalidation> LivenessInvalidations() => _sends
        .Where(s => s.Method == "AptitudesUpdated")
        .Select(s => new CapturedInvalidation(
            Kind: PayloadField(s.Payload, "kind") ?? "",
            Scope: PayloadField(s.Payload, "scope") ?? "",
            PlayerId: long.TryParse(PayloadField(s.Payload, "playerId"), out var pid) ? pid : 0,
            InstanceId: PayloadField(s.Payload, "instanceId")))
        .Where(i => i.Kind.Length > 0)
        .Distinct()
        .ToList();

    static string? PayloadField(object? payload, string name) =>
        payload?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(payload)?.ToString();

    sealed class CapturingClients : IHubClients
    {
        readonly CapturingHubContext _owner;
        public CapturingClients(CapturingHubContext owner) => _owner = owner;

        public IClientProxy All => Proxy("");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy("");
        public IClientProxy Client(string connectionId) => Proxy("");
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy("");
        public IClientProxy Group(string groupName) => Proxy(groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy(groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy(string.Join(",", groupNames));
        public IClientProxy User(string userId) => Proxy("");
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy("");

        IClientProxy Proxy(string group) => new CapturingProxy(_owner, group);
    }

    sealed class CapturingProxy : IClientProxy
    {
        readonly CapturingHubContext _owner;
        readonly string _group;

        public CapturingProxy(CapturingHubContext owner, string group)
        {
            _owner = owner;
            _group = group;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _owner._sends.Add((_group, method, args.Length > 0 ? args[0] : null));
            return Task.CompletedTask;
        }
    }

    sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
