using FusionRpg.Core.Vfx;
using FusionRpg.Contracts;
using System.Text.Json;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

public class VfxImpactStampTuningTests
{
    [Fact]
    public void Shipped_vfx_v4_json_loads_required_impact_stamp_contract()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "vfx.v4.json");
        var tuning = VfxTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(4, tuning.Version);
        var stamp = Assert.IsType<VfxImpactStampTuning>(tuning.ImpactStamp);
        Assert.True(stamp.PoolCap > 0);
        Assert.True(stamp.LifeSeconds > 0);
        Assert.True(stamp.SpanScale > 0);
        Assert.InRange(stamp.StartAlpha, 0d, 1d);
        Assert.Equal(0d, stamp.EndAlpha);
    }

    [Fact]
    public void Missing_impact_stamp_tuning_is_rejected_without_a_fallback()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "vfx.v4.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var missingImpactStamp = document.RootElement.EnumerateObject()
            .Where(property => property.Name != "impactStamp")
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        var error = Assert.Throws<VfxTuningRejection>(() =>
            VfxTuningLoader.Parse(JsonSerializer.Serialize(missingImpactStamp)));

        Assert.Contains("impactStamp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shipped_vfx_v7_json_loads_required_Earth_phase_contract()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "vfx.v7.json");
        var tuning = VfxTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(7, tuning.Version);
        var phase = Assert.IsType<VfxEarthPhaseTuning>(tuning.EarthPhase);
        Assert.True(phase.PoolCap > 0);
        Assert.True(phase.ChargeLifeSeconds > 0d);
        Assert.True(phase.TravelLifeSeconds > 0d);
        Assert.True(phase.ChargeSpanScale > 0d);
        Assert.True(phase.TravelLengthScale > 0d);
        Assert.True(phase.TravelThicknessScale > 0d);
        Assert.True(phase.TravelDelaySeconds >= phase.ChargeLifeSeconds);
        var stamp = Assert.IsType<VfxImpactStampTuning>(tuning.ImpactStamp);
        Assert.True(stamp.DelaySeconds >= phase.TravelDelaySeconds + phase.TravelLifeSeconds);
    }

    [Fact]
    public void Missing_Earth_phase_tuning_is_rejected_without_a_fallback()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "vfx.v7.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var withoutEarthPhase = document.RootElement.EnumerateObject()
            .Where(property => property.Name != "earthPhase")
            .ToDictionary(property => property.Name, property => property.Value.Clone());

        var error = Assert.Throws<VfxTuningRejection>(() =>
            VfxTuningLoader.Parse(JsonSerializer.Serialize(withoutEarthPhase)));

        Assert.Contains("earthPhase", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Impact_stamp_numbers_declare_their_units_in_v4()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "vfx.v4.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var units = document.RootElement.GetProperty("_meta").GetProperty("impactStampUnits");

        Assert.Equal("instances", units.GetProperty("poolCap").GetString());
        Assert.Equal("seconds-unscaled", units.GetProperty("lifeSeconds").GetString());
        Assert.Equal("unit-frame-spans", units.GetProperty("spanScale").GetString());
        Assert.Equal("ratio-0-to-1", units.GetProperty("startAlpha").GetString());
        Assert.Equal("ratio-0-to-1", units.GetProperty("endAlpha").GetString());
        Assert.Equal("multiplier", units.GetProperty("startScale").GetString());
        Assert.Equal("multiplier", units.GetProperty("endScale").GetString());
        Assert.Equal("sorting-order-delta", units.GetProperty("sortOffset").GetString());
    }

    [Theory]
    [InlineData(false, 0f, 1f, 1, 0f)]
    [InlineData(true, 0.55f, 1f, 1, 0f)]
    [InlineData(true, 0f, 1f, 2, 0f)]
    public void Impact_stamp_recipe_rejects_presentation_literals(
        bool requireElement, float lifeSeconds, float sizeScale, int count, float delaySeconds)
    {
        var recipe = new VfxRecipe
        {
            CueId = VfxCueIds.CombatHit,
            Primitives = new[]
            {
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.ImpactStamp,
                    RequireElement = requireElement,
                    LifeSeconds = lifeSeconds,
                    SizeScale = sizeScale,
                    Count = count,
                    DelaySeconds = delaySeconds
                }
            }
        };

        Assert.Throws<ArgumentException>(() => VfxCatalog.Validate(recipe));
    }

    [Fact]
    public void Combat_hit_recipe_declares_an_element_gated_tuning_owned_stamp()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());

        Assert.True(catalog.TryGet(VfxCueIds.CombatHit, out var hit));
        var stamp = Assert.Single(hit.Primitives.Where(p => p.Kind == VfxPrimitiveKind.ImpactStamp));
        Assert.True(stamp.RequireElement);
        Assert.Equal(0f, stamp.LifeSeconds);
        Assert.Equal(1f, stamp.SizeScale);
        Assert.Equal(1, stamp.Count);
    }

    [Fact]
    public void Only_a_concrete_Earth_element_selects_the_Earth_stamp()
    {
        var earth = VfxColorPlan.For(null, new[] { new ElementPayloadComponentDto { Element = "earth", Weight = 1d } }, true, 1);
        var hybrid = VfxColorPlan.For(null, new[]
        {
            new ElementPayloadComponentDto { Element = "earth", Weight = 1d },
            new ElementPayloadComponentDto { Element = "fire", Weight = 1d }
        }, true, 1);
        var disabled = VfxColorPlan.For(null, new[] { new ElementPayloadComponentDto { Element = "earth", Weight = 1d } }, false, 1);

        Assert.True(earth.IsConcreteElement("earth"));
        Assert.False(hybrid.IsConcreteElement("earth"));
        Assert.False(disabled.IsConcreteElement("earth"));
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FusionRpg.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
