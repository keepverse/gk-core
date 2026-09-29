using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.CorpseCache;

/// <summary>Task 0.1 (`corpse-cache`) — the delve leg of the death path
/// (spec-corpse-cache.md §Design 2 via <c>CloseDelve</c>'s permadeath-rung retire), plus the
/// negative proof that the BLOCKED wipe path was not silently built: below the permadeath gate a
/// wipe settles members to <c>Recovering</c> with packs worn and writes no <c>'wipe'</c> cache.
/// In-memory stores only.</summary>
public class DelveDeathCacheTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    readonly DungeonTuning _tuning;
    int _worldSeq;

    public DelveDeathCacheTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.AwardSouls(1, 1_000_000, "seed", "delve-death-cache");

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
        _tuning = DungeonTuningLoader.Parse(File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries);

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.delve-death-test", "", 1), KindId = "stat.modify",
            FamilyId = "atom.delve-death-test", Variant = "", Tier = 1, Name = "Delve Death Test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.delve-death-test", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.delve-death-test.t1") },
        }).IsOk);
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

    static WorldState BuildGraph(string worldId) => new()
    {
        WorldId = worldId, TemplateId = "layout.short-narrow-linear-001", Seed = 42UL, CurrentTurn = 0,
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "wild", Kind = WorldFactionKind.Wild, Name = "Wild", PolicyId = null },
        },
        Sectors = new[]
        {
            new WorldSector { SectorId = "r0c0", TypeId = "fight", Climate = null, OwnerFactionId = "dave" },
            new WorldSector { SectorId = "r1c0", TypeId = "cache", Climate = null },
            new WorldSector { SectorId = "r2c0", TypeId = "boss", Climate = null },
        },
        Lanes = new[]
        {
            new WorldLane { LaneId = "l0", FromSectorId = "r0c0", ToSectorId = "r1c0", TypeId = "passage" },
            new WorldLane { LaneId = "l1", FromSectorId = "r1c0", ToSectorId = "r2c0", TypeId = "passage" },
        },
        Entities = new[]
        {
            new WorldEntity { EntityId = "party-0", Kind = WorldEntityKind.Warband, OwnerFactionId = "dave", AtSectorId = "r0c0" },
        },
    };

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r1c0", 1, 0, "cache", "room.cache-none-001", false, false, null, null, null, null, "[]", 0),
        new DelveRoomRow("r2c0", 2, 0, "boss", "room.boss-none-001", false, false, null, null, null, null, "[]", 0),
    };

    DelveRow CreateDelve(string rungId = "hard", long playerId = 1)
    {
        var worldId = $"delve-death-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, _, delve) = _store.CreateDelve(
            playerId, "domain.fire-shallow-001", "solo", rungId, "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", 1UL, BuildGraph(worldId), BuildRooms(), _rooms, _doors);
        Assert.True(ok);
        return delve!;
    }

    static readonly CreatureSpeciesDef Species = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly && s.TraitPool.Count > 0);

    string MintBoundCreature()
    {
        var (specimen, _) = _store.MintCreature(1, new CreatureMintSpec
        {
            SpeciesId = Species.SpeciesId, Side = Species.Side, GameTypeId = Species.GameTypeId,
            Rarity = Species.BaseRarity.ToId(), Variant = "normal",
            ElementPrimary = Species.ElementPrimary.ToElementId(), ElementSecondary = Species.ElementSecondary?.ToElementId(),
            TraitIds = new List<string> { Species.TraitPool[0] }, Origin = "summon"
        });
        var id = specimen.Actor.InstanceId;
        Assert.True(_store.BindContract(1, id).Ok);
        return id;
    }

    static readonly Dictionary<string, long> FullPools = new(StringComparer.Ordinal)
    {
        ["hp"] = 1000, ["stamina"] = 1000, ["hunger"] = 700, ["spirit"] = 1000, ["qi"] = 1000, ["poise"] = 1000,
    };

    static DelveMemberState Member(string instanceId, bool downed = false, bool downedOnce = false, int nerveStacks = 0, long spirit = 1000, long hunger = 700) =>
        new(instanceId, new Dictionary<string, long>(FullPools, StringComparer.Ordinal) { ["spirit"] = spirit, ["hunger"] = hunger },
            Array.Empty<Core.Battle.BattleStatusSpec>(), null, nerveStacks, downed, downedOnce);

    static readonly PowerTuning GearTuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string SeedOwnedItem()
    {
        var container = _store.GetContainer("item.delve-death-test")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, GearTuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        var instanceId = _store.SaveInstance(inst!);
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = "1",
            AcquiredUtc = DateTime.UtcNow.ToString("o"),
        });
        return instanceId;
    }

    [Fact]
    public void A_downedOnce_member_on_a_permadeath_rung_moves_gear_into_a_delve_room_cache()
    {
        var permadeathRung = _tuning.Domain.PermadeathFromRung;
        var delve = CreateDelve(rungId: permadeathRung);
        var creature = MintBoundCreature();
        var gear = SeedOwnedItem();
        _store.SaveAssignment(creature, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, gear);
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: true) });
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0", "r1c0" });

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(creature)!.Phase);
        Assert.Empty(_store.ListAssignments(creature));
        var cache = Assert.Single(_store.ListCorpseCaches());
        Assert.Equal("delve_room", cache.PlaceKind);
        // Route's last room is r1c0 at (row 1, col 0).
        Assert.Equal($"delve:{delve.DelveId}:r1:c0", cache.PlaceRef);
        Assert.Equal("death", cache.SourceKind);
        var item = Assert.Single(_store.ListCorpseCacheItems(cache.CacheId));
        Assert.Equal(gear, item.InstanceId);
        Assert.Equal("owned", _store.GetItem(gear)!.Disposition);
    }

    [Fact]
    public void A_wipe_below_the_permadeath_gate_leaves_Recovering_packs_worn_and_writes_no_wipe_cache()
    {
        // The wipe path (spec §Design 3: every member + carry-in + haul into a 'wipe' cache) is
        // BLOCKED on the loot-pack §7 ask — this test pins that it was NOT silently built: a wipe
        // below the gate settles to Recovering with gear still worn and no cache anywhere.
        var delve = CreateDelve(rungId: "hard"); // "hard" sits below domain.permadeathFromRung
        var creature = MintBoundCreature();
        var gear = SeedOwnedItem();
        _store.SaveAssignment(creature, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, gear);
        _store.WritePartyMembers(delve.DelveId, 0, new[] { Member(creature, downedOnce: false) });

        _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false, _tuning);

        Assert.Equal(UniqueActorPhases.Recovering, _store.GetUniqueActor(creature)!.Phase);
        var row = Assert.Single(_store.ListAssignments(creature));
        Assert.Equal(gear, row.RefId);
        Assert.Empty(_store.ListCorpseCaches());
    }
}
