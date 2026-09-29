using FusionRpg.Contracts;
using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Locks SPEC W3: crit pop curve, amount tiers, and the combined floater font scale.</summary>
public class VfxFloaterCurveTests
{
    static List<ElementPayloadComponentDto> Fire() =>
        new() { new ElementPayloadComponentDto { Element = "fire", Weight = 1.0 } };

    [Fact]
    public void Pop_scale_overshoots_then_settles()
    {
        Assert.Equal(1.5f, VfxRules.PopScale(0f));
        Assert.Equal(VfxRules.CritFontScale, VfxRules.PopScale(VfxRules.CritPopSettleT));
        Assert.Equal(VfxRules.CritFontScale, VfxRules.PopScale(1f));
        var mid = VfxRules.PopScale(VfxRules.CritPopSettleT / 2f);
        Assert.True(mid < 1.5f && mid > VfxRules.CritFontScale);
        // monotone non-increasing across the settle window
        var prev = float.MaxValue;
        for (var t = 0f; t <= 1f; t += 0.05f)
        {
            var s = VfxRules.PopScale(t);
            Assert.True(s <= prev + 1e-5f);
            prev = s;
        }
    }

    [Fact]
    public void Amount_tiers_are_locked()
    {
        Assert.Equal(0.9f, VfxRules.AmountScale(0));
        Assert.Equal(0.9f, VfxRules.AmountScale(-49));
        Assert.Equal(1.0f, VfxRules.AmountScale(-50));
        Assert.Equal(1.0f, VfxRules.AmountScale(199));
        Assert.Equal(1.15f, VfxRules.AmountScale(-200));
        Assert.Equal(1.15f, VfxRules.AmountScale(9999));
    }

    [Fact]
    public void Crit_pops_with_and_without_element_payload()
    {
        var withElement = VfxColorPlan.For(DamageFxTag.Crit, Fire(), elementFxOn: true, amount: -100);
        var noPayload = VfxColorPlan.For(DamageFxTag.Crit, null, elementFxOn: true, amount: -100);
        foreach (var plan in new[] { withElement, noPayload })
        {
            Assert.Equal(1.5f, plan.FontScaleAt(0f));
            Assert.Equal(VfxRules.CritFontScale, plan.FontScaleAt(1f));
        }
    }

    [Fact]
    public void Plain_damage_scales_by_amount_only()
    {
        var small = VfxColorPlan.For(DamageFxTag.Neutral, null, true, amount: -10);
        var big = VfxColorPlan.For(DamageFxTag.Neutral, Fire(), true, amount: -500);
        Assert.Equal(0.9f, small.FontScaleAt(0f));
        Assert.Equal(0.9f, small.FontScaleAt(0.9f));
        Assert.Equal(1.15f, big.FontScaleAt(0.5f));
    }

    [Fact]
    public void Semantic_labels_never_scale()
    {
        foreach (var tag in new[] { DamageFxTag.Dodge, DamageFxTag.Block, DamageFxTag.Heal })
        {
            var plan = VfxColorPlan.For(tag, Fire(), true, amount: 500);
            Assert.Equal(1f, plan.FontScaleAt(0f));
            Assert.Equal(1f, plan.FontScaleAt(1f));
        }
    }

    [Fact]
    public void Crit_amount_tier_and_pop_multiply()
    {
        var plan = VfxColorPlan.For(DamageFxTag.Crit, Fire(), true, amount: -500);
        Assert.Equal(1.5f * 1.15f, plan.FontScaleAt(0f), 3);
        Assert.Equal(VfxRules.CritFontScale * 1.15f, plan.FontScaleAt(1f), 3);
    }
}
