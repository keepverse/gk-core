using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Static identity audit — no exact duplicate signatures; cluster color-only pairs documented.</summary>
public class StatusVfxIdentityCollisionTests
{
    [Fact]
    public void All_thirteen_custom_statuses_have_signatures()
    {
        // pin: closed-vocabulary StatusVfxIdentity.CustomIds — a new custom status VFX identity is a reviewed change
        Assert.Equal(13, StatusVfxIdentity.CustomIds.Count);
        foreach (var id in StatusVfxIdentity.CustomIds)
        {
            var sig = StatusVfxIdentity.Signature(id);
            Assert.Equal(id, sig.StatusId);
            Assert.NotNull(sig.AuraStyle);
        }
    }

    [Fact]
    public void No_two_custom_statuses_share_an_identical_full_signature()
    {
        var sigs = StatusVfxIdentity.AllCustomSignatures();
        var keys = sigs.Select(s => s.FullKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Motion_grammar_clusters_match_expected_high_risk_groups()
    {
        var grammar = StatusVfxIdentity.MotionGrammarPairs()
            .Select(p => (p.A, p.B))
            .OrderBy(x => x.A, StringComparer.Ordinal)
            .ThenBy(x => x.B, StringComparer.Ordinal)
            .ToList();

        // PulseRing×2 = 0 (batch-5 split pulsering cluster into unique styles).
        Assert.Empty(grammar);
    }

    [Fact]
    public void Batch5_pulsering_pair_has_no_shared_motion_grammar()
    {
        var grammar = StatusVfxIdentity.MotionGrammarPairs();
        Assert.DoesNotContain(grammar, p =>
            (p.A == "pact_mark" && p.B == "command") || (p.A == "command" && p.B == "pact_mark"));
    }

    [Fact]
    public void Batch3_orbit_trio_has_no_shared_motion_grammar()
    {
        var grammar = StatusVfxIdentity.MotionGrammarPairs();
        foreach (var a in new[] { "spore", "bond", "charm_pulse" })
        {
            foreach (var b in new[] { "spore", "bond", "charm_pulse" })
            {
                if (string.CompareOrdinal(a, b) >= 0) continue;
                Assert.DoesNotContain(grammar, p =>
                    (p.A == a && p.B == b) || (p.A == b && p.B == a));
            }
        }
    }

    [Fact]
    public void Batch2_crackle_trio_has_no_shared_motion_grammar()
    {
        var grammar = StatusVfxIdentity.MotionGrammarPairs();
        foreach (var a in new[] { "spark", "expose", "shatter" })
        {
            foreach (var b in new[] { "spark", "expose", "shatter" })
            {
                if (string.CompareOrdinal(a, b) >= 0) continue;
                Assert.DoesNotContain(grammar, p =>
                    (p.A == a && p.B == b) || (p.A == b && p.B == a));
            }
        }
    }

    [Fact]
    public void Batch1_drip_trio_has_no_shared_motion_grammar()
    {
        var grammar = StatusVfxIdentity.MotionGrammarPairs();
        foreach (var a in new[] { "wither", "blight", "rot" })
        {
            foreach (var b in new[] { "wither", "blight", "rot" })
            {
                if (string.CompareOrdinal(a, b) >= 0) continue;
                Assert.DoesNotContain(grammar, p =>
                    (p.A == a && p.B == b) || (p.A == b && p.B == a));
            }
        }
    }

    [Fact]
    public void Structural_color_only_pairs_are_empty_after_identity_batches()
    {
        var structural = StatusVfxIdentity.FindCollisions()
            .Where(p => p.Kind == "structural-color-only")
            .ToList();

        Assert.Empty(structural);
    }

    [Fact]
    public void Similar_apply_color_excludes_batch2_shape_differentiated_pairs()
    {
        var similar = StatusVfxIdentity.FindCollisions()
            .Where(p => p.Kind == "similar-apply-color")
            .ToList();

        Assert.DoesNotContain(similar, p =>
            (p.A == "spark" && p.B == "expose") || (p.A == "expose" && p.B == "spark"));
        Assert.Equal(35, StatusVfxIdentity.RgbDistance(
            StatusVfxIdentity.Signature("spark").ApplyRgb,
            StatusVfxIdentity.Signature("expose").ApplyRgb));
        Assert.NotEqual(
            StatusVfxIdentity.Signature("spark").ApplyBurstKey,
            StatusVfxIdentity.Signature("expose").ApplyBurstKey);
        Assert.NotEqual(
            StatusVfxIdentity.Signature("spark").ApplyBurstKey,
            StatusVfxIdentity.Signature("rally").ApplyBurstKey);
        Assert.DoesNotContain(similar, p =>
            (p.A == "spark" && p.B == "rally") || (p.A == "rally" && p.B == "spark"));
    }

    [Fact]
    public void Similar_apply_color_excludes_batch3_shape_differentiated_pairs()
    {
        var similar = StatusVfxIdentity.FindCollisions()
            .Where(p => p.Kind == "similar-apply-color")
            .ToList();

        Assert.DoesNotContain(similar, p =>
            (p.A == "charm_pulse" && p.B == "pact_mark") || (p.A == "pact_mark" && p.B == "charm_pulse"));
        Assert.DoesNotContain(similar, p =>
            (p.A == "bond" && p.B == "charm_pulse") || (p.A == "charm_pulse" && p.B == "bond"));
        Assert.NotEqual(
            StatusVfxIdentity.Signature("charm_pulse").ApplyBurstKey,
            StatusVfxIdentity.Signature("pact_mark").ApplyBurstKey);
        Assert.NotEqual(
            StatusVfxIdentity.Signature("bond").ApplyBurstKey,
            StatusVfxIdentity.Signature("charm_pulse").ApplyBurstKey);
    }

    [Fact]
    public void Similar_apply_color_excludes_batch4_shape_differentiated_pairs()
    {
        var similar = StatusVfxIdentity.FindCollisions()
            .Where(p => p.Kind == "similar-apply-color")
            .ToList();

        foreach (var (a, b) in new[] { ("leech", "shatter"), ("command", "wither"), ("rally", "blight"), ("bond", "leech") })
        {
            Assert.DoesNotContain(similar, p =>
                (p.A == a && p.B == b) || (p.A == b && p.B == a));
            Assert.NotEqual(
                StatusVfxIdentity.Signature(a).ApplyBurstKey,
                StatusVfxIdentity.Signature(b).ApplyBurstKey);
        }
    }

    [Fact]
    public void Similar_apply_color_collisions_are_empty()
    {
        var similar = StatusVfxIdentity.FindCollisions()
            .Where(p => p.Kind == "similar-apply-color")
            .ToList();

        Assert.Empty(similar);
    }

    [Fact]
    public void Custom_signatures_use_expected_aura_styles()
    {
        Assert.Equal(VfxAuraStyle.SporeDrift, StatusVfxIdentity.Signature("spore").AuraStyle);
        Assert.Equal(VfxAuraStyle.CharmHeartbeat, StatusVfxIdentity.Signature("charm_pulse").AuraStyle);
        Assert.Equal(VfxAuraStyle.Orbit, StatusVfxIdentity.Signature("bond").AuraStyle);
        Assert.Equal(VfxAuraStyle.PactFootPulse, StatusVfxIdentity.Signature("pact_mark").AuraStyle);
        Assert.Equal(VfxAuraStyle.CommandCrownPulse, StatusVfxIdentity.Signature("command").AuraStyle);
    }

    [Fact]
    public void Batch3_apply_burst_keys_are_pairwise_distinct()
    {
        const string defaultKey = StatusVfxIdentity.DefaultApplyBurstKey;
        var spore = StatusVfxIdentity.Signature("spore").ApplyBurstKey;
        var charm = StatusVfxIdentity.Signature("charm_pulse").ApplyBurstKey;
        var bond = StatusVfxIdentity.Signature("bond").ApplyBurstKey;

        Assert.NotEqual(spore, charm);
        Assert.NotEqual(spore, bond);
        Assert.NotEqual(charm, bond);
        Assert.NotEqual(defaultKey, spore);
        Assert.NotEqual(defaultKey, charm);
        Assert.NotEqual(defaultKey, bond);
    }

    [Fact]
    public void Batch4_apply_burst_keys_are_pairwise_distinct()
    {
        const string defaultKey = StatusVfxIdentity.DefaultApplyBurstKey;
        var leech = StatusVfxIdentity.Signature("leech").ApplyBurstKey;
        var rally = StatusVfxIdentity.Signature("rally").ApplyBurstKey;
        var pact = StatusVfxIdentity.Signature("pact_mark").ApplyBurstKey;
        var command = StatusVfxIdentity.Signature("command").ApplyBurstKey;

        Assert.NotEqual(leech, rally);
        Assert.NotEqual(leech, pact);
        Assert.NotEqual(leech, command);
        Assert.NotEqual(rally, pact);
        Assert.NotEqual(rally, command);
        Assert.NotEqual(pact, command);
        Assert.All(new[] { leech, rally, pact, command }, k => Assert.NotEqual(defaultKey, k));
    }

    [Fact]
    public void Marker_bearing_statuses_match_SPEC_grammar_set()
    {
        var withMarker = StatusVfxIdentity.AllCustomSignatures()
            .Where(s => s.MarkerShape.HasValue)
            .Select(s => s.StatusId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "bond", "command", "expose", "pact_mark" }, withMarker);
    }

    [Fact]
    public void Singles_use_unique_motion_grammar()
    {
        var clusters = StatusVfxIdentity.ClusterByAuraStyle();
        Assert.Single(clusters["StreamOut"]);
        Assert.Equal("leech", clusters["StreamOut"][0]);
        Assert.Single(clusters["RiseSparkle"]);
        Assert.Equal("rally", clusters["RiseSparkle"][0]);
        Assert.Single(clusters["WispOut"]);
        Assert.Equal("wither", clusters["WispOut"][0]);
        Assert.Single(clusters["BubbleRise"]);
        Assert.Equal("blight", clusters["BubbleRise"][0]);
        Assert.Single(clusters["ChunkFall"]);
        Assert.Equal("rot", clusters["ChunkFall"][0]);
        Assert.Single(clusters["SparkStrobe"]);
        Assert.Equal("spark", clusters["SparkStrobe"][0]);
        Assert.Single(clusters["ShardGlitter"]);
        Assert.Equal("shatter", clusters["ShardGlitter"][0]);
        Assert.Single(clusters["CrackleJitter"]);
        Assert.Equal("expose", clusters["CrackleJitter"][0]);
        Assert.Single(clusters["SporeDrift"]);
        Assert.Equal("spore", clusters["SporeDrift"][0]);
        Assert.Single(clusters["CharmHeartbeat"]);
        Assert.Equal("charm_pulse", clusters["CharmHeartbeat"][0]);
        Assert.Single(clusters["Orbit"]);
        Assert.Equal("bond", clusters["Orbit"][0]);
        Assert.Single(clusters["PactFootPulse"]);
        Assert.Equal("pact_mark", clusters["PactFootPulse"][0]);
        Assert.Single(clusters["CommandCrownPulse"]);
        Assert.Equal("command", clusters["CommandCrownPulse"][0]);
        Assert.False(clusters.ContainsKey("PulseRing") && clusters["PulseRing"].Count > 0);
    }

    [Fact]
    public void Batch1_apply_bursts_differ_by_shape_and_count()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());

        static VfxPrimitiveSpec Burst(VfxCatalog c, string id)
        {
            Assert.True(c.TryGet(StatusVfxCues.CueId(id), out var recipe), id);
            return recipe.Primitives.First(p => p.Kind == VfxPrimitiveKind.Burst);
        }

        var wither = Burst(catalog, "wither");
        var blight = Burst(catalog, "blight");
        var rot = Burst(catalog, "rot");
        var spark = Burst(catalog, "spark");

        // pin: closed-vocabulary VfxCatalog.wither — a design edit to the recipe is a reviewed change
        Assert.Equal(10, wither.Count);
        Assert.Equal(VfxBurstShape.Radial, wither.Shape);
        // pin: closed-vocabulary VfxCatalog.blight — a design edit to the recipe is a reviewed change
        Assert.Equal(12, blight.Count);
        Assert.Equal(VfxBurstShape.Rising, blight.Shape);
        // pin: closed-vocabulary VfxCatalog.rot — a design edit to the recipe is a reviewed change
        Assert.Equal(8, rot.Count);
        Assert.Equal(1.35f, rot.SizeScale);
    }

    [Fact]
    public void Batch2_apply_bursts_differ_by_shape_and_count()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());

        static VfxPrimitiveSpec Burst(VfxCatalog c, string id)
        {
            Assert.True(c.TryGet(StatusVfxCues.CueId(id), out var recipe), id);
            return recipe.Primitives.First(p => p.Kind == VfxPrimitiveKind.Burst);
        }

        var spark = Burst(catalog, "spark");
        var shatter = Burst(catalog, "shatter");
        var expose = Burst(catalog, "expose");

        // pin: closed-vocabulary VfxCatalog.spark — a design edit to the recipe is a reviewed change
        Assert.Equal(16, spark.Count);
        Assert.Equal(0.30f, spark.LifeSeconds);
        Assert.Equal(VfxBurstShape.Radial, spark.Shape);
        // pin: closed-vocabulary VfxCatalog.shatter — a design edit to the recipe is a reviewed change
        Assert.Equal(10, shatter.Count);
        Assert.Equal(VfxBurstShape.Directional, shatter.Shape);
        Assert.Equal(1.25f, shatter.SizeScale);
        // pin: closed-vocabulary VfxCatalog.expose — a design edit to the recipe is a reviewed change
        Assert.Equal(10, expose.Count);
        Assert.Equal(VfxBurstShape.Rising, expose.Shape);
        Assert.Equal(0.40f, expose.LifeSeconds);

        Assert.True(catalog.TryGet(StatusVfxCues.CueId("expose"), out var exposeRecipe));
        var exposeAura = exposeRecipe!.Primitives.First(p => p.Kind == VfxPrimitiveKind.Aura);
        Assert.Equal(0.85f, exposeAura.SizeScale);
    }

    [Fact]
    public void Batch3_apply_bursts_differ_by_shape_and_count()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());

        static VfxPrimitiveSpec Burst(VfxCatalog c, string id)
        {
            Assert.True(c.TryGet(StatusVfxCues.CueId(id), out var recipe), id);
            return recipe.Primitives.First(p => p.Kind == VfxPrimitiveKind.Burst);
        }

        var spore = Burst(catalog, "spore");
        var charm = Burst(catalog, "charm_pulse");
        var bond = Burst(catalog, "bond");

        // pin: closed-vocabulary VfxCatalog.spore — a design edit to the recipe is a reviewed change
        Assert.Equal(12, spore.Count);
        Assert.Equal(VfxBurstShape.Rising, spore.Shape);
        Assert.Equal(0.45f, spore.LifeSeconds);
        // pin: closed-vocabulary VfxCatalog.charm_pulse — a design edit to the recipe is a reviewed change
        Assert.Equal(14, charm.Count);
        Assert.Equal(VfxBurstShape.Radial, charm.Shape);
        Assert.Equal(0.35f, charm.LifeSeconds);
        Assert.Equal(0.9f, charm.SizeScale);
        // pin: closed-vocabulary VfxCatalog.bond — a design edit to the recipe is a reviewed change
        Assert.Equal(10, bond.Count);
        Assert.Equal(0.40f, bond.LifeSeconds);

        Assert.True(catalog.TryGet(StatusVfxCues.CueId("spore"), out var sporeRecipe));
        var sporeAura = sporeRecipe!.Primitives.First(p => p.Kind == VfxPrimitiveKind.Aura);
        Assert.Equal(1.15f, sporeAura.SizeScale);
    }

    [Fact]
    public void Batch4_apply_bursts_differ_by_shape_and_count()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());

        static VfxPrimitiveSpec Burst(VfxCatalog c, string id)
        {
            Assert.True(c.TryGet(StatusVfxCues.CueId(id), out var recipe), id);
            return recipe.Primitives.First(p => p.Kind == VfxPrimitiveKind.Burst);
        }

        var leech = Burst(catalog, "leech");
        var rally = Burst(catalog, "rally");
        var pact = Burst(catalog, "pact_mark");
        var command = Burst(catalog, "command");

        // pin: closed-vocabulary VfxCatalog.leech — a design edit to the recipe is a reviewed change
        Assert.Equal(10, leech.Count);
        Assert.Equal(VfxBurstShape.Directional, leech.Shape);
        Assert.Equal(0.40f, leech.LifeSeconds);
        // pin: closed-vocabulary VfxCatalog.rally — a design edit to the recipe is a reviewed change
        Assert.Equal(13, rally.Count);
        Assert.Equal(VfxBurstShape.Rising, rally.Shape);
        Assert.Equal(0.50f, rally.LifeSeconds);
        Assert.Equal(1.05f, rally.SizeScale);
        // pin: closed-vocabulary VfxCatalog.pact_mark — a design edit to the recipe is a reviewed change
        Assert.Equal(12, pact.Count);
        Assert.Equal(VfxBurstShape.Radial, pact.Shape);
        Assert.Equal(0.30f, pact.LifeSeconds);
        Assert.Equal(1.1f, pact.SizeScale);
        // pin: closed-vocabulary VfxCatalog.command — a design edit to the recipe is a reviewed change
        Assert.Equal(10, command.Count);
        Assert.Equal(0.35f, command.LifeSeconds);
        Assert.Equal(0.95f, command.SizeScale);

        Assert.True(catalog.TryGet(StatusVfxCues.CueId("pact_mark"), out var pactRecipe));
        var pactAura = pactRecipe!.Primitives.First(p => p.Kind == VfxPrimitiveKind.Aura);
        Assert.Equal(0.9f, pactAura.SizeScale);
        Assert.True(catalog.TryGet(StatusVfxCues.CueId("command"), out var commandRecipe));
        var commandAura = commandRecipe!.Primitives.First(p => p.Kind == VfxPrimitiveKind.Aura);
        Assert.Equal(1.05f, commandAura.SizeScale);
    }

    [Fact]
    public void All_custom_statuses_have_distinct_apply_burst_keys()
    {
        var keys = StatusVfxIdentity.AllCustomSignatures()
            .Select(s => s.ApplyBurstKey)
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.NotEqual(StatusVfxIdentity.DefaultApplyBurstKey, k));
    }

    [Fact]
    public void Engine_wrapped_statuses_use_default_apply_burst()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());

        foreach (var id in new[] { "butter", "freeze", "cold", "poison" })
        {
            Assert.True(catalog.TryGet(StatusVfxCues.CueId(id), out var recipe), id);
            var burst = recipe!.Primitives.First(p => p.Kind == VfxPrimitiveKind.Burst);
            var flash = recipe.Primitives.First(p => p.Kind == VfxPrimitiveKind.Flash);
            // pin: closed-vocabulary VfxCatalog.StatusApplyBurst — the shared default-arm burst spec; a design edit is a reviewed change
            Assert.Equal(14, burst.Count);
            Assert.Equal(0.45f, burst.LifeSeconds);
            Assert.Equal(VfxBurstShape.Radial, burst.Shape);
            Assert.Equal(1f, burst.SizeScale);
            Assert.Equal(0.18f, flash.LifeSeconds);
            Assert.Equal(StatusVfxIdentity.DefaultApplyBurstKey, StatusVfxIdentity.FormatApplyBurstKey(burst));
        }
    }
}
