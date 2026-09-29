using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Siege;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// base-defense `structure-instantiate` (module 26, spec-structure-instantiate.md) §3: "that
/// player's own tables ... each player play they own game." Reuses the ALREADY-SHIPPED, generic
/// <c>RpgStore.SaveInstanceAndBind</c> plus <see cref="OwnerKind.Slot"/> — "a world-map construction
/// slot", built for exactly this and never given a real caller before this test. No new table, no
/// new SQL beyond what `effect_instance`/`effect_binding` already are (guard-dal's own boundary:
/// SQL stays inside `FusionRpg.Data`, unchanged by this task).
/// </summary>
public class StructureInstanceStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public StructureInstanceStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static AtomRow? NoAtoms(string id) => null;
    static AffixRow? NoAffixes(string id) => null;
    static ContainerRow? NoContainers(string id) => null;

    static StructureDef Moat() => new()
    {
        StructureId = "moat", Name = "Moat", Kind = StructureKind.Obstacle,
        RequiredSlotKind = SlotKind.Wildland, MaterialTier = 1,
        AcquisitionPaths = new[] { AcquisitionPath.Built }, ContainerId = null,
    };

    static string SlotOwnerKey(string sectorId, int slotIndex) => $"{sectorId}-{slotIndex}";

    [Fact]
    public void A_structure_instance_saves_and_binds_to_its_own_world_slot()
    {
        var rollSeed = StructureInstantiate.DeriveRollSeed(worldSeed: 1UL, sectorId: "ash-waste", slotIndex: 2, buildTurn: 5);
        var ok = StructureInstantiate.TryInstantiateStructure(
            Moat(), NoContainers, NoAtoms, NoAffixes, rollSeed, thetaContent: 20, Tuning, out var instance);
        Assert.True(ok);

        var owner = new OwnerScope(OwnerKind.Slot, SlotOwnerKey("ash-waste", 2));
        var binding = new BindingRow { OwnerKind = owner.Kind, OwnerKey = owner.Key, Priority = 0, Source = "structure-instantiate" };

        var save = _store.SaveInstanceAndBind(instance!, binding, out var instanceId, out var bindingId);
        Assert.True(save.IsOk, save.ToString());
        Assert.NotEmpty(instanceId);
        Assert.NotEmpty(bindingId);

        var bindings = _store.ListBindings(owner);
        Assert.Single(bindings);
        Assert.Equal(instanceId, bindings[0].InstanceId);
    }

    [Fact]
    public void The_saved_instance_reads_back_byte_identical()
    {
        var rollSeed = StructureInstantiate.DeriveRollSeed(9UL, "green-hollow", 4, 12);
        StructureInstantiate.TryInstantiateStructure(
            Moat(), NoContainers, NoAtoms, NoAffixes, rollSeed, thetaContent: 20, Tuning, out var instance);

        var owner = new OwnerScope(OwnerKind.Slot, SlotOwnerKey("green-hollow", 4));
        _store.SaveInstanceAndBind(instance!, new BindingRow { OwnerKind = owner.Kind, OwnerKey = owner.Key, Source = "test" },
            out var instanceId, out _);

        var reread = _store.GetInstance(instanceId);
        Assert.NotNull(reread);
        Assert.Equal(instance!.ContentFingerprint(), reread!.ContentFingerprint());
    }

    [Fact]
    public void Two_different_slots_get_two_independent_instances_not_a_shared_catalog_row()
    {
        var seedA = StructureInstantiate.DeriveRollSeed(1UL, "ash-waste", 0, 1);
        var seedB = StructureInstantiate.DeriveRollSeed(1UL, "ash-waste", 1, 1);
        StructureInstantiate.TryInstantiateStructure(Moat(), NoContainers, NoAtoms, NoAffixes, seedA, 20, Tuning, out var instA);
        StructureInstantiate.TryInstantiateStructure(Moat(), NoContainers, NoAtoms, NoAffixes, seedB, 20, Tuning, out var instB);

        var ownerA = new OwnerScope(OwnerKind.Slot, SlotOwnerKey("ash-waste", 0));
        var ownerB = new OwnerScope(OwnerKind.Slot, SlotOwnerKey("ash-waste", 1));
        _store.SaveInstanceAndBind(instA!, new BindingRow { OwnerKind = ownerA.Kind, OwnerKey = ownerA.Key, Source = "test" }, out var idA, out _);
        _store.SaveInstanceAndBind(instB!, new BindingRow { OwnerKind = ownerB.Kind, OwnerKey = ownerB.Key, Source = "test" }, out var idB, out _);

        Assert.NotEqual(idA, idB);
        Assert.Single(_store.ListBindings(ownerA));
        Assert.Single(_store.ListBindings(ownerB));
        Assert.DoesNotContain(_store.ListBindings(ownerB), b => b.InstanceId == idA);
    }
}
