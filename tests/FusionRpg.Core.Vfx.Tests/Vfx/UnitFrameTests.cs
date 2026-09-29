using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

public class UnitFrameTests
{
    [Theory]
    [InlineData(VfxAuraStyle.PactFootPulse, VfxAnchorKind.Feet)]
    [InlineData(VfxAuraStyle.CommandCrownPulse, VfxAnchorKind.Crown)]
    [InlineData(VfxAuraStyle.Orbit, VfxAnchorKind.Body)]
    [InlineData(VfxAuraStyle.WispOut, VfxAnchorKind.Feet)]
    [InlineData(VfxAuraStyle.RiseSparkle, VfxAnchorKind.Crown)]
    public void AnchorCatalog_maps_identity_clusters(VfxAuraStyle style, VfxAnchorKind expected) =>
        Assert.Equal(expected, VfxAnchorCatalog.AnchorKindFor(style));

    [Fact]
    public void WorldX_uses_bounds_center_when_present() =>
        Assert.Equal(5f, VfxUnitFrameMath.WorldX(1f, 5f, hasBounds: true));

    [Fact]
    public void WorldX_falls_back_to_pivot_without_bounds() =>
        Assert.Equal(1f, VfxUnitFrameMath.WorldX(1f, 5f, hasBounds: false));

    [Fact]
    public void WorldY_feet_uses_lane() =>
        Assert.Equal(2f, VfxUnitFrameMath.WorldY(2f, 9f, halfCell: 0.5f, boundsHalfHeight: 2f, hasBounds: true, VfxAnchorKind.Feet));

    [Fact]
    public void WorldY_body_uses_bounds_center_when_present() =>
        Assert.Equal(9f, VfxUnitFrameMath.WorldY(2f, 9f, halfCell: 0.5f, boundsHalfHeight: 2f, hasBounds: true, VfxAnchorKind.Body));

    [Fact]
    public void WorldY_body_falls_back_to_lane_plus_half_cell() =>
        Assert.Equal(2.5f, VfxUnitFrameMath.WorldY(2f, 9f, halfCell: 0.5f, boundsHalfHeight: 2f, hasBounds: false, VfxAnchorKind.Body));

    [Fact]
    public void WorldY_crown_uses_upper_bounds_when_present() =>
        Assert.Equal(10.3f, VfxUnitFrameMath.WorldY(2f, 9f, halfCell: 0.5f, boundsHalfHeight: 2f, hasBounds: true, VfxAnchorKind.Crown), 1);

    [Fact]
    public void Span_uses_sprite_bounds_when_larger_than_cell()
    {
        var span = VfxSpanMath.ComputeSpan(
            cellSpan: 1f, boundsWidth: 2.4f, boundsHeight: 1.2f, hasBounds: true,
            spanScale: 1.5f, recipeSizeScale: 1f);
        Assert.Equal(3.6f, span, 3);
    }

    [Fact]
    public void Span_respects_recipe_scale_and_min_cell_floor()
    {
        var span = VfxSpanMath.ComputeSpan(
            cellSpan: 0.1f, boundsWidth: 0f, boundsHeight: 0f, hasBounds: false,
            spanScale: 2f, recipeSizeScale: 0.5f);
        Assert.Equal(VfxSpanMath.MinSpan, span, 3);
    }

    [Fact]
    public void VfxTuning_v3_loads_span_scale_and_sort_offset()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "version": 3,
              "tint": { "maxStrength": 0.35 },
              "burst": { "coneHalfAngle": 0.6, "risingSideFactor": 0.4, "directionalSideFactor": 0.5 },
              "rules": {
                "burstCap": 24, "burstLifeSeconds": 0.55, "floaterRateLimitSeconds": 0.05,
                "burstRateLimitSeconds": 0.15, "globalCuePerTickCap": 32, "cueQueueCap": 256,
                "critFontScale": 1.25, "critPopStartScale": 1.5, "critPopSettleT": 0.3,
                "amountTierSmallBelow": 50, "amountTierBigFrom": 200,
                "amountTierSmallScale": 0.9, "amountTierBigScale": 1.15
              },
              "sustained": {
                "globalCap": 24, "perHostCap": 2, "ttlGraceSeconds": 2.0, "infiniteTtlSeconds": 60.0,
                "auraPulseSeconds": 0.3, "auraMaxParticles": 6, "spanScale": 1.5
              },
              "render": {
                "burstParticles": 28, "particleSortingOrder": 80, "sortOffsetAboveUnit": 2,
                "sustainedWorldYOffset": 0.25,
                "particleTextureSize": 64, "markerEdgeSoftness": 0.14,
                "markerGlowStrength": 0.45, "markerSizeScale": 0.24, "markerYOffsetScale": 0.12,
                "shieldBar": {
                  "barWorldWidth": 0.95, "barWorldHeight": 0.12, "worldYOffset": -0.35,
                  "maxSegments": 3, "cap": 32, "maxPips": 3
                },
                "tintReassertSeconds": 0.25
              },
              "identity": {
                "similarRgbDistanceThreshold": 45, "similarApplyRgbDistanceThreshold": 35
              }
            }
            """;
        var tuning = VfxTuningLoader.Parse(json);
        Assert.Equal(3, tuning.Version);
        Assert.Equal(1.5, tuning.Sustained.SpanScale);
        Assert.Equal(2, tuning.Render.SortOffsetAboveUnit);
        Assert.Equal(0.25, tuning.Render.SustainedWorldYOffset);
        Assert.Equal(0.12, tuning.Render.MarkerYOffsetScale);
    }

    [Fact]
    public void WorldY_cell_uses_boundsCenterY() =>
        Assert.Equal(4f, VfxUnitFrameMath.WorldY(2f, 4f, halfCell: 0.5f, boundsHalfHeight: 1f, hasBounds: false, VfxAnchorKind.Cell));

    [Fact]
    public void Span_applies_recipe_scale()
    {
        var span = VfxSpanMath.ComputeSpan(
            cellSpan: 1f, boundsWidth: 2f, boundsHeight: 2f, hasBounds: true,
            spanScale: 1f, recipeSizeScale: 0.85f);
        Assert.Equal(1.7f, span, 3);
    }

    [Fact]
    public void AnchorCatalog_covers_every_VfxAuraStyle()
    {
        var expected = new Dictionary<VfxAuraStyle, VfxAnchorKind>
        {
            [VfxAuraStyle.Drip] = VfxAnchorKind.Feet,
            [VfxAuraStyle.WispOut] = VfxAnchorKind.Feet,
            [VfxAuraStyle.BubbleRise] = VfxAnchorKind.Feet,
            [VfxAuraStyle.ChunkFall] = VfxAnchorKind.Feet,
            [VfxAuraStyle.StreamOut] = VfxAnchorKind.Feet,
            [VfxAuraStyle.PactFootPulse] = VfxAnchorKind.Feet,
            [VfxAuraStyle.Orbit] = VfxAnchorKind.Body,
            [VfxAuraStyle.CrackleJitter] = VfxAnchorKind.Body,
            [VfxAuraStyle.SparkStrobe] = VfxAnchorKind.Body,
            [VfxAuraStyle.ShardGlitter] = VfxAnchorKind.Body,
            [VfxAuraStyle.SporeDrift] = VfxAnchorKind.Body,
            [VfxAuraStyle.CharmHeartbeat] = VfxAnchorKind.Body,
            [VfxAuraStyle.PulseRing] = VfxAnchorKind.Body,
            [VfxAuraStyle.CommandCrownPulse] = VfxAnchorKind.Crown,
            [VfxAuraStyle.RiseSparkle] = VfxAnchorKind.Crown,
        };

        foreach (var style in Enum.GetValues<VfxAuraStyle>())
            Assert.Equal(expected[style], VfxAnchorCatalog.AnchorKindFor(style));
    }

    [Fact]
    public void Shipped_vfx_v3_json_parses()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "vfx.v3.json");
        Assert.True(File.Exists(path), "missing " + path);
        var tuning = VfxTuningLoader.Parse(File.ReadAllText(path));
        Assert.Equal(3, tuning.Version);
        Assert.Equal(1.5, tuning.Sustained.SpanScale);
        Assert.Equal(1, tuning.Render.SortOffsetAboveUnit);
        Assert.Equal(0.25, tuning.Render.SustainedWorldYOffset);
        Assert.Equal(0.12, tuning.Render.MarkerYOffsetScale);
        Assert.Equal(0.45, tuning.Render.MarkerGlowStrength);
    }

    [Fact]
    public void Status_aura_styles_have_anchor_kinds()
    {
        foreach (var id in StatusVfxIdentity.CustomIds)
        {
            var style = StatusVfxIdentity.Signature(id).AuraStyle;
            Assert.NotNull(style);
            Assert.True(Enum.IsDefined(typeof(VfxAnchorKind), VfxAnchorCatalog.AnchorKindFor(style.Value)));
        }
    }

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Directory.Build.props")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Pact_and_command_use_different_anchor_kinds()
    {
        Assert.Equal(VfxAnchorKind.Feet, VfxAnchorCatalog.AnchorKindFor(VfxAuraStyle.PactFootPulse));
        Assert.Equal(VfxAnchorKind.Crown, VfxAnchorCatalog.AnchorKindFor(VfxAuraStyle.CommandCrownPulse));
    }
}
