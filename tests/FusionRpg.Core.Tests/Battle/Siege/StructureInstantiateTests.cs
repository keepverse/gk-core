using System.IO;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Siege;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Siege;

/// <summary>
/// base-defense `structure-instantiate` (module 26, spec-structure-instantiate.md). Confirms the
/// module's own central claim: this is a WIRING call over the real, already-shipped
/// <see cref="Instantiator.TryInstantiate"/> — never a second roll — and that a structure with no
/// `ContainerId` (every real structure today) instantiates to a correct, empty result rather than
/// an error.
/// </summary>
public class StructureInstantiateTests
{
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly Dictionary<string, AtomRow> Atoms = new(StringComparer.Ordinal);

    static StructureInstantiateTests()
    {
        var id = AtomRow.DeriveId("atom.dug-in", "", 1);
        Atoms[id] = new AtomRow
        {
            AtomId = id, KindId = "stat.modify", FamilyId = "atom.dug-in", Variant = "", Tier = 1,
            ParamsJson = "{\"channel\":\"combat.dodge.flat\",\"op\":\"flat\",\"amount\":40}",
        };
    }

    static AtomRow? LookupAtom(string atomId) => Atoms.TryGetValue(atomId, out var a) ? a : null;
    static AffixRow? LookupAffix(string affixId) => null;

    static readonly Func<string, ContainerRow?> NoContainers = _ => null;

    static StructureDef Def(string? containerId = null) => new()
    {
        StructureId = "test-structure", Name = "Test Structure",
        Kind = StructureKind.Obstacle, RequiredSlotKind = SlotKind.Wildland,
        AcquisitionPaths = new[] { AcquisitionPath.Built },
        ContainerId = containerId,
    };

    // ---- §1: no container is a correct, empty roll -- never an error --------------------------

    [Fact]
    public void A_structure_with_no_container_instantiates_to_an_empty_real_instance()
    {
        var ok = StructureInstantiate.TryInstantiateStructure(
            Def(containerId: null), NoContainers, LookupAtom, LookupAffix,
            rollSeed: 12345UL, thetaContent: 20, Tuning, out var instance);

        Assert.True(ok);
        Assert.NotNull(instance);
        Assert.Empty(instance!.Atoms);
    }

    [Fact]
    public void An_unresolvable_container_id_throws_not_a_silent_empty_result()
    {
        var def = Def(containerId: "structure.does-not-exist");
        Assert.Throws<InvalidOperationException>(() =>
            StructureInstantiate.TryInstantiateStructure(
                def, NoContainers, LookupAtom, LookupAffix, 1UL, 20, Tuning, out _));
    }

    // ---- §1: HP and every ordinal-derived magnitude are never rolled ---------------------------

    [Fact]
    public void Hp_is_not_rolled_the_structuredef_itself_is_untouched_by_the_call()
    {
        var def = Def(containerId: null) with { MaterialTier = 2 };
        StructureInstantiate.TryInstantiateStructure(
            def, NoContainers, LookupAtom, LookupAffix, 999UL, 20, Tuning, out _);

        // The call takes `StructureDef` by value and returns only an `InstanceRow` -- there is no
        // return path that could mutate MaterialTier/Cost/etc, proven directly by re-reading it.
        Assert.Equal(2, def.MaterialTier);
        Assert.Equal(0, def.Cost);
    }

    // ---- §2: rollSeed derivation -- never a clock or counter -----------------------------------

    [Fact]
    public void DeriveRollSeed_is_pure_and_deterministic()
    {
        var a = StructureInstantiate.DeriveRollSeed(worldSeed: 42UL, sectorId: "ash-waste", slotIndex: 3, buildTurn: 7);
        var b = StructureInstantiate.DeriveRollSeed(worldSeed: 42UL, sectorId: "ash-waste", slotIndex: 3, buildTurn: 7);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Two_placements_roll_differently()
    {
        var bySlot = StructureInstantiate.DeriveRollSeed(1UL, "ash-waste", 3, 7);
        var byOtherSlot = StructureInstantiate.DeriveRollSeed(1UL, "ash-waste", 4, 7);
        var byOtherTurn = StructureInstantiate.DeriveRollSeed(1UL, "ash-waste", 3, 8);
        var byOtherSector = StructureInstantiate.DeriveRollSeed(1UL, "green-hollow", 3, 7);

        Assert.NotEqual(bySlot, byOtherSlot);
        Assert.NotEqual(bySlot, byOtherTurn);
        Assert.NotEqual(bySlot, byOtherSector);
    }

    [Fact]
    public void Replay_reproduces_the_same_instance()
    {
        // The :603 re-derivation loop re-runs from turn zero with the same world seed -- proven
        // here as "the same four inputs always produce the same rollSeed, hence the same instance."
        var seed1 = StructureInstantiate.DeriveRollSeed(7UL, "ash-waste", 2, 11);
        var seed2 = StructureInstantiate.DeriveRollSeed(7UL, "ash-waste", 2, 11);

        var containers = ContainersWith(RealContainer());
        StructureInstantiate.TryInstantiateStructure(
            Def("world-buff.trench-cover"), containers, LookupAtom, LookupAffix, seed1, 20, Tuning, out var i1);
        StructureInstantiate.TryInstantiateStructure(
            Def("world-buff.trench-cover"), containers, LookupAtom, LookupAffix, seed2, 20, Tuning, out var i2);

        Assert.Equal(i1!.ContentFingerprint(), i2!.ContentFingerprint());
    }

    // ---- §2, real container path: Instantiator.TryInstantiate genuinely gets called ------------

    static ContainerRow RealContainer() => new()
    {
        ContainerId = "world-buff.trench-cover",
        Kind = ContainerKind.WorldBuff,
        Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId("atom.dug-in", "", 1)) },
    };

    static Func<string, ContainerRow?> ContainersWith(ContainerRow row) =>
        id => id == row.ContainerId ? row : null;

    [Fact]
    public void A_structure_with_a_real_container_genuinely_rolls_through_instantiator()
    {
        var ok = StructureInstantiate.TryInstantiateStructure(
            Def("world-buff.trench-cover"), ContainersWith(RealContainer()), LookupAtom, LookupAffix,
            rollSeed: 5UL, thetaContent: 20, Tuning, out var instance);

        Assert.True(ok);
        Assert.Single(instance!.Atoms);
        Assert.Equal(AtomRow.DeriveId("atom.dug-in", "", 1), instance.Atoms[0].AtomId);
    }

    [Fact]
    public void Same_seed_same_instance_10000_times()
    {
        var container = RealContainer();
        var containers = ContainersWith(container);
        string? first = null;
        for (var i = 0; i < 10_000; i++)
        {
            StructureInstantiate.TryInstantiateStructure(
                Def(container.ContainerId), containers, LookupAtom, LookupAffix,
                rollSeed: 777UL, thetaContent: 20, Tuning, out var instance);
            first ??= instance!.ContentFingerprint();
            Assert.Equal(first, instance!.ContentFingerprint());
        }
    }

    // ---- success criterion 2: no second roll implementation anywhere in this module -------------

    [Fact]
    public void No_second_roll_exists_in_this_module()
    {
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Battle", "Siege", "StructureInstantiate.cs");
        var src = File.ReadAllText(path);
        Assert.DoesNotContain("System.Random", src);
        Assert.DoesNotContain("new Random(", src);
        Assert.DoesNotContain("SeededRng(", src); // only DeriveStream, never a raw `new SeededRng`
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "structures"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
