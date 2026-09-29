using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Data.Tests.CargoTransfer;

/// <summary>
/// Task 2.1 (spec-cargo-transfer.md §Design 1-3, §Testing strategy; plan "Task 2.1" all 7 AC
/// bullets): the legion↔sector deposit/withdraw verb. All stores are in-memory
/// (<see cref="DataTestStore.Create"/>); no temp dir backs a store, nothing to delete.
///
/// <para>Sector capacity needs a live <c>ItemStorage</c> structure in <see cref="StructureCatalog"/>
/// (the 25 shipped rows carry none — Task 1.2a left them byte-identical), so the fixture layers one
/// in-memory depot row over the real corpus through <c>StructureCorpusOverlay</c> and restores the
/// real corpus on dispose. The rows are a superset with identical real defs, so a concurrently-running
/// suite reading a real structure id observes the same answer under either catalog; the only delta is
/// the one inert test id nothing else references. This fixture used to copy the whole corpus into a
/// temp directory and delete it in dispose — the write was removed, not cleaned up
/// (docs/contributing/testing-standard.md R1/R2).</para>
///
/// <para>Shares the <c>StructureCatalogSwap</c> xUnit collection with the WonderBuild suite (Task
/// 3.1): both fixtures wholesale-replace the static catalog, and neither superset contains the
/// other's test ids — running them in parallel lets one suite's restore blank the other's depot
/// mid-test (<c>cargo.sector-full</c> out of nowhere). Same collection = sequential, no race.</para>
/// </summary>
[Collection("StructureCatalogSwap")]
[Trait("VerificationId", "data.cargo-transfer")]
public class CargoTransferTests : IDisposable
{
    const long PlayerId = 1;
    const string PlayerIdStr = "1";
    const string WorldId = "w-cargo-transfer";
    const string MainLegion = "e-ct-legion";
    const string AwayLegion = "e-ct-away";
    const string RivalLegion = "e-ct-rival";
    const string TestDepotId = "test-depot-ct";
    const long TestDepotBonus = 6;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _anchorSector;
    readonly string _otherSector;
    readonly string _anchorFaction;
    readonly string _rivalFaction;

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    public CargoTransferTests()
    {
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));

        StructureCatalog.Configure(StructureCorpusOverlay.LoadWithRows(
            StructureCorpusOverlay.Depot(TestDepotId, TestDepotBonus)));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId);
        var anchor = built.Sectors.First(s => s.OwnerFactionId is not null);
        _anchorSector = anchor.SectorId;
        _anchorFaction = anchor.OwnerFactionId!;
        _rivalFaction = built.Factions.Select(f => f.FactionId)
            .First(f => !string.Equals(f, _anchorFaction, StringComparison.Ordinal));
        _otherSector = built.Sectors.First(s => !string.Equals(s.SectorId, _anchorSector, StringComparison.Ordinal)).SectorId;

        WorldEntity Legion(string id, string faction, string sector) => new()
        {
            EntityId = id,
            Kind = WorldEntityKind.Legion,
            OwnerFactionId = faction,
            AtSectorId = sector,
            Stance = "march",
            MovementRemaining = 1000,
            Members = new WorldEntityMember[]
            {
                new() { SpeciesId = "peashooterzombie", Level = 1, Hp = 110 },
                new() { SpeciesId = "conezombie", Level = 1, Hp = 110 },
            },
        };

        // Two members × tuning (100 weight, 2 slots) = 200 weight, 4 slots per legion.
        var customized = built with
        {
            Entities = built.Entities
                .Append(Legion(MainLegion, _anchorFaction, _anchorSector))
                .Append(Legion(AwayLegion, _anchorFaction, _otherSector))
                .Append(Legion(RivalLegion, _rivalFaction, _anchorSector))
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList(),
        };

        var (ok, reason, _) = _store.CreateWorld(PlayerId, customized);
        Assert.True(ok, reason);

        // The anchor's first slot becomes an active depot: capacity = TestDepotBonus, every other
        // slot names no ItemStorage structure, so EffectiveCapacity reads exactly the bonus.
        var slotIndex = _store.LoadWorldState(WorldId)!.Sectors
            .First(s => string.Equals(s.SectorId, _anchorSector, StringComparison.Ordinal))
            .Slots.First().SlotIndex;
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_slots SET structure_id = $id, construction_turns_remaining = NULL
            WHERE world_id = $w AND sector_id = $s AND slot_index = $i;
            """;
        cmd.Parameters.AddWithValue("$id", TestDepotId);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", _anchorSector);
        cmd.Parameters.AddWithValue("$i", slotIndex);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    public void Dispose()
    {
        RpgStore.TestProbeCargoTransfer = null;
        StructureCatalog.Configure(StructureCorpus.Load(StructureCorpusOverlay.RealCorpusRoot()));
        _testStore.Dispose();
    }

    // ---- fixture helpers ------------------------------------------------------------------


    /// <summary>
    /// A real owned instance: atom + container + roll + save, mirroring what an actual acquisition
    /// does (<c>rpg_item</c>'s FK to <c>effect_instance</c> is enforced, so the instance comes first).
    /// </summary>
    string SeedOwnedItem()
    {
        var tag = "ct" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId($"atom.{tag}", "", 1),
            KindId = "stat.modify", FamilyId = $"atom.{tag}", Variant = "", Tier = 1,
            Name = "Cargo Transfer Test", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
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

    /// <summary>Loads a fresh owned instance aboard <paramref name="entityId"/>; returns its id.</summary>
    string LoadInstanceAboard(string entityId, long weightEach)
    {
        var id = SeedOwnedItem();
        var (ok, reason) = _store.LoadCargo(WorldId, entityId, PlayerId,
            "instance", id, null, 0, weightEach);
        Assert.True(ok, reason);
        return id;
    }

    void InsertStorageRow(string sectorId, int seq, string kind,
        string? instanceId = null, string? containerId = null, long? qty = null)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_world_sector_storage
              (world_id, sector_id, seq, kind, instance_id, container_id, qty)
            VALUES ($w, $s, $seq, $k, $iid, $cid, $q);
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$iid", (object?)instanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cid", (object?)containerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$q", (object?)qty ?? DBNull.Value);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    WorldState WithSectorOwner(WorldState state, string sectorId, string newOwner) =>
        state with
        {
            Sectors = state.Sectors
                .Select(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal)
                    ? s with { OwnerFactionId = newOwner }
                    : s)
                .ToList()
        };

    // ---- AC 7 (move leg) + Testing strategy "Deposit moves, never copies" -----------------------

    [Fact]
    public void Deposit_moves_never_copies_by_row_count()
    {
        var id = LoadInstanceAboard(MainLegion, weightEach: 70);
        Assert.Single(_store.ListCargo(WorldId, MainLegion));
        Assert.Empty(_store.ListSectorStorage(WorldId, _anchorSector));

        var (ok, reason, newSeq) = _store.DepositCargo(WorldId, MainLegion, _anchorSector, seq: 0);
        Assert.True(ok, reason);

        Assert.Empty(_store.ListCargo(WorldId, MainLegion));
        var stored = Assert.Single(_store.ListSectorStorage(WorldId, _anchorSector));
        Assert.Equal(newSeq, stored.Seq);
        Assert.Equal("instance", stored.Kind);
        Assert.Equal(id, stored.InstanceId);
    }

    [Fact]
    public void Withdraw_moves_never_copies_by_row_count()
    {
        InsertStorageRow(_anchorSector, seq: 0, kind: "stack", containerId: "mat-ct-iron", qty: 4);
        Assert.Empty(_store.ListCargo(WorldId, MainLegion));

        var (ok, reason, newSeq) = _store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 0, weightEach: 25);
        Assert.True(ok, reason);

        Assert.Empty(_store.ListSectorStorage(WorldId, _anchorSector));
        var aboard = Assert.Single(_store.ListCargo(WorldId, MainLegion));
        Assert.Equal(newSeq, aboard.Seq);
        Assert.Equal("stack", aboard.Kind);
        Assert.Equal("mat-ct-iron", aboard.ContainerId);
        Assert.Equal(4, aboard.Qty);
        Assert.Equal(25, aboard.WeightEach);
    }

    // ---- AC 2: withdraw weight is fresh per call, never a stored column --------------------------

    [Fact]
    public void Withdraw_weight_comes_fresh_from_each_call()
    {
        InsertStorageRow(_anchorSector, seq: 0, kind: "stack", containerId: "mat-ct-iron", qty: 1);
        InsertStorageRow(_anchorSector, seq: 1, kind: "stack", containerId: "mat-ct-iron", qty: 1);

        Assert.True(_store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 0, weightEach: 40).Ok);
        Assert.True(_store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 1, weightEach: 90).Ok);

        var weights = _store.ListCargo(WorldId, MainLegion)
            .Select(r => r.WeightEach).OrderBy(w => w).ToList();
        Assert.Equal(new[] { 40L, 90L }, weights);
    }

    [Fact]
    public void Sector_storage_has_no_weight_each_column_loud_regression()
    {
        // The corrected design (plan Task 2.1): sector storage carries NO weight_each column, so a
        // withdraw can never "resolve" weight from it. Pin the exact 7-column shape, then prove a
        // column read fails loudly (no such column) instead of silently returning zero — if the
        // column is ever reintroduced, this test fails loudly at the reintroduction itself.
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var shape = db.CreateCommand();
        shape.CommandText = "SELECT name FROM pragma_table_info('rpg_world_sector_storage') ORDER BY cid;";
        using var r = shape.ExecuteReader();
        var columns = new List<string>();
        while (r.Read()) columns.Add(r.GetString(0));
        Assert.Equal(
            new[] { "world_id", "sector_id", "seq", "kind", "instance_id", "container_id", "qty" },
            columns);

        using var probe = db.CreateCommand();
        probe.CommandText = "SELECT weight_each FROM rpg_world_sector_storage LIMIT 1;";
        var ex = Assert.Throws<SqliteException>(() => probe.ExecuteScalar());
        Assert.Contains("no such column", ex.Message);
    }

    // ---- AC 1 + AC 5: the SameFaction gate (static cross-faction + same-turn live state) ---------

    [Fact]
    public void Deposit_refuses_wrong_faction_without_writing()
    {
        var id = LoadInstanceAboard(RivalLegion, weightEach: 10);

        var (ok, reason, newSeq) = _store.DepositCargo(WorldId, RivalLegion, _anchorSector, seq: 0);
        Assert.False(ok);
        Assert.Equal("cargo.wrong-faction", reason);
        Assert.Equal(-1, newSeq);

        Assert.Equal(id, Assert.Single(_store.ListCargo(WorldId, RivalLegion)).InstanceId);
        Assert.Empty(_store.ListSectorStorage(WorldId, _anchorSector));
    }

    [Fact]
    public void Withdraw_refuses_wrong_faction_without_writing()
    {
        InsertStorageRow(_anchorSector, seq: 0, kind: "instance", instanceId: "inst-ct-rival");

        var (ok, reason, _) = _store.WithdrawCargo(WorldId, _anchorSector, RivalLegion, seq: 0, weightEach: 10);
        Assert.False(ok);
        Assert.Equal("cargo.wrong-faction", reason);

        Assert.Single(_store.ListSectorStorage(WorldId, _anchorSector));
        Assert.Empty(_store.ListCargo(WorldId, RivalLegion));
    }

    [Fact]
    public void Deposit_reads_live_sector_owner_after_a_same_turn_capture()
    {
        var id = LoadInstanceAboard(MainLegion, weightEach: 70);

        // Only the sector's owner changed — the legion never moved, nothing was cached. The gate
        // must read the CURRENT owner, not the faction that owned the sector at load time.
        var state = _store.LoadWorldState(WorldId)!;
        _store.DiffCommitForTest(WorldId, WithSectorOwner(state, _anchorSector, _rivalFaction));

        var (ok, reason, _) = _store.DepositCargo(WorldId, MainLegion, _anchorSector, seq: 0);
        Assert.False(ok);
        Assert.Equal("cargo.wrong-faction", reason);

        Assert.Equal(id, Assert.Single(_store.ListCargo(WorldId, MainLegion)).InstanceId);
        Assert.Empty(_store.ListSectorStorage(WorldId, _anchorSector));
    }

    [Fact]
    public void Withdraw_reads_live_sector_owner_after_a_same_turn_capture()
    {
        InsertStorageRow(_anchorSector, seq: 0, kind: "instance", instanceId: "inst-ct-live");

        var state = _store.LoadWorldState(WorldId)!;
        _store.DiffCommitForTest(WorldId, WithSectorOwner(state, _anchorSector, _rivalFaction));

        var (ok, reason, _) = _store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 0, weightEach: 70);
        Assert.False(ok);
        Assert.Equal("cargo.wrong-faction", reason);

        Assert.Single(_store.ListSectorStorage(WorldId, _anchorSector));
        Assert.Empty(_store.ListCargo(WorldId, MainLegion));
    }

    // ---- AC 4: presence gate ---------------------------------------------------------------------

    [Fact]
    public void Deposit_refuses_not_present_without_writing()
    {
        // AwayLegion shares the anchor's faction — presence fires before faction, so the refusal
        // proves the AtSectorId check itself, not the gate behind it.
        var id = LoadInstanceAboard(AwayLegion, weightEach: 10);

        var (ok, reason, _) = _store.DepositCargo(WorldId, AwayLegion, _anchorSector, seq: 0);
        Assert.False(ok);
        Assert.Equal("cargo.not-present", reason);

        Assert.Equal(id, Assert.Single(_store.ListCargo(WorldId, AwayLegion)).InstanceId);
        Assert.Empty(_store.ListSectorStorage(WorldId, _anchorSector));
    }

    [Fact]
    public void Withdraw_refuses_not_present_without_writing()
    {
        InsertStorageRow(_anchorSector, seq: 0, kind: "instance", instanceId: "inst-ct-away");

        var (ok, reason, _) = _store.WithdrawCargo(WorldId, _anchorSector, AwayLegion, seq: 0, weightEach: 10);
        Assert.False(ok);
        Assert.Equal("cargo.not-present", reason);

        Assert.Single(_store.ListSectorStorage(WorldId, _anchorSector));
        Assert.Empty(_store.ListCargo(WorldId, AwayLegion));
    }

    // ---- AC 6: both capacity gates refuse independently --------------------------------------------

    [Fact]
    public void Deposit_into_a_full_sector_refuses_without_touching_legion_cargo()
    {
        for (var i = 0; i < TestDepotBonus; i++)
            InsertStorageRow(_anchorSector, seq: i, kind: "instance", instanceId: $"inst-ct-full-{i}");
        var id = LoadInstanceAboard(MainLegion, weightEach: 10);

        var (ok, reason, _) = _store.DepositCargo(WorldId, MainLegion, _anchorSector, seq: 0);
        Assert.False(ok);
        Assert.Equal("cargo.sector-full", reason);

        Assert.Equal(id, Assert.Single(_store.ListCargo(WorldId, MainLegion)).InstanceId);
        Assert.Equal(TestDepotBonus, _store.ListSectorStorage(WorldId, _anchorSector).Count);
    }

    [Fact]
    public void Withdraw_that_overweights_the_legion_refuses_without_touching_storage()
    {
        // MainLegion caps at 200 weight: 190 aboard leaves no room for a 50-weight withdraw.
        LoadInstanceAboard(MainLegion, weightEach: 190);
        InsertStorageRow(_anchorSector, seq: 0, kind: "instance", instanceId: "inst-ct-heavy");

        var (ok, reason, _) = _store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 0, weightEach: 50);
        Assert.False(ok);
        Assert.Equal("cargo.over-weight", reason);

        Assert.Single(_store.ListSectorStorage(WorldId, _anchorSector));
        Assert.Single(_store.ListCargo(WorldId, MainLegion));
    }

    [Fact]
    public void Withdraw_past_legion_slots_refuses_without_touching_storage()
    {
        // MainLegion caps at 4 slots: four weight-10 rows fill it, so a fifth refuses on slots
        // while weight (40 + 10 <= 200) would still fit — proving the slot gate is independent.
        for (var i = 0; i < 4; i++)
            LoadInstanceAboard(MainLegion, weightEach: 10);
        InsertStorageRow(_anchorSector, seq: 0, kind: "instance", instanceId: "inst-ct-slot");

        var (ok, reason, _) = _store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 0, weightEach: 10);
        Assert.False(ok);
        Assert.Equal("cargo.no-slots", reason);

        Assert.Single(_store.ListSectorStorage(WorldId, _anchorSector));
        Assert.Equal(4, _store.ListCargo(WorldId, MainLegion).Count);
    }

    // ---- AC 7 (atomicity leg): forced failure both directions ------------------------------------

    [Fact]
    public void Deposit_is_atomic_under_forced_failure()
    {
        var id = LoadInstanceAboard(MainLegion, weightEach: 70);

        RpgStore.TestProbeCargoTransfer = () => throw new InvalidOperationException("forced mid-deposit crash");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                _store.DepositCargo(WorldId, MainLegion, _anchorSector, seq: 0));
        }
        finally
        {
            RpgStore.TestProbeCargoTransfer = null;
        }

        Assert.Equal(id, Assert.Single(_store.ListCargo(WorldId, MainLegion)).InstanceId);
        Assert.Empty(_store.ListSectorStorage(WorldId, _anchorSector));
    }

    [Fact]
    public void Withdraw_is_atomic_under_forced_failure()
    {
        InsertStorageRow(_anchorSector, seq: 0, kind: "instance", instanceId: "inst-ct-crash");

        RpgStore.TestProbeCargoTransfer = () => throw new InvalidOperationException("forced mid-withdraw crash");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                _store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 0, weightEach: 70));
        }
        finally
        {
            RpgStore.TestProbeCargoTransfer = null;
        }

        Assert.Equal("inst-ct-crash",
            Assert.Single(_store.ListSectorStorage(WorldId, _anchorSector)).InstanceId);
        Assert.Empty(_store.ListCargo(WorldId, MainLegion));
    }

    // ---- AC 3: the item root is never touched -----------------------------------------------------

    [Fact]
    public void Transfer_cycle_never_touches_item_ownership_or_disposition_or_stock()
    {
        var id = LoadInstanceAboard(MainLegion, weightEach: 70);
        _store.AdjustStock(PlayerIdStr, "item.ct-rations", 10);
        Assert.True(_store.LoadCargo(WorldId, MainLegion, PlayerId,
            "stack", null, "item.ct-rations", 4, weightEach: 25).Ok);

        Assert.True(_store.DepositCargo(WorldId, MainLegion, _anchorSector, seq: 0).Ok);
        Assert.Equal("cargo", _store.GetItem(id)!.Disposition);
        Assert.Equal(PlayerIdStr, _store.GetItem(id)!.PlayerId);
        Assert.Equal(6, _store.ListStock(PlayerIdStr).Single(s => s.ContainerId == "item.ct-rations").Qty);

        Assert.True(_store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 0, weightEach: 70).Ok);
        Assert.Equal("cargo", _store.GetItem(id)!.Disposition);
        Assert.Equal(PlayerIdStr, _store.GetItem(id)!.PlayerId);
        Assert.Equal(6, _store.ListStock(PlayerIdStr).Single(s => s.ContainerId == "item.ct-rations").Qty);
    }

    // ---- SameFaction contract + misc guards -------------------------------------------------------

    [Fact]
    public void SameFaction_is_plain_ordinal_equality()
    {
        Assert.True(RpgStore.SameFaction("dave", "dave"));
        Assert.False(RpgStore.SameFaction("dave", "zomboss"));
        Assert.False(RpgStore.SameFaction("dave", "Dave"));
    }

    [Fact]
    public void Unknown_legion_or_sector_refuses_not_found()
    {
        Assert.Equal("cargo.not-found",
            _store.DepositCargo(WorldId, "no-such-legion", _anchorSector, seq: 0).Reason);
        Assert.Equal("cargo.not-found",
            _store.DepositCargo(WorldId, MainLegion, "no-such-sector", seq: 0).Reason);
        Assert.Equal("cargo.not-found",
            _store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 7, weightEach: 10).Reason);
    }

    [Fact]
    public void Withdraw_rejects_negative_weight_before_any_read()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _store.WithdrawCargo(WorldId, _anchorSector, MainLegion, seq: 0, weightEach: -1));
    }

    // ---- legion-to-legion per spec §Design 4 (module 1's verb, pinned — not rebuilt here) --------

    [Fact]
    public void Legion_to_legion_cross_empire_still_refuses_without_writing()
    {
        var id = LoadInstanceAboard(MainLegion, weightEach: 10);

        var (ok, reason, newSeq) = _store.TransferCargo(WorldId, MainLegion, RivalLegion, seq: 0, PlayerId);
        Assert.False(ok);
        Assert.Equal("cargo.cross-empire", reason);
        Assert.Equal(-1, newSeq);

        Assert.Equal(id, Assert.Single(_store.ListCargo(WorldId, MainLegion)).InstanceId);
        Assert.Empty(_store.ListCargo(WorldId, RivalLegion));
    }
}
