using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Aptitudes;

/// <summary>species-progression module 6 (`species-layer-delivery`) step 6.2 (SP6.4) —
/// SpeciesLayerSource, the `ctx -> ProjectedLayerRow[]` resolution behind the injector's
/// `speciesLayers` delegate. Mirrors SpeciesAllocationSourceTests' own shape: fully provable with
/// fake resolvers, no running game.</summary>
public class SpeciesLayerSourceTests
{
    static readonly ProjectedLayerRow BaseRow = new(
        "resource.max.stamina", DerivedModifierOp.Flat, new LayerValue.Fixed(1), "species-base:melon-pult");
    static readonly ProjectedLayerRow DaveModRow = new(
        "resource.max.hp", DerivedModifierOp.Flat, new LayerValue.Fixed(500), "species-player:melon-pult:fusion-pick");
    static readonly ProjectedLayerRow ZombossModRow = new(
        "combat.defense.omni", DerivedModifierOp.Flat, new LayerValue.Fixed(300), "species-player:melon-pult:fusion-pick");

    static SpeciesLayerSource NewSource(
        Func<string, string?>? resolveBoundInstanceId = null,
        Func<string, EmpireId?>? resolveSpecimenOwnerEmpire = null) => new(
        resolveSpeciesId: (side, typeId) => typeId == 42
            ? SpeciesLookupResult.Hit("melon-pult")
            : SpeciesLookupResult.NoSpecies,
        resolveBaseRows: speciesId => speciesId == "melon-pult"
            ? new[] { BaseRow } : Array.Empty<ProjectedLayerRow>(),
        resolveModRows: (empire, speciesId) => speciesId != "melon-pult"
            ? Array.Empty<ProjectedLayerRow>()
            : empire == EmpireId.Dave ? new[] { DaveModRow }
            : empire == EmpireId.Zomboss ? new[] { ZombossModRow }
            : Array.Empty<ProjectedLayerRow>(),
        resolveBoundInstanceId: resolveBoundInstanceId,
        resolveSpecimenOwnerEmpire: resolveSpecimenOwnerEmpire);

    static StatContext PlantCtx(int typeId, string entityKey = "P1") =>
        new() { Side = StatSide.Plant, TypeId = typeId, EntityKey = entityKey };

    static StatContext ZombieCtx(int typeId, string entityKey = "Z1") =>
        new() { Side = StatSide.Zombie, TypeId = typeId, EntityKey = entityKey };

    [Fact]
    public void NoSpeciesAtThisTypeId_returnsNothing_aNoneAnswer()
    {
        var source = NewSource();
        var rows = source.Resolve(PlantCtx(typeId: 999));
        Assert.Empty(rows);
    }

    [Fact]
    public void UnconfiguredIndex_returnsNothing_neverThrows()
    {
        var source = new SpeciesLayerSource(
            resolveSpeciesId: (_, _) => SpeciesLookupResult.NotConfigured,
            resolveBaseRows: _ => Array.Empty<ProjectedLayerRow>(),
            resolveModRows: (_, _) => Array.Empty<ProjectedLayerRow>());
        Assert.Empty(source.Resolve(PlantCtx(42)));
    }

    [Fact]
    public void AGeneralPlant_getsBaseAndModOfItsOwnSideEmpire_dave()
    {
        var source = NewSource();
        var rows = source.Resolve(PlantCtx(42));

        Assert.Contains(BaseRow, rows);
        Assert.Contains(DaveModRow, rows);
        Assert.DoesNotContain(ZombossModRow, rows);
    }

    [Fact]
    public void AGeneralZombie_getsBaseAndModOfItsOwnSideEmpire_zomboss()
    {
        var source = NewSource();
        var rows = source.Resolve(ZombieCtx(42));

        Assert.Contains(BaseRow, rows);
        Assert.Contains(ZombossModRow, rows);
        Assert.DoesNotContain(DaveModRow, rows);
    }

    [Fact]
    public void ABoundSpecimen_getsBaseAndModOfItsOwnerEmpire_neverItsSide()
    {
        // A zombie-side, HUMAN-OWNED specimen: side says zombie, owner says Dave. The specimen answer
        // must follow the OWNER, never ctx.Side -- the exact rule that makes a human-owned specimen on
        // the zombie side carry the human empire's progression on the lawn.
        var source = NewSource(
            resolveBoundInstanceId: key => key == "Z1" ? "instance-abc" : null,
            resolveSpecimenOwnerEmpire: key => key == "Z1" ? EmpireId.Dave : null);

        var rows = source.Resolve(ZombieCtx(42, entityKey: "Z1"));

        Assert.Contains(BaseRow, rows);
        Assert.Contains(DaveModRow, rows);
        Assert.DoesNotContain(ZombossModRow, rows);
    }

    [Fact]
    public void ABoundSpecimenWithNoOwnerResolver_defaultsToDave_theSameFallbackTheAllocationSourceUses()
    {
        var source = NewSource(resolveBoundInstanceId: key => key == "Z1" ? "instance-abc" : null);
        // resolveSpecimenOwnerEmpire omitted entirely
        var rows = source.Resolve(ZombieCtx(42, entityKey: "Z1"));

        Assert.Contains(DaveModRow, rows);
    }

    [Fact]
    public void ANonBoundEntity_withResolveBoundInstanceIdWired_stillResolvesAsAGeneral()
    {
        var source = NewSource(resolveBoundInstanceId: _ => null); // wired, but never hits for this ctx
        var rows = source.Resolve(PlantCtx(42, entityKey: "P1"));

        Assert.Contains(DaveModRow, rows);
    }

    [Fact]
    public void EmptySpecies_returnsEmptyList_neitherBaseNorModPresent()
    {
        var source = new SpeciesLayerSource(
            resolveSpeciesId: (_, _) => SpeciesLookupResult.Hit("empty-species"),
            resolveBaseRows: _ => Array.Empty<ProjectedLayerRow>(),
            resolveModRows: (_, _) => Array.Empty<ProjectedLayerRow>());

        Assert.Empty(source.Resolve(PlantCtx(42)));
    }

    [Fact]
    public void NullCtx_throws() =>
        Assert.Throws<ArgumentNullException>(() => NewSource().Resolve(null!));

    [Fact]
    public void NullResolveSpeciesId_throwsAtConstruction() =>
        Assert.Throws<ArgumentNullException>(() => new SpeciesLayerSource(
            resolveSpeciesId: null!,
            resolveBaseRows: _ => Array.Empty<ProjectedLayerRow>(),
            resolveModRows: (_, _) => Array.Empty<ProjectedLayerRow>()));

    [Fact]
    public void NullResolveBaseRows_throwsAtConstruction() =>
        Assert.Throws<ArgumentNullException>(() => new SpeciesLayerSource(
            resolveSpeciesId: (_, _) => SpeciesLookupResult.NoSpecies,
            resolveBaseRows: null!,
            resolveModRows: (_, _) => Array.Empty<ProjectedLayerRow>()));

    [Fact]
    public void NullResolveModRows_throwsAtConstruction() =>
        Assert.Throws<ArgumentNullException>(() => new SpeciesLayerSource(
            resolveSpeciesId: (_, _) => SpeciesLookupResult.NoSpecies,
            resolveBaseRows: _ => Array.Empty<ProjectedLayerRow>(),
            resolveModRows: null!));
}
