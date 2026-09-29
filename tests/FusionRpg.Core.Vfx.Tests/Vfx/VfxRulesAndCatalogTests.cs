using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Locks vfx-ssot.md §7 constants and §6 catalog validity.</summary>
public class VfxRulesAndCatalogTests
{
    [Fact]
    public void Rules_constants_match_ssot_locks()
    {
        Assert.Equal(64, VfxRules.FloaterCap);
        Assert.Equal(24, VfxRules.BurstCap);
        Assert.Equal(0.9f, VfxRules.FloaterLifeSeconds);
        Assert.Equal(0.55f, VfxRules.BurstLifeSeconds);
        Assert.Equal(56f, VfxRules.RisePixels);
        Assert.Equal(0.05f, VfxRules.FloaterRateLimitSeconds);
        Assert.Equal(0.15f, VfxRules.BurstRateLimitSeconds);
        Assert.Equal(32, VfxRules.GlobalCuePerTickCap);
        Assert.Equal(1.25f, VfxRules.CritFontScale);
    }

    [Fact]
    public void Seed_catalog_contains_v1_roster_and_validates()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());
        Assert.True(catalog.TryGet(VfxCueIds.CombatHit, out var hit));
        Assert.True(catalog.TryGet(VfxCueIds.CombatHeal, out var heal));
        Assert.True(catalog.TryGet(VfxCueIds.DebugProbe, out var probe));
        // 3 combat/debug cues + shield.broken + 4 Rift cues + 24 status.{id}.apply cues (21 + the 3
        // nerve.* tiers added 2026-09-20, combat-math-dedup D17, backlog-clean-up BCU8.1).
        // pin: closed-vocabulary VfxSeedCatalog.CreateAll — a new VFX cue is a reviewed change
        Assert.Equal(32, catalog.Ids.Count);

        // combat.hit = floater + burst + flash + Earth impact/charge/travel; heal = rising motes.
        Assert.Equal(6, hit.Primitives.Count);
        Assert.Equal(VfxPrimitiveKind.Floater, hit.Primitives[0].Kind);
        Assert.False(hit.Primitives[0].RequireElement); // plain damage always keeps its number
        Assert.Equal(VfxPrimitiveKind.Burst, hit.Primitives[1].Kind);
        Assert.Equal(VfxBurstShape.Radial, hit.Primitives[1].Shape);
        Assert.True(hit.Primitives[1].RequireElement);  // burst/flash are element-only (owner call)
        Assert.Equal(VfxPrimitiveKind.Flash, hit.Primitives[2].Kind);
        Assert.True(hit.Primitives[2].RequireElement);
        Assert.Equal(VfxPrimitiveKind.ImpactStamp, hit.Primitives[3].Kind);
        Assert.True(hit.Primitives[3].RequireElement);
        Assert.Equal(VfxColorSourceKind.TagOrElement, hit.Primitives[3].Color);
        Assert.Equal(0f, hit.Primitives[3].LifeSeconds);
        Assert.Equal(1f, hit.Primitives[3].SizeScale);
        Assert.Equal(VfxPrimitiveKind.Charge, hit.Primitives[4].Kind);
        Assert.True(hit.Primitives[4].RequireElement);
        Assert.Equal(VfxPrimitiveKind.Travel, hit.Primitives[5].Kind);
        Assert.True(hit.Primitives[5].RequireElement);
        Assert.Equal(2, heal.Primitives.Count);
        Assert.Equal(VfxPrimitiveKind.Floater, heal.Primitives[0].Kind);
        Assert.Equal(VfxPrimitiveKind.Burst, heal.Primitives[1].Kind);
        Assert.Equal(VfxBurstShape.Rising, heal.Primitives[1].Shape);
        Assert.Single(probe.Primitives);
        Assert.Equal(VfxColorSourceKind.Fixed, probe.Primitives[0].Color);
        Assert.Equal(VfxSeedCatalog.ProbeOrange, probe.Primitives[0].FixedRgb);
    }

    [Fact]
    public void Rift_recipes_keep_portal_and_quarantine_visual_grammar_distinct()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());

        Assert.True(catalog.TryGet(VfxCueIds.RiftPortalOpen, out var open));
        Assert.True(catalog.TryGet(VfxCueIds.RiftPortalSurge, out var surge));
        Assert.True(catalog.TryGet(VfxCueIds.RiftQuarantineSeal, out var seal));
        Assert.True(catalog.TryGet(VfxCueIds.RiftQuarantineFade, out var fade));
        Assert.Equal(VfxBurstShape.Radial, open.Primitives.Single(p => p.Kind == VfxPrimitiveKind.Burst).Shape);
        Assert.Equal(VfxBurstShape.Directional, surge.Primitives.Single(p => p.Kind == VfxPrimitiveKind.Burst).Shape);
        Assert.Equal(VfxBurstShape.Directional, seal.Primitives.Single(p => p.Kind == VfxPrimitiveKind.Burst).Shape);
        Assert.Equal(VfxBurstShape.Rising, fade.Primitives.Single(p => p.Kind == VfxPrimitiveKind.Burst).Shape);
        Assert.NotEqual(open.Primitives.Single(p => p.Kind == VfxPrimitiveKind.Burst).FixedRgb,
            surge.Primitives.Single(p => p.Kind == VfxPrimitiveKind.Burst).FixedRgb);
        Assert.NotEqual(surge.Primitives.Single(p => p.Kind == VfxPrimitiveKind.Burst).FixedRgb,
            seal.Primitives.Single(p => p.Kind == VfxPrimitiveKind.Burst).FixedRgb);
    }

    [Fact]
    public void Catalog_lookup_is_case_insensitive_and_misses_cleanly()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());
        Assert.True(catalog.TryGet("COMBAT.HIT", out _));
        Assert.False(catalog.TryGet("status.unknown.apply", out _));
        Assert.False(catalog.TryGet("", out _));
        Assert.False(catalog.TryGet(null, out _));
    }

    [Fact]
    public void Validate_rejects_broken_recipes()
    {
        Assert.Throws<ArgumentException>(() => VfxCatalog.Validate(new VfxRecipe { CueId = "" }));
        Assert.Throws<ArgumentException>(() => VfxCatalog.Validate(new VfxRecipe { CueId = "x" }));
        Assert.Throws<ArgumentException>(() => VfxCatalog.Validate(new VfxRecipe
        {
            CueId = "x",
            Primitives = new[] { new VfxPrimitiveSpec { LifeSeconds = 0f } }
        }));
        Assert.Throws<ArgumentException>(() => VfxCatalog.Validate(new VfxRecipe
        {
            CueId = "x",
            Primitives = new[] { new VfxPrimitiveSpec { LifeSeconds = 1f, Count = 0 } }
        }));
        Assert.Throws<ArgumentException>(() => VfxCatalog.Validate(new VfxRecipe
        {
            CueId = "x",
            Primitives = new[] { new VfxPrimitiveSpec { LifeSeconds = 1f, Count = 100 } }
        }));
    }
}
