using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Expeditions;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// D2: rpg_expeditions + soft-lock membership rows (Cold-plane, no FSM change) + the material
/// inventory. The lock is consulted in BOTH directions: expedition dispatch refuses deployed
/// specimens; PvZ deploy refuses expedition members.
/// </summary>
public class ExpeditionStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ExpeditionStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // DeployMode != HypnoAlly: this file's subject is expedition/PvZ-deploy soft-locking, unrelated to
    // creature-lawn-deploy T1.4's DeployMode — excluding HypnoAlly keeps `TryBeginUniqueDeploy` calls here
    // from incidentally tripping T1.4's own deploy.hypno-ally-not-implemented refusal.
    static readonly FusionRpg.Core.Creatures.CreatureSpeciesDef CatalogSpecies =
        FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All.First(s =>
            s.Side == "zombie" && s.DeployMode != FusionRpg.Core.Creatures.CreatureDeployMode.HypnoAlly);

    CreatureMintSpec Spec(string origin = "summon") => new()
    {
        SpeciesId = CatalogSpecies.SpeciesId,
        Side = "zombie",
        GameTypeId = CatalogSpecies.GameTypeId,
        Rarity = "chaff",
        Variant = "normal",
        ElementPrimary = "fire",
        TraitIds = new List<string> { "swift" },
        Origin = origin
    };

    string Mint()
    {
        var (specimen, _) = _store.MintCreature(1, Spec());
        return specimen.Actor.InstanceId;
    }

    [Fact]
    public void Dispatch_creates_row_locks_members_and_computes_due()
    {
        var a = Mint();
        var b = Mint();
        var now = DateTimeOffset.Parse("2026-08-21T10:00:00Z");
        var (ok, reason, row) = _store.DispatchExpedition(
            1, "exp-1", "scout-30m", new[] { a, b }, seed: 42, utcNow: now);

        Assert.True(ok, reason);
        Assert.Equal("Dispatched", row!.State);
        Assert.Equal("scout-30m", row.TierId);
        Assert.Equal(now.AddMinutes(30).UtcDateTime.ToString("o"), row.DueUtc);
        Assert.True(_store.HasActiveExpeditionMembership(a));
        Assert.True(_store.HasActiveExpeditionMembership(b));
        Assert.Single(_store.ListExpeditions(1));
    }

    [Fact]
    public void Dispatch_validates_tier_slots_and_ownership()
    {
        var a = Mint();
        Assert.False(_store.DispatchExpedition(1, "x1", "no-such-tier", new[] { a }, 1).Ok);
        // scout-30m has 2 slots — 3 specimens refuse.
        var squad = new[] { Mint(), Mint(), Mint() };
        Assert.Equal("squad.toolarge", _store.DispatchExpedition(1, "x2", "scout-30m", squad, 1).Reason);
        Assert.Equal("squad.empty", _store.DispatchExpedition(1, "x3", "scout-30m", Array.Empty<string>(), 1).Reason);
        Assert.Equal("squad.unknown-specimen",
            _store.DispatchExpedition(1, "x4", "scout-30m", new[] { "ghost-id" }, 1).Reason);
    }

    [Fact]
    public void Soft_lock_refuses_cross_mode_both_ways()
    {
        var onExpedition = Mint();
        var deployed = Mint();

        // Direction 1: a specimen mid-PvZ-deploy cannot be dispatched.
        var (dOk, _, _, _) = _store.TryBeginUniqueDeploy(deployed, "pvz-corr-1");
        Assert.True(dOk);
        Assert.Equal("specimen.deployed",
            _store.DispatchExpedition(1, "e1", "scout-30m", new[] { deployed }, 1).Reason);

        // Direction 2: an expedition member cannot be PvZ-deployed.
        Assert.True(_store.DispatchExpedition(1, "e2", "scout-30m", new[] { onExpedition }, 1).Ok);
        var (deployOk, deployReason, _, _) = _store.TryBeginUniqueDeploy(onExpedition, "pvz-corr-2");
        Assert.False(deployOk);
        Assert.Equal("expedition.locked", deployReason);
    }

    [Fact]
    public void Double_dispatch_of_the_same_specimen_refuses()
    {
        var a = Mint();
        Assert.True(_store.DispatchExpedition(1, "e1", "scout-30m", new[] { a }, 1).Ok);
        Assert.Equal("specimen.on-expedition",
            _store.DispatchExpedition(1, "e2", "scout-30m", new[] { a }, 2).Reason);
    }

    [Fact]
    public void Closing_releases_the_lock()
    {
        var a = Mint();
        var (_, _, row) = _store.DispatchExpedition(1, "e1", "scout-30m", new[] { a }, 1);
        Assert.True(_store.TryCloseExpedition(row!.Id, "Collected"));
        Assert.False(_store.HasActiveExpeditionMembership(a));
        var (deployOk, _, _, _) = _store.TryBeginUniqueDeploy(a, "pvz-after");
        Assert.True(deployOk);
        Assert.Equal("Collected", _store.ListExpeditions(1).Single().State);
    }

    [Fact]
    public void Correlation_replays_return_the_stored_expedition()
    {
        var a = Mint();
        var first = _store.DispatchExpedition(1, "exp-dup", "scout-30m", new[] { a }, 7);
        var replay = _store.DispatchExpedition(1, "exp-dup", "scout-30m", new[] { a }, 999);
        Assert.True(replay.Ok);
        Assert.Equal("replay", replay.Reason);
        Assert.Equal(first.Expedition!.Id, replay.Expedition!.Id);
        Assert.Equal(7UL, replay.Expedition.Seed);
        Assert.Single(_store.ListExpeditions(1));
    }

    [Fact]
    public void A_dispatch_stamped_in_the_past_is_due_without_rewriting_the_row()
    {
        // RS3 increment 5a (owner ruling on RS-F16, candidate 1): this replaces
        // `Force_due_rewinds_the_timer`, which exercised the SIM-only `due_utc` UPDATE. The store already
        // takes the clock as an INPUT (`DispatchExpedition(..., utcNow)`), so an expedition dispatched
        // with a past stamp is due with no UPDATE against the row at all — and that is what lets the
        // bypass be retired in 5b without losing this coverage.
        var a = Mint();
        var past = DateTimeOffset.UtcNow.AddHours(-9); // hunt-8h is 480 minutes
        var (ok, reason, row) = _store.DispatchExpedition(1, "e1", "hunt-8h", new[] { a }, 1, past);
        Assert.True(ok, reason);

        var reread = _store.ListExpeditions(1).Single();
        Assert.Equal(past.UtcDateTime.ToString("o"), reread.DispatchedUtc);
        Assert.Equal(past.AddMinutes(480).UtcDateTime.ToString("o"), reread.DueUtc);
        // The row is due BECAUSE the input clock said so — nothing rewrote it afterwards.
        Assert.True(DateTimeOffset.Parse(reread.DueUtc) < DateTimeOffset.UtcNow,
            "a past-stamped dispatch must already be due, or this proves nothing about the seam");
    }

    [Fact]
    public async Task Independent_handles_cannot_both_activate_the_same_specimen()
    {
        var instanceId = Mint();
        using var left = _testStore.Reopen();
        using var right = _testStore.Reopen();
        using var beforeExclusiveWrite = new Barrier(2);

        var leftDispatch = Task.Run(() => left.DispatchExpedition(
            1, "multi-left", "scout-30m", new[] { instanceId }, seed: 1,
            utcNow: null, beforeExclusiveWrite: () => beforeExclusiveWrite.SignalAndWait()));
        var rightDispatch = Task.Run(() => right.DispatchExpedition(
            1, "multi-right", "scout-30m", new[] { instanceId }, seed: 2,
            utcNow: null, beforeExclusiveWrite: () => beforeExclusiveWrite.SignalAndWait()));

        var results = await Task.WhenAll(leftDispatch, rightDispatch);

        Assert.Single(results, result => result.Ok);
        Assert.Contains(results, result => !result.Ok && result.Reason == "specimen.on-expedition");
        Assert.Single(_store.ListExpeditions(1));
        Assert.True(_store.HasActiveExpeditionMembership(instanceId));
    }

    [Fact]
    public void Committed_manifest_survives_a_new_handle_and_replays_without_double_pay()
    {
        var instanceId = Mint();
        var (_, _, row) = _store.DispatchExpedition(
            1, "durable-result", "scout-30m", new[] { instanceId }, seed: 7);
        var expeditionId = row!.Id;
        var rewards = new RpgStore.ExpeditionRewardApply(
            EventSouls: 120,
            Materials: new[] { ("essence.fire", 2L) },
            SpecimenXp: new[] { (instanceId, 30L) },
            WildMints: new[] { Spec("expedition") });
        var result = new ExpeditionCollectResult(
            ExpeditionStates.Collected,
            ElapsedTicks: 0,
            Ticks: new[]
            {
                new ExpeditionTickOutcome(0, ExpeditionTickKinds.FoundSouls, -1, 5,
                    null, false, null, null)
            },
            Battles: Array.Empty<ExpeditionBattleResult>(),
            SoulsAwarded: 120,
            Materials: new[] { new MaterialDrop("essence.fire", 2) },
            WildJoins: Array.Empty<CreatureSpecimenDto>(),
            SpecimenXp: new[] { new ExpeditionSpecimenXp(instanceId, 30) });

        var committed = _store.CommitExpeditionRewardsAndResult(
            expeditionId, 1, ExpeditionStates.Collected, rewards, result);
        Assert.True(committed.Applied);

        using var reopened = _testStore.Reopen();
        var persisted = reopened.TryGetExpeditionCollectResult(expeditionId);
        Assert.NotNull(persisted);
        Assert.Single(persisted!.WildJoins);
        Assert.Equal(120, reopened.GetSoulBalance(1).Balance);
        Assert.Equal(2, reopened.ListCreatureMaterials(1).Single(m => m.MaterialId == "essence.fire").Qty);
        Assert.Equal(2, reopened.ListCreatureRoster(1).Items.Count);

        var replay = reopened.CommitExpeditionRewardsAndResult(
            expeditionId, 1, ExpeditionStates.Collected, rewards, result);
        Assert.False(replay.Applied);
        Assert.Equal("expedition.replay", replay.Reason);
        Assert.Equal(
            JsonSerializer.Serialize(persisted),
            JsonSerializer.Serialize(replay.Result));
        Assert.Equal(120, reopened.GetSoulBalance(1).Balance);
        Assert.Equal(2, reopened.ListCreatureMaterials(1).Single(m => m.MaterialId == "essence.fire").Qty);
        Assert.Equal(2, reopened.ListCreatureRoster(1).Items.Count);
    }

    [Fact]
    public async Task Concurrent_collect_across_handles_applies_once_and_returns_the_same_manifest()
    {
        var instanceId = Mint();
        var (_, _, row) = _store.DispatchExpedition(
            1, "concurrent-collect", "scout-30m", new[] { instanceId }, seed: 11);
        var rewards = new RpgStore.ExpeditionRewardApply(
            EventSouls: 75,
            Materials: new[] { ("essence.fire", 3L) },
            SpecimenXp: new[] { (instanceId, 15L) },
            WildMints: Array.Empty<CreatureMintSpec>());
        var result = new ExpeditionCollectResult(
            ExpeditionStates.Collected,
            ElapsedTicks: 0,
            Ticks: Array.Empty<ExpeditionTickOutcome>(),
            Battles: Array.Empty<ExpeditionBattleResult>(),
            SoulsAwarded: 75,
            Materials: new[] { new MaterialDrop("essence.fire", 3) },
            WildJoins: Array.Empty<CreatureSpecimenDto>(),
            SpecimenXp: new[] { new ExpeditionSpecimenXp(instanceId, 15) });

        using var left = _testStore.Reopen();
        using var right = _testStore.Reopen();
        using var beforeCommit = new Barrier(2);
        var leftCollect = Task.Run(() => left.CommitExpeditionRewardsAndResult(
            row!.Id, 1, ExpeditionStates.Collected, rewards, result,
            utcNow: null,
            beforeCommit: () => beforeCommit.SignalAndWait()));
        var rightCollect = Task.Run(() => right.CommitExpeditionRewardsAndResult(
            row!.Id, 1, ExpeditionStates.Collected, rewards, result,
            utcNow: null,
            beforeCommit: () => beforeCommit.SignalAndWait()));

        var outcomes = await Task.WhenAll(leftCollect, rightCollect);

        Assert.Single(outcomes, outcome => outcome.Applied);
        Assert.All(outcomes, outcome => Assert.Equal(outcome.Applied ? "" : "expedition.replay", outcome.Reason));
        Assert.All(outcomes, outcome => Assert.NotNull(outcome.Result));
        Assert.Equal(
            JsonSerializer.Serialize(outcomes[0].Result),
            JsonSerializer.Serialize(outcomes[1].Result));
        Assert.Equal(75, _store.GetSoulBalance(1).Balance);
        Assert.Equal(3, _store.ListCreatureMaterials(1).Single(m => m.MaterialId == "essence.fire").Qty);
    }

    [Fact]
    public void Init_upgrades_a_pre_manifest_schema_and_installs_the_unique_active_boundary()
    {
        using var legacy = DataTestStore.CreateWithPreInitHot(db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE rpg_expeditions (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  player_id INTEGER NOT NULL,
                  correlation_id TEXT NOT NULL,
                  state TEXT NOT NULL,
                  tier_id TEXT NOT NULL,
                  squad_json TEXT NOT NULL,
                  seed TEXT NOT NULL,
                  dispatched_utc TEXT NOT NULL,
                  due_utc TEXT NOT NULL,
                  collected_utc TEXT,
                  UNIQUE(player_id, correlation_id)
                );
                CREATE TABLE rpg_expedition_members (
                  expedition_id INTEGER NOT NULL,
                  instance_id TEXT NOT NULL,
                  active INTEGER NOT NULL DEFAULT 1,
                  PRIMARY KEY (expedition_id, instance_id)
                );
                CREATE INDEX ix_rpg_expedition_members_active
                  ON rpg_expedition_members(instance_id) WHERE active = 1;
                """;
            command.ExecuteNonQuery();
        });

        using var db = SqliteConnectionFactory.Open(legacy.Store.HotPath);
        using var columns = db.CreateCommand();
        columns.CommandText = "PRAGMA table_info('rpg_expeditions');";
        using var reader = columns.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) names.Add(reader.GetString(1));
        Assert.Contains("result_schema_version", names);
        Assert.Contains("result_json", names);

        using var index = db.CreateCommand();
        index.CommandText = "SELECT sql FROM sqlite_master WHERE name='ux_rpg_expedition_members_active';";
        Assert.Contains("CREATE UNIQUE INDEX", Assert.IsType<string>(index.ExecuteScalar()), StringComparison.Ordinal);
    }

    [Fact]
    public void Durable_result_read_refuses_an_unknown_schema_version()
    {
        var instanceId = Mint();
        var (_, _, row) = _store.DispatchExpedition(
            1, "result-version", "scout-30m", new[] { instanceId }, seed: 13);
        var result = new ExpeditionCollectResult(
            ExpeditionStates.Collected, 0,
            Array.Empty<ExpeditionTickOutcome>(), Array.Empty<ExpeditionBattleResult>(),
            0, Array.Empty<MaterialDrop>(), Array.Empty<CreatureSpecimenDto>(),
            Array.Empty<ExpeditionSpecimenXp>());
        Assert.True(_store.CommitExpeditionRewardsAndResult(
            row!.Id, 1, ExpeditionStates.Collected,
            new RpgStore.ExpeditionRewardApply(
                0, Array.Empty<(string, long)>(), Array.Empty<(string, long)>(),
                Array.Empty<CreatureMintSpec>()),
            result).Applied);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "UPDATE rpg_expeditions SET result_schema_version=$v WHERE id=$id;";
            command.Parameters.AddWithValue("$v", ExpeditionResultContract.Version + 1);
            command.Parameters.AddWithValue("$id", row.Id);
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => _store.TryGetExpeditionCollectResult(row.Id));
    }

    [Fact]
    public void Materials_accumulate_and_validate_ids()
    {
        _store.AddCreatureMaterials(1, new[] { ("essence.fire", 3L), ("shard.rare", 1L) });
        _store.AddCreatureMaterials(1, new[] { ("essence.fire", 2L) });
        var shelf = _store.ListCreatureMaterials(1);
        Assert.Equal(5, shelf.Single(m => m.MaterialId == "essence.fire").Qty);
        Assert.Equal(1, shelf.Single(m => m.MaterialId == "shard.rare").Qty);

        Assert.ThrowsAny<Exception>(() =>
            _store.AddCreatureMaterials(1, new[] { ("essence.plasma", 1L) }));
    }
}
