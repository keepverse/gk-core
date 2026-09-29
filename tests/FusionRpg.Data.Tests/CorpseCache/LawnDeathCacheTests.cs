using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.CorpseCache;

/// <summary>Task 4C.1 (lawn-death producer, explicit lawn→Retire trigger — not the injury-tiers
/// build): a bound specimen named by a real <c>plant.die</c>/<c>zombie.die</c> event retires for
/// real and its rolled gear moves into a lawn cache in the same transaction. Survivors still walk
/// home through <c>board.end</c>/<c>match.result</c> with gear worn (recover rule unchanged).
/// In-memory stores only (<c>DataTestStore.Create()</c>).</summary>
[Collection(FusionRpg.Data.Tests.CorpseCache.LawnAttritionTuningCollection.Name)]
public class LawnDeathCacheTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;

    public LawnDeathCacheTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.lawn-death-test", "", 1), KindId = "stat.modify",
            FamilyId = "atom.lawn-death-test", Variant = "", Tier = 1, Name = "Lawn Death Test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.lawn-death-test", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.lawn-death-test.t1") },
        }).IsOk);
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string SeedOwnedItem()
    {
        var container = _store.GetContainer("item.lawn-death-test")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        var instanceId = _store.SaveInstance(inst!);
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = _playerId.ToString(),
            AcquiredUtc = DateTime.UtcNow.ToString("o"),
        });
        return instanceId;
    }

    /// <summary>Deploy and ack, returning (instanceId, ptr) of a bound specimen.</summary>
    (string InstanceId, string Ptr) DeployBound(string side, string matchKey)
    {
        var a = _store.CreateUniqueActor(_playerId, side, 3);
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, matchKey).Ok);
        var ptr = "ptr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryAckUniqueSpawn(corr, ptr, matchKey).Ok);
        return (a.InstanceId, ptr);
    }

    /// <summary>The real sequence: equip while the specimen is still <c>Roster</c>, then deploy.
    ///
    /// <para>Since 2026-09-17 an assignment write refuses a non-<c>Roster</c> specimen
    /// (`RpgStore.Items.RequireRosterPhaseForAssignmentUnlocked`), so a fixture that deployed first and
    /// equipped second was writing the very fraud
    /// <c>CorpseCacheTests.SaveAssignment_refuses_a_non_Roster_specimen</c> forbids. This order is also
    /// the true one — gear is committed before a specimen goes out, which is what makes "what it died
    /// with" and "what it deployed with" the same rows by construction.</para></summary>
    (string InstanceId, string Ptr) EquipThenDeployBound(
        string side, string matchKey, ItemRole role, string refKind, string refId)
    {
        var a = _store.CreateUniqueActor(_playerId, side, 3);
        _store.SaveAssignment(a.InstanceId, role, refKind, refId);
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, matchKey).Ok);
        var ptr = "ptr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryAckUniqueSpawn(corr, ptr, matchKey).Ok);
        return (a.InstanceId, ptr);
    }

    void FireDie(string kind, string matchKey, string ptr) =>
        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            (kind, matchKey, "{\"ptr\":\"" + ptr + "\"}"),
        });

    [Fact]
    public void PlantDie_with_gear_retires_and_moves_gear_into_a_lawn_cache()
    {
        const string match = "m-lawn-die-plant";
        var gear = SeedOwnedItem();
        var (specimen, ptr) = EquipThenDeployBound(
            "plant", match, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, gear);

        FireDie("plant.die", match, ptr);

        // Real death, through the real die path: Retired, bind fields cleared.
        var dead = _store.GetUniqueActor(specimen)!;
        Assert.Equal(UniqueActorPhases.Retired, dead.Phase);
        Assert.Null(dead.MatchKey);
        Assert.Null(dead.LastPtr);
        Assert.False(_store.HasAnyActiveBoundUniqueActors());

        // Rolled gear moved into a lawn cache keyed to the match, same transaction.
        Assert.Empty(_store.ListAssignments(specimen));
        var cache = Assert.Single(_store.ListCorpseCaches());
        Assert.Equal("lawn", cache.PlaceKind);
        Assert.Equal(match, cache.PlaceRef);
        Assert.Equal("death", cache.SourceKind);
        var item = Assert.Single(_store.ListCorpseCacheItems(cache.CacheId));
        Assert.Equal("instance", item.Kind);
        Assert.Equal(gear, item.InstanceId);
        Assert.Equal(specimen, item.OriginOwner);
        // The item row itself is untouched (no new disposition value).
        Assert.Equal("owned", _store.GetItem(gear)!.Disposition);
    }

    [Fact]
    public void ZombieDie_with_gear_retires_and_moves_gear_into_a_lawn_cache()
    {
        const string match = "m-lawn-die-zombie";
        var gear = SeedOwnedItem();
        var (specimen, ptr) = EquipThenDeployBound(
            "zombie", match, ItemRole.CoreGuard, EquipRefKinds.Rolled, gear);

        FireDie("zombie.die", match, ptr);

        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(specimen)!.Phase);
        Assert.Empty(_store.ListAssignments(specimen));
        var cache = Assert.Single(_store.ListCorpseCaches());
        Assert.Equal("lawn", cache.PlaceKind);
        Assert.Equal(match, cache.PlaceRef);
        Assert.Equal("death", cache.SourceKind);
        var item = Assert.Single(_store.ListCorpseCacheItems(cache.CacheId));
        Assert.Equal(gear, item.InstanceId);
    }

    [Fact]
    public void BoardEnd_survivor_recovers_to_Roster_with_gear_worn_and_no_cache()
    {
        // The recover rule is UNCHANGED: only die-named specimens retire. A survivor of the
        // same match walks home through board.end with everything still worn.
        const string match = "m-lawn-die-survivor";
        var deadGear = SeedOwnedItem();
        var survivorGear = SeedOwnedItem();
        var (dead, deadPtr) = EquipThenDeployBound(
            "plant", match, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, deadGear);
        var (survivor, _) = EquipThenDeployBound(
            "zombie", match, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, survivorGear);

        FireDie("plant.die", match, deadPtr);
        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("board.end", match, "{}"),
        });

        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(dead)!.Phase);
        var home = _store.GetUniqueActor(survivor)!;
        Assert.Equal(UniqueActorPhases.Roster, home.Phase);
        var worn = Assert.Single(_store.ListAssignments(survivor));
        Assert.Equal(survivorGear, worn.RefId);

        // Exactly one cache (the dead specimen's); nothing of the survivor's is in any cache.
        var cache = Assert.Single(_store.ListCorpseCaches());
        var items = _store.ListCorpseCacheItems(cache.CacheId);
        Assert.Single(items);
        Assert.DoesNotContain(items, i => i.InstanceId == survivorGear);
    }

    [Fact]
    public void Die_with_no_gear_retires_without_creating_a_cache()
    {
        const string match = "m-lawn-die-bare";
        var (specimen, ptr) = DeployBound("plant", match);

        FireDie("plant.die", match, ptr);

        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(specimen)!.Phase);
        Assert.Empty(_store.ListCorpseCaches());
    }

    [Fact]
    public void Die_never_reads_or_writes_the_commander_pouch()
    {
        const string match = "m-lawn-die-pouch";
        var gear = SeedOwnedItem();
        var (specimen, ptr) = EquipThenDeployBound(
            "plant", match, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, gear);
        var pouchKey = "player:" + _playerId;
        var pouchGear = SeedOwnedItem();
        _store.SavePlayerItemAssignment(pouchKey, ItemRole.CoreGuard, EquipRefKinds.Rolled, pouchGear);

        FireDie("plant.die", match, ptr);

        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(specimen)!.Phase);
        var pouch = Assert.Single(_store.ListPlayerItemAssignments(pouchKey));
        Assert.Equal(pouchGear, pouch.RefId);
        Assert.All(_store.ListCorpseCaches(),
            c => Assert.All(_store.ListCorpseCacheItems(c.CacheId),
                i => Assert.NotEqual(pouchGear, i.InstanceId)));
    }
}
