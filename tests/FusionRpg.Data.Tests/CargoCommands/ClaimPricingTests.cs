using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Data.Tests.CargoCommands;

// world-action-economy `claim-pricing` (docs/architecture/world-action-economy/spec-claim-pricing.md):
// the five priced verbs wired onto the landed `DebitActCostUnlocked` seam (claim 250 before the
// per-row loop after the reachability re-check; deposit/withdraw 100, load/unload 50 after gates
// before the move; transfer enters the seam charged-0), claim-vs-decay precedence, replay-once per
// `CommandId` (convergent `correlationId := CommandId`), deposit-pipe end-to-end proof, and the
// per-verb golden delta table (zero-kind + transfer-only identical on 12; each priced kind
// diverges by exactly its debit). No order/refusal/number re-decisions: the seam body, the
// debit-after-refill site, `entity.spent`, and RulesetVersion 12 all ride landed (4A.6/4A.1).
// Costs read through the Hub (never literals); the provisional numbers (claim 250, deposit 100,
// withdraw 100, load 50, unload 50, transfer 0) appear only in comments. In-memory only
// (DataTestStore.Create); the catalog is the shipped corpus plus one in-memory depot row
// (`StructureCorpusOverlay`), so nothing is written to disk.
//
// Shares the `StructureCatalogSwap` collection with the sibling cargo suites: the fixture
// wholesale-replaces the static catalog, so it runs sequentially, never raced.
[Collection("StructureCatalogSwap")]
public sealed class ClaimPricingTests : IDisposable
{
    const string WorldId = "claim-pricing";
    const string Commander = "dave";
    const string PlayerIdStr = "1";
    const long PlayerId = 1;
    const string HomeSector = "homeworld";
    const string OtherSector = "ember-hollow";
    const string FarSector = "frost-mire";
    const string MainLegion = "e-pricing-legion-1";
    const string SecondLegion = "e-pricing-legion-2";
    const string AwayLegion = "e-pricing-away-1";
    const string RivalLegion = "e-pricing-rival-1";
    const string TestDepotId = "test-pricing-depot";
    const int TestDepotBonus = 10_000;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    public ClaimPricingTests()
    {
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
        StructureCatalog.Configure(StructureCorpus.Load(StructureCorpusOverlay.RealCorpusRoot()));
        _testStore.Dispose();
    }


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

    WorldTurnCommitResult CommitAll(int turn = 0)
    {
        _store.CommitWorldTurn(WorldId, "wild", turn);
        _store.CommitWorldTurn(WorldId, "zomboss", turn);
        return _store.CommitWorldTurn(WorldId, Commander, turn);
    }

    TurnReport Report(int turn = 0) => _store.GetWorldTurnReport(WorldId, turn)!;

    static void Probe(Func<RpgStore.CargoWeightKey, long?> resolve) =>
        RpgStore.TestCargoWeightProbe = resolve;

    static void ProbeFixed(long mass) => Probe(_ => mass);

    string SeedOwnedItem()
    {
        var tag = "cp" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId($"atom.{tag}", "", 1),
            KindId = "stat.modify", FamilyId = $"atom.{tag}", Variant = "", Tier = 1,
            Name = "Claim Pricing Test", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
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
        var cacheId = "cp_" + Guid.NewGuid().ToString("N");
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

    int CacheItemCount(string cacheId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_corpse_cache_item WHERE cache_id = $id;";
        cmd.Parameters.AddWithValue("$id", cacheId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    int StorageCount(string sectorId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_world_sector_storage WHERE world_id = $w AND sector_id = $s;";
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    int ReadBudget(string entityId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT movement_remaining FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;";
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? -9999);
    }

    (int Cost, bool Ok, string Detail)? ReadDebit(int turn, string commandId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT cost, ok, detail FROM rpg_world_act_debit WHERE world_id = $w AND turn = $t AND command_id = $id;";
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$t", turn);
        cmd.Parameters.AddWithValue("$id", commandId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetInt32(0), r.GetInt32(1) != 0, r.GetString(2));
    }

    static int ClaimCost() => WorldTuningHub.Tuning.Movement.ClaimCostMilli;
    static int DepositCost() => WorldTuningHub.Tuning.Movement.DepositCostMilli;
    static int WithdrawCost() => WorldTuningHub.Tuning.Movement.WithdrawCostMilli;
    static int LoadCost() => WorldTuningHub.Tuning.Movement.LoadCostMilli;
    static int UnloadCost() => WorldTuningHub.Tuning.Movement.UnloadCostMilli;
    static int MarchRefill() => MovementPolicy.BudgetFor("march");

    void FileStandFastForOthers(string suffix = "")
    {
        foreach (var faction in new[] { "wild", "zomboss" })
        {
            var (ok, reason, _) = _store.SubmitWorldCommand(WorldId, new WorldCommand
            {
                CommanderId = faction, CommandId = "cp-stand-" + faction + suffix, Kind = WorldCommandKinds.StandFast,
            });
            Assert.True(ok, reason);
        }
    }

    (WorldState World, string StateHash) StepWorld(int turn = 0)
    {
        var before = _store.LoadWorldState(WorldId)!;
        var logged = _store.ListWorldCommands(WorldId, turn);
        var seed = _store.GetWorldHeader(WorldId)!.Seed;
        var stepped = TurnEngine.Step(before, logged, seed, DistrictAssaultResolver.Instance);
        return (stepped.World, stepped.StateHash);
    }

    static void AssertDivergesByExactDebit(string commitHash, WorldState steppedWorld, string entityId, int cost)
    {
        var replayWorld = steppedWorld with
        {
            Entities = steppedWorld.Entities.Select(e =>
                string.Equals(e.EntityId, entityId, StringComparison.Ordinal)
                    ? e with { MovementRemaining = checked(e.MovementRemaining - cost) }
                    : e).ToList(),
        };
        Assert.Equal(commitHash, StateHasher.Hash(replayWorld));
    }

    // ---- Deposit-pipe proof, one filed→admitted→committed→reported round trip per verb ---------------
    // Each asserts the exact detail string plus the exact post-refill budget, plus the golden
    // consequence (priced verbs diverge from the Step hash by exactly the debit; transfer does not).

    [Fact]
    public void Claim_pipe_pays_250_with_exact_detail_and_diverges_by_exactly_the_debit()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)"inst-cp-claim-0", (string?)null, (long?)null),
        });
        Probe(key => key.InstanceId == "inst-cp-claim-0" ? 10 : null);
        FileOrder(Cmd("cp-claim-1", WorldCommandKinds.ClaimCache) with { CacheId = cacheId });
        FileStandFastForOthers();

        var stepped = StepWorld();
        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var entry = Assert.Single(Report().Entries, e => e.Subject == "cp-claim-1" && e.Kind == TurnReportKinds.Event);
        Assert.Equal(TurnReportKinds.Event, entry.Kind);
        Assert.Equal("cache.claimed:1+0", entry.Detail);
        Assert.Equal(MarchRefill() - ClaimCost(), ReadBudget(MainLegion));
        Assert.Equal(0, CacheItemCount(cacheId));
        Assert.Equal(1, CargoCount(MainLegion));
        var debit = ReadDebit(0, "cp-claim-1");
        Assert.NotNull(debit);
        Assert.True(debit!.Value.Ok);
        Assert.Equal(ClaimCost(), debit!.Value.Cost);

        Assert.NotEqual(stepped.StateHash, commit.StateHash);
        AssertDivergesByExactDebit(commit.StateHash!, stepped.World, MainLegion, ClaimCost());
    }

    [Fact]
    public void Deposit_pipe_pays_100_with_exact_detail_and_diverges_by_exactly_the_debit()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        ProbeFixed(10);
        FileOrder(Cmd("cp-deposit-1", WorldCommandKinds.DepositCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });
        FileStandFastForOthers();

        var stepped = StepWorld();
        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var entry = Assert.Single(Report().Entries, e => e.Subject == "cp-deposit-1" && e.Kind == TurnReportKinds.Event);
        Assert.Equal(TurnReportKinds.Event, entry.Kind);
        Assert.Equal("cargo.deposited:0", entry.Detail);
        Assert.Equal(MarchRefill() - DepositCost(), ReadBudget(MainLegion));
        Assert.Equal(0, CargoCount(MainLegion));
        Assert.Equal(1, StorageCount(HomeSector));

        Assert.NotEqual(stepped.StateHash, commit.StateHash);
        AssertDivergesByExactDebit(commit.StateHash!, stepped.World, MainLegion, DepositCost());
    }

    [Fact]
    public void Withdraw_pipe_pays_100_with_exact_detail_and_diverges_by_exactly_the_debit()
    {
        InsertStorageRow(HomeSector, 0, "stack", containerId: "cont-cp-wd", qty: 1);
        Probe(key => key.ContainerId == "cont-cp-wd" ? 10 : null);
        FileOrder(Cmd("cp-withdraw-1", WorldCommandKinds.WithdrawCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });
        FileStandFastForOthers();

        var stepped = StepWorld();
        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var entry = Assert.Single(Report().Entries, e => e.Subject == "cp-withdraw-1" && e.Kind == TurnReportKinds.Event);
        Assert.Equal(TurnReportKinds.Event, entry.Kind);
        Assert.Equal("cargo.withdrawn:0", entry.Detail);
        Assert.Equal(MarchRefill() - WithdrawCost(), ReadBudget(MainLegion));
        Assert.Equal(1, CargoCount(MainLegion));
        Assert.Equal(0, StorageCount(HomeSector));

        Assert.NotEqual(stepped.StateHash, commit.StateHash);
        AssertDivergesByExactDebit(commit.StateHash!, stepped.World, MainLegion, WithdrawCost());
    }

    [Fact]
    public void Load_pipe_pays_50_with_exact_detail_and_diverges_by_exactly_the_debit()
    {
        var instanceId = SeedOwnedItem();
        Probe(key => key.InstanceId == instanceId ? 10 : null);
        FileOrder(Cmd("cp-load-1", WorldCommandKinds.LoadCargo) with
        {
            CargoKind = "instance", InstanceId = instanceId,
        });
        FileStandFastForOthers();

        var stepped = StepWorld();
        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var entry = Assert.Single(Report().Entries, e => e.Subject == "cp-load-1" && e.Kind == TurnReportKinds.Event);
        Assert.Equal(TurnReportKinds.Event, entry.Kind);
        Assert.Equal("cargo.loaded:0", entry.Detail);
        Assert.Equal(MarchRefill() - LoadCost(), ReadBudget(MainLegion));
        Assert.Equal(1, CargoCount(MainLegion));

        Assert.NotEqual(stepped.StateHash, commit.StateHash);
        AssertDivergesByExactDebit(commit.StateHash!, stepped.World, MainLegion, LoadCost());
    }

    [Fact]
    public void Unload_pipe_pays_50_with_exact_detail_and_diverges_by_exactly_the_debit()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        ProbeFixed(10);
        FileOrder(Cmd("cp-unload-1", WorldCommandKinds.UnloadCargo) with { Seq = 0 });
        FileStandFastForOthers();

        var stepped = StepWorld();
        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var entry = Assert.Single(Report().Entries, e => e.Subject == "cp-unload-1" && e.Kind == TurnReportKinds.Event);
        Assert.Equal(TurnReportKinds.Event, entry.Kind);
        Assert.Equal("cargo.unloaded:0", entry.Detail);
        Assert.Equal(MarchRefill() - UnloadCost(), ReadBudget(MainLegion));
        Assert.Equal(0, CargoCount(MainLegion));

        Assert.NotEqual(stepped.StateHash, commit.StateHash);
        AssertDivergesByExactDebit(commit.StateHash!, stepped.World, MainLegion, UnloadCost());
    }

    // ---- Golden delta: transfer-only logs are identical on 12 -----------------------------------------

    [Fact]
    public void Transfer_only_log_replays_identical_on_12()
    {
        // Transfer enters the seam charged-0: the budget row is untouched and the re-hash is a
        // fixed point, so a transfer-only log hashes exactly like the Step replay (the seam-
        // entered-but-unpriced path moves nothing). Cargo itself is unhashed by lock.
        LoadInstanceAboard(SecondLegion, mass: 10);
        ProbeFixed(10);
        FileOrder(new WorldCommand
        {
            CommanderId = Commander, CommandId = "cp-xfer-1", Kind = WorldCommandKinds.TransferCargo,
            EntityId = SecondLegion, TargetEntityId = MainLegion, Seq = 0,
        });
        FileStandFastForOthers();

        var stepped = StepWorld();
        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var entry = Assert.Single(Report().Entries, e => e.Subject == "cp-xfer-1" && e.Kind == TurnReportKinds.Event);
        Assert.Equal(TurnReportKinds.Event, entry.Kind);
        Assert.Equal("cargo.transferred:0", entry.Detail);
        Assert.Equal(MarchRefill(), ReadBudget(MainLegion));
        Assert.Equal(MarchRefill(), ReadBudget(SecondLegion));
        Assert.Equal(stepped.StateHash, commit.StateHash);
        Assert.Equal(TurnEngine.RulesetVersion, _store.GetWorldTurnLog(WorldId, 0)!.RulesetVersion);
    }

    // ---- Refusal-free: gone / routed leave every budget untouched --------------------------------------

    [Fact]
    public void Gone_order_refuses_free_with_a_zero_cost_debit_row()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        var secondCargoBefore = CargoCount(SecondLegion);
        FileOrder(Cmd("cp-gone-1", WorldCommandKinds.UnloadCargo) with { Seq = 0 });
        FileStandFastForOthers();
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var gone = db.CreateCommand())
        {
            gone.CommandText = "DELETE FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;";
            gone.Parameters.AddWithValue("$w", WorldId);
            gone.Parameters.AddWithValue("$e", MainLegion);
            Assert.Equal(1, gone.ExecuteNonQuery());
        }

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        // Two drops share the subject: Step's generic stale-subject gate drops the filed order
        // first, then the cargo pass drops it again — both verbatim `entity.gone`, neither
        // spending. The count is existing Step+pass behavior, not this module's contract, so
        // this pins the reason on every drop without pinning the duplication itself.
        var drops = Report().Entries
            .Where(e => e.Subject == "cp-gone-1" && e.Kind == TurnReportKinds.CommandDropped)
            .ToList();
        // Step's Reveal gate drops the filed order first (`entity.unknown` — Core's
        // vocabulary for a missing subject, owned outside this module and asserted here only
        // as observed context), then the cargo pass drops it again verbatim `entity.gone`.
        // This module owns the second drop: it must be present, free, and budget-untouched.
        Assert.NotEmpty(drops);
        Assert.Contains(drops, e =>
            string.Equals(e.Phase, TurnEngine.Phases.Snapshot, StringComparison.Ordinal)
            && e.Detail == "entity.gone");
        // Free retry by construction: the refusal logged a zero-cost row, and no surviving
        // legion's refilled budget moved for it.
        var debit = ReadDebit(0, "cp-gone-1");
        Assert.NotNull(debit);
        Assert.False(debit!.Value.Ok);
        Assert.Equal(0, debit!.Value.Cost);
        Assert.Equal(MarchRefill(), ReadBudget(SecondLegion));
        Assert.Equal(secondCargoBefore, CargoCount(SecondLegion));
    }

    [Fact]
    public void Routed_order_refuses_free_with_the_budget_unchanged()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        var command = Cmd("cp-routed-1", WorldCommandKinds.UnloadCargo) with { Seq = 0 };
        var budgetBefore = ReadBudget(MainLegion);
        var cargoBefore = CargoCount(MainLegion);

        (bool Ok, string Detail, string? SectorId) outcome;
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
        {
            using (var rout = db.CreateCommand())
            {
                rout.Transaction = tx;
                rout.CommandText = "UPDATE rpg_world_entities SET routed = 1 WHERE world_id = $w AND entity_id = $e;";
                rout.Parameters.AddWithValue("$w", WorldId);
                rout.Parameters.AddWithValue("$e", MainLegion);
                Assert.Equal(1, rout.ExecuteNonQuery());
            }
            outcome = _store.ResolveCargoCommandUnlocked(
                db, tx, WorldId, command, PlayerId, DateTime.UtcNow.ToString("o"));
            tx.Rollback();
        }

        Assert.False(outcome.Ok);
        Assert.Equal("entity.routed", outcome.Detail);
        Assert.Equal(budgetBefore, ReadBudget(MainLegion));
        Assert.Equal(cargoBefore, CargoCount(MainLegion));
    }

    // ---- Short budget on a non-claim verb refuses spent with zero writes --------------------------------

    [Fact]
    public void Exhausted_budget_deposit_refuses_spent_with_zero_writes_and_zero_budget_change()
    {
        // Four 250-cost claims exhaust the 1000 refill in (commander, command) order; the deposit
        // filed after them refuses `entity.spent`. Its cargo row stays aboard, the vault stays
        // empty, and the budget row stays exactly zero.
        var caches = new string[4];
        for (var i = 0; i < 4; i++)
            caches[i] = SeedCacheWithRows("world_sector", HomeSector,
                new[] { ("stack" as string, (string?)null, "cont-cp-spent-" + i, (long?)1) });
        Probe(key => key.ContainerId != null && key.ContainerId.StartsWith("cont-cp-spent-", StringComparison.Ordinal) ? 10 : null);
        // No pre-loaded row: the four claims fill the legion's four slots exactly (2 members x
        // 2 slots), so every claim fits and pays, and the deposit's Seq 0 names a real claimed
        // row it must leave untouched.
        var cargoBefore = CargoCount(MainLegion);
        for (var i = 0; i < 4; i++)
            FileOrder(Cmd("cp-spent-claim-" + i, WorldCommandKinds.ClaimCache) with { CacheId = caches[i] });
        FileOrder(Cmd("cp-spent-deposit", WorldCommandKinds.DepositCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });
        FileStandFastForOthers();

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        var entry = Assert.Single(Report().Entries, e => e.Subject == "cp-spent-deposit" && e.Kind == TurnReportKinds.CommandDropped);
        Assert.Equal(TurnReportKinds.CommandDropped, entry.Kind);
        Assert.Equal("entity.spent", entry.Detail);
        Assert.Equal(0, ReadBudget(MainLegion));
        Assert.Equal(cargoBefore + 4, CargoCount(MainLegion));
        Assert.Equal(0, StorageCount(HomeSector));
        var debit = ReadDebit(0, "cp-spent-deposit");
        Assert.NotNull(debit);
        Assert.False(debit!.Value.Ok);
        Assert.Equal(DepositCost(), debit!.Value.Cost);
        Assert.Equal("entity.spent", debit!.Value.Detail);
    }

    // ---- Replay debits once: claim byte-identical with no second subtract + double-POST ------------------

    [Fact]
    public void Re_resolving_a_recorded_claim_returns_the_identical_detail_with_no_second_subtract()
    {
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)"inst-cp-replay-0", (string?)null, (long?)null),
        });
        Probe(key => key.InstanceId == "inst-cp-replay-0" ? 10 : null);
        var command = Cmd("cp-replay-1", WorldCommandKinds.ClaimCache) with { CacheId = cacheId };
        FileOrder(command);
        FileStandFastForOthers();

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);
        var firstDetail = Assert.Single(Report().Entries, e => e.Subject == "cp-replay-1" && e.Kind == TurnReportKinds.Event).Detail;
        Assert.Equal("cache.claimed:1+0", firstDetail);
        var budgetAfterCommit = ReadBudget(MainLegion);
        var cargoAfterCommit = CargoCount(MainLegion);

        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var tx = db.BeginTransaction();
        var again = _store.ResolveCargoCommandUnlocked(db, tx, WorldId, 0, command, PlayerId, DateTime.UtcNow.ToString("o"));
        tx.Rollback();

        Assert.True(again.Ok);
        Assert.Equal(firstDetail, again.Detail);
        Assert.Equal(budgetAfterCommit, ReadBudget(MainLegion));
        Assert.Equal(cargoAfterCommit, CargoCount(MainLegion));
        Assert.Equal(0, CacheItemCount(cacheId));
    }

    [Fact]
    public void Double_post_of_the_same_command_id_replays_with_one_stored_row()
    {
        // Submit idempotency on (commander, command): the second filing returns Replayed with
        // zero new rows, so a double-tapped pick-up button files once and can only ever pay once.
        var cacheId = SeedCacheWithRows("world_sector", HomeSector, new[]
        {
            ("instance", (string?)"inst-cp-dbl-0", (string?)null, (long?)null),
        });
        _ = cacheId;
        ProbeFixed(10);
        var command = Cmd("cp-dbl-1", WorldCommandKinds.ClaimCache) with { CacheId = cacheId };

        var first = _store.SubmitWorldCommand(WorldId, command);
        Assert.True(first.Ok, first.Reason);
        Assert.False(first.Replayed);
        var second = _store.SubmitWorldCommand(WorldId, command);
        Assert.True(second.Ok);
        Assert.True(second.Replayed);

        Assert.Equal(1, _store.ListWorldCommands(WorldId, 0).Count(c =>
            string.Equals(c.CommandId, "cp-dbl-1", StringComparison.Ordinal)));
    }
}
