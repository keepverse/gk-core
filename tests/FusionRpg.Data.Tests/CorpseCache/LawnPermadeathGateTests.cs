using FusionRpg.Contracts;
using FusionRpg.Core.Battle.Attrition;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.CorpseCache;

/// <summary>
/// `solid-remediation` SR-17, closed 2026-09-17 by owner ruling: *"Lawn permanent Death? Use damage
/// scale and low chance permanent death, that mode is casual, dont kill all player unique demon, so
/// injury also low too."*
///
/// <para>The lawn die path now asks <see cref="LawnPermadeathLadder"/> whether a death is permanent
/// instead of retiring unconditionally. These tests prove the gate actually gates — both outcomes, from
/// the same event shape, with only the tuning changed.</para>
///
/// <para><b>Why this class mutates a process-wide hub and therefore shares a collection.</b>
/// <see cref="LawnAttritionTuningHub"/> is static, the assembly bootstrap sets an
/// always-permadeath default so the pre-existing lawn-death tests stay deterministic, and these tests
/// need to move it. A class mutating a process-wide static while sibling classes read it is exactly the
/// flake this program spent 2026-09-17 removing from `WorldMarchCostProjectionTests`, so every class
/// that reads this hub is serialized into one xUnit collection rather than left to interleave.</para>
/// </summary>
[Collection(LawnAttritionTuningCollection.Name)]
public class LawnPermadeathGateTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly LawnAttritionTuning _original = LawnAttritionTuningHub.Tuning;

    public LawnPermadeathGateTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.lawn-gate-test", "", 1), KindId = "stat.modify",
            FamilyId = "atom.lawn-gate-test", Variant = "", Tier = 1, Name = "Lawn Gate Test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.lawn-gate-test", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.lawn-gate-test.t1") },
        }).IsOk);
    }

    public void Dispose()
    {
        LawnAttritionTuningHub.Configure(_original); // never leak a mutated hub to the next class
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    static void SetPermadeathChance(int chanceAtFullMilli) =>
        LawnAttritionTuningHub.Configure(new LawnAttritionTuning(
            SchemaVersion: 1, Version: 1,
            InjuryFloorMilli: 250, InjuryChanceAtFullMilli: 120,
            PermadeathFloorMilli: 700, PermadeathChanceAtFullMilli: chanceAtFullMilli));

    string SeedOwnedItem()
    {
        var container = _store.GetContainer("item.lawn-gate-test")!;
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

    (string InstanceId, string Ptr) EquipThenDeploy(string matchKey, string gear)
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 3);
        _store.SaveAssignment(a.InstanceId, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, gear);
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, matchKey).Ok);
        var ptr = "ptr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryAckUniqueSpawn(corr, ptr, matchKey).Ok);
        return (a.InstanceId, ptr);
    }

    void FireDie(string matchKey, string ptr) =>
        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("plant.die", matchKey, "{\"ptr\":\"" + ptr + "\"}"),
        });

    /// <summary>The casual case the ruling asks for, and the one that will be overwhelmingly common at
    /// the shipped 40‰: the specimen dies on the board and is NOT lost. It walks home to Roster with
    /// its gear still worn, and no corpse cache is created — nothing to reclaim, because nothing
    /// dropped.</summary>
    [Fact]
    public void A_lawn_death_that_misses_the_permadeath_roll_recovers_to_Roster_with_gear_worn()
    {
        SetPermadeathChance(0); // certain survival
        const string match = "m-lawn-gate-survive";
        var gear = SeedOwnedItem();
        var (specimen, ptr) = EquipThenDeploy(match, gear);

        FireDie(match, ptr);

        var after = _store.GetUniqueActor(specimen)!;
        Assert.Equal(UniqueActorPhases.Roster, after.Phase);
        Assert.Null(after.MatchKey);
        Assert.Null(after.LastPtr);

        // Gear stays worn: the assignment row survives and no cache row exists.
        var worn = Assert.Single(_store.ListAssignments(specimen));
        Assert.Equal(gear, worn.RefId);
        Assert.Empty(_store.ListCorpseCaches());
        Assert.Equal("owned", _store.GetItem(gear)!.Disposition);
    }

    /// <summary>The rare case: permanent death. Same event, same fixture, only the tuning differs — so
    /// this pair together proves the LADDER decides the outcome, not the fact of dying.</summary>
    [Fact]
    public void A_lawn_death_that_makes_the_permadeath_roll_retires_and_caches_the_gear()
    {
        SetPermadeathChance(1000); // certain permadeath: a 0..999 roll is always under it
        const string match = "m-lawn-gate-perma";
        var gear = SeedOwnedItem();
        var (specimen, ptr) = EquipThenDeploy(match, gear);

        FireDie(match, ptr);

        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(specimen)!.Phase);
        Assert.Empty(_store.ListAssignments(specimen));
        var cache = Assert.Single(_store.ListCorpseCaches());
        Assert.Equal("lawn", cache.PlaceKind);
        Assert.Equal(match, cache.PlaceRef);
        var item = Assert.Single(_store.ListCorpseCacheItems(cache.CacheId));
        Assert.Equal(gear, item.InstanceId);
    }

    /// <summary>The roll is derived from the event, never drawn fresh — so re-ingesting the same
    /// `plant.die` cannot flip a specimen between alive and permanently dead. Without this, a replay
    /// would be a second coin toss on a player's actor.</summary>
    [Fact]
    public void Re_ingesting_the_same_die_event_cannot_change_the_outcome()
    {
        SetPermadeathChance(0);
        const string match = "m-lawn-gate-replay";
        var gear = SeedOwnedItem();
        var (specimen, ptr) = EquipThenDeploy(match, gear);

        FireDie(match, ptr);
        var first = _store.GetUniqueActor(specimen)!.Phase;

        FireDie(match, ptr); // same event again
        FireDie(match, ptr);

        Assert.Equal(first, _store.GetUniqueActor(specimen)!.Phase);
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(specimen)!.Phase);
        Assert.Empty(_store.ListCorpseCaches());
    }
}
