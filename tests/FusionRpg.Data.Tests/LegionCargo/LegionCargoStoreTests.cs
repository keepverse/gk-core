using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.Loam;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.LegionCargo;

/// <summary>
/// Task 1.1 (spec-legion-cargo.md §Design 2-5, §Testing strategy): the cargo overlay table, the
/// load/unload boundary verbs, and legion-to-legion transfer. All stores are in-memory
/// (<see cref="DataTestStore.Create"/>); no temp dir, nothing to delete.
/// </summary>
public class LegionCargoStoreTests : IDisposable
{
    const long PlayerId = 1;
    const string PlayerIdStr = "1";
    const string WorldId = "w-cargo";
    const string DaveLegion = "e-dave-legion-1";
    const string DaveLegion2 = "e-dave-legion-2";
    const string ZombossBand = "e-zomboss-band-1";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    public LegionCargoStoreTests()
    {
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId);
        var customized = built with
        {
            Entities = built.Entities
                .Select(e => e.EntityId == DaveLegion
                    ? e with
                    {
                        Members = new WorldEntityMember[]
                        {
                            new() { SpeciesId = "peashooterzombie", Level = 1, Hp = 110 },
                            new() { SpeciesId = "conezombie", Level = 1, Hp = 110 },
                            new() { SpeciesId = "paperzombie", Level = 1, Hp = 110 },
                            new() { SpeciesId = "flagzombie", Level = 1, Hp = 110 },
                            new() { SpeciesId = "paperzombie", Level = 1, Hp = 110, Role = WorldEntityMemberRole.Bearer },
                        }
                    }
                    : e)
                .Append(new WorldEntity
                {
                    EntityId = DaveLegion2,
                    Kind = WorldEntityKind.Legion,
                    OwnerFactionId = "dave",
                    AtSectorId = "homeworld",
                    Stance = "march",
                    MovementRemaining = 1000,
                    Members = new WorldEntityMember[]
                    {
                        new() { SpeciesId = "peashooterzombie", Level = 1, Hp = 110 },
                        new() { SpeciesId = "conezombie", Level = 1, Hp = 110 },
                    },
                })
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList(),
        };

        var (ok, reason, _) = _store.CreateWorld(PlayerId, customized);
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
    string SeedOwnedItem(string playerId = PlayerIdStr)
    {
        var tag = "lc" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId($"atom.{tag}", "", 1),
            KindId = "stat.modify", FamilyId = $"atom.{tag}", Variant = "", Tier = 1,
            Name = "Cargo Test", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
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
            PlayerId = playerId,
            AcquiredUtc = DateTime.UtcNow.ToString("O"),
            Disposition = "owned",
        });
        return instanceId;
    }

    void AddMemberRow(string entityId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        // Simulates the world-turn membership write this module does not own (a member joining):
        // capacity must track it with no second write anywhere.
        cmd.CommandText = """
            INSERT INTO rpg_world_entity_members
              (world_id, entity_id, member_index, instance_id, species_id, level, hp, wounds, role)
            SELECT $w, $e, COALESCE(MAX(member_index), -1) + 1, NULL, 'peashooterzombie', 1, 110, 0, 'Fighter'
            FROM rpg_world_entity_members WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    void RemoveMemberRow(string entityId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM rpg_world_entity_members
            WHERE world_id = $w AND entity_id = $e
              AND member_index = (SELECT MAX(member_index) FROM rpg_world_entity_members
                                  WHERE world_id = $w AND entity_id = $e);
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ---- AC 1: the table exists with the exact column shape ----------------------------------

    [Fact]
    public void Cargo_table_exists_with_the_exact_spec_column_shape()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(rpg_world_entity_cargo);";
        using var r = cmd.ExecuteReader();

        var columns = new List<string>();
        while (r.Read())
            columns.Add(r.GetString(1)); // column 1 of table_info is the column name

        Assert.Equal(
            new[] { "container_id", "entity_id", "instance_id", "kind", "qty", "seq", "weight_each", "world_id" },
            columns.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Cargo_table_carries_no_ownership_column()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(rpg_world_entity_cargo);";
        using var r = cmd.ExecuteReader();

        var columns = new List<string>();
        while (r.Read())
            columns.Add(r.GetString(1));

        Assert.DoesNotContain("player_id", columns);
        Assert.DoesNotContain("owner_faction_id", columns);
    }

    // ---- capacity tracks the live member count -------------------------------------------------

    [Fact]
    public void Capacity_tracks_live_member_count_with_no_second_write()
    {
        Assert.Equal(500L, _store.LegionWeightCapacity(WorldId, DaveLegion));
        Assert.Equal(10, _store.LegionSlotCapacity(WorldId, DaveLegion));

        AddMemberRow(DaveLegion);
        Assert.Equal(600L, _store.LegionWeightCapacity(WorldId, DaveLegion));
        Assert.Equal(12, _store.LegionSlotCapacity(WorldId, DaveLegion));

        RemoveMemberRow(DaveLegion);
        Assert.Equal(500L, _store.LegionWeightCapacity(WorldId, DaveLegion));
        Assert.Equal(10, _store.LegionSlotCapacity(WorldId, DaveLegion));
    }

    // ---- AC 4: capacity uses ALL members, not Bearer-filtered (regression) ----------------------

    /// <summary>
    /// The spec's deliberate divergence from <c>LegionSupply</c>'s Bearer-only precedent, proven
    /// through real SQL: this legion holds 4 Fighters + 1 Bearer. A Bearer-filtered rule would see
    /// capacity for 1 member (weight 100); the real rule carries 5 × 90 = 450 without refusing.
    /// Confirms the <c>LegionSupply</c> precedent reads differently on the same fixture.
    /// </summary>
    [Fact]
    public void Capacity_uses_all_members_not_bearer_filtered()
    {
        var loaded = _store.LoadWorldState(WorldId)!;
        var legion = loaded.Entities.Single(e => e.EntityId == DaveLegion);
        Assert.Equal(1, LegionSupply.BearerCount(legion));
        Assert.Equal(5, legion.Members.Count);

        for (var i = 0; i < 5; i++)
        {
            var id = SeedOwnedItem();
            var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion, PlayerId,
                "instance", id, null, 0, weightEach: 90);
            Assert.True(ok, $"load {i}: {reason}");
        }

        Assert.Equal(5, _store.ListCargo(WorldId, DaveLegion).Count);
    }

    /// <summary>
    /// The sharp half of the same regression: an all-Fighter legion (zero Bearers — loam-carry
    /// capacity exactly 0) still loads cargo. Under a Bearer-filtered rule every load here would
    /// refuse.
    /// </summary>
    [Fact]
    public void An_all_fighter_legion_with_zero_bearers_still_loads_cargo()
    {
        var loaded = _store.LoadWorldState(WorldId)!;
        var legion = loaded.Entities.Single(e => e.EntityId == DaveLegion2);
        Assert.All(legion.Members, m => Assert.Equal(WorldEntityMemberRole.Fighter, m.Role));
        Assert.Equal(0, LegionSupply.BearerCount(legion));

        var id = SeedOwnedItem();
        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion2, PlayerId,
            "instance", id, null, 0, weightEach: 150);
        Assert.True(ok, reason);
    }

    // ---- AC 2: Load refuses before any write -----------------------------------------------------

    [Fact]
    public void Load_instance_moves_the_item_aboard_and_snapshots_weight_once()
    {
        var id = SeedOwnedItem();
        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 70);
        Assert.True(ok, reason);

        var row = Assert.Single(_store.ListCargo(WorldId, DaveLegion));
        Assert.Equal("instance", row.Kind);
        Assert.Equal(id, row.InstanceId);
        Assert.Equal(70, row.WeightEach);

        // Aboard means out of the armoury listing with no second write: ListItemsByPlayer already
        // filters disposition = 'owned', and load marked it 'cargo'.
        Assert.Empty(_store.ListItemsByPlayer(PlayerIdStr));
        Assert.Equal("cargo", _store.GetItem(id)!.Disposition);
    }

    [Fact]
    public void Load_stack_decrements_stock_and_records_qty()
    {
        _store.AdjustStock(PlayerIdStr, "item.cargo-rations", 10);

        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "stack", null, "item.cargo-rations", 4, weightEach: 25);
        Assert.True(ok, reason);

        var row = Assert.Single(_store.ListCargo(WorldId, DaveLegion));
        Assert.Equal("stack", row.Kind);
        Assert.Equal("item.cargo-rations", row.ContainerId);
        Assert.Equal(4, row.Qty);
        Assert.Equal(25, row.WeightEach);
        Assert.Equal(6, _store.ListStock(PlayerIdStr).Single(s => s.ContainerId == "item.cargo-rations").Qty);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("stack")]
    public void Load_refuses_not_owned_without_writing(string kind)
    {
        _store.AdjustStock(PlayerIdStr, "item.cargo-rations", 3);
        var otherItem = SeedOwnedItem(playerId: "2");

        (bool ok, string reason) = kind == "instance"
            ? _store.LoadCargo(WorldId, DaveLegion, PlayerId, "instance", otherItem, null, 0, weightEach: 10)
            : _store.LoadCargo(WorldId, DaveLegion, PlayerId, "stack", null, "item.cargo-rations", 99, weightEach: 10);

        Assert.False(ok);
        Assert.Equal("cargo.not-owned", reason);
        Assert.Empty(_store.ListCargo(WorldId, DaveLegion));
    }

    [Fact]
    public void Load_refuses_an_unknown_instance()
    {
        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", "no-such-instance", null, 0, weightEach: 10);
        Assert.False(ok);
        Assert.Equal("cargo.not-owned", reason);
        Assert.Empty(_store.ListCargo(WorldId, DaveLegion));
    }

    [Fact]
    public void Load_refuses_an_item_equipped_on_a_specimen()
    {
        var id = SeedOwnedItem();
        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, "item", id);

        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 10);
        Assert.False(ok);
        Assert.Equal("cargo.not-owned", reason);
        Assert.Empty(_store.ListCargo(WorldId, DaveLegion));
    }

    [Fact]
    public void Load_refuses_an_item_already_aboard()
    {
        var id = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 10).Ok);

        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion2, PlayerId,
            "instance", id, null, 0, weightEach: 10);
        Assert.False(ok);
        Assert.Equal("cargo.not-owned", reason);
        Assert.Empty(_store.ListCargo(WorldId, DaveLegion2));
    }

    [Fact]
    public void Load_refuses_into_an_unknown_legion()
    {
        var id = SeedOwnedItem();
        var (ok, reason) = _store.LoadCargo(WorldId, "no-such-legion", PlayerId,
            "instance", id, null, 0, weightEach: 10);
        Assert.False(ok);
        Assert.Equal("cargo.not-owned", reason);
        Assert.NotNull(_store.GetItem(id));
    }

    [Fact]
    public void Load_refuses_over_weight_without_writing()
    {
        // DaveLegion2: 2 members × 100 = 200 weight, 4 slots.
        var first = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion2, PlayerId,
            "instance", first, null, 0, weightEach: 150).Ok);

        var second = SeedOwnedItem();
        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion2, PlayerId,
            "instance", second, null, 0, weightEach: 150);
        Assert.False(ok);
        Assert.Equal("cargo.over-weight", reason);

        Assert.Single(_store.ListCargo(WorldId, DaveLegion2));
        Assert.Equal("owned", _store.GetItem(second)!.Disposition);
    }

    [Fact]
    public void Stack_weight_counts_qty_times_weight_each()
    {
        _store.AdjustStock(PlayerIdStr, "item.cargo-rations", 10);

        // 2 members × 100 = 200: 4 × 60 = 240 does not fit.
        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion2, PlayerId,
            "stack", null, "item.cargo-rations", 4, weightEach: 60);
        Assert.False(ok);
        Assert.Equal("cargo.over-weight", reason);
        Assert.Equal(10, _store.ListStock(PlayerIdStr).Single(s => s.ContainerId == "item.cargo-rations").Qty);
    }

    [Fact]
    public void Load_refuses_no_slots_without_writing()
    {
        // DaveLegion2: 2 members × 2 = 4 slots. Two 1-slot rows fit; weight 10 each stays far under 200.
        for (var i = 0; i < 4; i++)
        {
            var id = SeedOwnedItem();
            Assert.True(_store.LoadCargo(WorldId, DaveLegion2, PlayerId,
                "instance", id, null, 0, weightEach: 10).Ok);
        }

        var extra = SeedOwnedItem();
        var (ok, reason) = _store.LoadCargo(WorldId, DaveLegion2, PlayerId,
            "instance", extra, null, 0, weightEach: 10);
        Assert.False(ok);
        Assert.Equal("cargo.no-slots", reason);
        Assert.Equal(4, _store.ListCargo(WorldId, DaveLegion2).Count);
        Assert.Equal("owned", _store.GetItem(extra)!.Disposition);
    }

    // ---- AC 3: weight_each captured once, never re-resolved ---------------------------------------

    [Fact]
    public void Stored_weight_is_the_load_time_snapshot_used_by_capacity_math()
    {
        _store.AdjustStock(PlayerIdStr, "item.cargo-rations", 10);
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "stack", null, "item.cargo-rations", 3, weightEach: 40).Ok);
        var id = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 70).Ok);

        var rows = _store.ListCargo(WorldId, DaveLegion);
        Assert.Equal(2, rows.Count);
        // Capacity math reads the stored column: used = 3×40 + 70 = 190, leaving 500 − 190 = 310.
        // A 311-weight load refuses; a 310-weight load fits — proving the sum above, not a re-join.
        var heavy = SeedOwnedItem();
        Assert.Equal("cargo.over-weight",
            _store.LoadCargo(WorldId, DaveLegion, PlayerId, "instance", heavy, null, 0, weightEach: 311).Reason);
        var exact = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId, "instance", exact, null, 0, weightEach: 310).Ok);
    }

    // ---- unload: the exact reverse -----------------------------------------------------------------

    [Fact]
    public void Unload_instance_restores_the_armoury_row()
    {
        var id = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 70).Ok);

        var (ok, reason) = _store.UnloadCargo(WorldId, DaveLegion, 0, PlayerId);
        Assert.True(ok, reason);

        Assert.Empty(_store.ListCargo(WorldId, DaveLegion));
        Assert.Equal("owned", _store.GetItem(id)!.Disposition);
        Assert.NotEmpty(_store.ListItemsByPlayer(PlayerIdStr));
    }

    [Fact]
    public void Unload_stack_credits_the_qty_back()
    {
        _store.AdjustStock(PlayerIdStr, "item.cargo-rations", 10);
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "stack", null, "item.cargo-rations", 4, weightEach: 25).Ok);

        var (ok, reason) = _store.UnloadCargo(WorldId, DaveLegion, 0, PlayerId);
        Assert.True(ok, reason);

        Assert.Empty(_store.ListCargo(WorldId, DaveLegion));
        Assert.Equal(10, _store.ListStock(PlayerIdStr).Single(s => s.ContainerId == "item.cargo-rations").Qty);
    }

    [Fact]
    public void Unload_missing_row_refuses_not_found()
    {
        var (ok, reason) = _store.UnloadCargo(WorldId, DaveLegion, 7, PlayerId);
        Assert.False(ok);
        Assert.Equal("cargo.not-found", reason);
    }

    // ---- AC 5: TransferCargoUnlocked -----------------------------------------------------------------

    [Fact]
    public void Transfer_same_empire_moves_the_row()
    {
        var id = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 70).Ok);

        var (ok, reason, newSeq) = _store.TransferCargo(WorldId, DaveLegion, DaveLegion2, 0, PlayerId);
        Assert.True(ok, reason);

        Assert.Empty(_store.ListCargo(WorldId, DaveLegion));
        var moved = Assert.Single(_store.ListCargo(WorldId, DaveLegion2));
        Assert.Equal(newSeq, moved.Seq);
        Assert.Equal("instance", moved.Kind);
        Assert.Equal(id, moved.InstanceId);
        Assert.Equal(70, moved.WeightEach);
    }

    [Fact]
    public void Transfer_is_atomic_under_forced_failure()
    {
        var id = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 70).Ok);

        RpgStore.TestProbeMidWrite = () => throw new InvalidOperationException("forced mid-transfer crash");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                _store.TransferCargo(WorldId, DaveLegion, DaveLegion2, 0, PlayerId));
        }
        finally
        {
            RpgStore.TestProbeMidWrite = null;
        }

        // No interrupted-transaction state is ever observable: source unchanged, destination empty.
        var source = Assert.Single(_store.ListCargo(WorldId, DaveLegion));
        Assert.Equal(id, source.InstanceId);
        Assert.Empty(_store.ListCargo(WorldId, DaveLegion2));
    }

    [Fact]
    public void Load_is_atomic_under_forced_failure()
    {
        var id = SeedOwnedItem();

        RpgStore.TestProbeMidWrite = () => throw new InvalidOperationException("forced mid-load crash");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                _store.LoadCargo(WorldId, DaveLegion, PlayerId, "instance", id, null, 0, weightEach: 70));
        }
        finally
        {
            RpgStore.TestProbeMidWrite = null;
        }

        Assert.Empty(_store.ListCargo(WorldId, DaveLegion));
        Assert.Equal("owned", _store.GetItem(id)!.Disposition);
    }

    [Fact]
    public void Transfer_cross_empire_refuses_without_writing()
    {
        var id = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 10).Ok);

        // e-zomboss-band-1 answers to 'zomboss', not 'dave'.
        var (ok, reason, newSeq) = _store.TransferCargo(WorldId, DaveLegion, ZombossBand, 0, PlayerId);
        Assert.False(ok);
        Assert.Equal("cargo.cross-empire", reason);
        Assert.Equal(-1, newSeq);

        Assert.Single(_store.ListCargo(WorldId, DaveLegion));
        Assert.Empty(_store.ListCargo(WorldId, ZombossBand));
    }

    [Fact]
    public void Transfer_checks_destination_capacity_before_any_write()
    {
        var id = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 190).Ok);

        // DaveLegion2 caps at 200: 190 fits, so fill it first, then prove the second move refuses.
        var filler = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion2, PlayerId,
            "instance", filler, null, 0, weightEach: 190).Ok);

        var (ok, reason, _) = _store.TransferCargo(WorldId, DaveLegion, DaveLegion2, 0, PlayerId);
        Assert.False(ok);
        Assert.Equal("cargo.over-weight", reason);

        Assert.Single(_store.ListCargo(WorldId, DaveLegion));
        Assert.Single(_store.ListCargo(WorldId, DaveLegion2));
    }

    [Fact]
    public void Transfer_checks_destination_slots_before_any_write()
    {
        // DaveLegion2: 4 slots. Fill all four with weight-10 rows, then move a fifth at it.
        for (var i = 0; i < 4; i++)
        {
            var filler = SeedOwnedItem();
            Assert.True(_store.LoadCargo(WorldId, DaveLegion2, PlayerId,
                "instance", filler, null, 0, weightEach: 10).Ok);
        }

        var id = SeedOwnedItem();
        Assert.True(_store.LoadCargo(WorldId, DaveLegion, PlayerId,
            "instance", id, null, 0, weightEach: 10).Ok);

        var (ok, reason, _) = _store.TransferCargo(WorldId, DaveLegion, DaveLegion2, 0, PlayerId);
        Assert.False(ok);
        Assert.Equal("cargo.no-slots", reason);

        Assert.Single(_store.ListCargo(WorldId, DaveLegion));
        Assert.Equal(4, _store.ListCargo(WorldId, DaveLegion2).Count);
    }

    [Fact]
    public void Transfer_missing_row_refuses_not_found()
    {
        var (ok, reason, _) = _store.TransferCargo(WorldId, DaveLegion, DaveLegion2, 3, PlayerId);
        Assert.False(ok);
        Assert.Equal("cargo.not-found", reason);
    }

    // ---- tuning-driven, not constant ------------------------------------------------------------------

    [Fact]
    public void Capacity_follows_the_configured_tuning()
    {
        Assert.Equal(500L, _store.LegionWeightCapacity(WorldId, DaveLegion));

        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 1000, CargoSlotsPerUnit: 99));
        try
        {
            Assert.Equal(5000L, _store.LegionWeightCapacity(WorldId, DaveLegion));
            Assert.Equal(495, _store.LegionSlotCapacity(WorldId, DaveLegion));
        }
        finally
        {
            ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));
        }
    }
}
