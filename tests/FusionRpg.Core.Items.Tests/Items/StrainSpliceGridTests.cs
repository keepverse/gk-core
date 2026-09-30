using System.Reflection;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// Item module 21 (`strain-splice-gen`), spec-strain-splice-gen.md, against the REAL shipped
/// <c>gk-core/data/tuning/strain-splice.v1.json</c>, <c>gk-core/data/tuning/sockets.v1.json</c>, the real 740-entry
/// base-type corpus and the real <c>build-themes.v1.json</c> registry — not synthetic fixtures.
///
/// ⭐ <b>The most important assertion in this file is a spec correction.</b> The spec measured the
/// shipped corpus on 2026-09-03 and concluded "the maximum <c>socketMax</c> anywhere is 2 … so no
/// Strain and no Splice is buildable on any shipped chassis", and prescribed a FAILING fixture that
/// would flip when module 6 re-issued the table. Module 6 re-issued it (committed 2026-09-04): the
/// corpus now ships <c>socketMax = 4</c> on <c>armament-primary</c> and <c>core-guard</c>, the two
/// roles ssot-sockets §4.1 assigns 4. <b>The fixture has flipped</b>, and it is written here in its
/// flipped form with the old state named, so the transition is a recorded fact rather than a test
/// nobody remembers deleting.
/// </summary>
public class StrainSpliceGridTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static SocketTuning Sockets() => SocketTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    static StrainSpliceTuning Shipped() => StrainSpliceTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice)),
        Sockets());

    /// <summary>The archetype axis, READ from module 13's registry — never declared in a test either.</summary>
    internal static IReadOnlyList<string> Archetypes()
    {
        var path = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_registry",
            "build-themes.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var seen = new List<string>();
        foreach (var theme in doc.RootElement.GetProperty("themes").EnumerateArray())
        {
            var value = theme.GetProperty("archetype").GetString()!;
            if (!seen.Contains(value)) seen.Add(value);
        }
        return seen;
    }

    // ── The grid ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_grid_yields_exactly_36_strains_and_66_splices()
    {
        var archetypes = Archetypes();
        Assert.Equal(3, archetypes.Count);
        // AptitudeCatalog.Count is pinned once, AptitudeCatalogTests (population-pin SE3.3,
        // 2026-09-19); `n` below captures it live and the formula exercises it.

        Assert.Equal(36, StrainSpliceGrid.Strains(archetypes).Count);
        Assert.Equal(66, StrainSpliceGrid.Splices().Count);
        Assert.Equal(102, StrainSpliceGrid.All(archetypes).Count);

        // Both the literal and the RE-DERIVATION, so a thirteenth aptitude grows the grid instead
        // of turning this test red for the wrong reason (module 16's resonance-count precedent).
        var n = AptitudeCatalog.Count;
        Assert.Equal(n * archetypes.Count + n * (n - 1) / 2,
            StrainSpliceGrid.ExpectedCount(archetypes));
        Assert.Equal(102, StrainSpliceGrid.ExpectedCount(archetypes));
    }

    [Fact]
    public void A_splice_pair_is_unordered_by_id_construction()
    {
        var might = AptitudeCatalog.Get("Might");
        var agility = AptitudeCatalog.Get("Agility");

        Assert.Equal("combo.splice-might-agility", StrainSpliceGrid.SpliceId(might, agility));
        Assert.Equal("combo.splice-might-agility", StrainSpliceGrid.SpliceId(agility, might));

        // C(12,2) yields ONE id per pair — proven by counting, not by trusting the loop shape.
        var ids = StrainSpliceGrid.Splices().Select(c => c.ComboId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        Assert.Throws<ArgumentException>(() => StrainSpliceGrid.SpliceId(might, might));
    }

    [Fact]
    public void Every_grid_id_is_a_legal_container_id_and_the_two_shapes_never_collide()
    {
        var all = StrainSpliceGrid.AllIds(Archetypes()).ToList();
        // The exact count is proven by formula in The_grid_yields_exactly_36_strains_and_66_splices
        // (ExpectedCount) -- never pinned twice (population-pin SE3.3, 2026-09-19); this only proves
        // non-vacuous before the per-id shape checks below.
        Assert.NotEmpty(all);

        foreach (var id in all)
        {
            Assert.Equal(1, id.Count(c => c == '.'));
            Assert.Matches("^[a-z][a-z0-9]*\\.[a-z0-9]+(-[a-z0-9]+)*$", id);
            Assert.StartsWith("combo.", id, StringComparison.Ordinal);
        }

        Assert.Equal(36, all.Count(i => i.StartsWith(StrainSpliceGrid.StrainPrefix, StringComparison.Ordinal)));
        Assert.Equal(66, all.Count(i => i.StartsWith(StrainSpliceGrid.SplicePrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void No_id_or_rule_contains_the_word_runeword()
    {
        // ⛔ D20's one hard vocabulary rule, asserted over the emitted corpus AND over this module's
        // own source text — a comment saying "runeword" would teach the next reader the wrong word.
        foreach (var id in StrainSpliceGrid.AllIds(Archetypes()))
            Assert.DoesNotContain("runeword", id, StringComparison.OrdinalIgnoreCase);

        var source = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Sockets");
        foreach (var file in Directory.GetFiles(source, "*.cs"))
            Assert.DoesNotContain("runeword", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_archetype_axis_is_injected_and_an_empty_or_repeated_one_is_refused()
    {
        Assert.Throws<ArgumentException>(() => StrainSpliceGrid.Strains(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() =>
            StrainSpliceGrid.Strains(new[] { "offense", "offense" }));
    }

    // ── The module-6 dependency, FLIPPED ────────────────────────────────────────────────────────

    [Fact]
    public void The_host_set_is_every_role_whose_ceiling_reaches_the_ingredient_count()
    {
        // strain-splice-host SSH5.1 (circuit-topology §5, R11): the tuning host set is DERIVED from
        // the per-role ceilings, never a pinned role list — no role is named here.
        var sockets = Sockets();
        var wanted = sockets.StrainSpliceIngredientCount;
        var derived = sockets.SocketCeiling
            .Where(kv => kv.Value >= wanted)
            .Select(kv => ItemRoles.Id(kv.Key))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
        var hostSet = SocketGeometry.RolesThatCanHostAStrain(sockets).Select(ItemRoles.Id)
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.Equal(derived, hostSet);
        Assert.NotEmpty(hostSet);
    }

    [Fact]
    public void The_corpus_host_set_equals_the_tuning_host_set_and_no_row_exceeds_its_ceiling()
    {
        // strain-splice-host SSH5.1 (circuit-topology §5) tightened by SSH5.12: two role-free
        // contracts over the real corpus. (2) the set of roles in which some base type reaches the
        // ingredient count EQUALS the tuning host set — every role the tuning offers a word in can
        // carry one on the shipped corpus, and the corpus names no role the tuning does not offer;
        // (3) no row's socketMax exceeds its own role's ceiling. SSH5.12's v2 re-stamp is what lifts
        // this from subset to equality, so the re-stamped corpus and this contract land together.
        var sockets = Sockets();
        var wanted = sockets.StrainSpliceIngredientCount;
        var hostSet = SocketGeometry.RolesThatCanHostAStrain(sockets)
            .Select(ItemRoles.Id).ToHashSet(StringComparer.Ordinal);
        var ceiling = sockets.SocketCeiling.ToDictionary(
            kv => ItemRoles.Id(kv.Key), kv => kv.Value, StringComparer.Ordinal);

        var total = 0;
        var corpusHosts = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types");
        foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("kind", out var kind) ||
                kind.GetString() != "base-type") continue;
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                total++;
                var role = entry.GetProperty("role").GetString()!;
                var max = entry.TryGetProperty("socketMax", out var sm) ? sm.GetInt32() : 0;
                if (ceiling.TryGetValue(role, out var capacity))
                    Assert.True(max <= capacity,
                        $"{Path.GetFileName(file)}: role '{role}' socketMax {max} exceeds its ceiling {capacity}");
                if (max >= wanted) corpusHosts.Add(role);
            }
        }

        Assert.True(total >= 740, $"expected at least 740 shipped base types, saw {total}");
        // Equality, not subset: a tuning host role the corpus never reaches is a role whose words can
        // never fire on a shipped chassis. Both sides sorted, so the diff names the missing roles.
        Assert.Equal(
            hostSet.OrderBy(r => r, StringComparer.Ordinal).ToList(),
            corpusHosts.OrderBy(r => r, StringComparer.Ordinal).ToList());
    }

    // ── The tuning file ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shipped_tuning_parses_and_carries_D20s_four_ingredient_plan()
    {
        var sockets = Sockets();
        var tuning = Shipped();

        Assert.Equal(4, sockets.StrainSpliceIngredientCount);
        Assert.Equal(sockets.StrainSpliceIngredientCount, tuning.MinTierPlan.Count);
        Assert.Equal(tuning.MinTierPlan.OrderBy(t => t), tuning.MinTierPlan);
        Assert.All(tuning.MinTierPlan, t => Assert.InRange(t, 1, sockets.InsertTierCount));

        Assert.Equal(1, tuning.BaseTierFor(ComboShape.Strain));
        Assert.Equal(1, tuning.BaseTierFor(ComboShape.Splice));
        Assert.Equal(45, tuning.CatalogueSizeBar);
    }

    [Fact]
    public void The_tuning_declares_none_of_module_16s_socket_numbers()
    {
        // ⛔ Two sources of truth for an ingredient count or an attunement bonus is how a generated
        // combination stops matching the evaluator that has to fire it. Asserted against the file's
        // own KEYS, not its prose — the ownership note names all six of them deliberately.
        var text = File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice));
        using var doc = JsonDocument.Parse(text);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
                foreach (var p in node.EnumerateObject()) { keys.Add(p.Name); Walk(p.Value); }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var v in node.EnumerateArray()) Walk(v);
        }
        Walk(doc.RootElement);

        foreach (var owned in new[]
                 {
                     "maxCombosPerActor", "attunedTierBonus", "ingredientCount",
                     "structuralCeiling", "socketCeiling", "attunedEffectiveCountBonus",
                 })
            Assert.DoesNotContain(owned, keys);
    }

    [Fact]
    public void The_parser_refuses_rather_than_defaults_section_by_section()
    {
        var sockets = Sockets();
        var full = File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice));

        foreach (var section in new[] { "recipe", "learnability", "distinctness" })
        {
            using var doc = JsonDocument.Parse(full);
            var stripped = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name != section) stripped[p.Name] = p.Value.Clone();
            var json = JsonSerializer.Serialize(stripped);
            Assert.Throws<InvalidOperationException>(() => StrainSpliceTuning.Parse(json, sockets));
        }
    }

    [Fact]
    public void A_min_tier_plan_that_disagrees_with_the_ingredient_count_throws_at_load()
    {
        var sockets = Sockets();
        // Three ingredients against D20's four — the exact drift that would author 102 recipes the
        // evaluator can never match, and it fails at LOAD rather than at the first socketed item.
        var json = """
        {"recipe": {"minTierPlan": [1, 1, 2], "baseTier": {"strain": 1, "splice": 1}},
         "learnability": {"catalogueSizeBar": 45},
         "distinctness": {"exactDuplicateNamesMax": 0, "nearDuplicateRateMaxPermille": 5}}
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => StrainSpliceTuning.Parse(json, sockets));
        Assert.Contains("ingredient count", ex.Message);
    }

    [Fact]
    public void A_base_tier_row_for_a_generated_resonance_shape_is_refused()
    {
        var sockets = Sockets();
        var json = """
        {"recipe": {"minTierPlan": [1, 1, 2, 2],
                    "baseTier": {"strain": 1, "splice": 1, "pure": 2}},
         "learnability": {"catalogueSizeBar": 45},
         "distinctness": {"exactDuplicateNamesMax": 0, "nearDuplicateRateMaxPermille": 5}}
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => StrainSpliceTuning.Parse(json, sockets));
        Assert.Contains("ResonanceGenerator", ex.Message);
    }

    [Fact]
    public void The_tuning_file_carries_no_content_ceiling()
    {
        // A cap on how many combinations may exist would be a hard progression ceiling on content
        // breadth. D17's dead tail, protected the way module 13 protects its own.
        var text = File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice));
        foreach (var forbidden in new[] { "maxCombinations", "maxStrains", "maxSplices", "gridCap" })
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);

        // …and the learnability bar says in its own note that it is reported, never enforced.
        Assert.Contains("REPORTED, NEVER ENFORCED", text, StringComparison.Ordinal);
    }

    // ── D22 as amended: affinity is a bonus, never a gate ───────────────────────────────────────

    [Fact]
    public void Matching_affinity_grants_an_enhanced_tier_and_never_gates()
    {
        var sockets = Sockets();
        var tuning = Shipped();

        var plain = tuning.GrantedTier(ComboShape.Strain, sockets, allAttuned: false);
        var attuned = tuning.GrantedTier(ComboShape.Strain, sockets, allAttuned: true);

        Assert.Equal(sockets.AttunedTierBonus, attuned - plain);
        // Failure must be IMPOSSIBLE: the unattuned arm still returns a real, positive tier.
        Assert.True(plain >= 1, "a mismatched fill still produces the combination");
    }

    [Fact]
    public void A_mismatched_affinity_still_produces_the_combination()
    {
        // The same rule from the abuse side, at the evaluator module 16 owns rather than restated:
        // a fill whose insert element does not match its socket affinity still fires.
        var sockets = Sockets();
        var recipe = StrainRecipe(sockets, "atom.might");
        var host = new SocketHost("item.h", ItemRole.ArmamentPrimary, "humanoid", 4);
        var fill = Enumerable.Range(0, 4)
            .Select(i => new SocketFill(i, "fire", new InsertDef($"gem.x{i}", "atom.might", "ice", 3)))
            .ToList();

        var fired = CombinationEvaluator.Evaluate(host, fill, new[] { recipe }, sockets);
        var strain = Assert.Single(fired, r => r.Shape == ComboShape.Strain);
        Assert.False(strain.AllAttuned);
        Assert.Equal(recipe.BaseTier, strain.GrantedTier);
    }

    [Fact]
    public void No_aptitude_to_element_mapping_is_introduced()
    {
        // ⚠ Nothing in the repo maps the twelve aptitudes to the six concrete elements, and
        // D22-as-amended needs none (RULED 2026-09-04: the bonus keys on the ingredient gem's own
        // element). Asserted structurally, so the gap stays VISIBLE rather than quietly filled.
        var grid = typeof(StrainSpliceGrid);
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items",
            "Sockets", "StrainSpliceGrid.cs"));

        foreach (var element in new[] { "\"fire\"", "\"ice\"", "\"earth\"", "\"air\"", "\"light\"",
                                        "\"dark\"", "ElementTypeId" })
            Assert.DoesNotContain(element, source, StringComparison.Ordinal);

        // Nothing on the type accepts or returns an element, either.
        foreach (var method in grid.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            Assert.DoesNotContain("Element", method.ReturnType.Name, StringComparison.Ordinal);
            foreach (var p in method.GetParameters())
                Assert.DoesNotContain("Element", p.ParameterType.Name, StringComparison.Ordinal);
        }
    }

    // ── D21 exclusivity, at module 21's angle ───────────────────────────────────────────────────

    [Fact]
    public void A_set_piece_may_not_carry_a_strain_or_splice()
    {
        var sockets = Sockets();
        var recipe = StrainRecipe(sockets, "atom.bulwark");
        var fill = Enumerable.Range(0, 4)
            .Select(i => new SocketFill(i, "", new InsertDef($"gem.b{i}", "atom.bulwark", "", 3)))
            .ToList();

        var plain = new SocketHost("item.plain", ItemRole.CoreGuard, "plant", 4);
        Assert.Single(CombinationEvaluator.Evaluate(plain, fill, new[] { recipe }, sockets),
            r => r.Shape == ComboShape.Strain);

        var setPiece = new SocketHost("item.setpiece", ItemRole.CoreGuard, "plant", 4, IsSetPiece: true);
        Assert.DoesNotContain(CombinationEvaluator.Evaluate(setPiece, fill, new[] { recipe }, sockets),
            r => ComboShapes.IsStrainOrSplice(r.Shape));
    }

    // ── The authored-content validator ──────────────────────────────────────────────────────────

    [Fact]
    public void A_recipe_off_the_grid_is_refused_by_name_and_mints_no_new_reason_code()
    {
        var sockets = Sockets();
        var archetypes = Archetypes();
        var bad = StrainRecipe(sockets, "atom.might") with { ComboId = "combo.strain-mighty-offence" };

        var problems = StrainSpliceGrid.ValidateRecipe(bad, sockets, archetypes, Shipped());
        var rejection = Assert.Single(problems);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, rejection.Reason);
        Assert.Contains(StrainSpliceRules.NotOnTheGrid, rejection.Detail + rejection.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_refusal_is_a_namespaced_content_rule_and_all_of_them_are_returned()
    {
        var sockets = Sockets();
        var tuning = Shipped();
        var bad = new ComboRecipe(
            ComboId: "combo.strain-nonsuch-offense",
            Shape: ComboShape.Strain,
            Element: "",
            Threshold: 0,
            HostRole: "jewel-major",             // ceiling 1 — cannot hold four
            HostFrame: "",
            MinSockets: 2,                       // not the derived 4
            BaseTier: 9,                         // UNREAD since SSH7.5: the tier is tuning's
            Ingredients: new[] { new ComboIngredient("atom.might") });   // one, not four

        var problems = StrainSpliceGrid.ValidateRecipe(bad, sockets, Archetypes(), tuning);
        Assert.Equal(4, problems.Count);
        Assert.All(problems, p => Assert.Equal(AtomRejectionReason.ContentRuleViolated, p.Reason));

        // SSH7.5: `BaseTier: 9` above is deliberately no longer a refusal — `CombinationCorpus`
        // fills the tier from tuning, so a recipe cannot declare one and the rule that compared it is
        // DELETED. Asserted as the absence, so a resurrected rule id is a failure rather than silence.
        var ruleIds = typeof(StrainSpliceRules)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.DoesNotContain(ruleIds, rule => rule.Contains("base-tier-not-tunable"));

        StrainSpliceRules.EnsureRegistered();
        Assert.Contains(StrainSpliceRules.Namespace, ContentRuleNamespaces.All);
    }

    [Fact]
    public void The_validator_never_fires_on_a_generated_resonance()
    {
        // The 25 are ResonanceGenerator's and are not cells of this grid. Asserted against the real
        // generated set, because a validator that refused them would break module 16's own seeding.
        var sockets = Sockets();
        var archetypes = Archetypes();
        foreach (var recipe in ResonanceGenerator.Generate(sockets))
            Assert.Empty(StrainSpliceGrid.ValidateRecipe(recipe, sockets, archetypes, Shipped()));
    }

    [Fact]
    public void A_well_formed_grid_recipe_passes_every_rule()
    {
        var sockets = Sockets();
        var good = StrainRecipe(sockets, "atom.might");
        Assert.Empty(StrainSpliceGrid.ValidateRecipe(good, sockets, Archetypes(), Shipped()));
    }

    // ── The learnability debt, measured ─────────────────────────────────────────────────────────

    [Fact]
    public void The_catalogue_size_is_127_against_the_45_bar_and_nothing_enforces_it()
    {
        var sockets = Sockets();
        var tuning = Shipped();
        var resonances = ResonanceGenerator.Generate(sockets).Count;
        var combinations = StrainSpliceGrid.ExpectedCount(Archetypes());

        Assert.Equal(25, resonances);
        Assert.Equal(102, combinations);
        Assert.Equal(127, resonances + combinations);
        Assert.True(resonances + combinations > tuning.CatalogueSizeBar,
            "ssot-sockets §4.4's ~45 bar is exceeded — module 20's compendium reveal and socket-UI " +
            "preview are REQUIREMENTS, not niceties (spec-strain-splice-gen.md)");

        // …and it is a report, not a gate: nothing here refuses the 102nd combination.
        Assert.Equal(102, StrainSpliceGrid.All(Archetypes()).Count);
    }

    // ── strain-splice-host SSH7.1: the tier ladder ──────────────────────────────────────────────

    static string ShippedJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning",
            FusionRpg.Core.Items.Sockets.SocketTuningFiles.StrainSplice));

    /// <summary>The shipped file with <c>recipe.tierLadder</c> replaced by <paramref name="ladderJson"/>.</summary>
    static StrainSpliceTuning WithLadder(string ladderJson, SocketTuning sockets)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(ShippedJson())!.AsObject();
        root["recipe"]!["tierLadder"] = System.Text.Json.Nodes.JsonNode.Parse(ladderJson);
        return StrainSpliceTuning.Parse(root.ToJsonString(), sockets);
    }

    const string TwoRungLadder = """
        [{"rung":1,"floors":[1,1,2,2],"grantDelta":0},{"rung":2,"floors":[1,1,2,3],"grantDelta":1}]
        """;

    [Fact]
    public void A_file_with_no_ladder_loads_as_the_one_rung_the_shipped_floor_describes()
    {
        // v1 has no `tierLadder`, and that is not a substituted default: rung 1 IS the flat floor the
        // shipped `minTierPlan` states, with no grant delta.
        var tuning = Shipped();
        var rung = Assert.Single(tuning.TierLadder);
        Assert.Equal(1, rung.Rung);
        Assert.Equal(tuning.MinTierPlan, rung.Floors);
        Assert.Equal(0, rung.GrantDelta);

        // …and a published ladder replaces it whole.
        Assert.Equal(2, WithLadder(TwoRungLadder, Sockets()).TierLadder.Count);
    }

    [Fact]
    public void A_non_ascending_or_non_increasing_ladder_throws()
    {
        var sockets = Sockets();

        var notAscending = Assert.Throws<InvalidOperationException>(() => WithLadder(
            """[{"rung":1,"floors":[1,1,3,2],"grantDelta":0},{"rung":2,"floors":[1,1,2,3],"grantDelta":1}]""",
            sockets));
        Assert.Contains("not ascending", notAscending.Message);

        var flatDelta = Assert.Throws<InvalidOperationException>(() => WithLadder(
            """[{"rung":1,"floors":[1,1,2,2],"grantDelta":0},{"rung":2,"floors":[1,1,2,3],"grantDelta":0}]""",
            sockets));
        Assert.Contains("does not rise", flatDelta.Message);

        var asksForLess = Assert.Throws<InvalidOperationException>(() => WithLadder(
            """[{"rung":1,"floors":[1,1,3,4],"grantDelta":0},{"rung":2,"floors":[1,1,2,4],"grantDelta":1}]""",
            sockets));
        Assert.Contains("never ask for less", asksForLess.Message);

        var outsideTheLadder = Assert.Throws<InvalidOperationException>(() => WithLadder(
            $$"""[{"rung":1,"floors":[1,1,2,2],"grantDelta":0},{"rung":2,"floors":[1,1,2,{{sockets.InsertTierCount + 1}}],"grantDelta":1}]""",
            sockets));
        Assert.Contains("outside the shipped insert ladder", outsideTheLadder.Message);
    }

    [Fact]
    public void A_ladder_whose_top_exceeds_the_atom_ladder_throws()
    {
        // The F7 bound read at the TOP of the ladder: a rung that grants past the atom catalog binds
        // no atom, and the load throws rather than clamping the rung down.
        var over = Assert.Throws<InvalidOperationException>(() => WithLadder(
            $$"""[{"rung":1,"floors":[1,1,2,2],"grantDelta":0},{"rung":2,"floors":[1,1,2,3],"grantDelta":{{FusionRpg.Core.Effects.Atoms.Generation.FamilyExpansion.TierCount}}}]""",
            Sockets()));
        Assert.Contains("bind no atom", over.Message);
        Assert.Contains("never clamp", over.Message);
    }

    [Fact]
    public void A_multi_rung_ladder_needs_measured_combination_pricing()
    {
        // SSH6.6's pure rule, read through the ladder's OWN rung count: three tiers of combination
        // bind at tiers nobody measured, and the fix is the report plus a publish — never a cap.
        var ladder = WithLadder(TwoRungLadder, Sockets());
        Assert.Equal(2, ladder.TierLadder.Count);

        var loaded = new ComboPricingLoadedRevisions(
            SocketsVersion: 2, StrainSpliceVersion: 1, MaterialsVersion: 4,
            CircuitSize: SocketLimits.SocketCircuitSize, CombinationCorpusDigest: new string('a', 64));
        var refusal = ComboPricingProvenance.Check(measuredAgainst: null, loaded, ladder.TierLadder.Count);

        Assert.NotNull(refusal);
        Assert.Equal(ComboPricingProvenanceRefusal.UnmeasuredRule, refusal!.Rule);
        Assert.Contains("2 rungs", refusal.Detail);

        // One rung is unaffected — the shipped state keeps loading with no measurement at all.
        Assert.Null(ComboPricingProvenance.Check(null, loaded, Shipped().TierLadder.Count));
    }

    [Fact]
    public void A_ladder_publish_invalidates_the_previous_measurement()
    {
        // Measured while the ladder was one rung; SSH7.7's publish makes it two in the same revision
        // that carries the ladder, so the recorded measurement can no longer bind.
        var measured = new ComboPricingMeasuredAgainst(
            SocketsVersion: 2, StrainSpliceVersion: 1, MaterialsVersion: 4,
            CircuitSize: SocketLimits.SocketCircuitSize, CombinationCorpusDigest: new string('a', 64));
        var loaded = new ComboPricingLoadedRevisions(
            SocketsVersion: 2, StrainSpliceVersion: 2, MaterialsVersion: 4,
            CircuitSize: SocketLimits.SocketCircuitSize, CombinationCorpusDigest: new string('a', 64));

        var refusal = ComboPricingProvenance.Check(
            measured, loaded, WithLadder(TwoRungLadder, Sockets()).TierLadder.Count);

        Assert.NotNull(refusal);
        Assert.Equal(ComboPricingProvenanceRefusal.StaleRule, refusal!.Rule);
        Assert.Contains("strainSpliceVersion measured 1, loaded 2", refusal.Detail);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    static ComboRecipe StrainRecipe(SocketTuning sockets, string family)
    {
        var plan = StrainSpliceTuning.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.StrainSplice)),
            sockets);
        var count = sockets.StrainSpliceIngredientCount;
        return new ComboRecipe(
            ComboId: StrainSpliceGrid.StrainId(AptitudeCatalog.Get("Might"), "offense"),
            Shape: ComboShape.Strain,
            Element: "",
            Threshold: count,
            HostRole: "",
            HostFrame: "",
            MinSockets: count,
            BaseTier: plan.BaseTierFor(ComboShape.Strain),
            Ingredients: new[] { new ComboIngredient(family, count) },
            // SSH7.8: the flat rung-1 floor is the recipe's own now that ComboIngredient carries no
            // tier — one t1 per required fill, the shape the importer fills from tuning.
            BaseFloors: Enumerable.Repeat(1, count).ToArray());
    }
}
