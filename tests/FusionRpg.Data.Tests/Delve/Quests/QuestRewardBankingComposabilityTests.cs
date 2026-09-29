using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Delve.Quests;

/// <summary>
/// `delve-quests` D4.12/D4.14 (party-dungeon-todo.md, 2026-09-07): proves the real, previously-
/// missing property — `SaveInstanceUnlocked`/`AcquireItemUnlocked`/`PersistLootUnlocked` (built this
/// same session) really do compose into ONE externally-owned transaction, the exact shape
/// `RpgStore.Delve.CloseDelve` will need once D4.16's own domain SQL-import arm lands (this test's
/// own honest gap: it proves the tx-scoped primitives compose, not that `CloseDelve` calls them yet
/// — it does not, blocked on D4.16, named in the todo file). Mirrors
/// `DomainProgressStoreTests.The_unlocked_writers_compose_into_one_externally_owned_transaction`'s
/// own established pattern exactly: open a raw connection, call every `Unlocked` writer on it, commit
/// once, prove every write landed.
/// </summary>
public class QuestRewardBankingComposabilityTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public QuestRewardBankingComposabilityTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.vitality", "", 1),
            KindId = "stat.modify", FamilyId = "atom.vitality", Variant = "", Tier = 1,
            Name = "atom.vitality", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "trait.quest-reward-test",
            Kind = ContainerKind.Trait,
            Atoms = new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
        }).IsOk);
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    InstanceRow BuildInstance(long seed)
    {
        var container = _store.GetContainer("trait.quest-reward-test")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, seed, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return inst!;
    }

    static LootManifest Manifest(string correlationId) =>
        new(correlationId, "drop.dungeon.fire.cache", 0x51EED, 20, Array.Empty<LootGrant>(),
            Array.Empty<string>(), "{}", new LootPityState(0, 0), new LootPityState(0, 0), null, false, null);

    [Fact]
    public void The_three_new_Unlocked_writers_compose_into_one_externally_owned_transaction()
    {
        var inst = BuildInstance(seed: 99);
        var manifest = Manifest("quest:composability-test-1");
        string instanceId;

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
        {
            instanceId = _store.SaveInstanceUnlocked(db, tx, inst);
            var item = new RpgItemRow { InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = "2026-09-07T00:00:00Z", OriginKind = "quest-reward" };
            var acquireRejection = _store.AcquireItemUnlocked(db, tx, item);
            Assert.True(acquireRejection.IsOk, acquireRejection.ToString());
            _store.PersistLootUnlocked(db, tx, "p1", manifest, "dungeon-quest", "delve-1:quest:q1", 0, 0, Array.Empty<ItemGenerationRow>());
            tx.Commit();
        }

        // Every write landed, through the store's own PUBLIC read surface -- proof the composed
        // transaction really persisted, not just that no exception was thrown.
        Assert.NotNull(_store.GetInstance(instanceId));
        Assert.NotNull(_store.GetItem(instanceId));
        Assert.Contains(_store.ListDropLog("p1", 10), r => r.CorrelationId == "quest:composability-test-1");
    }

    [Fact]
    public void None_of_the_three_new_Unlocked_writers_secretly_commits_its_own_transaction()
    {
        var inst = BuildInstance(seed: 100);
        var manifest = Manifest("quest:composability-test-2");
        string instanceId;

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
        {
            instanceId = _store.SaveInstanceUnlocked(db, tx, inst);
            var item = new RpgItemRow { InstanceId = instanceId, PlayerId = "p2", AcquiredUtc = "2026-09-07T00:00:00Z" };
            _store.AcquireItemUnlocked(db, tx, item);
            _store.PersistLootUnlocked(db, tx, "p2", manifest, "dungeon-quest", "delve-2:quest:q1", 0, 0, Array.Empty<ItemGenerationRow>());
            // deliberately NOT committing -- a rollback (the `using` disposing tx without Commit)
            // must leave nothing behind if none of the three writers secretly committed on its own.
        }

        Assert.Null(_store.GetInstance(instanceId));
        Assert.Null(_store.GetItem(instanceId));
        Assert.Empty(_store.ListDropLog("p2", 10));
    }
}
