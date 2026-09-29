using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.CargoFate;

/// <summary>
/// Task 2.2 (<c>cargo-fate</c>, spec-cargo-fate.md §Design 1-2, §Testing strategy): a destroyed
/// legion's cargo moves into a corpse-cache row (sector death → <c>place_kind='world_sector'</c>,
/// lane death → <c>place_kind='world_lane'</c>, both <c>source_kind='legion_death'</c>) inside
/// <c>DiffEntities</c> BEFORE <c>DeleteMissing</c> fires the <c>ON DELETE CASCADE</c> on
/// <c>rpg_world_entity_cargo</c>. All stores are in-memory (<c>DataTestStore.Create</c>).
///
/// <para>Proof structure for AC1 (move-before-delete): <c>Cascade_is_armed</c> proves the FK cascade
/// is live in this substrate (a raw entity-row delete wipes cargo to zero), so the survival +
/// content-equality asserted by the two death tests through the single real
/// <c>DiffCommitForTest</c> commit can only mean the hook's move landed before the delete.</para>
/// </summary>
public class CargoFateTests : IDisposable
{
    const long PlayerId = 1;
    const string PlayerIdStr = "1";
    const string WorldId = "w-cargo-fate";
    const string Legion = "e-dave-legion-1";
    const string HomeSector = "homeworld";
    const string Lane = "l-home-ember";
    const string LaneToward = "ember-hollow";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    public CargoFateTests()
    {
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        // e-dave-legion-1: 3 members → 300 weight / 6 slots. At homeworld per the template.
        var (ok, reason, _) = _store.CreateWorld(
            PlayerId, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId));
        Assert.True(ok, reason);
    }

    public void Dispose()
    {
        RpgStore.TestProbeMidWrite = null;
        _testStore.Dispose();
    }

    // ---- fixture helpers ------------------------------------------------------------------

    /// <summary>
    /// A real owned instance: atom + container + roll + save, mirroring what an actual acquisition
    /// does (<c>rpg_item</c>'s FK to <c>effect_instance</c> is enforced, so the instance comes first).
    /// </summary>
    string SeedOwnedItem()
    {
        var tag = "cf" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId($"atom.{tag}", "", 1),
            KindId = "stat.modify", FamilyId = $"atom.{tag}", Variant = "", Tier = 1,
            Name = "Cargo Fate Test", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = $"item.{tag}", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, $"atom.{tag}.t1") },
        }).IsOk);

        var container = _store.GetContainer($"item.{tag}")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());

        var instanceId = _store.SaveInstance(inst!);
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId,
            PlayerId = PlayerIdStr,
            AcquiredUtc = DateTime.UtcNow.ToString("O"),
            Disposition = "owned",
        });
        return instanceId;
    }

    /// <summary>Loads one instance row (70 wt) + one stack row (4 × 25 wt) aboard the legion.</summary>
    (string InstanceId, string ContainerId) LoadTwoRows(string entityId = Legion)
    {
        var instanceId = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, entityId, PlayerId,
            "instance", instanceId, null, 0, weightEach: 70).Ok);

        const string container = "item.cargo-fate-rations";
        _store.AdjustStock(PlayerIdStr, container, 10);
        Assert.True(_store.LoadCargo(WorldId, entityId, PlayerId,
            "stack", null, container, 4, weightEach: 25).Ok);

        Assert.Equal(2, _store.ListCargo(WorldId, entityId).Count);
        return (instanceId, container);
    }

    /// <summary>Destroys an entity through the real diff commit path (same call production uses).</summary>
    WorldState DestroyEntity(string entityId)
    {
        var before = _store.LoadWorldState(WorldId)!;
        var next = before with
        {
            Entities = before.Entities.Where(e => e.EntityId != entityId).ToList()
        };
        return _store.DiffCommitForTest(WorldId, next);
    }

    void MoveLegionToLane(string entityId = Legion)
    {
        var before = _store.LoadWorldState(WorldId)!;
        var next = before with
        {
            Entities = before.Entities
                .Select(e => e.EntityId == entityId
                    ? e with
                    {
                        AtSectorId = null,
                        OnLaneId = Lane,
                        OnLaneTowardSectorId = LaneToward,
                        LaneProgressMilli = 500,
                    }
                    : e)
                .ToList()
        };
        _store.DiffCommitForTest(WorldId, next);
    }

    static void AssertDecayClockStarted(RpgStore store, string cacheId)
    {
        using var db = SqliteConnectionFactory.Open(store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT decay_started_turn, in_void FROM rpg_corpse_cache WHERE cache_id = $id;";
        cmd.Parameters.AddWithValue("$id", cacheId);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(0, r.GetInt32(0)); // stamped at the map world's current_turn
        Assert.Equal(0, r.GetInt32(1)); // world-map caches stay field-reachable, never void (V5)
    }

    // ---- AC1: sector death moves cargo before the cascade ------------------------------------

    [Fact]
    public void Sector_death_moves_cargo_to_world_sector_cache_before_delete()
    {
        var (instanceId, container) = LoadTwoRows();

        var after = DestroyEntity(Legion);

        // The entity row itself is gone (the DeleteMissing half ran).
        Assert.DoesNotContain(after.Entities, e => e.EntityId == Legion);

        // Exactly one cache, keyed to the legion's last sector, tagged legion_death.
        var cache = Assert.Single(_store.ListCorpseCaches());
        Assert.Equal("world_sector", cache.PlaceKind);
        Assert.Equal(HomeSector, cache.PlaceRef);
        Assert.Equal("legion_death", cache.SourceKind);

        // Moved, not copied and not destroyed: row count + content equality.
        var items = _store.ListCorpseCacheItems(cache.CacheId);
        Assert.Equal(2, items.Count);
        var instanceItem = Assert.Single(items, i => i.Kind == "instance");
        Assert.Equal(instanceId, instanceItem.InstanceId);
        Assert.Equal(Legion, instanceItem.OriginOwner);
        var stackItem = Assert.Single(items, i => i.Kind == "stack");
        Assert.Equal(container, stackItem.ContainerId);
        Assert.Equal(4, stackItem.Qty);
        Assert.Equal(Legion, stackItem.OriginOwner);

        // Source rows are gone — never left orphaned beside the cache.
        Assert.Empty(_store.ListCargo(WorldId, Legion));

        // The decay clock started (owner resolved from the world row), staying reachable.
        AssertDecayClockStarted(_store, cache.CacheId);
    }

    // ---- AC1/AC2: lane death is symmetric -----------------------------------------------------

    [Fact]
    public void Lane_death_moves_cargo_to_world_lane_cache()
    {
        MoveLegionToLane();
        var (instanceId, container) = LoadTwoRows();

        var after = DestroyEntity(Legion);

        Assert.DoesNotContain(after.Entities, e => e.EntityId == Legion);

        var cache = Assert.Single(_store.ListCorpseCaches());
        Assert.Equal("world_lane", cache.PlaceKind);
        Assert.Equal(Lane, cache.PlaceRef);
        Assert.Equal("legion_death", cache.SourceKind);

        var items = _store.ListCorpseCacheItems(cache.CacheId);
        Assert.Equal(2, items.Count);
        Assert.Equal(instanceId, Assert.Single(items, i => i.Kind == "instance").InstanceId);
        var stackItem = Assert.Single(items, i => i.Kind == "stack");
        Assert.Equal(container, stackItem.ContainerId);
        Assert.Equal(4, stackItem.Qty);

        Assert.Empty(_store.ListCargo(WorldId, Legion));
        AssertDecayClockStarted(_store, cache.CacheId);
    }

    // ---- AC1: the cascade race, proven armed (negative control) -------------------------------

    /// <summary>
    /// Control for the two death tests above: a raw entity-row delete with no hook wipes cargo to
    /// zero, proving the FK cascade is live in this substrate. Survival in the death tests is
    /// therefore proof the move committed before <c>DeleteMissing</c>, not proof the cascade is off.
    /// </summary>
    [Fact]
    public void Cascade_is_armed_a_raw_entity_delete_wipes_cargo()
    {
        LoadTwoRows();

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;";
            cmd.Parameters.AddWithValue("$w", WorldId);
            cmd.Parameters.AddWithValue("$e", Legion);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        Assert.Empty(_store.ListCargo(WorldId, Legion));
    }

    // ---- AC2: cargo-less legions create no empty cache -----------------------------------------

    [Theory]
    [InlineData("sector")]
    [InlineData("lane")]
    public void Destroyed_legion_with_no_cargo_creates_no_cache(string place)
    {
        if (place == "lane")
            MoveLegionToLane();

        Assert.Empty(_store.ListCargo(WorldId, Legion));
        DestroyEntity(Legion);

        Assert.Empty(_store.ListCorpseCaches());
    }

    // ---- Cascade-fix verdict: a rewritten survivor must keep its cargo --------------------------

    /// <summary>
    /// <c>DiffEntities</c> rewrites a surviving entity's row on any field change; with
    /// <c>INSERT OR REPLACE</c> that rewrite is DELETE + INSERT and the FK cascade wipes the
    /// survivor's cargo. The <c>ON CONFLICT DO UPDATE</c> conversion (same fix 1.2b applied for
    /// sectors) must leave cargo untouched. Fails on the REPLACE form, passes on DO UPDATE.
    /// </summary>
    [Fact]
    public void Surviving_legion_row_rewrite_keeps_cargo()
    {
        var (instanceId, container) = LoadTwoRows();

        var before = _store.LoadWorldState(WorldId)!;
        var next = before with
        {
            Entities = before.Entities
                .Select(e => e.EntityId == Legion ? e with { Stance = "hold" } : e)
                .ToList()
        };
        var after = _store.DiffCommitForTest(WorldId, next);

        // The rewrite landed...
        Assert.Equal("hold", after.Entities.Single(e => e.EntityId == Legion).Stance);
        // ...and the cargo survived it.
        var rows = _store.ListCargo(WorldId, Legion);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Kind == "instance" && r.InstanceId == instanceId);
        Assert.Contains(rows, r => r.Kind == "stack" && r.ContainerId == container && r.Qty == 4);
    }

    // ---- Scope regression: DiffSectors untouched --------------------------------------------------

    /// <summary>
    /// Spec §0 scope correction: sector-capture transfer is module 2's resolved no-op territory.
    /// A sector changing owner creates no corpse-cache row of any kind through this module.
    /// </summary>
    [Fact]
    public void Sector_owner_change_creates_no_cache()
    {
        var before = _store.LoadWorldState(WorldId)!;
        var next = before with
        {
            Sectors = before.Sectors
                .Select(s => s.SectorId == HomeSector ? s with { OwnerFactionId = "zomboss" } : s)
                .ToList()
        };
        var after = _store.DiffCommitForTest(WorldId, next);

        Assert.Equal("zomboss", after.Sectors.Single(s => s.SectorId == HomeSector).OwnerFactionId);
        Assert.Empty(_store.ListCorpseCaches());
    }

    // ---- AC3: neither-placed legions are unreachable — no fallback path ----------------------------

    /// <summary>
    /// <c>WorldState.cs:292</c>'s own invariant says a live entity is never neither-placed, so the
    /// hook has no third branch. A corrupt neither-placed row still deletes cleanly (no throw, no
    /// cache, no fallback) rather than inventing a place.
    /// </summary>
    [Fact]
    public void Neither_placed_row_deletes_without_cache_and_without_throw()
    {
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE rpg_world_entities SET at_sector_id = NULL, on_lane_id = NULL
                WHERE world_id = $w AND entity_id = $e;
                """;
            cmd.Parameters.AddWithValue("$w", WorldId);
            cmd.Parameters.AddWithValue("$e", Legion);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        var after = DestroyEntity(Legion);

        Assert.DoesNotContain(after.Entities, e => e.EntityId == Legion);
        Assert.Empty(_store.ListCorpseCaches());
    }
}
