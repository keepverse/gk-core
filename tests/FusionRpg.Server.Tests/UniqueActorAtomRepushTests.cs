using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Progression;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// T6.1 (`mods-absorption`, 2026-09-06) — the real remaining gap the audit found: the Hello-time
/// atom-push union (<see cref="AtomPushService.OwnersForSave"/>) never re-fired mid-session, so a
/// unique actor deployed or recovered after Hello never actually reached the injector's runner. Proves
/// the WHOLE chain against a real in-process host — a real ack event through the real
/// <c>/api/events</c> endpoint, through the real <see cref="EventIngest"/> background drain, into the
/// real <see cref="UniqueActorService.ObserveEvents"/> — lands a real, correctly-shaped
/// <c>effects.grants.apply</c> command in the real <see cref="InjectorCommandInbox"/>, not just that
/// <c>ObserveUniqueActorEvents</c> returns the right player id in isolation (proven separately in
/// <c>UniqueActorStoreTests</c>).
/// </summary>
public class UniqueActorAtomRepushTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        // The real Program configures the progression hub before accepting events.  This fixture
        // hosts only the event endpoint, so configure the same SSOT explicitly; otherwise a
        // zombie.die event fails its activity projection before the unique-actor recovery observer
        // can run, leaving the specimen ActiveBound and making the test depend on another class's
        // process-global tuning setup.
        var tuningPath = Path.Combine(FindRepoRoot(), "data", "tuning", "progression.v3.json");
        ProgressionTuningHub.Configure(ProgressionTuningLoader.Parse(File.ReadAllText(tuningPath)));
        var soulTuningPath = Path.Combine(FindRepoRoot(), "data", "tuning", "souls.v1.json");
        FusionRpg.Core.Creatures.SoulEarnPolicy.Configure(
            FusionRpg.Core.Creatures.SoulEarnTuningLoader.Parse(File.ReadAllText(soulTuningPath)));

        var testStore = DataTestStore.Create();
        var store = testStore.Store;

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>(); // RpgHub ctor dependency (NS1.6)
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventIngest>());
        builder.WebHost.UseUrls(baseUrl);
        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.MapHub<RpgHub>("/hub/rpg");
        app.MapUniqueActors();

        // Program.cs:842's own inline mapping — replicated exactly (never a hand-simplified stand-in)
        // so this test exercises the real request shape a real injector POST would send, not an
        // invented one.
        app.MapPost("/api/events", (JsonElement body, EventIngest ingest) =>
        {
            var accepted = 0;
            var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("events", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var env = item.Deserialize<EventEnvelope>(json);
                    if (env is not null && !string.IsNullOrWhiteSpace(env.Kind))
                        accepted += ingest.Enqueue(env);
                }
            }
            else
            {
                var env = body.Deserialize<EventEnvelope>(json);
                if (env is not null && !string.IsNullOrWhiteSpace(env.Kind))
                    accepted += ingest.Enqueue(env);
            }
            return Results.Ok(new { accepted });
        });

        await app.StartAsync();

        _testStore = testStore;
        _store = store;
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
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

    async Task PostEventAsync(string kind, string? matchKey, object payload)
    {
        var resp = await _http.PostAsJsonAsync("/api/events", new { kind, matchKey, payload });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await _app.Services.GetRequiredService<EventIngest>().FlushPendingAsync();
    }

    List<CommandDto> DrainInbox() => _app.Services.GetRequiredService<InjectorCommandInbox>().Drain(64);

    static bool IsAtomPush(CommandDto cmd) =>
        cmd.Name == EffectGrantRehydrate.ApplyCommandName
        && cmd.Payload is JsonElement p
        && p.TryGetProperty("runnerBindings", out _)
        && p.TryGetProperty("defs", out _);

    [Fact]
    public async Task An_ack_event_pushes_a_real_atom_command_into_the_inbox()
    {
        var player = _store.CreatePlayer("Owner");
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { playerId = player.Id, side = "plant", typeId = 2 });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>();
        Assert.NotNull(actor);

        var deploy = await _http.PostAsJsonAsync(
            $"/api/unique/actors/{actor!.InstanceId}/deploy",
            new { correlationId = "repush-test-corr" });
        Assert.Equal(HttpStatusCode.OK, deploy.StatusCode);

        DrainInbox(); // clear whatever the create/deploy calls themselves enqueued (pvz.spawn.extra)

        await PostEventAsync("pvz.spawn.extra.ack", "m-repush",
            new { correlationId = "repush-test-corr", ptr = "0xREPUSH" });

        Assert.Equal(UniqueActorPhases.ActiveBound, _store.GetUniqueActor(actor.InstanceId)!.Phase);

        var commands = DrainInbox();
        Assert.Contains(commands, IsAtomPush);
    }

    [Fact]
    public async Task A_die_event_after_ack_also_pushes_a_fresh_atom_command()
    {
        // Task 4C.1: zombie.die retires for real — and still reports the affected player, so the
        // atom re-push still fires (the push is keyed off the affected-player list, not the phase).
        var player = _store.CreatePlayer("Owner");
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { playerId = player.Id, side = "zombie", typeId = 3 });
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>();
        await _http.PostAsJsonAsync($"/api/unique/actors/{actor!.InstanceId}/deploy", new { correlationId = "repush-recover-corr" });
        await PostEventAsync("pvz.spawn.extra.ack", "m-recover", new { correlationId = "repush-recover-corr", ptr = "0xRECOVER" });
        DrainInbox(); // clear the ack's own push (proven above) so this test isolates the die push

        await PostEventAsync("zombie.die", "m-recover", new { ptr = "0xRECOVER" });

        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(actor.InstanceId)!.Phase);
        var commands = DrainInbox();
        Assert.Contains(commands, IsAtomPush);
    }

    // ---- T6.2: a compiled grant reaches the wire through BOTH real push paths ----------------------
    //
    // ⛔ Found 2026-09-06 and PRE-EXISTING (true of the original Player-only push, long before the
    // equip-runtime work): AtomPushCodec.BuildPayload fills AtomPushDto.Grants from catalog.Compiled,
    // and both server call sites then hand-rolled a payload dictionary carrying `defs` +
    // `runnerBindings` and dropped it -- so every passive, non-triggered atom compiled to a grant that
    // was never transmitted. A def with no grant naming it does nothing at the far end.
    //
    // These two tests drive the REAL hub and the REAL event pipeline, not the payload builder alone.

    static readonly FusionRpg.Core.Power.PowerTuning Tuning = FusionRpg.Core.Power.PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    /// <summary>One passive <c>stat.modify</c> atom (no trigger, so it COMPILES) bound to one owner.</summary>
    void SeedCompiledAtomFor(OwnerKind ownerKind, string ownerKey)
    {
        var atomId = AtomRow.DeriveId("atom.vitality", "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.modify", FamilyId = "atom.vitality", Variant = "", Tier = 1,
            Name = "atom.vitality", ParamsJson = """{"channel":"maxHp","op":"flat","amount":45}""",
            WhenJson = "{}",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "trait.stalwart",
            Kind = ContainerKind.Trait,
            Atoms = new[] { new ContainerAtomRow(1, atomId) },
        }).IsOk);

        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        Assert.True(Instantiator.TryInstantiate(_store.GetContainer("trait.stalwart")!,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst).IsOk);

        var instanceId = _store.SaveInstance(inst! with { CatalogRevision = _store.GetCatalogRevision() });
        Assert.True(_store.Bind(new BindingRow
        {
            InstanceId = instanceId, OwnerKind = ownerKind, OwnerKey = ownerKey, Priority = 0, Source = "test",
        }, Guid.NewGuid().ToString("N")).IsOk);
    }

    static List<JsonElement> GrantsOf(CommandDto cmd)
    {
        var payload = (JsonElement)cmd.Payload!;
        Assert.True(payload.TryGetProperty("grants", out var arr), "payload has no grants[]");
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        return arr.EnumerateArray().ToList();
    }

    /// <summary>2026-09-15 live: after a server restart <c>GET /api/debug/session</c> read inactive while the game was
    /// still running a lab session, so a "shipped configuration" probe trusted a contaminated board. Hello now carries
    /// the injector's session state and resyncs the in-memory mirror in both directions. (Lives in this class because it
    /// already hosts a real <see cref="RpgHub"/>.)</summary>
    [Fact]
    public async Task Hello_resyncs_the_debug_session_mirror_from_the_injector()
    {
        var hub = ActivatorUtilities.CreateInstance<RpgHub>(_app.Services);
        try
        {
            DebugSessionState.Active = false;
            DebugSessionState.ScenarioId = "";
            await hub.Hello(new HelloDto { Game = "pvz", DebugSessionActive = true, DebugScenarioId = "cae1f85426ed" });
            Assert.True(DebugSessionState.Active);
            Assert.Equal("cae1f85426ed", DebugSessionState.ScenarioId);

            await hub.Hello(new HelloDto { Game = "pvz", DebugSessionActive = false, DebugScenarioId = "stale" });
            Assert.False(DebugSessionState.Active);
            Assert.Equal("", DebugSessionState.ScenarioId);
        }
        finally
        {
            DebugSessionState.Active = false;
            DebugSessionState.ScenarioId = "";
        }
    }

    [Fact]
    public async Task A_real_Hello_puts_the_compiled_grant_on_the_wire_not_only_its_def()
    {
        // The Hello path -- RpgHub.BuildApplyCommand -- for a PLAYER-scoped compiled grant. This is
        // the case that predates every specimen: it shipped with E19 and has been inert ever since.
        SeedCompiledAtomFor(OwnerKind.Player,
            _store.GetCurrentPlayerId().ToString(System.Globalization.CultureInfo.InvariantCulture));
        DrainInbox();

        // Clients is null on a hand-built hub, so the SendAsync inside PushGrantSnapshotAsync throws
        // and is swallowed by its own catch -- exactly as it is when no injector is connected. The
        // inbox enqueue happens first and is the reliable path either way (SendInjectorCommand's own
        // comment says so), so the real command is observable here.
        var hub = ActivatorUtilities.CreateInstance<RpgHub>(_app.Services);
        await hub.Hello(new HelloDto { Game = "pvz" });

        var push = DrainInbox().Single(IsAtomPush);
        var grant = Assert.Single(GrantsOf(push));

        Assert.Equal(AtomRow.DeriveId("atom.vitality", "", 1), grant.GetProperty("effectId").GetString());
        Assert.Equal(EffectOwnerKeys.Match, grant.GetProperty("ownerKey").GetString());

        // And the def it names travels with it -- a grant whose effectId the catalog never saw makes
        // EffectBag.Grant throw at the far end.
        var payload = (JsonElement)push.Payload!;
        Assert.Contains(payload.GetProperty("defs").EnumerateArray(),
            d => d.GetProperty("effectId").GetString() == AtomRow.DeriveId("atom.vitality", "", 1));
    }

    [Fact]
    public async Task A_mid_session_repush_carries_the_deployed_specimens_own_compiled_grant()
    {
        // The other real call site -- UniqueActorService.PushAtomUnionAsync -- for a UNIQUEACTOR-scoped
        // compiled grant, carrying the per-owner key today's earlier fix stamps.
        //
        // P1.5-L (2026-09-07): this test's OWN assertion used to encode the real, previously-
        // undiscovered bug it should have caught -- it asserted `ownerKey == "instance:{id}"` as the
        // CORRECT end state, when `instance:` is exactly the one owner key the injector's own
        // `RunEffectGrant` refuses outright ("instance: forbidden in Hot; bind to entity:{ptr}"),
        // confirmed against a real running server + game. Found live, traced to
        // `AtomPushService.Build` never rewriting a UniqueActor-scoped grant's durable `instance:{id}`
        // key to the specimen's own live `entity:{ptr}` before this test was written. Fixed in
        // `AtomPushService.Build` (rewrites via `UniqueOwnerBinder.BindGrant`, using the specimen's
        // `LastPtr` — exactly what this test's own `ptr = "0xCOMPILED"` ack just set). `OwnerKind`
        // itself is intentionally left unchanged by `BindGrant` — the same behavior its one other
        // caller (`UniqueLoadoutSpec.BindToPtr`) already relies on, and the hot-path refusal check
        // reads the KEY's prefix, never this field.
        var player = _store.CreatePlayer("Owner");
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { playerId = player.Id, side = "plant", typeId = 5 });
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>();
        SeedCompiledAtomFor(OwnerKind.UniqueActor, actor!.InstanceId);

        await _http.PostAsJsonAsync($"/api/unique/actors/{actor.InstanceId}/deploy", new { correlationId = "repush-compiled-corr" });
        DrainInbox();

        await PostEventAsync("pvz.spawn.extra.ack", "m-compiled",
            new { correlationId = "repush-compiled-corr", ptr = "0xCOMPILED" });
        Assert.Equal(UniqueActorPhases.ActiveBound, _store.GetUniqueActor(actor.InstanceId)!.Phase);

        var grant = Assert.Single(GrantsOf(DrainInbox().Single(IsAtomPush)));
        Assert.Equal(AtomRow.DeriveId("atom.vitality", "", 1), grant.GetProperty("effectId").GetString());
        // "0xCOMPILED" normalizes (strip 0x, upper-invariant) to "COMPILED" -- MatchUniqueBindingsFacet.NormalizePtr.
        Assert.Equal(EffectOwnerKeys.Entity("COMPILED"), grant.GetProperty("ownerKey").GetString());
        Assert.False(FusionRpg.Core.Stats.StatApplyScope.IsInstanceOwnerKey(grant.GetProperty("ownerKey").GetString()),
            "a durable instance: key reaching the wire is exactly the live refusal this test now regresses");
    }

    [Fact]
    public async Task An_atom_repush_always_carries_a_grants_array_so_the_injector_does_not_refuse_it()
    {
        // Regression for a real bug caught before shipping: the injector's own RunEffectsGrantsApply
        // refuses the WHOLE command (never reaching InstallAtomPush) when "grants" is absent — an
        // atoms-only payload with no "grants" key at all would have silently done nothing.
        var player = _store.CreatePlayer("Owner");
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { playerId = player.Id, side = "plant", typeId = 4 });
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>();
        await _http.PostAsJsonAsync($"/api/unique/actors/{actor!.InstanceId}/deploy", new { correlationId = "repush-grants-corr" });
        DrainInbox();

        await PostEventAsync("pvz.spawn.extra.ack", "m-grants", new { correlationId = "repush-grants-corr", ptr = "0xGRANTS" });

        var push = DrainInbox().Single(IsAtomPush);
        var payload = (JsonElement)push.Payload!;
        Assert.True(payload.TryGetProperty("grants", out var grants));
        Assert.Equal(JsonValueKind.Array, grants.ValueKind);
    }
}
