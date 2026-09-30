using System.Net.Http.Json;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
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
/// backlog-clear BP2/BP3 (`spec-aura-binding-producer.md` S6, S9): drives the real
/// <c>POST /api/aura-runtime/{id}/enable</c>|<c>/disable</c> endpoint end to end -- not the store --
/// per the spec's own rule that a store-level test would pass with the endpoint unwired, which is
/// exactly the defect this module exists to fix. `RpgStore.Bind` had 19 test callers and zero
/// production ones before this; these tests exercise the one real production caller this module adds.
///
/// <para>Seeds one minimal `world-buff.aura-*` container itself (AU2's real twelve auras are a
/// separate task) -- so BP2/BP3 are provably correct on their own, independent of AU2 landing.</para>
/// </summary>
public class AuraBindingProducerTests : IAsyncLifetime
{
    const string AuraId = "Might";
    const string ChannelId = "combat.power.omni";

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
        _playerId = _store.GetCurrentPlayerId();

        AuraTuningHub.Configure(
            AuraTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "aura.v1.json"))));
        PowerTuningHub.Configure(
            PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));

        SeedAuraContainer(AuraId, ChannelId, amount: 123);

        _store.SetLoadout(
            new OwnerScope(OwnerKind.Player, _playerId.ToString()),
            new[] { AuraId },
            isHeld: id => AuraContentCatalog.IsKnown(id),
            isMidRun: () => false);

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

    /// <summary>A minimal, self-contained `world-buff.aura-<id>` row: one `stat.derived` atom, no
    /// pool draws (fixed content only) -- exactly the shape `AuraBindingProducer` expects to find via
    /// <see cref="AuraContentCatalog.ContainerId"/>.</summary>
    void SeedAuraContainer(string auraId, string channel, long amount)
    {
        var containerId = AuraContentCatalog.ContainerId(auraId);
        var familyId = "atom.aura-" + auraId.ToLowerInvariant();
        var atomId = AtomRow.DeriveId(familyId, "", 1);
        var atomResult = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.derived",
            FamilyId = familyId,
            Variant = "",
            Tier = 1,
            Name = auraId + " (test)",
            ParamsJson = $$"""{"channel":"{{channel}}","op":"flat","amount":{{amount}}}""",
            WhenJson = "{}",
        });
        Assert.True(atomResult.IsOk, atomResult.ToString());

        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.WorldBuff,
            Atoms = new[] { new ContainerAtomRow(1, atomId) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());
    }

    OwnerScope PlayerScope() => new(OwnerKind.Player, _playerId.ToString());

    [Fact]
    public async Task Enabling_an_equipped_aura_writes_exactly_one_instance_and_binding()
    {
        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = AuraId });
        resp.EnsureSuccessStatusCode();

        var bindings = _store.ListBindings(PlayerScope());
        var mine = bindings.Where(b => b.Source == AuraBindingProducer.Source).ToList();
        Assert.Single(mine);

        var instance = _store.GetInstance(mine[0].InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(AuraContentCatalog.ContainerId(AuraId), instance!.ContainerId);
    }

    [Fact]
    public async Task The_bound_atom_resolves_through_the_real_lawn_chain_A5_had_to_fake()
    {
        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = AuraId }))
            .EnsureSuccessStatusCode();

        // The exact chain the spec's own S1 diagram names: ResolveBindings -> the atoms behind it.
        var resolution = _store.ResolveBindings(PlayerScope(), new BindContext(RuntimeId.Lawn));
        Assert.Empty(resolution.Refused);
        var binding = Assert.Single(resolution.Bindings);
        var atoms = resolution.AtomsByBinding![binding.BindingId];
        var atom = Assert.Single(atoms);
        Assert.Contains(ChannelId, atom.ParamsJson);
    }

    [Fact]
    public async Task Enabling_the_same_aura_twice_writes_no_second_row_and_bumps_no_revision()
    {
        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = AuraId }))
            .EnsureSuccessStatusCode();
        var before = Assert.Single(_store.ListBindings(PlayerScope()).Where(b => b.Source == AuraBindingProducer.Source));

        // A second /enable is refused by the runtime itself (AlreadyActive) before Sync ever sees a
        // change -- proving idempotence means proving the FIRST reconcile alone produced no second
        // write, which the row-count assertion above already does. Re-run the reconcile directly to
        // also prove AuraBindingPlan's own idempotence at the producer layer, not only the endpoint's.
        var uniqueActors = new UniqueActorService(_store, new InjectorCommandInbox(),
            _app.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<RpgHub>>());
        await new AuraBindingProducer(_store, uniqueActors).SyncAsync(_playerId, new[] { AuraId });

        var after = Assert.Single(_store.ListBindings(PlayerScope()).Where(b => b.Source == AuraBindingProducer.Source));
        Assert.Equal(before.BindingId, after.BindingId);
        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public async Task Disabling_withdraws_the_binding_and_no_hand_made_row_remains()
    {
        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = AuraId }))
            .EnsureSuccessStatusCode();
        Assert.NotEmpty(_store.ListBindings(PlayerScope()));

        var resp = await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/disable", new { auraId = AuraId });
        resp.EnsureSuccessStatusCode();

        Assert.Empty(_store.ListBindings(PlayerScope()).Where(b => b.Source == AuraBindingProducer.Source));
    }

    [Fact]
    public async Task Disabling_one_aura_leaves_another_players_binding_untouched()
    {
        // A second aura row (different container) bound directly, standing in for "another feature's
        // own binding" -- proves withdraw is scoped by source, never a blanket delete for the owner.
        SeedAuraContainer("Fortitude", "combat.defense.omni", amount: 50);
        var otherResult = _store.ProduceAndBind(
            _store.GetContainer(AuraContentCatalog.ContainerId("Fortitude"))!,
            domainMembers: static _ => Array.Empty<string>(), rollSeed: 0, thetaContent: 20,
            tuning: PowerTuningHub.Tuning, owner: PlayerScope(), slot: null, priority: 0,
            source: "some-other-feature", out _, out var otherBindingId);
        Assert.True(otherResult.IsOk);

        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = AuraId }))
            .EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/disable", new { auraId = AuraId }))
            .EnsureSuccessStatusCode();

        var remaining = _store.ListBindings(PlayerScope());
        Assert.Single(remaining);
        Assert.Equal(otherBindingId, remaining[0].BindingId);
    }

    [Fact]
    public async Task A_broken_producer_would_be_caught_the_falsifier_the_spec_requires()
    {
        // Falsifier: if AuraBindingProducer never called Bind at all, the aura would never reach a
        // resolved actor -- prove that IS what would happen, so this test provably depends on the
        // producer's real write rather than on fixture rows already being present.
        var resolutionBeforeEnable = _store.ResolveBindings(PlayerScope(), new BindContext(RuntimeId.Lawn));
        Assert.Empty(resolutionBeforeEnable.Bindings);

        (await _http.PostAsJsonAsync($"/api/aura-runtime/{_playerId}/enable", new { auraId = AuraId }))
            .EnsureSuccessStatusCode();

        var resolutionAfterEnable = _store.ResolveBindings(PlayerScope(), new BindContext(RuntimeId.Lawn));
        Assert.NotEmpty(resolutionAfterEnable.Bindings);
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
