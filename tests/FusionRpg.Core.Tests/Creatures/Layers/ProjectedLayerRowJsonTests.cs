using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures.Layers;

/// <summary>species-progression step 6.2, Transport (SP6.3) — the ONE wire shape a
/// <see cref="ProjectedLayerRow"/> serializes to and parses back from, over the
/// `speciesLayers` field. Round-trip only: the actual HTTP wire is proven end to end by
/// `AptitudeEndpointsTests`; this file proves the mapping is faithful in isolation.</summary>
public class ProjectedLayerRowJsonTests
{
    [Fact]
    public void FixedRow_roundTrips()
    {
        var row = new ProjectedLayerRow("resource.max.hp", DerivedModifierOp.Flat,
            new LayerValue.Fixed(42.5), "species-base:melon-pult");

        var dto = ProjectedLayerRowJson.ToWire(row);
        Assert.Equal("fixed", dto.Kind);
        Assert.Equal(42.5, dto.Amount);
        Assert.Null(dto.KMicro);

        var back = ProjectedLayerRowJson.FromWire(dto);
        Assert.Equal(row, back);
    }

    [Fact]
    public void LadderMicroRow_roundTrips()
    {
        var row = new ProjectedLayerRow("combat.power.omni", DerivedModifierOp.Increased,
            new LayerValue.LadderMicro(1_234_567), "species-empire:dave:melon-pult:Might");

        var dto = ProjectedLayerRowJson.ToWire(row);
        Assert.Equal("ladderMicro", dto.Kind);
        Assert.Equal(1_234_567, dto.KMicro);
        Assert.Null(dto.Amount);

        var back = ProjectedLayerRowJson.FromWire(dto);
        Assert.Equal(row, back);
    }

    [Theory]
    [InlineData(DerivedModifierOp.Flat, "flat")]
    [InlineData(DerivedModifierOp.Increased, "increased")]
    [InlineData(DerivedModifierOp.Replace, "replace")]
    [InlineData(DerivedModifierOp.Flag, "flag")]
    public void EveryOp_roundTrips(DerivedModifierOp op, string token)
    {
        var row = new ProjectedLayerRow("combat.power.omni", op, new LayerValue.Fixed(1), "species-base:x");
        var dto = ProjectedLayerRowJson.ToWire(row);
        Assert.Equal(token, dto.Op);
        Assert.Equal(op, ProjectedLayerRowJson.FromWire(dto).Op);
    }

    [Fact]
    public void FromWire_unknownOp_throws()
    {
        var dto = new ProjectedLayerRowDto("c", "not-an-op", "fixed", 1, null, "s");
        Assert.Throws<ArgumentException>(() => ProjectedLayerRowJson.FromWire(dto));
    }

    [Fact]
    public void FromWire_unknownKind_throws()
    {
        var dto = new ProjectedLayerRowDto("c", "flat", "not-a-kind", 1, null, "s");
        Assert.Throws<ArgumentException>(() => ProjectedLayerRowJson.FromWire(dto));
    }

    [Fact]
    public void FromWire_fixedRowMissingAmount_throws()
    {
        var dto = new ProjectedLayerRowDto("c", "flat", "fixed", null, null, "s");
        Assert.Throws<ArgumentException>(() => ProjectedLayerRowJson.FromWire(dto));
    }

    [Fact]
    public void FromWire_ladderMicroRowMissingKMicro_throws()
    {
        var dto = new ProjectedLayerRowDto("c", "flat", "ladderMicro", null, null, "s");
        Assert.Throws<ArgumentException>(() => ProjectedLayerRowJson.FromWire(dto));
    }
}
