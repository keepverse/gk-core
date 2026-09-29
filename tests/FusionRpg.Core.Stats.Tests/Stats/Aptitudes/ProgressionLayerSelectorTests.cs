using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Aptitudes;

/// <summary>
/// `species-progression` SP1.1 (`layer-source-selector`, map C1) — the six-cell matrix: the 3 closed
/// `CreatureProgressionSource` variants × the 2 relations an empire can have to the save (is / is not
/// the human empire). All six are pinned, with the reason: the source set is a closed vocabulary the
/// code owns (validation-ssot.md) — the set of EMPIRES is never pinned (an open id, a population).
/// </summary>
public class ProgressionLayerSelectorTests
{
    static readonly EmpireId Human = EmpireId.Dave;
    static readonly EmpireId NotHuman = EmpireId.Zomboss;

    // ---- the six cells --------------------------------------------------------------------------

    [Fact]
    public void UniqueSpecimen_humanEmpire_carriesCommanderAndItsOwnSpecimenAllocation()
    {
        var source = CreatureProgressionSource.UniqueSpecimen("specimen-1", "occ-1");

        var layers = ProgressionLayerSelector.Select(source, Human, Human);

        Assert.Equal(Human, layers.Empire);
        Assert.True(layers.CarriesCommander);
        var owner = Assert.IsType<ProgressionOwner.Specimen>(layers.Owner);
        Assert.Equal("specimen-1", owner.InstanceId);
    }

    [Fact]
    public void UniqueSpecimen_notHumanEmpire_carriesItsOwnersCommanderTermAndItsOwnSpecimenAllocation()
    {
        var source = CreatureProgressionSource.UniqueSpecimen("specimen-2", "occ-1");

        var layers = ProgressionLayerSelector.Select(source, NotHuman, Human);

        Assert.Equal(NotHuman, layers.Empire);
        // ai-empire-species EP4.18 (R23, R4 mirrored): no longer human-only. WHICH empire's pool the
        // term is rides on `Empire`, and whether that pool is empty is the pool read's answer.
        Assert.True(layers.CarriesCommander);
        var owner = Assert.IsType<ProgressionOwner.Specimen>(layers.Owner);
        Assert.Equal("specimen-2", owner.InstanceId);
    }

    [Fact]
    public void EmpireGeneral_humanEmpire_carriesCommanderAndTheEmpiresSpeciesAllocation()
    {
        var source = CreatureProgressionSource.EmpireGeneral("peashooter");

        var layers = ProgressionLayerSelector.Select(source, Human, Human);

        Assert.Equal(Human, layers.Empire);
        Assert.True(layers.CarriesCommander);
        var owner = Assert.IsType<ProgressionOwner.Species>(layers.Owner);
        Assert.Equal("peashooter", owner.SpeciesId);
    }

    [Fact]
    public void EmpireGeneral_notHumanEmpire_carriesItsOwnersCommanderTermAndTheEmpiresSpeciesAllocation()
    {
        var source = CreatureProgressionSource.EmpireGeneral("conezombie");

        var layers = ProgressionLayerSelector.Select(source, NotHuman, Human);

        Assert.Equal(NotHuman, layers.Empire);
        // ai-empire-species EP4.18 (R23, R4 mirrored): no longer human-only. WHICH empire's pool the
        // term is rides on `Empire`, and whether that pool is empty is the pool read's answer.
        Assert.True(layers.CarriesCommander);
        var owner = Assert.IsType<ProgressionOwner.Species>(layers.Owner);
        Assert.Equal("conezombie", owner.SpeciesId);
    }

    [Fact]
    public void Commander_humanEmpire_carriesCommanderAndNoSpeciesLayer()
    {
        var source = CreatureProgressionSource.Commander("dave");

        var layers = ProgressionLayerSelector.Select(source, Human, Human);

        Assert.Equal(Human, layers.Empire);
        Assert.True(layers.CarriesCommander);
        Assert.IsType<ProgressionOwner.None>(layers.Owner);
    }

    [Fact]
    public void Commander_notHumanEmpire_carriesItsOwnersCommanderTermAndNoSpeciesLayer()
    {
        var source = CreatureProgressionSource.Commander("zomboss");

        var layers = ProgressionLayerSelector.Select(source, NotHuman, Human);

        Assert.Equal(NotHuman, layers.Empire);
        // ai-empire-species EP4.18 (R23, R4 mirrored): no longer human-only. WHICH empire's pool the
        // term is rides on `Empire`, and whether that pool is empty is the pool read's answer.
        Assert.True(layers.CarriesCommander);
        Assert.IsType<ProgressionOwner.None>(layers.Owner);
    }

    // ---- 2a/2b mutual exclusion, structurally ---------------------------------------------------

    [Fact]
    public void OwnerHasExactlyOneSlot_neverBothSpecimenAndSpecies()
    {
        // Structural, not behavioural: ProgressionOwner is a closed hierarchy of exactly three
        // single-purpose leaves -- there is no shape that could hold both an InstanceId and a
        // SpeciesId at once, which is the map C1 defect made impossible by construction.
        var t = typeof(ProgressionOwner);
        Assert.True(t.IsAbstract);
        var leaves = new[] { typeof(ProgressionOwner.Specimen), typeof(ProgressionOwner.Species), typeof(ProgressionOwner.None) };
        foreach (var leaf in leaves)
            Assert.True(t.IsAssignableFrom(leaf));
    }

    // ---- an unknown source subtype throws ------------------------------------------------------

    /// <summary>Real, constructible fifth case: `CreatureProgressionSource`'s primary constructor is
    /// public (an abstract record's default), so nothing stops a caller outside its own file from
    /// deriving a case its own three named factories never produce -- exactly the scenario `Select`'s
    /// default arm exists to refuse rather than silently mis-route.</summary>
    sealed record RogueSource() : CreatureProgressionSource("rogue.v1", "rogue:1", "rogue");

    [Fact]
    public void AnUnknownSourceSubtypeThrows()
    {
        var rogue = new RogueSource();

        Assert.Throws<ArgumentOutOfRangeException>(() => ProgressionLayerSelector.Select(rogue, Human, Human));
    }
}
