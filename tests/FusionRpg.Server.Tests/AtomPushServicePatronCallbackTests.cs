using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Creatures.Patron;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// T6.2b (`patron-absorption`, 2026-09-06) — the spec's own "Revised testing strategy" table names
/// three tests at exactly this level (<see cref="AtomPushService"/> against a real <see cref="RpgStore"/>,
/// not <c>PatronAbsorptionGridEqualityTests</c>' isolated <c>AtomCompiler.Compile</c> call) that were
/// never actually written when the grid test shipped.
///
/// <para><b>A real, found-not-hypothetical gap.</b> Re-reading the spec's own table after the grid
/// test passed surfaced that <see cref="AtomPushService.Build"/>'s <c>ResolveBindings</c> loop can
/// NEVER discover <c>patron.aura</c>'s atoms for any owner — nothing ever creates a
/// <c>BindingRow</c> for it (a patron is designated via <c>RpgStore.SetPatron</c>, which writes
/// <c>rpg_patron</c>, never a binding, unlike gear/traits). Proving the compile MATH is right (the
/// grid test) never proved the DEF reaches a real push. Fixed the same session by adding
/// <c>AtomPushService.PatronAuraAtoms()</c> plus an isolated-compile, defs-only merge in
/// <c>Build</c> — these tests prove that wiring end to end, against a real store.</para>
///
/// <para><b>PT7b (2026-09-20) adds the OTHER producer's tests here.</b> <c>patron:aura</c> has two
/// producers: the injector's own match-start plugin (<c>PatronSecondaryPlugin</c>, whose scope is pinned
/// by <c>FusionRpg.Core.Tests.PatronAuraScopeTests</c>) and
/// <see cref="PatronEndpoints.TryBuildPatronSessionGrant"/> — the session grant the server upserts at
/// every `board.start` and re-ships on Hello/reconnect. That second one stamped `match` until now, and
/// because the injector's bag is keyed by grant id, its re-push overwrote the narrow scope the plugin had
/// just set: the aura reached the zombies in a real game (the live PT7 finding). The tests below enter
/// through the real producer and the real wire, never a hand-built DTO — the defect WAS the producer, so
/// a store-only or Core-only test cannot see it.</para>
/// </summary>
public class AtomPushServicePatronCallbackTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly AtomPushService _push;

    public AtomPushServicePatronCallbackTests()
    {
        // MintCreature's real call chain auto-binds a contract for the new specimen, which needs these
        // three policies configured — not covered by this assembly's [ModuleInitializer] bootstrap,
        // same combination BuildSquadEquippedActionsTests.cs already needs for the same reason.
        var tuningDir = Path.Combine(RepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        SummoningTuningHub.Configure(SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        ContractPolicy.Configure(ContractTuningLoader.Parse(Read("contracts.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        // PatronEndpoints.Compute -> ServerPowerIndexProvider.ActorIndex -> GetRpgProgressionSummary
        // needs this too — the player's own Θ read is part of the real live lookup being proven here.
        Core.Progression.ProgressionTuningHub.Configure(
            Core.Progression.ProgressionTuningLoader.Parse(Read("progression.v3.json")));
        // The real AuraMilli formula itself — the whole point of this suite.
        Core.Creatures.Patron.PatronPolicy.Configure(
            Core.Creatures.Patron.PatronTuningLoader.Parse(Read("patron.v1.json")));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        SeedRealPatronAuraContent();
        _push = new AtomPushService(_store);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>The REAL committed `gk-data/packs/fusion/data/seed/atoms/patron-aura.json` + the real `patron.aura` shape,
    /// loaded from disk rather than hand-typed — this suite is worthless if it silently drifts from
    /// what the game actually ships. Mirrors `PatronAbsorptionGridEqualityTests`' own load pattern.</summary>
    void SeedRealPatronAuraContent()
    {
        var root = RepoRoot();
        var atomsPath = Path.Combine(root, "data", "seed", "atoms", "patron-aura.json");
        var collected = AtomSeedFile.Collect(new[] { (atomsPath, File.ReadAllText(atomsPath)) });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        // The exact count (12 today, one per element x power/defense) is a growing content fact, never
        // pinned (population-pin SE3.4, 2026-09-20) -- same disposition as MigrationParityTests' own
        // patron.aura atom count.
        Assert.NotEmpty(collected.Content.Atoms);

        foreach (var atom in collected.Content.Atoms)
            Assert.True(_store.UpsertAtom(atom).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "patron.aura",
            Kind = ContainerKind.Patron,
            Atoms = collected.Content.Atoms
                .Select((a, i) => new ContainerAtomRow(i + 1, a.AtomId))
                .ToList(),
        }).IsOk);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static readonly CreatureSpeciesDef FireSpecies = CreatureSpeciesCatalog.All
        .First(s => s.ElementPrimary.ToElementId() == "fire" && s.Acquisition != CreatureAcquisition.CaptureOnly);
    static readonly CreatureSpeciesDef DarkSpecies = CreatureSpeciesCatalog.All
        .First(s => s.ElementPrimary.ToElementId() == "dark" && s.Acquisition != CreatureAcquisition.CaptureOnly);

    string Mint(CreatureSpeciesDef species)
    {
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
            Origin = "summon",
        });
        return specimen.Actor.InstanceId;
    }

    static OwnerScope PlayerOwner(long id) =>
        new(OwnerKind.Player, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    static BindContext Lawn() => new(RuntimeId.Lawn);

    static EffectDefDto PatronDef(AtomPushDto payload) =>
        Assert.Single(payload.Defs, d => d.EffectId == "fx.patron_aura");

    static long Flat(EffectDefDto def, string channel) =>
        Convert.ToInt64(def.Actions.Single(a => Equals(a.Params.GetValueOrDefault("channel"), channel)).Params["flat"]);

    [Fact]
    public void A_player_with_a_patron_set_gets_the_real_live_aura_baked_into_the_push()
    {
        var creature = Mint(FireSpecies);
        Assert.True(_store.SetPatron(1, creature, "corr-1").Ok);
        var expected = PatronEndpoints.Compute(_store, 1);
        Assert.NotNull(expected);

        var payload = _push.Build(PlayerOwner(1), Lawn(), matchSeed: 1);

        var def = PatronDef(payload);
        Assert.Equal(expected!.Value.Aura.PowerMilli, Flat(def, "combat.power.fire"));
        Assert.Equal(expected.Value.Aura.DefenseMilli, Flat(def, "combat.defense.fire"));
    }

    [Fact]
    public void A_player_with_no_patron_set_still_gets_the_def_but_every_channel_is_zero_not_a_throw()
    {
        var payload = _push.Build(PlayerOwner(1), Lawn(), matchSeed: 1);

        var def = PatronDef(payload);
        Assert.All(def.Actions, a => Assert.Equal(0L, Convert.ToInt64(a.Params["flat"])));
    }

    [Fact]
    public void The_auto_generated_compile_grant_is_never_pushed_only_the_def_the_plugin_grants_separately()
    {
        // AtomCompiler.Compile always emits at least a match-scoped grant per compiled group (its own
        // "grantOwnerKeys" doc: "Null is the shipped behaviour verbatim: one grant per ICD group at
        // Match"). AtomPushService.Build deliberately discards that auto-grant for patron.aura and
        // merges only its Defs — PatronSecondaryPlugin's own existing grant (GrantId "patron:aura")
        // must stay the SOLE grant, or the actor would carry two grants naming the same EffectId and
        // double the aura (GrantedDerivedAtomReader has no de-dup across grants).
        var creature = Mint(FireSpecies);
        Assert.True(_store.SetPatron(1, creature, "corr-2").Ok);

        var payload = _push.Build(PlayerOwner(1), Lawn(), matchSeed: 1);

        Assert.DoesNotContain(payload.Grants, g => g.EffectId == "fx.patron_aura");
        PatronDef(payload); // still asserts the def itself IS present, just ungranted by this path
    }

    [Fact]
    public void Two_pushes_after_a_patron_switch_reflect_the_new_designations_aura_immediately()
    {
        // "A promotion" (the spec's own named scenario) needs a real, rarity-matched, soul- and
        // material-funded fusion — a switch to a DIFFERENT designated patron proves the identical
        // underlying property this test exists for (nothing about the aura's rarity/star/level/Θ
        // inputs is cached or frozen between pushes) far more reliably than depending on fusion RNG.
        _store.AwardSouls(1, 500, "seed", "patron-push-bank");
        var first = Mint(FireSpecies);
        Assert.True(_store.SetPatron(1, first, "corr-3").Ok);
        var firstExpected = PatronEndpoints.Compute(_store, 1)!.Value.Aura;

        var firstPayload = _push.Build(PlayerOwner(1), Lawn(), matchSeed: 1);
        Assert.Equal(firstExpected.PowerMilli, Flat(PatronDef(firstPayload), "combat.power.fire"));

        var second = Mint(DarkSpecies);
        Assert.True(_store.SetPatron(1, second, "corr-4").Ok); // funded switch
        var secondExpected = PatronEndpoints.Compute(_store, 1)!.Value.Aura;
        Assert.NotEqual(firstExpected.ElementPrimary, secondExpected.ElementPrimary);

        // No receiverRevision echoed — a fresh cold rebuild, exactly like a reconnect after the switch.
        var secondPayload = _push.Build(PlayerOwner(1), Lawn(), matchSeed: 2);
        var secondDef = PatronDef(secondPayload);
        Assert.Equal(secondExpected.PowerMilli, Flat(secondDef, "combat.power.dark"));
        // And fire (the OLD patron's element) is back to zero — no leftover from the first push.
        Assert.Equal(0L, Flat(secondDef, "combat.power.fire"));
    }

    // ── PT7b: the session grant's own scope, through the real producer and the real wire ──────────

    /// <summary>
    /// The session grants AS THE INJECTOR RECEIVES THEM, built by nothing but production code:
    /// <c>EventIngest.Enqueue(board.start)</c> → <c>PatronEndpoints.TryBuildPatronSessionGrant</c> →
    /// <c>EffectGrantSession</c> → <c>AtomPushService.BuildApplyPayload</c> (the same payload
    /// <c>RpgHub.PushGrantSnapshotAsync</c> puts on the wire) → JSON → <c>EffectGrantDto</c>. No part of
    /// the grant is hand-built, which is the point: the defect was a second producer, so the proof has
    /// to sit on the producer.
    /// </summary>
    List<EffectGrantDto> SessionGrantsAsShipped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton(_store);
        services.AddSingleton<EffectGrantSession>();
        services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        services.AddSingleton<CompactionWorker>();
        services.AddSingleton<InjectorCommandInbox>();
        services.AddSingleton<UniqueActorService>();
        services.AddSingleton<EventIngest>();
        using var provider = services.BuildServiceProvider();

        // The real pvzrh ingress path that upserts the marker — `EventIngest.Enqueue`'s own board.start
        // arm, not a call to TryBuildPatronSessionGrant with its trigger skipped.
        provider.GetRequiredService<EventIngest>().Enqueue(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Game = RpgConstants.GameId39,
            Kind = "board.start",
            Payload = new { },
        });

        var payload = AtomPushService.BuildApplyPayload(
            atoms: null, provider.GetRequiredService<EffectGrantSession>().Snapshot());

        // Serialize/reparse the payload the way the hub's JSON protocol hands it to the injector: a key
        // this wire type carried would be a key the game never sees.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.True(doc.RootElement.TryGetProperty("grants", out var grants),
            "effects.grants.apply refuses the whole command without grants[]");
        return JsonSerializer.Deserialize<List<EffectGrantDto>>(grants.GetRawText())!;
    }

    /// <summary>The aura's own channel — the primary element's power, the channel the live PT7 probe
    /// read back at the aura magnitude on **both** sides (the defect this test pins shut).</summary>
    const string PatronAuraChannel = "combat.power.fire";

    /// <summary>The real compiled defs the production push carries for the patron aura — the catalog
    /// <c>EffectBag.Grant</c> needs to exist before a grant can land. Read from
    /// <see cref="AtomPushService.Build"/> (the committed seed atoms through the real `AtomCompiler`),
    /// not a synthetic stand-in.</summary>
    IEnumerable<EffectDef> RealPatronAuraDefs() =>
        _push.Build(PlayerOwner(1), Lawn(), matchSeed: 1).Defs.Select(AtomPushCodec.ToDef);

    static InMemoryEffectCatalog RealPatronAuraCatalog(IEnumerable<EffectDef> defs)
    {
        var catalog = new InMemoryEffectCatalog();
        foreach (var def in defs) catalog.Upsert(def);
        return catalog;
    }

    static StatContext PlantRead(int typeId) =>
        new() { Side = StatSide.Plant, TypeId = typeId, EntityKey = "P1" };

    static StatContext ZombieRead(int typeId) =>
        new() { Side = StatSide.Zombie, TypeId = typeId, EntityKey = "Z1" };

    static bool ReachesChannel(IReadOnlyList<BoundDerivedAtom> atoms, string channel) =>
        atoms.Any(a => string.Equals(a.Channel, channel, StringComparison.Ordinal));

    /// <summary>
    /// ⭐ PT7b's acceptance line. The grant the SERVER ships is plant-side, and the zombie-side lookup
    /// that the live PT7 probe found the aura answering no longer finds it. The last block is the
    /// non-vacuity control: the same DTO re-keyed to the old `match` scope IS found by the zombie lookup,
    /// so the empty result above is about the key, not about the query shape.
    /// </summary>
    [Fact]
    public void The_session_grant_the_server_ships_is_plant_side_and_a_zombie_lookup_misses_it()
    {
        var creature = Mint(FireSpecies);
        Assert.True(_store.SetPatron(1, creature, "corr-pt7b").Ok);

        var grant = Assert.Single(SessionGrantsAsShipped(), g => g.GrantId == "patron:aura");

        // (1) the key the wire carries, and the ownerKind that has to name the same side: the reader
        // looks a plant up as ("plant", "plant:{typeId}") and ForOwner filters on BOTH fields, so a
        // plant:* key under ownerKind `match` would be found by neither query.
        Assert.Equal("plant:*", grant.OwnerKey);
        Assert.Equal(EffectOwnerKey.PlantSide, grant.OwnerKey);
        Assert.Equal(OwnerScope.Name(OwnerKind.Plant), grant.OwnerKind);

        // (2) the injector's own conversion of that wire DTO, into the store the derived reader
        // queries — then the reader itself, which is the chain a lawn actor's derived channel comes
        // from (GrantedDerivedAtomReader -> AtomDerivedSubsystem -> ActorHub).
        var catalog = RealPatronAuraCatalog(RealPatronAuraDefs());
        var bag = new InMemoryEffectGrantStore();
        bag.Upsert(EffectGrant.FromDto(grant));

        // side-wide, not type-keyed: any plant type id is answered, on the plant side only.
        Assert.Single(bag.ForOwner(OwnerScope.Name(OwnerKind.Plant), EffectOwnerKeys.PlantType(3)));
        Assert.Single(bag.ForOwner(OwnerScope.Name(OwnerKind.Plant), EffectOwnerKeys.PlantType(999)));
        Assert.Empty(bag.ForOwner(OwnerScope.Name(OwnerKind.Zombie), EffectOwnerKeys.ZombieType(3)));
        Assert.Empty(bag.ForOwner(OwnerScope.Name(OwnerKind.Zombie), EffectOwnerKeys.ZombieType(999)));

        Assert.True(ReachesChannel(
            GrantedDerivedAtomReader.Read(bag, catalog, PlantRead(3)), PatronAuraChannel), "plant read");
        Assert.Empty(GrantedDerivedAtomReader.Read(bag, catalog, ZombieRead(3)));

        // (3) the same key at the apply gate — `plant:*` is one side, deliberately not match-wide.
        Assert.False(StatApplyScope.IsMatchWide(grant.OwnerKey));
        Assert.True(StatApplyScope.Matches(grant.OwnerKey,
            new StatContext { Side = StatSide.Plant, TypeId = 3 }));
        Assert.False(StatApplyScope.Matches(grant.OwnerKey,
            new StatContext { Side = StatSide.Zombie, TypeId = 3 }));

        // (4) non-vacuity: the SAME DTO re-keyed to the scope this defect shipped DOES reach the zombie
        // read (and answers the `("match", "match")` lookup the reader performs for every entity,
        // which is how a match-scoped grant reached both sides). So the empty zombie read in (2) is the
        // key, not a reader that never returns anything.
        var asMatch = EffectGrant.FromDto(grant);
        asMatch.OwnerKey = EffectOwnerKeys.Match;
        asMatch.OwnerKind = EffectOwnerKeys.Match;
        var matchBag = new InMemoryEffectGrantStore();
        matchBag.Upsert(asMatch);
        Assert.Single(matchBag.ForOwner(OwnerScope.Name(OwnerKind.Match), EffectOwnerKeys.Match));
        Assert.True(ReachesChannel(
            GrantedDerivedAtomReader.Read(matchBag, catalog, ZombieRead(3)), PatronAuraChannel), "zombie read");
    }

    /// <summary>
    /// The two producers cannot drift apart again: the injector's own match-start plugin and the
    /// server's session endpoint must stamp the SAME scope, or the later write silently re-scopes the
    /// bag entry the earlier one set (exactly how PT7 shipped a zombie-buffing "plant-side" aura).
    /// The server copy is the one that survives a reconnect — the plugin's `OnMatchStart` never replays
    /// — so they converge only while they agree.
    /// </summary>
    [Fact]
    public void The_injector_plugin_and_the_server_endpoint_stamp_the_same_patron_aura_scope()
    {
        var creature = Mint(FireSpecies);
        Assert.True(_store.SetPatron(1, creature, "corr-pt7b-agree").Ok);
        var server = Assert.Single(SessionGrantsAsShipped(), g => g.GrantId == "patron:aura");

        // Both sides compared as the wire DTO each producer stamps — the scope fields are what the
        // injector's grant bag converges on, whichever of the two writes lands last.
        EffectGrantDto plugin;
        PatronRuntimeState.Set(1, new PatronAura("fire", null, 47, 23, 0, 0));
        try
        {
            // The injector's real plugin registration (SecondaryPluginRegistry.CreateDefault), run
            // through the same host the injector's EffectRuntime builds.
            var host = new SimEffectHost(catalog: RealPatronAuraDefs());
            host.BeginMatch("pt7b-producers");
            plugin = Assert.Single(host.Snapshot().Grants, g => g.GrantId == "patron:aura");
        }
        finally
        {
            PatronRuntimeState.Set(0, null);
        }

        Assert.Equal(plugin.OwnerKey, server.OwnerKey);
        Assert.Equal(plugin.OwnerKind, server.OwnerKind);
        Assert.Equal(plugin.EffectId, server.EffectId);
        Assert.Equal(plugin.PluginId, server.PluginId);
        Assert.Equal(EffectOwnerKey.PlantSide, server.OwnerKey);
    }
}
