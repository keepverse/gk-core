using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.CorpseCache;

/// <summary>Task 0.1 (`corpse-cache`, deployment-hierarchy module 3) — the death path
/// (spec <c>docs/architecture/deployment-hierarchy/spec-corpse-cache.md</c> §Design 1-2):
/// a real <c>Retired</c> transition moves rolled gear into a place-pinned cache, same
/// transaction, never a copy. In-memory stores only (<c>DataTestStore.Create()</c>).</summary>
[Collection(FusionRpg.Data.Tests.CorpseCache.LawnAttritionTuningCollection.Name)]
public class CorpseCacheTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;

    public CorpseCacheTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.corpse-test", "", 1), KindId = "stat.modify",
            FamilyId = "atom.corpse-test", Variant = "", Tier = 1, Name = "Corpse Test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.corpse-test", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.corpse-test.t1") },
        }).IsOk);
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string SeedInstance()
    {
        var container = _store.GetContainer("item.corpse-test")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    string SeedOwnedItem()
    {
        var instanceId = SeedInstance();
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = _playerId.ToString(),
            AcquiredUtc = DateTime.UtcNow.ToString("o"),
        });
        return instanceId;
    }

    /// <summary>A fresh specimen sitting in <c>Roster</c> — the only phase in which gear may be
    /// assigned (`RpgStore.Items.RequireRosterPhaseForAssignmentUnlocked`).</summary>
    string NewRosterSpecimen() => _store.CreateUniqueActor(_playerId, "plant", 3).InstanceId;

    /// <summary>Takes an existing Roster specimen to <c>ActiveBound</c> with a real match_key — the
    /// lawn-death shape.</summary>
    void Deploy(string specimenId, string matchKey)
    {
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryBeginUniqueDeploy(specimenId, corr, matchKey).Ok);
        var ack = _store.TryAckUniqueSpawn(corr, "ptr-" + Guid.NewGuid().ToString("N"), matchKey);
        Assert.True(ack.Ok);
    }

    /// <summary>Create + deploy in one step, for a test that needs no gear on the specimen.
    ///
    /// <para><b>Gear-carrying tests must NOT use this.</b> Since 2026-09-17 an assignment write refuses
    /// a non-<c>Roster</c> specimen, which is the property
    /// <see cref="SaveAssignment_refuses_a_non_Roster_specimen"/> pins — so a fixture that deploys
    /// first and equips second is now writing the fraud it is supposed to forbid. Equip in Roster,
    /// then <see cref="Deploy"/>, which is also the real order: gear is committed before a specimen
    /// goes out.</para></summary>
    string DeployToMatch(string matchKey)
    {
        var id = NewRosterSpecimen();
        Deploy(id, matchKey);
        return id;
    }

    /// <summary>The real sequence in one call: equip while Roster, then deploy.</summary>
    string EquipThenDeploy(string matchKey, ItemRole role, string refKind, string refId)
    {
        var id = NewRosterSpecimen();
        _store.SaveAssignment(id, role, refKind, refId);
        Deploy(id, matchKey);
        return id;
    }

    [Fact]
    public void A_real_death_moves_never_copies()
    {
        var gear = SeedOwnedItem();
        var specimen = EquipThenDeploy("m-corpse-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, gear);

        var retire = _store.TryRetireUniqueActor(specimen);
        Assert.True(retire.Ok);

        // Gone from the assignment table ...
        Assert.Empty(_store.ListAssignments(specimen));
        // ... present in a lawn cache keyed to the match, same instance id ...
        var cache = Assert.Single(_store.ListCorpseCaches());
        Assert.Equal("lawn", cache.PlaceKind);
        Assert.Equal("m-corpse-1", cache.PlaceRef);
        Assert.Equal("death", cache.SourceKind);
        Assert.False(cache.InVoid);
        Assert.Null(cache.DecayStartedUtc);
        var items = _store.ListCorpseCacheItems(cache.CacheId);
        var item = Assert.Single(items);
        Assert.Equal("instance", item.Kind);
        Assert.Equal(gear, item.InstanceId);
        Assert.Equal(specimen, item.OriginOwner);
        // ... and the item row itself is untouched (no new disposition value).
        Assert.Equal("owned", _store.GetItem(gear)!.Disposition);
    }

    [Fact]
    public void Two_specimens_same_role_one_cache_no_collision()
    {
        // The exact PK collision the spec's Locked anchors rule out for a "repoint the
        // assignment row" design: two same-role rows cannot share (specimen_id, role), but two
        // cache rows share (cache_id) freely — seq differs.
        const string match = "m-corpse-shared";
        var gear1 = SeedOwnedItem();
        var gear2 = SeedOwnedItem();
        var first = EquipThenDeploy(match, ItemRole.CoreGuard, EquipRefKinds.Rolled, gear1);
        var second = EquipThenDeploy(match, ItemRole.CoreGuard, EquipRefKinds.Rolled, gear2);

        Assert.True(_store.TryRetireUniqueActor(first).Ok);
        Assert.True(_store.TryRetireUniqueActor(second).Ok);

        var cache = Assert.Single(_store.ListCorpseCaches());
        var items = _store.ListCorpseCacheItems(cache.CacheId);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.InstanceId == gear1);
        Assert.Contains(items, i => i.InstanceId == gear2);
        Assert.NotEqual(items[0].Seq, items[1].Seq);
        Assert.Empty(_store.ListAssignments(first));
        Assert.Empty(_store.ListAssignments(second));
    }

    [Fact]
    public void Stock_backed_assignment_never_drops_and_is_left_in_place()
    {
        var specimen = EquipThenDeploy(
            "m-corpse-stock", ItemRole.ArmamentPrimary, EquipRefKinds.Stock, "item.corpse-test");

        Assert.True(_store.TryRetireUniqueActor(specimen).Ok);

        // Left in place (spec §Design 2: "left in place"), and no empty cache row created.
        var row = Assert.Single(_store.ListAssignments(specimen));
        Assert.Equal(EquipRefKinds.Stock, row.RefKind);
        Assert.Empty(_store.ListCorpseCaches());
    }

    [Fact]
    public void A_retire_with_no_movable_gear_creates_no_cache()
    {
        var specimen = DeployToMatch("m-corpse-bare");

        Assert.True(_store.TryRetireUniqueActor(specimen).Ok);

        Assert.Empty(_store.ListCorpseCaches());
    }

    [Fact]
    public void Commander_pouch_is_never_read_or_written_by_the_move()
    {
        var gear = SeedOwnedItem();
        var specimen = EquipThenDeploy("m-corpse-pouch", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, gear);
        var pouchKey = "player:" + _playerId;
        var pouchGear = SeedOwnedItem();
        _store.SavePlayerItemAssignment(pouchKey, ItemRole.CoreGuard, EquipRefKinds.Rolled, pouchGear);

        Assert.True(_store.TryRetireUniqueActor(specimen).Ok);

        // Specimen gear moved; the pouch row is byte-identical.
        Assert.Empty(_store.ListAssignments(specimen));
        var pouch = Assert.Single(_store.ListPlayerItemAssignments(pouchKey));
        Assert.Equal(pouchGear, pouch.RefId);
        Assert.All(_store.ListCorpseCaches(),
            c => Assert.All(_store.ListCorpseCacheItems(c.CacheId),
                i => Assert.NotEqual(pouchGear, i.InstanceId)));
    }

    [Fact]
    public void Closed_vocabularies_refuse_unknown_values()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        Assert.Throws<ArgumentException>(() =>
            _store.ResolveOrCreateCacheUnlocked(db, null, "bogus_place", "ref", "death"));
        Assert.Throws<ArgumentException>(() =>
            _store.ResolveOrCreateCacheUnlocked(db, null, "lawn", "ref", "bogus_source"));
        Assert.Throws<ArgumentException>(() =>
            _store.ResolveOrCreateCacheUnlocked(db, null, "lawn", "", "death"));
    }

    [Fact]
    public void Resolve_is_place_keyed_and_idempotent()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        var first = _store.ResolveOrCreateCacheUnlocked(db, null, "lawn", "m-idem", "death");
        var second = _store.ResolveOrCreateCacheUnlocked(db, null, "lawn", "m-idem", "death");
        Assert.Equal(first, second);
        var other = _store.ResolveOrCreateCacheUnlocked(db, null, "lawn", "m-other", "death");
        Assert.NotEqual(first, other);
    }

    /// <summary>The ask landed 2026-09-17: <c>SaveAssignment</c>/<c>RemoveAssignment</c> now carry the
    /// same <c>Phase == Roster</c> gate <c>RpgStore.Expeditions.cs:60-61</c> already applied to dispatch,
    /// so the deploy-time-snapshot property is provable against shipped code. Was skipped until then.</summary>
    [Fact]
    public void SaveAssignment_refuses_a_non_Roster_specimen()
    {
        var specimen = DeployToMatch("m-corpse-fraud");
        var gear = SeedOwnedItem();

        // Assigning mid-deployment refuses, mirroring RpgStore.Expeditions.cs:60-61 (non-Roster
        // dispatch refuses "specimen.deployed").
        Assert.Throws<InvalidOperationException>(() =>
            _store.SaveAssignment(specimen, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, gear));

        // The mirror half: stripping gear mid-deployment is the same fraud, so it refuses too.
        Assert.Throws<InvalidOperationException>(() =>
            _store.RemoveAssignment(specimen, ItemRole.ArmamentPrimary));
    }
}
