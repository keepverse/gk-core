using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Data.Tests.CargoCommands;

// Task 4A.1 (`cargo-commands`, empire-inventory-surfaces module 1): the six kinds filed via
// `SubmitWorldCommands` and resolved by the `CommitWorldTurn` post-Step pass — moves never copy,
// refusals are the verbs' own strings verbatim with zero writes, claim skips-but-never-refuses
// with log replay idempotency, claims land before the decay tick, old logs replay hash-identical
// (`RulesetVersion` stays 10), mass resolves server-side with loud refusal, and the D1/D3 guards
// pin helper reuse.
//
// All stores are in-memory (`DataTestStore.Create`); nothing here touches the disk. The catalog is
// the shipped corpus plus one in-memory depot row (sector item capacity needs a live `ItemStorage`
// row — the 25 shipped rows carry none on this kind), layered by `StructureCorpusOverlay`. This
// fixture used to copy the whole corpus into a temp directory and delete it in dispose; the write
// was removed, not cleaned up (docs/contributing/testing-standard.md R1/R2).
//
// Shares the `StructureCatalogSwap` xUnit collection with the CargoTransfer/WonderBuild suites:
// every fixture wholesale-replaces the static catalog, so they run sequentially, never raced.
[Collection("StructureCatalogSwap")]
public class CargoCommandResolveTests : IDisposable
{
    const long PlayerId = 1;
    const string PlayerIdStr = "1";
    const string WorldId = "w-cargo-commands";
    const string Commander = "dave";
    const string HomeSector = "homeworld";
    const string OtherSector = "ember-hollow";
    const string FarSector = "frost-mire";
    const string MainLegion = "e-cc-main";
    const string SecondLegion = "e-cc-second";
    const string AwayLegion = "e-cc-away";
    const string RivalLegion = "e-cc-rival";
    const string TestDepotId = "test-depot-cc";
    const long TestDepotBonus = 6;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    public CargoCommandResolveTests()
    {
        // Same tuning every cargo suite configures (100 mass / 2 slots per member): identical
        // values keep the suites order-independent under parallel runs.
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));
        RpgStore.TestCargoWeightProbe = null;

        StructureCatalog.Configure(StructureCorpusOverlay.LoadWithRows(
            StructureCorpusOverlay.Depot(TestDepotId, TestDepotBonus)));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId);
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

        // Placement is fight-free by construction (`ContactResolver.SectorContacts` fights
        // hostile pairs standing in the same sector, and a fight at the depot sector would throw
        // out of `BattleEngine.ValidateActorKey` on the depot's guard-less test structure — not a
        // cargo defect, just a sector this fixture must never let fight): homeworld holds only
        // dave legions, ember-hollow only Away, frost-mire only Rival. The template's own three
        // forces (dave@homeworld, zomboss@black-gate, wild@ash-waste) already stand alone.
        var customized = built with
        {
            Entities = built.Entities
                .Append(Legion(MainLegion, Commander, HomeSector))
                .Append(Legion(SecondLegion, Commander, HomeSector))
                .Append(Legion(AwayLegion, Commander, OtherSector))
                .Append(Legion(RivalLegion, "zomboss", FarSector))
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList(),
        };

        var (ok, reason, _) = _store.CreateWorld(PlayerId, customized);
        Assert.True(ok, reason);

        // Homeworld's last slot becomes an active depot: capacity = TestDepotBonus, every other
        // slot names no ItemStorage structure, so EffectiveCapacity reads exactly the bonus. The
        // last slot, never the first: slot 0 is the Seat — overwriting it would leave the seat
        // guard with no combat magnitudes, and the first hostile-contact fight at this sector
        // would throw out of `BattleEngine.ValidateActorKey` instead of resolving.
        var slotIndex = _store.LoadWorldState(WorldId)!.Sectors
            .First(s => string.Equals(s.SectorId, HomeSector, StringComparison.Ordinal))
            .Slots.Last().SlotIndex;
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_slots SET structure_id = $id, construction_turns_remaining = NULL
            WHERE world_id = $w AND sector_id = $s AND slot_index = $i;
            """;
        cmd.Parameters.AddWithValue("$id", TestDepotId);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", HomeSector);
        cmd.Parameters.AddWithValue("$i", slotIndex);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    public void Dispose()
    {
        RpgStore.TestCargoWeightProbe = null;
        RpgStore.TestProbeMidWrite = null;
        StructureCatalog.Configure(StructureCorpus.Load(StructureCorpusOverlay.RealCorpusRoot()));
        _testStore.Dispose();
    }

    // ---- fixture helpers ------------------------------------------------------------------------


    static WorldCommand Cmd(string id, string kind) => new()
    {
        CommanderId = Commander,
        CommandId = id,
        Kind = kind,
        EntityId = MainLegion,
    };

    void FileOrder(WorldCommand command)
    {
        var (ok, reason, _) = _store.SubmitWorldCommand(WorldId, command);
        Assert.True(ok, reason);
    }

    WorldTurnCommitResult CommitAll()
    {
        // The human goes last: the first commit fills every AI faction, so committing Dave first
        // would resolve the turn before the others got a word in (WorldTurnCommitTests pattern).
        _store.CommitWorldTurn(WorldId, "wild", 0);
        _store.CommitWorldTurn(WorldId, "zomboss", 0);
        return _store.CommitWorldTurn(WorldId, Commander, 0);
    }

    TurnReport Report() => _store.GetWorldTurnReport(WorldId, 0)!;

    // The mass source until a real one ships: tests resolve fixture identities through the named
    // probe, never the wire.
    static void Probe(Func<RpgStore.CargoWeightKey, long?> resolve) =>
        RpgStore.TestCargoWeightProbe = resolve;

    static void ProbeFixed(long mass) => Probe(_ => mass);

    /// <summary>
    /// A real owned instance: atom + container + roll + save, mirroring what an actual acquisition
    /// does (`rpg_item`'s FK to `effect_instance` is enforced, so the instance comes first).
    /// </summary>
    string SeedOwnedItem()
    {
        var tag = "cc" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId($"atom.{tag}", "", 1),
            KindId = "stat.modify", FamilyId = $"atom.{tag}", Variant = "", Tier = 1,
            Name = "Cargo Command Test", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
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

    void SeedStock(string containerId, long qty)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_item_stock (player_id, container_id, qty, updated_utc)
            VALUES ($p, $c, $q, $utc);
            """;
        cmd.Parameters.AddWithValue("$p", PlayerIdStr);
        cmd.Parameters.AddWithValue("$c", containerId);
        cmd.Parameters.AddWithValue("$q", qty);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    long StockQty(string containerId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT qty FROM rpg_item_stock WHERE player_id = $p AND container_id = $c;";
        cmd.Parameters.AddWithValue("$p", PlayerIdStr);
        cmd.Parameters.AddWithValue("$c", containerId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }

    string ItemDisposition(string instanceId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT disposition FROM rpg_item WHERE instance_id = $id;";
        cmd.Parameters.AddWithValue("$id", instanceId);
        return (string)cmd.ExecuteScalar()!;
    }

    /// <summary>Loads a fresh owned instance aboard <paramref name="entityId"/> via the direct verb
    /// (explicit mass — the command path is what resolves mass, not this setup).</summary>
    string LoadInstanceAboard(string entityId, long mass)
    {
        var id = SeedOwnedItem();
        var (ok, reason) = _store.LoadCargo(WorldId, entityId, PlayerId,
            "instance", id, null, 0, mass);
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

    string SeedCacheWithRows(
        string placeKind, string placeRef,
        (string Kind, string? InstanceId, string? ContainerId, long? Qty)[] rows)
    {
        var cacheId = "cc_" + Guid.NewGuid().ToString("N");
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using (var ins = db.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO rpg_corpse_cache
                  (cache_id, place_kind, place_ref, source_kind, created_utc, decay_started_utc, in_void, revision)
                VALUES ($id, $k, $r, 'legion_death', $now, NULL, 0, 0);
                """;
            ins.Parameters.AddWithValue("$id", cacheId);
            ins.Parameters.AddWithValue("$k", placeKind);
            ins.Parameters.AddWithValue("$r", placeRef);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            Assert.Equal(1, ins.ExecuteNonQuery());
        }
        var seq = 0;
        foreach (var row in rows)
        {
            using var item = db.CreateCommand();
            item.CommandText = """
                INSERT INTO rpg_corpse_cache_item
                  (cache_id, seq, kind, instance_id, container_id, qty, origin_owner)
                VALUES ($id, $s, $k, $iid, $cid, $q, 'e-dead-legion');
                """;
            item.Parameters.AddWithValue("$id", cacheId);
            item.Parameters.AddWithValue("$s", seq++);
            item.Parameters.AddWithValue("$k", row.Kind);
            item.Parameters.AddWithValue("$iid", (object?)row.InstanceId ?? DBNull.Value);
            item.Parameters.AddWithValue("$cid", (object?)row.ContainerId ?? DBNull.Value);
            item.Parameters.AddWithValue("$q", (object?)row.Qty ?? DBNull.Value);
            Assert.Equal(1, item.ExecuteNonQuery());
        }
        return cacheId;
    }

    int CargoCount(string entityId) => _store.ListCargo(WorldId, entityId).Count;

    int StorageCount(string sectorId) => _store.ListSectorStorage(WorldId, sectorId).Count;

    int CacheItemCount(string cacheId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_corpse_cache_item WHERE cache_id = $id;";
        cmd.Parameters.AddWithValue("$id", cacheId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    void ExecEntitySql(string sql, string entityId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ---- Testing bullet 1: per-kind resolve moves, never copies -----------------------------------

    [Fact]
    public void Load_instance_moves_from_armoury_to_cargo_by_row_count()
    {
        var id = SeedOwnedItem();
        ProbeFixed(10);
        FileOrder(Cmd("load-1", WorldCommandKinds.LoadCargo) with
        {
            CargoKind = "instance", InstanceId = id,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var aboard = Assert.Single(_store.ListCargo(WorldId, MainLegion));
        Assert.Equal("instance", aboard.Kind);
        Assert.Equal(id, aboard.InstanceId);
        Assert.Equal(10, aboard.WeightEach);
        Assert.Equal("cargo", ItemDisposition(id));
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("cargo.loaded:", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_stack_draws_stock_and_snapshots_server_mass()
    {
        SeedStock("cont-ld-stock", 5);
        Probe(key => key.ContainerId == "cont-ld-stock" ? 10 : null);
        FileOrder(Cmd("load-2", WorldCommandKinds.LoadCargo) with
        {
            CargoKind = "stack", ContainerId = "cont-ld-stock", Qty = 3,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var aboard = Assert.Single(_store.ListCargo(WorldId, MainLegion));
        Assert.Equal("stack", aboard.Kind);
        Assert.Equal(3, aboard.Qty);
        Assert.Equal(10, aboard.WeightEach);
        Assert.Equal(2, StockQty("cont-ld-stock"));
    }

    [Fact]
    public void Unload_moves_from_cargo_to_armoury_by_row_count()
    {
        var id = LoadInstanceAboard(MainLegion, mass: 10);
        FileOrder(Cmd("unload-1", WorldCommandKinds.UnloadCargo) with { Seq = 0 });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Empty(_store.ListCargo(WorldId, MainLegion));
        Assert.Equal("owned", ItemDisposition(id));
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail == "cargo.unloaded:0");
    }

    [Fact]
    public void Transfer_moves_from_legion_to_legion_by_row_count()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        FileOrder(Cmd("transfer-1", WorldCommandKinds.TransferCargo) with
        {
            TargetEntityId = SecondLegion, Seq = 0,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Empty(_store.ListCargo(WorldId, MainLegion));
        var aboard = Assert.Single(_store.ListCargo(WorldId, SecondLegion));
        Assert.Equal(10, aboard.WeightEach);
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("cargo.transferred:", StringComparison.Ordinal));
    }

    [Fact]
    public void Deposit_moves_from_cargo_to_vault_by_row_count()
    {
        var id = LoadInstanceAboard(MainLegion, mass: 10);
        FileOrder(Cmd("deposit-1", WorldCommandKinds.DepositCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Empty(_store.ListCargo(WorldId, MainLegion));
        var stored = Assert.Single(_store.ListSectorStorage(WorldId, HomeSector));
        Assert.Equal("instance", stored.Kind);
        Assert.Equal(id, stored.InstanceId);
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("cargo.deposited:", StringComparison.Ordinal));
    }

    [Fact]
    public void Withdraw_moves_from_vault_to_cargo_by_row_count()
    {
        InsertStorageRow(HomeSector, seq: 0, kind: "stack", containerId: "cont-wd", qty: 4);
        ProbeFixed(25);
        FileOrder(Cmd("withdraw-1", WorldCommandKinds.WithdrawCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Empty(_store.ListSectorStorage(WorldId, HomeSector));
        var aboard = Assert.Single(_store.ListCargo(WorldId, MainLegion));
        Assert.Equal("stack", aboard.Kind);
        Assert.Equal(4, aboard.Qty);
        Assert.Equal(25, aboard.WeightEach);
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("cargo.withdrawn:", StringComparison.Ordinal));
    }

    [Fact]
    public void Claim_moves_every_fitting_row_from_cache_to_cargo()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)"inst-claim-0", (string?)null, (long?)null),
            ("instance", (string?)"inst-claim-1", (string?)null, (long?)null),
        });
        ProbeFixed(10);
        FileOrder(Cmd("claim-1", WorldCommandKinds.ClaimCache) with { CacheId = cacheId });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(2, CargoCount(MainLegion));
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail == "cache.claimed:2+0");
    }

    // ---- Testing bullet 2: refusals verbatim, nothing written --------------------------------------

    void AssertDropped(string detail, int cargoBefore, int storageBefore)
    {
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == detail);
        Assert.Equal(cargoBefore, CargoCount(MainLegion));
        Assert.Equal(storageBefore, StorageCount(HomeSector));
    }

    [Fact]
    public void Deposit_from_a_legion_not_at_the_sector_refuses_not_present()
    {
        // Resolved directly in an uncommitted transaction, never through a commit: Away stands
        // alone at ember-hollow, an unsupplied sector, and `Step`'s own pressure phase would
        // starve-and-disband it (`legion.starved`) before the pass ever ran — a real mechanic,
        // but this refusal is about position, not upkeep. The resolver is the unit under test
        // (same function the commit hook calls); the commit path is proven by every other
        // refusal in this file.
        LoadInstanceAboard(AwayLegion, mass: 10);
        var command = Cmd("dep-np", WorldCommandKinds.DepositCargo) with
        {
            EntityId = AwayLegion, SectorId = HomeSector, Seq = 0,
        };
        Assert.Equal(1, CargoCount(AwayLegion));

        (bool Ok, string Detail, string? SectorId) outcome;
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
            outcome = _store.ResolveCargoCommandUnlocked(
                db, tx, WorldId, command, PlayerId, DateTime.UtcNow.ToString("o"));

        Assert.False(outcome.Ok);
        Assert.Equal("cargo.not-present", outcome.Detail);
        Assert.Equal(HomeSector, outcome.SectorId);
        Assert.Equal(1, CargoCount(AwayLegion));
        Assert.Empty(_store.ListSectorStorage(WorldId, HomeSector));
        // No commit: the transaction rolls back on dispose, moving nothing.
    }

    [Fact]
    public void Deposit_by_a_foreign_legion_refuses_wrong_faction()
    {
        // Resolved directly in an uncommitted transaction, never through a commit: homeworld is
        // the only owned sector, so a foreign legion AT it is a hostile pair and `Step` would
        // fight (correctly) before the pass ever ran — routing or killing the very subject this
        // refusal is about. The resolver is the unit under test here (same function the commit
        // hook calls); every other refusal in this file proves the commit path.
        LoadInstanceAboard(RivalLegion, mass: 10);
        var command = new WorldCommand
        {
            CommanderId = "zomboss", CommandId = "dep-wf", Kind = WorldCommandKinds.DepositCargo,
            EntityId = RivalLegion, SectorId = HomeSector, Seq = 0,
        };

        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var tx = db.BeginTransaction();
        using (var move = db.CreateCommand())
        {
            move.Transaction = tx;
            move.CommandText = """
                UPDATE rpg_world_entities SET at_sector_id = $s WHERE world_id = $w AND entity_id = $e;
                """;
            move.Parameters.AddWithValue("$s", HomeSector);
            move.Parameters.AddWithValue("$w", WorldId);
            move.Parameters.AddWithValue("$e", RivalLegion);
            Assert.Equal(1, move.ExecuteNonQuery());
        }

        var (ok, detail, sectorId) = _store.ResolveCargoCommandUnlocked(
            db, tx, WorldId, command, PlayerId, DateTime.UtcNow.ToString("o"));
        Assert.False(ok);
        Assert.Equal("cargo.wrong-faction", detail);
        Assert.Equal(HomeSector, sectorId);
        tx.Rollback();
        Assert.Equal(1, CargoCount(RivalLegion));
        Assert.Empty(_store.ListSectorStorage(WorldId, HomeSector));
        // No commit: the transaction rolled back, moving nothing.
    }

    [Fact]
    public void Deposit_into_a_full_vault_refuses_sector_full()
    {
        for (var i = 0; i < TestDepotBonus; i++)
            InsertStorageRow(HomeSector, seq: i, kind: "stack", containerId: "cont-fill", qty: 1);
        LoadInstanceAboard(MainLegion, mass: 10);
        FileOrder(Cmd("dep-full", WorldCommandKinds.DepositCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        AssertDropped("cargo.sector-full", cargoBefore: 1, storageBefore: (int)TestDepotBonus);
    }

    [Fact]
    public void Withdraw_beyond_capacity_refuses_over_weight()
    {
        InsertStorageRow(HomeSector, seq: 0, kind: "stack", containerId: "cont-big", qty: 1);
        ProbeFixed(10_000);
        FileOrder(Cmd("wd-ow", WorldCommandKinds.WithdrawCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        AssertDropped("cargo.over-weight", cargoBefore: 0, storageBefore: 1);
    }

    [Fact]
    public void Transfer_across_empires_after_a_same_turn_reassignment_refuses_cross_empire()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        FileOrder(Cmd("tr-ce", WorldCommandKinds.TransferCargo) with
        {
            TargetEntityId = AwayLegion, Seq = 0,
        });

        // The destination changes hands between filing and commit: admission saw an owned
        // legion, the resolver must read live state, never the stale filing. Away stands alone
        // at ember-hollow, so the reassignment creates no hostile pair and `Step` fights
        // nothing — the refusal below is the resolver's, not a battle's.
        ExecEntitySql(
            "UPDATE rpg_world_entities SET owner_faction_id = 'zomboss' WHERE world_id = $w AND entity_id = $e;",
            AwayLegion);

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == "cargo.cross-empire");
        Assert.Equal(1, CargoCount(MainLegion));
        Assert.Equal(0, CargoCount(AwayLegion));
    }

    [Fact]
    public void Load_beyond_capacity_refuses_over_weight()
    {
        var id = SeedOwnedItem();
        ProbeFixed(10_000);
        FileOrder(Cmd("ld-ow", WorldCommandKinds.LoadCargo) with
        {
            CargoKind = "instance", InstanceId = id,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        AssertDropped("cargo.over-weight", cargoBefore: 0, storageBefore: 0);
        Assert.Equal("owned", ItemDisposition(id));
    }

    [Fact]
    public void Unload_of_a_missing_row_refuses_not_found()
    {
        FileOrder(Cmd("uld-nf", WorldCommandKinds.UnloadCargo) with { Seq = 99 });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        AssertDropped("cargo.not-found", cargoBefore: 0, storageBefore: 0);
    }

    [Fact]
    public void Claim_of_a_forged_cache_refuses_unreachable()
    {
        ProbeFixed(10);
        FileOrder(Cmd("cl-un", WorldCommandKinds.ClaimCache) with { CacheId = "cc_does_not_exist" });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == "cache.unreachable");
        Assert.Equal(0, CargoCount(MainLegion));
    }

    [Fact]
    public void Command_path_load_with_no_mass_source_refuses_weight_unknown()
    {
        // No probe set: the lookup misses loudly (`cargo.weight-unknown`), never zero-fills (D3).
        var id = SeedOwnedItem();
        FileOrder(Cmd("ld-wu", WorldCommandKinds.LoadCargo) with
        {
            CargoKind = "instance", InstanceId = id,
        });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        AssertDropped("cargo.weight-unknown", cargoBefore: 0, storageBefore: 0);
        Assert.Equal("owned", ItemDisposition(id));
    }

    [Fact]
    public void Order_for_a_destroyed_legion_drops_entity_gone()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        FileOrder(Cmd("gone-1", WorldCommandKinds.UnloadCargo) with { Seq = 0 });
        ExecEntitySql("DELETE FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;", MainLegion);

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == "entity.gone");
    }

    [Fact]
    public void Order_for_a_routed_legion_drops_entity_routed()
    {
        // Resolved directly in an uncommitted transaction: `Step` spends a rout at the top of
        // the turn it costs (`TurnEngine.cs` — clearing it there lets a force broken again this
        // same turn keep the new rout), so a legion routed at filing always reaches the pass
        // recovered. This arm is for the mid-turn rout — modelled here by routing inside the
        // transaction, after the filing. Zero verb writes, proven behaviorally: every
        // `*Unlocked` cargo verb fires `TestProbeMidWrite` between its delete and its insert.
        SeedStock("cont-routed", 5);
        var (loaded, loadReason) = _store.LoadCargo(WorldId, MainLegion, PlayerId,
            "stack", null, "cont-routed", 3, weightEach: 10);
        Assert.True(loaded, loadReason);
        var command = Cmd("routed-1", WorldCommandKinds.UnloadCargo) with { Seq = 0 };

        var wrote = false;
        RpgStore.TestProbeMidWrite = () => wrote = true;
        (bool Ok, string Detail, string? SectorId) outcome;
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
        {
            using (var rout = db.CreateCommand())
            {
                rout.Transaction = tx;
                rout.CommandText = """
                    UPDATE rpg_world_entities SET routed = 1 WHERE world_id = $w AND entity_id = $e;
                    """;
                rout.Parameters.AddWithValue("$w", WorldId);
                rout.Parameters.AddWithValue("$e", MainLegion);
                Assert.Equal(1, rout.ExecuteNonQuery());
            }
            outcome = _store.ResolveCargoCommandUnlocked(
                db, tx, WorldId, command, PlayerId, DateTime.UtcNow.ToString("o"));
            tx.Rollback();
        }
        RpgStore.TestProbeMidWrite = null;

        Assert.False(outcome.Ok);
        Assert.Equal("entity.routed", outcome.Detail);
        Assert.False(wrote);
        Assert.Equal(1, CargoCount(MainLegion));
    }

    // ---- Testing bullet 3: claim skip-not-refuse + idempotency ---------------------------------------

    [Fact]
    public void Mixed_fit_claim_reports_claimed_plus_skipped_and_leaves_the_rest()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)"inst-fit-0", (string?)null, (long?)null),
            ("instance", (string?)"inst-fit-1", (string?)null, (long?)null),
            ("stack", (string?)null, "cont-heavy", (long?)5),
        });
        // Two light rows fit (2 × 10 mass, 2 slots of 200 mass / 4 slots); the heavy row
        // (5 × 1000) fits neither gate and stays behind — never a whole-claim refusal.
        Probe(key => key.ContainerId == "cont-heavy" ? 1000 : 10);
        FileOrder(Cmd("claim-mix", WorldCommandKinds.ClaimCache) with { CacheId = cacheId });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail == "cache.claimed:2+1");
        Assert.Equal(2, CargoCount(MainLegion));
        Assert.Equal(1, CacheItemCount(cacheId));
    }

    [Fact]
    public void Re_resolving_the_same_command_id_returns_the_recorded_result_untouched()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)"inst-re-0", (string?)null, (long?)null),
        });
        ProbeFixed(10);
        var command = Cmd("claim-replay", WorldCommandKinds.ClaimCache) with { CacheId = cacheId };
        FileOrder(command);

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(1, CargoCount(MainLegion));

        // The claim log already holds this correlation: resolving again must replay the recorded
        // result byte-identically (same detail string) with zero new movement — and resolve no
        // mass twice (the probe throws if consulted). The resolution runs alone inside its
        // transaction (no other connection touches the store until it rolls back on dispose);
        // counts are asserted before and after, never across the open transaction.
        Probe(_ => throw new InvalidOperationException("replay must never re-resolve mass"));
        (bool Ok, string Detail, string? SectorId) replayed;
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
            replayed = _store.ResolveCargoCommandUnlocked(
                db, tx, WorldId, command, PlayerId, DateTime.UtcNow.ToString("o"));
        Assert.True(replayed.Ok);
        Assert.Equal("cache.claimed:1+0", replayed.Detail);
        Assert.Equal(1, CargoCount(MainLegion));
        Assert.Equal(0, CacheItemCount(cacheId));
    }

    // ---- Testing bullet 4: claim-before-decay ----------------------------------------------------------

    [Fact]
    public void Claimed_rows_are_absent_and_skipped_rows_present_after_the_decay_tick_commit()
    {
        // The pass runs before `TickCorpseCacheDecayForPlayerUnlocked` in the same commit
        // (`RpgStore.WorldTurns.cs`): a claimed row is gone before decay sees it, a skipped
        // (left-behind) row remains in the cache and stays subject to it.
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)"inst-dec-0", (string?)null, (long?)null),
            ("stack", (string?)null, "cont-dec-heavy", (long?)5),
        });
        Probe(key => key.ContainerId == "cont-dec-heavy" ? 1000 : 10);
        FileOrder(Cmd("claim-decay", WorldCommandKinds.ClaimCache) with { CacheId = cacheId });

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail == "cache.claimed:1+1");
        var aboard = Assert.Single(_store.ListCargo(WorldId, MainLegion));
        Assert.Equal("inst-dec-0", aboard.InstanceId);
        Assert.Equal(1, CacheItemCount(cacheId));
    }

    // ---- Testing bullet 5: old-log replay hash unchanged, RulesetVersion stays 10 -----------------------

    [Fact]
    public void Old_log_replay_hash_is_unchanged_with_the_pass_present_and_ruleset_unbumped()
    {
        // No-bump proof for the unaffected population: the tree's `RulesetVersion` (12 —
        // `budget-debit` rides 11 and bumps 11 → 12; a zero-priced log replays identical because
        // no debit fires and the post-pass re-hash is a fixed point) is what the log carries,
        // and a turn with zero new kinds hashes identically through the commit
        // (Step + pass + spend + decay + re-hash) and through a bare `TurnEngine.Step` replay —
        // the pass is a fast-path no-op on old logs.
        var ruleset = TurnEngine.RulesetVersion;

        // A log with zero new kinds: every faction stands fast, filed explicitly so the AI fill
        // skips them and the recorded command set is exactly what the replay below re-runs.
        foreach (var faction in new[] { Commander, "wild", "zomboss" })
            FileOrder(new WorldCommand
            {
                CommanderId = faction, CommandId = "stand-" + faction, Kind = WorldCommandKinds.StandFast,
            });

        var before = _store.LoadWorldState(WorldId)!;
        var logged = _store.ListWorldCommands(WorldId, 0);
        var seed = _store.GetWorldHeader(WorldId)!.Seed;
        var expected = TurnEngine.Step(before, logged, seed, DistrictAssaultResolver.Instance).StateHash;

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);
        Assert.Equal(expected, commit.StateHash);
        Assert.Equal(ruleset, _store.GetWorldTurnLog(WorldId, 0)!.RulesetVersion);
    }

    // ---- Testing bullet 6: mass-probe extension + helper-reuse guard ---------------------------------------

    [Fact]
    public void Resolver_issues_no_mass_column_read_and_no_tally_outside_the_owner_files()
    {
        // D1 locked (reuse, never redefine) + D3 locked (no mass on the wire, server-side
        // resolution): `RpgStore.CargoCommands.cs` calls the shared capacity helpers through the
        // verbs and resolves mass through the named probe — it holds no `weight_each` column read
        // (against any table) and no cargo tally of its own. A reintroduction fails here, loudly,
        // at the reintroduction itself — mirroring the sector-storage shape regression
        // (`CargoTransferTests.Sector_storage_has_no_weight_each_column_loud_regression`).
        var path = LocateSource("src", "FusionRpg.Data", "Sqlite", "RpgStore.CargoCommands.cs");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("weight_each", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sum(", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("count(", text, StringComparison.OrdinalIgnoreCase);
    }

    static string LocateSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = parts.Aggregate(dir.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not locate " + string.Join('/', parts));
    }

    // ---- Testing bullet 7 is Core-side: WorldCommandAdmissionTests (cargo arms) -----------------------------
    // ---- Debit seam: accept-always today, debit-after-refill position ------------------------------------------

    [Fact]
    public void Debit_seam_accepts_always_and_costs_nothing_today()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var tx = db.BeginTransaction();
        var (ok, reason) = _store.DebitActCostUnlocked(db, tx, WorldId, MainLegion, WorldCommandKinds.DepositCargo);
        Assert.True(ok);
        Assert.Equal("ok", reason);
    }
}
