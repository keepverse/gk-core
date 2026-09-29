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

// world-action-economy `budget-debit` (docs/architecture/world-action-economy/spec-budget-debit.md):
// the filled `DebitActCostUnlocked` seam — lookup via `WorldTuningHub`, subtract checked,
// debit-once per `CommandId`, re-hash after the pass. Seven Testing bullets plus the re-hash
// proof, the old-log hash pair (zero-priced identical on 12, priced diverges), and trim
// convergence. Costs read through the Hub (never literals); the provisional numbers
// (claim 250, deposit 100, withdraw 100, load 50, unload 50, transfer 0) appear only in
// comments. In-memory only (DataTestStore.Create); the catalog is the shipped corpus plus one
// in-memory depot row (`StructureCorpusOverlay`), so nothing is written to disk.
//
// Shares the `StructureCatalogSwap` collection with the sibling cargo suites: the fixture
// wholesale-replaces the static catalog, so it runs sequentially, never raced.
[Collection("StructureCatalogSwap")]
public sealed class BudgetDebitTests : IDisposable
{
    const string WorldId = "budget-debit";
    const string Commander = "dave";
    const string PlayerIdStr = "1";
    const long PlayerId = 1;
    const string HomeSector = "homeworld";
    const string MainLegion = "e-budget-legion-1";
    const string SecondLegion = "e-budget-legion-2";
    const string AwayLegion = "e-budget-away-1";
    const string RivalLegion = "e-budget-rival-1";
    const string OtherSector = "ember-hollow";
    const string FarSector = "frost-mire";
    const string TestDepotId = "test-budget-depot";
    const int TestDepotBonus = 10_000;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    public BudgetDebitTests()
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
        var tag = "bd" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId($"atom.{tag}", "", 1),
            KindId = "stat.modify", FamilyId = $"atom.{tag}", Variant = "", Tier = 1,
            Name = "Budget Debit Test", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
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

    string SeedCacheWithRows(
        string placeKind, string placeRef,
        (string Kind, string? InstanceId, string? ContainerId, long? Qty)[] rows)
    {
        var cacheId = "bd_" + Guid.NewGuid().ToString("N");
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

    int ReadBudget(string entityId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT movement_remaining FROM rpg_world_entities WHERE world_id = $w AND entity_id = $e;";
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? -9999);
    }

    void WriteBudget(string entityId, int remaining)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE rpg_world_entities SET movement_remaining = $m WHERE world_id = $w AND entity_id = $e;";
        cmd.Parameters.AddWithValue("$m", remaining);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
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

    void FileStandFastForOthers(string suffix = "")
    {
        // Filed explicitly so the AI fill skips wild/zomboss and the recorded command set is
        // exactly what a Step replay re-runs (the Old_log pattern in the sibling suite).
        foreach (var faction in new[] { "wild", "zomboss" })
        {
            var (ok, reason, _) = _store.SubmitWorldCommand(WorldId, new WorldCommand
            {
                CommanderId = faction, CommandId = "bd-stand-" + faction + suffix, Kind = WorldCommandKinds.StandFast,
            });
            Assert.True(ok, reason);
        }
    }

    // ---- Testing bullet 1: debit-after-refill ------------------------------------------------

    [Fact]
    public void Deposit_pays_from_the_refill_not_the_exhausted_leftover()
    {
        // The legion's pre-turn leftover is forced to zero: had the debit read the leftover,
        // a 100-cost deposit would refuse `entity.spent`. Post-Step the refill lands first
        // (march refills to the full frame), so the same act pays from the refill.
        LoadInstanceAboard(MainLegion, mass: 10);
        WriteBudget(MainLegion, 0);
        ProbeFixed(10);
        FileOrder(Cmd("bd-refill-1", WorldCommandKinds.DepositCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });
        FileStandFastForOthers();

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("cargo.deposited:", StringComparison.Ordinal));

        var refill = MovementPolicy.BudgetFor("march");
        Assert.Equal(refill - DepositCost(), ReadBudget(MainLegion));
        var debit = ReadDebit(0, "bd-refill-1");
        Assert.NotNull(debit);
        Assert.True(debit!.Value.Ok);
        Assert.Equal(DepositCost(), debit!.Value.Cost);
    }

    // ---- Testing bullet 2: flat per-act ------------------------------------------------------

    [Fact]
    public void Two_identical_claims_cost_twice_regardless_of_contents_and_transfer_costs_nothing()
    {
        var cacheA = SeedCacheWithRows("world_sector", HomeSector, new[] { ("stack" as string, (string?)null, "cont-flat-a", (long?)1) });
        var cacheB = SeedCacheWithRows("world_sector", HomeSector, new[] { ("stack" as string, (string?)null, "cont-flat-b", (long?)1) });
        Probe(key => key.ContainerId is "cont-flat-a" or "cont-flat-b" ? 10 : null);
        var before = ReadBudget(MainLegion);

        FileOrder(Cmd("bd-flat-1", WorldCommandKinds.ClaimCache) with { CacheId = cacheA });
        FileOrder(Cmd("bd-flat-2", WorldCommandKinds.ClaimCache) with { CacheId = cacheB });
        var donor = LoadInstanceAboard(SecondLegion, mass: 10);
        _ = donor;
        FileOrder(new WorldCommand
        {
            CommanderId = Commander, CommandId = "bd-flat-xfer", Kind = WorldCommandKinds.TransferCargo,
            EntityId = SecondLegion, TargetEntityId = MainLegion, Seq = 0,
        });
        FileStandFastForOthers();

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        // Two claims pay twice even though each moved a single unit; the transfer moves a row
        // for free. Member/level/faction never enter the lookup — the table owns the numbers.
        Assert.Equal(before - ClaimCost() - ClaimCost(), ReadBudget(MainLegion));
        Assert.Equal(1000, ReadBudget(SecondLegion));
        Assert.Contains(Report().Entries, e => e.Detail.StartsWith("cache.claimed:", StringComparison.Ordinal));
        Assert.Contains(Report().Entries, e => e.Detail.StartsWith("cargo.transferred:", StringComparison.Ordinal));
    }

    // ---- Testing bullet 3: short budget refuses ----------------------------------------------

    [Fact]
    public void Short_budget_refuses_spent_with_zero_writes_and_zero_budget_change()
    {
        // March refills to the full frame (1000); five 250-cost claims need 1250, so the fifth
        // exhausts the budget and refuses `entity.spent`. The refill overwrites any pre-commit
        // leftover, so exhaustion must come from same-turn spends, never a manual pre-set.
        var caches = new string[5];
        for (var i = 0; i < 5; i++)
            caches[i] = SeedCacheWithRows("world_sector", HomeSector,
                new[] { ("stack" as string, (string?)null, "cont-spent-" + i, (long?)1) });
        Probe(key => key.ContainerId != null && key.ContainerId.StartsWith("cont-spent-", StringComparison.Ordinal) ? 10 : null);
        var cargoBefore = CargoCount(MainLegion);
        for (var i = 0; i < 5; i++)
            FileOrder(Cmd("bd-spent-" + i, WorldCommandKinds.ClaimCache) with { CacheId = caches[i] });
        FileStandFastForOthers();

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        // Four pay (1000 -> 0), the fifth refuses with nothing written and nothing further spent.
        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == "entity.spent");
        Assert.Equal(0, ReadBudget(MainLegion));
        Assert.Equal(cargoBefore + 4, CargoCount(MainLegion));
        Assert.Equal(0, CacheItemCount(caches[0]));
        Assert.Equal(1, CacheItemCount(caches[4]));
    }

    // ---- Testing bullet 4: attempt-pays / refusal-free ----------------------------------------

    [Fact]
    public void Unreachable_claim_is_free_but_an_all_skipped_claim_pays_full()
    {
        // Forged cache: reachability fails before the seam, so nothing is owed.
        ProbeFixed(10);
        var budgetBefore = ReadBudget(MainLegion);
        FileOrder(Cmd("bd-free-1", WorldCommandKinds.ClaimCache) with { CacheId = "cache-does-not-exist" });

        // All-skipped: fill the legion solid (2 members -> 4 slots), so the one row fits
        // nowhere. The attempt still reaches the loop, so it pays the full claim price by lock.
        LoadInstanceAboard(MainLegion, mass: 10);
        LoadInstanceAboard(MainLegion, mass: 10);
        LoadInstanceAboard(MainLegion, mass: 10);
        LoadInstanceAboard(MainLegion, mass: 10);
        Assert.Equal(4, CargoCount(MainLegion));
        var skipCache = SeedCacheWithRows("world_sector", HomeSector, new[] { ("stack" as string, (string?)null, "cont-skip", (long?)1) });
        Probe(key => key.ContainerId == "cont-skip" ? 10_000 : 10);
        FileOrder(Cmd("bd-pays-1", WorldCommandKinds.ClaimCache) with { CacheId = skipCache });
        FileStandFastForOthers();

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);

        Assert.Contains(Report().Entries, e =>
            e.Kind == TurnReportKinds.CommandDropped && e.Detail == "cache.unreachable");
        var skipped = Assert.Single(Report().Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("cache.claimed:0+", StringComparison.Ordinal));
        _ = skipped;
        // One free refusal plus one full-price attempt: only the attempt debited.
        Assert.Equal(budgetBefore - ClaimCost(), ReadBudget(MainLegion));
        Assert.Equal(1, CacheItemCount(skipCache));
    }

    // ---- Testing bullet 5a: replay debits once -------------------------------------------------

    [Fact]
    public void Re_resolving_a_recorded_deposit_returns_the_detail_with_no_second_subtract()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        ProbeFixed(10);
        var cmd = Cmd("bd-replay-1", WorldCommandKinds.DepositCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        };
        FileOrder(cmd);
        FileStandFastForOthers();
        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);
        var firstDetail = Assert.Single(Report().Entries, e => e.Subject == "bd-replay-1" && e.Kind == TurnReportKinds.Event).Detail;
        var budgetAfterCommit = ReadBudget(MainLegion);
        var cargoAfterCommit = CargoCount(MainLegion);

        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var tx = db.BeginTransaction();
        var again = _store.ResolveCargoCommandUnlocked(db, tx, WorldId, 0, cmd, PlayerId, DateTime.UtcNow.ToString("o"));
        tx.Rollback();

        Assert.True(again.Ok);
        Assert.Equal(firstDetail, again.Detail);
        Assert.Equal(budgetAfterCommit, ReadBudget(MainLegion));
        Assert.Equal(cargoAfterCommit, CargoCount(MainLegion));
    }

    // ---- Testing bullet 5b: trim convergence ----------------------------------------------------

    [Fact]
    public void Post_trim_re_derivation_from_the_untrimmed_log_converges_to_the_post_debit_hash()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        ProbeFixed(10);
        FileOrder(Cmd("bd-trim-1", WorldCommandKinds.DepositCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        });
        FileStandFastForOthers();
        var before = _store.LoadWorldState(WorldId)!;
        var loggedBefore = _store.ListWorldCommands(WorldId, 0);
        var seed = _store.GetWorldHeader(WorldId)!.Seed;
        var stepped = TurnEngine.Step(before, loggedBefore, seed, DistrictAssaultResolver.Instance);

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);
        var stored = _store.GetWorldTurnLog(WorldId, 0)!;
        var storedHash = stored.StateHash;
        var budgetAfter = ReadBudget(MainLegion);

        // Commands and debit rows are never trimmed; only report bodies go.
        var commandsBefore = _store.ListWorldCommands(WorldId, 0).Count;
        Assert.True(commandsBefore > 0);
        _store.TrimWorldTurnReports(WorldId, keepLast: 0);
        Assert.NotNull(_store.GetWorldTurnLog(WorldId, 0));
        Assert.Equal(commandsBefore, _store.ListWorldCommands(WorldId, 0).Count);
        Assert.NotNull(ReadDebit(0, "bd-trim-1"));

        // Step gives the pre-debit world, the stored debit row gives the exact spend, and
        // re-hashing converges to storage — hash recomputed post-debit both times.
        Assert.NotEqual(stepped.StateHash, storedHash);
        var debit = ReadDebit(0, "bd-trim-1")!;
        var replayWorld = stepped.World with
        {
            Entities = stepped.World.Entities.Select(e =>
                string.Equals(e.EntityId, MainLegion, StringComparison.Ordinal)
                    ? e with { MovementRemaining = checked(e.MovementRemaining - debit.Value.Cost) }
                    : e).ToList(),
        };
        Assert.Equal(storedHash, StateHasher.Hash(replayWorld));
        Assert.Equal(MovementPolicy.BudgetFor("march") - DepositCost(), budgetAfter);
    }

    // ---- Testing bullet 6: old-log hash pair ------------------------------------------------------

    [Fact]
    public void Zero_priced_log_replays_identical_on_12()
    {
        // Zero priced kinds: no debit fires, the post-pass re-hash is a fixed point.
        foreach (var faction in new[] { Commander, "wild", "zomboss" })
            FileOrder(new WorldCommand
            {
                CommanderId = faction, CommandId = "bd-old-0-" + faction, Kind = WorldCommandKinds.StandFast,
            });
        var before = _store.LoadWorldState(WorldId)!;
        var logged = _store.ListWorldCommands(WorldId, 0);
        var seed = _store.GetWorldHeader(WorldId)!.Seed;
        var expected = TurnEngine.Step(before, logged, seed, DistrictAssaultResolver.Instance).StateHash;

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);
        Assert.Equal(expected, commit.StateHash);
        Assert.Equal(TurnEngine.RulesetVersion, _store.GetWorldTurnLog(WorldId, 0)!.RulesetVersion);
    }

    [Fact]
    public void Priced_log_diverges_by_exactly_the_debit_plus_rehash()
    {
        LoadInstanceAboard(MainLegion, mass: 10);
        ProbeFixed(10);
        var order = Cmd("bd-old-1", WorldCommandKinds.DepositCargo) with
        {
            SectorId = HomeSector, Seq = 0,
        };
        FileOrder(order);
        FileStandFastForOthers();

        var before = _store.LoadWorldState(WorldId)!;
        var logged = _store.ListWorldCommands(WorldId, 0);
        var seed = _store.GetWorldHeader(WorldId)!.Seed;
        var stepped = TurnEngine.Step(before, logged, seed, DistrictAssaultResolver.Instance);

        var commit = CommitAll();
        Assert.True(commit.Advanced, commit.Reason);
        Assert.NotEqual(stepped.StateHash, commit.StateHash);

        var replayWorld = stepped.World with
        {
            Entities = stepped.World.Entities.Select(e =>
                string.Equals(e.EntityId, MainLegion, StringComparison.Ordinal)
                    ? e with { MovementRemaining = checked(e.MovementRemaining - DepositCost()) }
                    : e).ToList(),
        };
        Assert.Equal(commit.StateHash, StateHasher.Hash(replayWorld));
    }

    // ---- Testing bullet 7: hold allowance integration (was hold-zero interim) -----------------------

    [Fact]
    public void A_holder_pays_one_claim_from_the_allowance_then_refuses_spent()
    {
        // Hold-allowance already landed (holders refill to the tuned allowance, not zero), so
        // the interim "zero refuses" becomes integration: one 250-cost act fits the allowance,
        // the second same-turn attempt is spent — no hole, no crash.
        FileOrder(Cmd("bd-hold-stance", WorldCommandKinds.Stance) with { Stance = "hold" });
        FileStandFastForOthers();
        Assert.True(CommitAll().Advanced);
        Assert.Equal(WorldTuningHub.Tuning.Movement.HoldAllowanceMilli, ReadBudget(MainLegion));

        var cacheA = SeedCacheWithRows("world_sector", HomeSector, new[] { ("stack" as string, (string?)null, "cont-hold-a", (long?)1) });
        var cacheB = SeedCacheWithRows("world_sector", HomeSector, new[] { ("stack" as string, (string?)null, "cont-hold-b", (long?)1) });
        Probe(key => key.ContainerId is "cont-hold-a" or "cont-hold-b" ? 10 : null);
        var turn = _store.GetWorldHeader(WorldId)!.CurrentTurn;
        FileOrder(Cmd("bd-hold-1", WorldCommandKinds.ClaimCache) with { CacheId = cacheA });
        FileOrder(Cmd("bd-hold-2", WorldCommandKinds.ClaimCache) with { CacheId = cacheB });
        FileStandFastForOthers("-t" + turn);
        var commit = CommitAll(turn);
        Assert.True(commit.Advanced, commit.Reason);

        var entries = Report(turn).Entries.ToList();
        Assert.Contains(entries, e => e.Kind == TurnReportKinds.Event && e.Detail.StartsWith("cache.claimed:", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "entity.spent");
        Assert.Equal(0, ReadBudget(MainLegion));
    }
}
