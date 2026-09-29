using FusionRpg.Core.Delve;
using FusionRpg.Core.Delve.Pack;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Items;
using FusionRpg.Core.World;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>D3.22 (spec-loot-pack.md §5, §7) — the pack lock table and `CloseDelve`'s own pack-
/// settlement hook. Reads the real, shipped dungeon tuning/registry files, matching
/// `DelveAttritionSettlementTests.cs`'s own established reason.</summary>
public class DelvePackSettlementTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly RoomTypeCatalog _rooms;
    readonly DoorTypeCatalog _doors;
    readonly DungeonTuning _tuning;
    int _worldSeq;

    public DelvePackSettlementTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var repoRoot = FindRepoRoot();
        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(repoRoot, "data", "seed", "dungeon", "_registry"));
        _rooms = new RoomTypeCatalog(registries.RoomKinds);
        _doors = new DoorTypeCatalog(registries.DoorKinds);
        _tuning = DungeonTuningLoader.Parse(File.ReadAllText(Path.Combine(repoRoot, "data", "tuning", "dungeon.v3.json")), registries);
    }

    public void Dispose() => _testStore.Dispose();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

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

    static IReadOnlyList<DelveRoomRow> BuildRooms() => new[]
    {
        new DelveRoomRow("r0c0", 0, 0, "fight", "room.fight-none-001", true, false, null, null, null, null, "[]", 0),
    };

    DelveRow CreateDelve(long playerId = 1)
    {
        var worldId = $"delve-pack-{Interlocked.Increment(ref _worldSeq)}";
        var (ok, _, delve) = _store.CreateDelve(
            playerId, "domain.fire-shallow-001", "solo", "hard", "corr-" + worldId, null,
            worldId, "layout.short-narrow-linear-001", 1UL, BuildGraph(worldId), BuildRooms(), _rooms, _doors);
        Assert.True(ok);
        return delve!;
    }

    static PackCell Cell(int row, int col, string kind, string refId, string? instanceId, long qty, PackItemOrigin origin, int w = 1, int h = 1) =>
        new(row, col, new PackItem(kind, refId, instanceId, qty, w, h, GrantIndex: 0, origin));

    /// <summary>`rpg_item.instance_id` is a foreign key into `effect_instance` (`RpgItemRow`'s own doc
    /// comment: "1:1 with effect_instance.instance_id. No second identity.") — a bare `SaveItem` call
    /// with no backing instance fails the FK constraint. `SaveInstance` first, then `SaveItem` under
    /// the id it actually returns.</summary>
    string CreateOwnedGearInstance(string instanceId, string playerId)
    {
        var realId = _store.SaveInstance(new FusionRpg.Core.Effects.Atoms.InstanceRow
        {
            ContainerId = "item.test-relic", RollSeed = 1, CatalogRevision = 0, ThetaContent = 20, ContentScaleMilli = 1000,
        }, instanceId: instanceId);
        _store.SaveItem(new RpgItemRow { InstanceId = realId, PlayerId = playerId, OriginKind = "drop" });
        return realId;
    }

    // ---- WritePartyPack ----

    [Fact]
    public void WritePartyPack_round_trips_through_the_real_store()
    {
        var delve = CreateDelve();
        var pack = new DelvePartyPackState(4, 10, new[] { Cell(0, 0, "Equipment", "sword", "inst-1", 1, PackItemOrigin.Haul) });

        var updated = _store.WritePartyPack(delve.DelveId, partyEntityId: 0, pack);

        var party = updated!.Parties.Single(p => p.EntityId == 0);
        Assert.NotNull(party.Pack);
        Assert.Equal(4, party.Pack!.Rows);
        Assert.Single(party.Pack.Cells);
        Assert.Equal("sword", party.Pack.Cells[0].Item.RefId);
    }

    [Fact]
    public void WritePartyPack_creates_the_party_row_the_first_time_a_delve_has_none_yet()
    {
        var delve = CreateDelve();
        var updated = _store.WritePartyPack(delve.DelveId, partyEntityId: 3, new DelvePartyPackState(4, 10, Array.Empty<PackCell>()));
        var party = updated!.Parties.Single(p => p.EntityId == 3);
        Assert.NotNull(party.Pack);
        Assert.Empty(party.Pack!.Cells);
    }

    [Fact]
    public void A_party_written_before_this_field_existed_deserializes_pack_to_null_not_empty()
    {
        var delve = CreateDelve();
        _store.WritePartyRoute(delve.DelveId, 0, new[] { "r0c0" }); // creates the party row with no Pack at all
        var reloaded = _store.LoadDelve(delve.DelveId)!;
        Assert.Null(reloaded.Parties.Single(p => p.EntityId == 0).Pack);
    }

    // ---- Lock ----

    [Fact]
    public void LockPackInstance_then_IsPackLocked_reads_true()
    {
        var delve = CreateDelve();
        Assert.False(_store.IsPackLocked("inst-x"));
        _store.LockPackInstance(delve.DelveId, "inst-x");
        Assert.True(_store.IsPackLocked("inst-x"));
    }

    [Fact]
    public void Locking_the_same_instance_twice_for_the_same_delve_is_a_harmless_replay()
    {
        var delve = CreateDelve();
        _store.LockPackInstance(delve.DelveId, "inst-x");
        _store.LockPackInstance(delve.DelveId, "inst-x"); // must not throw a UNIQUE-constraint error
        Assert.True(_store.IsPackLocked("inst-x"));
    }

    // ---- CloseDelve's own pack-settlement hook ----

    [Fact]
    public void Extracted_unlocks_haul_gear_and_it_stays_owned()
    {
        var delve = CreateDelve();
        CreateOwnedGearInstance("inst-1", "1");
        _store.LockPackInstance(delve.DelveId, "inst-1");
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10,
            new[] { Cell(0, 0, "Equipment", "relic", "inst-1", 1, PackItemOrigin.Haul) }));

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.False(_store.IsPackLocked("inst-1"));
        Assert.NotNull(_store.GetItem("inst-1")); // still owned -- extraction never deletes a haul instance
    }

    [Fact]
    public void Extracted_banks_a_haul_stack_into_rpg_item_stock()
    {
        var delve = CreateDelve(playerId: 7);
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10,
            new[] { Cell(0, 0, "Material", "shard.hard", null, 15, PackItemOrigin.Haul) }));

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        var stock = _store.ListStock("7").Single(s => s.ContainerId == "shard.hard");
        Assert.Equal(15, stock.Qty);
    }

    [Fact]
    public void Extracted_unlocks_carry_in_gear_and_banks_unconsumed_carry_in_stock()
    {
        var delve = CreateDelve(playerId: 9);
        CreateOwnedGearInstance("inst-2", "9");
        _store.LockPackInstance(delve.DelveId, "inst-2");
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10, new[]
        {
            Cell(0, 0, "Equipment", "sword", "inst-2", 1, PackItemOrigin.CarryIn),
            Cell(1, 0, "Material", "ration", null, 4, PackItemOrigin.CarryIn),
        }));

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false, _tuning);

        Assert.False(_store.IsPackLocked("inst-2"));
        Assert.Equal(4, _store.ListStock("9").Single(s => s.ContainerId == "ration").Qty);
    }

    [Fact]
    public void Wiped_destroys_haul_gear_the_instance_row_itself_is_gone()
    {
        var delve = CreateDelve();
        CreateOwnedGearInstance("inst-3", "1");
        _store.LockPackInstance(delve.DelveId, "inst-3");
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10,
            new[] { Cell(0, 0, "Equipment", "relic", "inst-3", 1, PackItemOrigin.Haul) }));

        _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false, _tuning);

        Assert.Null(_store.GetItem("inst-3"));
        Assert.False(_store.IsPackLocked("inst-3"));
    }

    [Fact]
    public void Wiped_never_banks_a_haul_stack()
    {
        var delve = CreateDelve(playerId: 11);
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10,
            new[] { Cell(0, 0, "Material", "shard.hard", null, 15, PackItemOrigin.Haul) }));

        _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false, _tuning);

        Assert.DoesNotContain(_store.ListStock("11"), s => s.ContainerId == "shard.hard");
    }

    [Fact]
    public void Wiped_still_returns_carry_in_gear_and_stock_home()
    {
        var delve = CreateDelve(playerId: 13);
        CreateOwnedGearInstance("inst-4", "13");
        _store.LockPackInstance(delve.DelveId, "inst-4");
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10, new[]
        {
            Cell(0, 0, "Equipment", "sword", "inst-4", 1, PackItemOrigin.CarryIn),
            Cell(1, 0, "Material", "ration", null, 2, PackItemOrigin.CarryIn),
        }));

        _store.CloseDelve(delve.DelveId, DelveStates.Wiped, archiveNow: false, _tuning);

        Assert.False(_store.IsPackLocked("inst-4"));
        Assert.NotNull(_store.GetItem("inst-4")); // carry-in gear survives a wipe
        Assert.Equal(2, _store.ListStock("13").Single(s => s.ContainerId == "ration").Qty);
    }

    [Fact]
    public void Without_tuning_the_pack_lock_is_left_untouched()
    {
        var delve = CreateDelve();
        _store.LockPackInstance(delve.DelveId, "inst-5");
        _store.WritePartyPack(delve.DelveId, 0, new DelvePartyPackState(4, 10,
            new[] { Cell(0, 0, "Equipment", "relic", "inst-5", 1, PackItemOrigin.Haul) }));

        _store.CloseDelve(delve.DelveId, DelveStates.Extracted, archiveNow: false); // no tuning

        Assert.True(_store.IsPackLocked("inst-5")); // pack settlement is gated the same way loot earn is
    }

    // ---- the check "owed to the item program": salvage already refuses on Locked, whatever sets it ----

    [Fact]
    public void A_pack_locked_instance_flows_into_SalvageGuards_own_existing_lock_refusal()
    {
        var delve = CreateDelve();
        _store.LockPackInstance(delve.DelveId, "inst-6");

        var candidate = new SalvageCandidate("inst-6", Assigned: false, Locked: _store.IsPackLocked("inst-6"), InAnyLoadout: false, BestInRole: false);
        var (kept, excluded) = SalvageGuards.Preview(new[] { candidate });

        Assert.Empty(kept);
        var exclusion = Assert.Single(excluded);
        Assert.Equal(SalvageExclusionReason.Locked, exclusion.Reason);
    }
}
