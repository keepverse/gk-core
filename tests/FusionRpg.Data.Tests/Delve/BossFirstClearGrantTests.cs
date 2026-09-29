using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>
/// D3.15 (spec-dungeon-loot.md §7: "the once-domain boss relic... never enters the pack: the
/// `dungeon-clear` grant banks at the clear itself, owned then, so a later wipe cannot take it") —
/// `RpgStore.RecordClear`'s own optional boss-grant hook (`RpgStore.ApplyBossFirstClearGrantUnlocked`).
///
/// <para>Real, hand-built fixture data, not real imported content — matching this whole program's
/// established discipline: no real shipped domain authors a `firstClearRef` yet (D4.28: only 4/144
/// unique anchors are concretely buildable today), so every test here builds its OWN domain via
/// `QuestRewardBankingCloseDelveTests.cs`'s own established "fully-passing `DomainPreflightInputs`
/// fixture" shape, and its own `CreateDelve` helper (fixed seed `1UL`, mirrored exactly) — a
/// hand-built container stands in for a real relic, `DelveLootTests.cs`'s own `Container()` shape
/// (Instantiator's raw `TryInstantiate` path needs no Frame/BaseTypeId/Rarity; those are
/// `LootPipeline.MintUnique`'s own separate concern, never exercised by this hook).</para>
/// </summary>
public class BossFirstClearGrantTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    int _worldSeq;

    public BossFirstClearGrantTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    static readonly PowerTuning MintTuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    // ---- relic-container fixture (mirrors DelveLootTests.cs's own Container()/Catalog shape) --------

    static readonly Dictionary<string, AtomRow> AtomCatalog = new(StringComparer.Ordinal);

    static BossFirstClearGrantTests()
    {
        var id = AtomRow.DeriveId("atom.boss-relic-vitality", "", 1);
        AtomCatalog[id] = new AtomRow
        {
            AtomId = id, KindId = "stat.modify", FamilyId = "atom.boss-relic-vitality", Variant = "", Tier = 1,
            Name = "atom.boss-relic-vitality",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        };
    }

    static AtomRow? LookupAtom(string atomId) => AtomCatalog.TryGetValue(atomId, out var a) ? a : null;
    static AffixRow? LookupAffix(string _) => null;

    static RpgStore.BossFirstClearGrantInputs Inputs() => new(LookupAtom, LookupAffix, MintTuning);

    void SeedRelicContainer(string containerId)
    {
        var atomResult = _store.UpsertAtom(AtomCatalog.Values.Single());
        Assert.True(atomResult.IsOk, atomResult.ToString());
        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(0, AtomRow.DeriveId("atom.boss-relic-vitality", "", 1)) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());
    }

    // ---- delve fixture (mirrors QuestRewardBankingCloseDelveTests.cs's own established shape) --------

    static WorldState BuildGraph(string worldId) => new()
    {
        WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = 42UL, CurrentTurn = 0,
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = null },
        },
        Sectors = new[] { new WorldSector { SectorId = "r0c0", TypeId = "fight", Climate = null, OwnerFactionId = "dave" } },
        Lanes = Array.Empty<WorldLane>(),
        Entities = new[] { new WorldEntity { EntityId = "party-0", Kind = WorldEntityKind.Warband, OwnerFactionId = "dave", AtSectorId = "r0c0" } },
    };

    // `rooms` (the `rpg_delve_rooms` rows CreateDelve writes verbatim) is never validated against
    // `_rooms`/`_doors` -- only `WorldState.Sectors` is (via WorldValidation.Validate inside
    // CreateDelve) -- so a "boss" room kind here needs no matching room-kind registry entry.
    static IReadOnlyList<DelveRoomRow> BossRoomAt(int row, int col) => new[]
    {
        new DelveRoomRow($"r{row}c{col}", row, col, "boss", "room.boss-none-001", true, false, null, null, null, null, "[]", 0),
    };

    static readonly IReadOnlyList<DelveRoomRow> FightRoom = new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
    };

    /// <summary>Fixed seed `1UL` throughout (matching `QuestRewardBankingCloseDelveTests.CreateDelve`'s
    /// own convention) -- the determinism test below relies on two different delves sharing the SAME
    /// seed to prove the grant reproduces byte-identically.</summary>
    DelveRow CreateDelve(string domainId, IReadOnlyList<DelveRoomRow> rooms, long playerId)
    {
        var worldId = $"delve-bfc-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, reason, delve) = _store.CreateDelve(
            playerId, domainId, "solo", "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", 1UL, BuildGraph(worldId), rooms, _rooms, _doors);
        Assert.True(ok, reason);
        return delve!;
    }

    // ---- domain fixture (mirrors QuestRewardBankingCloseDelveTests.cs's own established shape) -------

    static DomainRow Domain(string id, string? firstClearRef) =>
        new(id, "Test Domain", "A test flavor.", "theme.overgrown", "fire", "shallow", "many",
            "layout.standard", "species.warden", null, "Lair", null, firstClearRef);

    static DomainPreflightInputs PassingDomainInputs() => new(
        DangerBandOrdinals: new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 },
        KnownLayoutIds: new[] { "layout.standard" },
        KnownSpeciesIds: new[] { "species.warden" },
        ThreatBandOrdinalFor: _ => 5,
        BossFloorRungOrdinal: 3,
        CellsLayoutCanPlace: _ => Array.Empty<(string, string)>(),
        CellsPaletteFills: _ => Array.Empty<(string, string)>(),
        CheckGraphs: _ => Array.Empty<DomainRefusal>(),
        CheckObjects: _ => Array.Empty<DomainRefusal>(),
        CheckEncounters: _ => Array.Empty<DomainRefusal>(),
        CheckEvents: _ => Array.Empty<DomainRefusal>(),
        CheckQuests: _ => Array.Empty<DomainRefusal>(),
        KnownDropTableIds: Array.Empty<string>(),
        BoundLootKinds: Array.Empty<string>(),
        LootBindingFor: _ => new Dictionary<string, string>(StringComparer.Ordinal),
        OfferedRungCountFor: _ => 3);

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyRooms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyQuests = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyLoot = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
    static readonly IReadOnlyDictionary<string, string> EmptyProvenance = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Imports through the REAL production write path (`ImportDungeonDomains`), so
    /// `ReadDomainFirstClearRefUnlocked` -- the exact read the new hook makes -- reads a genuinely
    /// written row, not a shortcut.</summary>
    string ImportDomain(string domainId, string? firstClearRef)
    {
        var outcome = _store.ImportDungeonDomains(
            new[] { Domain(domainId, firstClearRef) }, PassingDomainInputs(), EmptyRooms, EmptyQuests, EmptyLoot, EmptyProvenance, 32);
        Assert.True(outcome.IsOk, string.Join(";", outcome.Refusals));
        return domainId;
    }

    // ==================================================================================================
    // The happy path -- the task's own required "real instance, correctly attributed" proof, PLUS
    // "the relic never enters a pack" (D3.15's own explicit Verify line).
    // ==================================================================================================

    [Fact]
    public void A_boss_room_clear_with_a_relic_authored_mints_and_banks_a_real_instance_never_in_a_pack()
    {
        const string refId = "item.boss-relic-happy";
        SeedRelicContainer(refId);
        var domainId = ImportDomain("domain.bfc-happy", refId);
        var delve = CreateDelve(domainId, BossRoomAt(0, 0), playerId: 1);

        var updated = _store.RecordClear(delve.DelveId, 0, 0, thetaRoom: 40, Inputs());

        Assert.NotNull(updated);
        var playerId = delve.PlayerId.ToString();
        var items = _store.ListItemsByPlayer(playerId);
        var item = Assert.Single(items);
        Assert.Equal("dungeon-clear", item.OriginKind);
        Assert.Equal(domainId, item.OriginRef);

        var instance = _store.GetInstance(item.InstanceId);
        Assert.NotNull(instance);
        Assert.Equal(refId, instance!.ContainerId);
        Assert.NotEqual(0L, instance.RollSeed); // "never flat", the same headline InstantiateBossFirstClearGrant itself proves

        Assert.True(_store.HasFirstClear(playerId, "dungeon-clear", domainId));

        // The Verify line's own second headline: the relic never enters a pack.
        Assert.False(_store.IsPackLocked(item.InstanceId));
        var log = _store.ListDropLog(playerId, 10);
        Assert.DoesNotContain(log, r => r.SourceKind == "dungeon-clear"); // never PersistLootUnlocked's own path either
    }

    // ==================================================================================================
    // The two independently-mutation-testable gates.
    // ==================================================================================================

    [Fact]
    public void A_non_boss_room_clear_never_mints_even_with_bossGrant_supplied()
    {
        const string refId = "item.boss-relic-nonboss";
        SeedRelicContainer(refId);
        var domainId = ImportDomain("domain.bfc-nonboss", refId);
        var delve = CreateDelve(domainId, FightRoom, playerId: 1);

        _store.RecordClear(delve.DelveId, 0, 0, thetaRoom: 40, Inputs());

        var playerId = delve.PlayerId.ToString();
        Assert.Empty(_store.ListItemsByPlayer(playerId));
        Assert.False(_store.HasFirstClear(playerId, "dungeon-clear", domainId));
    }

    [Fact]
    public void A_replayed_boss_room_clear_mints_no_second_instance()
    {
        const string refId = "item.boss-relic-replay";
        SeedRelicContainer(refId);
        var domainId = ImportDomain("domain.bfc-replay", refId);
        var delve = CreateDelve(domainId, BossRoomAt(0, 0), playerId: 1);

        _store.RecordClear(delve.DelveId, 0, 0, thetaRoom: 40, Inputs());
        var playerId = delve.PlayerId.ToString();
        var firstItems = _store.ListItemsByPlayer(playerId);
        Assert.Single(firstItems);

        _store.RecordClear(delve.DelveId, 0, 0, thetaRoom: 40, Inputs()); // replay -- the same room clear reported twice

        var secondItems = _store.ListItemsByPlayer(playerId);
        Assert.Single(secondItems);
        Assert.Equal(firstItems[0].InstanceId, secondItems[0].InstanceId);
    }

    // ==================================================================================================
    // The honest "nothing happens" cases.
    // ==================================================================================================

    [Fact]
    public void A_domain_with_no_relic_authored_mints_nothing()
    {
        var domainId = ImportDomain("domain.bfc-norelic", firstClearRef: null);
        var delve = CreateDelve(domainId, BossRoomAt(0, 0), playerId: 1);

        _store.RecordClear(delve.DelveId, 0, 0, thetaRoom: 40, Inputs());

        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
    }

    [Fact]
    public void Without_bossGrant_supplied_a_boss_room_clear_mints_nothing_even_with_a_relic_authored()
    {
        const string refId = "item.boss-relic-noinput";
        SeedRelicContainer(refId);
        var domainId = ImportDomain("domain.bfc-noinput", refId);
        var delve = CreateDelve(domainId, BossRoomAt(0, 0), playerId: 1);

        var updated = _store.RecordClear(delve.DelveId, 0, 0, thetaRoom: 40); // no bossGrant -- byte-identical to pre-D3.15 callers

        Assert.NotNull(updated);
        Assert.Equal(40, updated!.ThetaRun); // the pre-existing watermark write still ran
        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
    }

    [Fact]
    public void An_authored_but_unwritten_container_id_is_skipped_not_thrown()
    {
        var domainId = ImportDomain("domain.bfc-dangling", firstClearRef: "item.does-not-exist");
        var delve = CreateDelve(domainId, BossRoomAt(0, 0), playerId: 1);

        var updated = _store.RecordClear(delve.DelveId, 0, 0, thetaRoom: 40, Inputs());

        Assert.NotNull(updated);
        Assert.Empty(_store.ListItemsByPlayer(delve.PlayerId.ToString()));
    }

    // ==================================================================================================
    // The verify line's own headline: banks at the clear, independent of the eventual outcome -- a
    // later wipe cannot take it back (spec-dungeon-loot.md §7; spec-domain-catalog.md's own staleness
    // section: "the dungeon-clear row survives the wipe, HasFirstClear").
    // ==================================================================================================

    [Fact]
    public void The_grant_survives_a_wipe_after_the_clear()
    {
        const string refId = "item.boss-relic-wipe";
        SeedRelicContainer(refId);
        var domainId = ImportDomain("domain.bfc-wipe", refId);
        var delve = CreateDelve(domainId, BossRoomAt(0, 0), playerId: 1);

        _store.RecordClear(delve.DelveId, 0, 0, thetaRoom: 40, Inputs());
        var playerId = delve.PlayerId.ToString();
        var itemsBeforeWipe = _store.ListItemsByPlayer(playerId);
        Assert.Single(itemsBeforeWipe);

        // CloseDelve(Wiped), deliberately with NO tuning -- proves the relic's own survival needs
        // none of CloseDelve's other settlement hooks; it was already owned before this call ran.
        var closed = _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false);
        Assert.True(closed);

        Assert.True(_store.HasFirstClear(playerId, "dungeon-clear", domainId));
        var itemsAfterWipe = _store.ListItemsByPlayer(playerId);
        Assert.Single(itemsAfterWipe);
        Assert.Equal(itemsBeforeWipe[0].InstanceId, itemsAfterWipe[0].InstanceId);
    }

    // ==================================================================================================
    // The clear golden -- mirrors DelveLootTests.cs's own
    // "The_same_seed_and_grant_index_reproduce_an_identical_instance": two INDEPENDENT delves, same
    // fixed CreateDelve seed (1UL), same container, same thetaRoom -> byte-identical ContentFingerprint.
    // ==================================================================================================

    [Fact]
    public void Two_delves_sharing_the_same_seed_and_relic_bank_a_byte_identical_fingerprint()
    {
        const string refId = "item.boss-relic-golden";
        SeedRelicContainer(refId);
        var domainId1 = ImportDomain("domain.bfc-golden-1", refId);
        var domainId2 = ImportDomain("domain.bfc-golden-2", refId);
        var delve1 = CreateDelve(domainId1, BossRoomAt(0, 0), playerId: 1);
        var delve2 = CreateDelve(domainId2, BossRoomAt(0, 0), playerId: 2);

        _store.RecordClear(delve1.DelveId, 0, 0, thetaRoom: 40, Inputs());
        _store.RecordClear(delve2.DelveId, 0, 0, thetaRoom: 40, Inputs());

        var item1 = Assert.Single(_store.ListItemsByPlayer(delve1.PlayerId.ToString()));
        var item2 = Assert.Single(_store.ListItemsByPlayer(delve2.PlayerId.ToString()));
        var instance1 = _store.GetInstance(item1.InstanceId)!;
        var instance2 = _store.GetInstance(item2.InstanceId)!;

        Assert.Equal(instance1.ContentFingerprint(), instance2.ContentFingerprint());
    }
}
