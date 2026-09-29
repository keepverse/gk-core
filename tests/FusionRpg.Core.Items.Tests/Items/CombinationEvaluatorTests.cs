using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// spec-sockets.md §8/§9 — the combination evaluator, against the real shipped tuning and the real
/// generated resonance catalog.
/// </summary>
public class CombinationEvaluatorTests
{
    static SocketTuning Tuning => SocketGeometryTests.Shipped();
    static IReadOnlyList<ComboRecipe> Resonances => ResonanceGenerator.Generate(Tuning);

    static InsertDef Gem(string element, int tier = 3, string family = "atom.elemental-power") =>
        new($"gem.{(element.Length == 0 ? "plain" : element)}-shard.t{tier}", family, element, tier);

    static SocketFill Fill(int index, string affinity, InsertDef insert) => new(index, affinity, insert);

    // strain-splice-host SSH1.3: Capacity defaults to `sockets` -- a fully-bored chassis, the same
    // shape every caller of this helper already meant before Capacity existed as its own axis.
    static SocketHost Host(int sockets = 4, ItemRole role = ItemRole.ArmamentPrimary, bool setPiece = false) =>
        new("item.test", role, "plant", sockets, setPiece, Capacity: sockets);

    // ── The generated 25 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_generator_produces_exactly_twenty_five_resonances_and_re_derives_its_own_count()
    {
        var tuning = Tuning;
        var rows = ResonanceGenerator.Generate(tuning);

        var expected = ElementRoster.Concrete.Count * tuning.PureThresholds.Count
                       + tuning.RingOrder.Count
                       + 1
                       + tuning.DiversityThresholds.Count;

        // The count is fully implied by the formula above -- never pinned twice (population-pin SE3.3,
        // 2026-09-19).
        Assert.Equal(expected, rows.Count);
        Assert.Equal(18, rows.Count(r => r.Shape == ComboShape.Pure));
        Assert.Equal(4, rows.Count(r => r.Shape == ComboShape.Ring));
        Assert.Equal(1, rows.Count(r => r.Shape == ComboShape.Eclipse));
        Assert.Equal(2, rows.Count(r => r.Shape == ComboShape.Diversity));
    }

    [Fact]
    public void D27_renamed_every_combination_and_no_gem_combo_id_survives()
    {
        foreach (var r in Resonances)
        {
            Assert.StartsWith("combo.", r.ComboId, StringComparison.Ordinal);
            Assert.DoesNotContain("gem.", r.ComboId, StringComparison.Ordinal);
        }

        Assert.Contains(Resonances, r => r.ComboId == "combo.pure-fire-3");
        Assert.Contains(Resonances, r => r.ComboId == "combo.ring-fire-ice");
        Assert.Contains(Resonances, r => r.ComboId == "combo.eclipse");
        Assert.Contains(Resonances, r => r.ComboId == "combo.diversity-3");
    }

    [Fact]
    public void A_generated_resonance_names_no_ingredient_family_so_ssot_6_4_cannot_be_violated()
    {
        // "A resonance container may not repeat a family its triggering inserts carry" is enforced in
        // the generator by construction — there is no family to repeat.
        Assert.All(Resonances, r => Assert.Empty(r.Ingredients));
    }

    // ── Purity and determinism ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_is_pure_and_consumes_no_rng()
    {
        var fill = new[] { Fill(0, "fire", Gem("fire")), Fill(1, "", Gem("ice")), Fill(2, "earth", Gem("earth")) };
        var first = CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning);

        for (var i = 0; i < 20; i++)
            Assert.Equal(
                first.Select(r => r.ComboId + ":" + r.EffectiveCount + ":" + r.GrantedTier),
                CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning)
                    .Select(r => r.ComboId + ":" + r.EffectiveCount + ":" + r.GrantedTier));
    }

    [Fact]
    public void The_same_inserts_in_any_arrangement_resolve_to_the_same_combination()
    {
        // ✅ D41 — recipes are UNORDERED. This is the spec's own named test.
        var a = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("ice")), Fill(2, "", Gem("earth")) };
        var b = new[] { Fill(0, "", Gem("earth")), Fill(1, "", Gem("fire")), Fill(2, "", Gem("ice")) };
        var c = new[] { Fill(0, "", Gem("ice")), Fill(1, "", Gem("earth")), Fill(2, "", Gem("fire")) };

        var ra = CombinationEvaluator.Evaluate(Host(), a, Resonances, Tuning).Select(r => r.ComboId).OrderBy(x => x);
        var rb = CombinationEvaluator.Evaluate(Host(), b, Resonances, Tuning).Select(r => r.ComboId).OrderBy(x => x);
        var rc = CombinationEvaluator.Evaluate(Host(), c, Resonances, Tuning).Select(r => r.ComboId).OrderBy(x => x);

        Assert.Equal(ra, rb);
        Assert.Equal(rb, rc);
    }

    [Fact]
    public void The_preview_form_writes_nothing_and_is_the_same_function()
    {
        var fill = new[] { Fill(0, "fire", Gem("fire")), Fill(1, "fire", Gem("fire")) };
        Assert.Equal(
            CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning),
            CombinationEvaluator.Preview(Host(), fill, Resonances, Tuning));

        // "One insert away" — module 20's hint, computed by the same function over a hypothetical.
        var away = CombinationEvaluator.PreviewWithOneMore(
            Host(), fill, Fill(2, "fire", Gem("fire")), Resonances, Tuning);
        Assert.Contains(away, r => r.ComboId == "combo.pure-fire-4");

        // The hypothetical changed nothing about the real fill.
        Assert.DoesNotContain(
            CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning),
            r => r.ComboId == "combo.pure-fire-4");
    }

    // ── §6, affinity as a BONUS (D22 as amended) ────────────────────────────────────────────────

    [Fact]
    public void Affinity_is_a_bonus_and_a_mismatched_fill_still_fires()
    {
        var mismatched = new[] { Fill(0, "ice", Gem("fire")), Fill(1, "earth", Gem("fire")) };
        var results = CombinationEvaluator.Evaluate(Host(), mismatched, Resonances, Tuning);

        // The gate is gone: two fire inserts in wrong-element sockets still reach k=2.
        var pure = Assert.Single(results, r => r.Shape == ComboShape.Pure);
        Assert.Equal("combo.pure-fire-2", pure.ComboId);
        Assert.False(pure.AllAttuned);
        Assert.Equal(2, pure.EffectiveCount);
    }

    [Fact]
    public void All_attuned_raises_resonance_count_by_one()
    {
        // ssot-sockets.md §7.1's worked example, reproduced: two earth inserts in two earth-affinity
        // sockets reach the k=3 step.
        var fill = new[] { Fill(0, "earth", Gem("earth")), Fill(1, "earth", Gem("earth")) };
        var pure = Assert.Single(
            CombinationEvaluator.Evaluate(Host(3), fill, Resonances, Tuning), r => r.Shape == ComboShape.Pure);

        Assert.Equal("combo.pure-earth-3", pure.ComboId);
        Assert.True(pure.AllAttuned);
        Assert.Equal(3, pure.EffectiveCount);
    }

    [Fact]
    public void One_unattuned_contributor_removes_the_whole_bonus()
    {
        // §7.2's worked example: socket 0 is fire-affinity, socket 2 has none, so NOT every
        // contributor is attuned — no +1, and the item lands on pure-fire-2.
        var fill = new[] { Fill(0, "fire", Gem("fire")), Fill(2, "", Gem("fire", tier: 4)) };
        var pure = Assert.Single(
            CombinationEvaluator.Evaluate(Host(3), fill, Resonances, Tuning), r => r.Shape == ComboShape.Pure);

        Assert.Equal("combo.pure-fire-2", pure.ComboId);
        Assert.False(pure.AllAttuned);
    }

    [Fact]
    public void All_attuned_raises_a_strain_tier_by_one_because_it_has_no_count()
    {
        var strain = new ComboRecipe(
            "combo.strain-might-offense", ComboShape.Strain, "", 0, "", "", 4, BaseTier: 2,
            new[] { new ComboIngredient("atom.elemental-power", 4) }, BaseFloors: new[] { 1, 1, 1, 1 });

        var attuned = Enumerable.Range(0, 4).Select(i => Fill(i, "fire", Gem("fire"))).ToArray();
        var loose = Enumerable.Range(0, 4).Select(i => Fill(i, "", Gem("fire"))).ToArray();

        var withBonus = Assert.Single(
            CombinationEvaluator.Evaluate(Host(), attuned, new[] { strain }, Tuning));
        var without = Assert.Single(
            CombinationEvaluator.Evaluate(Host(), loose, new[] { strain }, Tuning));

        Assert.Equal(3, withBonus.GrantedTier);
        Assert.Equal(2, without.GrantedTier);
        Assert.True(withBonus.AllAttuned);
    }

    [Fact]
    public void Affinity_never_scales_an_inserts_magnitude()
    {
        // The evaluator has nowhere to put a scaled magnitude: its result carries a count and a tier
        // and nothing else. Asserted by reflection, because "we would never do that" is not a test.
        var props = typeof(CombinationResult).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "AllAttuned", "Circuit", "ComboId", "EffectiveCount", "GrantedTier", "Shape" }, props);
    }

    // ── §8's stacking rules ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_highest_k_per_element_fires()
    {
        var fill = Enumerable.Range(0, 3).Select(i => Fill(i, "", Gem("fire"))).ToArray();
        var pures = CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning)
            .Where(r => r.Shape == ComboShape.Pure).ToList();

        var only = Assert.Single(pures);
        Assert.Equal("combo.pure-fire-3", only.ComboId);
    }

    [Fact]
    public void Ring_eclipse_and_diversity_stack_with_each_other_and_with_pure()
    {
        // A fire/ice/light/dark fill: Eclipse + Diversity(4) + Ring(fire-ice) and no Pure.
        var fill = new[]
        {
            Fill(0, "", Gem("fire")), Fill(1, "", Gem("ice")),
            Fill(2, "", Gem("light")), Fill(3, "", Gem("dark")),
        };
        var ids = CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning).Select(r => r.ComboId).ToList();

        Assert.Contains("combo.eclipse", ids);
        Assert.Contains("combo.diversity-4", ids);
        Assert.Contains("combo.ring-fire-ice", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("combo.pure-", StringComparison.Ordinal));
        Assert.DoesNotContain("combo.diversity-3", ids); // only the highest diversity step
    }

    [Fact]
    public void Ring_only_fires_for_elements_adjacent_on_the_cycle()
    {
        // earth and fire are NOT adjacent on fire -> ice -> earth -> air -> fire.
        var fill = new[] { Fill(0, "", Gem("earth")), Fill(1, "", Gem("fire")) };
        var ids = CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning).Select(r => r.ComboId).ToList();
        Assert.DoesNotContain(ids, id => id.StartsWith("combo.ring-", StringComparison.Ordinal));

        // ...but earth and air are.
        var adjacent = new[] { Fill(0, "", Gem("earth")), Fill(1, "", Gem("air")) };
        Assert.Contains(
            CombinationEvaluator.Evaluate(Host(), adjacent, Resonances, Tuning),
            r => r.ComboId == "combo.ring-earth-air");
    }

    [Fact]
    public void An_omni_insert_counts_toward_diversity_only()
    {
        var fill = new[]
        {
            Fill(0, "", Gem("omni")), Fill(1, "", Gem("omni")),
            Fill(2, "", Gem("fire")), Fill(3, "", Gem("ice")),
        };
        var results = CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning);
        var ids = results.Select(r => r.ComboId).ToList();

        // Two omni inserts do NOT make a Pure, a Ring or an Eclipse.
        Assert.DoesNotContain(ids, id => id.Contains("pure-omni", StringComparison.Ordinal));
        Assert.DoesNotContain("combo.eclipse", ids);
        // But omni IS a distinct member for Diversity: fire + ice + omni = 3.
        Assert.Contains("combo.diversity-3", ids);
    }

    [Fact]
    public void An_element_free_insert_counts_toward_nothing_at_all()
    {
        var fill = new[]
        {
            Fill(0, "", Gem("", family: "atom.vitality")),
            Fill(1, "", Gem("", family: "atom.vitality")),
            Fill(2, "", Gem("fire")), Fill(3, "", Gem("ice")),
        };
        var ids = CombinationEvaluator.Evaluate(Host(), fill, Resonances, Tuning).Select(r => r.ComboId).ToList();

        // "" is an absent element, not a seventh one — so Diversity sees 2, below its floor of 3.
        Assert.DoesNotContain(ids, id => id.StartsWith("combo.diversity", StringComparison.Ordinal));
        Assert.Contains("combo.ring-fire-ice", ids);
    }

    [Fact]
    public void One_item_fires_at_most_one_strain_or_splice_and_ties_break_on_the_lowest_id()
    {
        var ingredients = new[] { new ComboIngredient("atom.elemental-power", 2) };
        var catalog = new[]
        {
            new ComboRecipe("combo.splice-zeal-wrath", ComboShape.Splice, "", 0, "", "", 2, 1, ingredients, BaseFloors: new[] { 1, 1 }),
            new ComboRecipe("combo.strain-anima-balance", ComboShape.Strain, "", 0, "", "", 2, 1, ingredients, BaseFloors: new[] { 1, 1 }),
        };

        var fill = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")) };
        var results = CombinationEvaluator.Evaluate(Host(2), fill, catalog, Tuning);

        var only = Assert.Single(results);
        Assert.Equal("combo.splice-zeal-wrath", only.ComboId); // ordinal-lowest container_id wins
    }

    [Fact]
    public void Host_role_frame_and_min_sockets_all_gate_a_recipe()
    {
        var recipe = new ComboRecipe(
            "combo.strain-x", ComboShape.Strain, "", 0, "armament-primary", "plant", 4, 1,
            new[] { new ComboIngredient("atom.elemental-power", 2) }, BaseFloors: new[] { 1, 1 });
        var fill = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")) };

        Assert.Single(CombinationEvaluator.Evaluate(Host(4), fill, new[] { recipe }, Tuning));
        Assert.Empty(CombinationEvaluator.Evaluate(Host(3), fill, new[] { recipe }, Tuning));                       // min sockets
        Assert.Empty(CombinationEvaluator.Evaluate(Host(4, ItemRole.CoreGuard), fill, new[] { recipe }, Tuning));  // role
        Assert.Empty(CombinationEvaluator.Evaluate(
            new SocketHost("item.h", ItemRole.ArmamentPrimary, "humanoid", 4), fill, new[] { recipe }, Tuning));   // frame
    }

    [Fact]
    public void A_multiset_recipe_needs_the_quantities_not_just_the_families()
    {
        var recipe = new ComboRecipe(
            "combo.strain-y", ComboShape.Strain, "", 0, "", "", 4, 1,
            new[] { new ComboIngredient("atom.elemental-power", 3), new ComboIngredient("atom.vitality", 1) },
            BaseFloors: new[] { 1, 1, 1, 1 });

        var short1 = new[]
        {
            Fill(0, "", Gem("fire")), Fill(1, "", Gem("ice")),
            Fill(2, "", Gem("", family: "atom.vitality")),
        };
        Assert.Empty(CombinationEvaluator.Evaluate(Host(), short1, new[] { recipe }, Tuning));

        var complete = new[]
        {
            Fill(0, "", Gem("fire")), Fill(1, "", Gem("ice")), Fill(2, "", Gem("earth")),
            Fill(3, "", Gem("", tier: 2, family: "atom.vitality")),
        };
        Assert.Single(CombinationEvaluator.Evaluate(Host(), complete, new[] { recipe }, Tuning));
    }

    [Fact]
    public void One_socket_fill_satisfies_two_requirements_of_the_same_family_without_starving_either()
    {
        // SSH7.8: the requirement is (family, quantity) — tiers are the LADDER's, so there is no
        // "higher-tier requirement" that could starve. The property that survives: a single fill is
        // claimed at most once, and two claims of one family need two fills.
        var recipe = new ComboRecipe(
            "combo.strain-z", ComboShape.Strain, "", 0, "", "", 2, 1,
            new[] { new ComboIngredient("atom.might"), new ComboIngredient("atom.might") },
            BaseFloors: new[] { 1, 1 });

        var fill = new[]
        {
            new SocketFill(0, "", new InsertDef("gem.a.t1", "atom.might", "", 1)),
            new SocketFill(1, "", new InsertDef("gem.a.t5", "atom.might", "", 5)),
        };

        Assert.Single(CombinationEvaluator.Evaluate(Host(2), fill, new[] { recipe }, Tuning));
    }

    // ── §9, D21's exclusivity validator ─────────────────────────────────────────────────────────

    [Fact]
    public void A_set_piece_never_fires_a_strain_or_splice()
    {
        var strain = new ComboRecipe(
            "combo.strain-set-test", ComboShape.Strain, "", 0, "", "", 2, 1,
            new[] { new ComboIngredient("atom.elemental-power", 2) }, BaseFloors: new[] { 1, 1 });
        var fill = new[] { Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")) };

        Assert.Single(CombinationEvaluator.Evaluate(Host(2), fill, new[] { strain }, Tuning));
        Assert.Empty(CombinationEvaluator.Evaluate(Host(2, setPiece: true), fill, new[] { strain }, Tuning));

        Assert.False(SetExclusivityValidator.MayFire(Host(2, setPiece: true), ComboShape.Strain));
        Assert.False(SetExclusivityValidator.MayFire(Host(2, setPiece: true), ComboShape.Splice));
        Assert.True(SetExclusivityValidator.MayFire(Host(2, setPiece: true), ComboShape.Pure));
    }

    [Fact]
    public void Attunement_reaches_a_step_the_socket_count_alone_could_not()
    {
        // ⛔ ssot-sockets.md §7.4's payoff, and the reason generated resonances carry min_sockets = 0:
        // two attuned inserts in a TWO-socket item fire the k=3 step. A generated row gated on
        // min_sockets = k would make the +1 unreachable by construction — the exact defence §4.2 calls
        // "the single most load-bearing anti-tax mechanism in the design".
        var fill = new[] { Fill(0, "earth", Gem("earth")), Fill(1, "earth", Gem("earth")) };
        var pure = Assert.Single(
            CombinationEvaluator.Evaluate(Host(2), fill, Resonances, Tuning), r => r.Shape == ComboShape.Pure);
        Assert.Equal("combo.pure-earth-3", pure.ComboId);

        Assert.All(Resonances, r => Assert.Equal(0, r.MinSockets));
    }

    [Fact]
    public void A_set_piece_may_still_be_socketed_and_still_fires_resonances()
    {
        var fill = new[] { Fill(0, "fire", Gem("fire")), Fill(1, "fire", Gem("fire")) };
        var results = CombinationEvaluator.Evaluate(Host(2, setPiece: true), fill, Resonances, Tuning);

        Assert.Contains(results, r => r.ComboId == "combo.pure-fire-3"); // attuned +1
        Assert.True(SetExclusivityValidator.MaySocket(Host(2, setPiece: true), Gem("fire")));
    }

    [Fact]
    public void No_reason_code_is_minted_for_an_unsatisfied_combination()
    {
        // Evaluate returns a list, never a rejection: there is no channel through which a suppressed
        // combination could become a code, which is the point (§9's "not a rejection code").
        Assert.Equal(
            typeof(IReadOnlyList<CombinationResult>),
            typeof(CombinationEvaluator).GetMethod(nameof(CombinationEvaluator.Evaluate))!.ReturnType);

        // The set-piece suppression is explained in prose the UI can show, not as a code.
        var reason = SetExclusivityValidator.SuppressionReason(Host(2, setPiece: true), ComboShape.Strain);
        Assert.Contains("D21", reason);
        Assert.Equal("", SetExclusivityValidator.SuppressionReason(Host(2), ComboShape.Strain));
    }

    // ── Evaluation order ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Strains_resolve_before_pure_before_ring_eclipse_and_diversity()
    {
        var strain = new ComboRecipe(
            "combo.strain-order", ComboShape.Strain, "", 0, "", "", 4, 1,
            new[] { new ComboIngredient("atom.elemental-power", 2) }, BaseFloors: new[] { 1, 1 });

        var fill = new[]
        {
            Fill(0, "", Gem("fire")), Fill(1, "", Gem("fire")),
            Fill(2, "", Gem("light")), Fill(3, "", Gem("dark")),
        };
        var shapes = CombinationEvaluator
            .Evaluate(Host(), fill, Resonances.Concat(new[] { strain }).ToList(), Tuning)
            .Select(r => (int)r.Shape)
            .ToList();

        Assert.Equal(shapes.OrderBy(s => s), shapes);
        Assert.Equal((int)ComboShape.Strain, shapes[0]);
    }

    // ── Circuits (circuit-topology §2-§3) ───────────────────────────────────────────────────────

    static ComboRecipe CircuitStrain() => new(
        "combo.strain-circuit-test", ComboShape.Strain, "", 0, "", "", 4, BaseTier: 1,
        new[] { new ComboIngredient("atom.elemental-power", 4) }, BaseFloors: new[] { 1, 1, 1, 1 });

    [Fact]
    public void Indices_three_and_four_never_combine()
    {
        // socket 3 is the last of circuit 0, socket 4 the first of circuit 1. A four-ingredient
        // recipe split across them belongs to no circuit and must never fire.
        var gem = Gem("fire");
        var inOneCircuit = new[] { Fill(0, "", gem), Fill(1, "", gem), Fill(2, "", gem), Fill(3, "", gem) };
        Assert.Single(CombinationEvaluator.Evaluate(
            Host(8), inOneCircuit, new[] { CircuitStrain() }, Tuning), r => r.Shape == ComboShape.Strain);

        var acrossCircuits = new[] { Fill(3, "", gem), Fill(4, "", gem), Fill(5, "", gem), Fill(6, "", gem) };
        Assert.DoesNotContain(
            CombinationEvaluator.Evaluate(Host(8), acrossCircuits, new[] { CircuitStrain() }, Tuning),
            r => r.Shape == ComboShape.Strain);
    }

    [Fact]
    public void An_eight_socket_host_fires_two_independent_combinations()
    {
        // Two complete circuits, each carrying the same word: two results, one per circuit, because
        // the identity rule is PER CIRCUIT.
        var gem = Gem("fire");
        var fill = Enumerable.Range(0, 8).Select(i => Fill(i, "", gem)).ToArray();

        var strains = CombinationEvaluator.Evaluate(Host(8), fill, new[] { CircuitStrain() }, Tuning)
            .Where(r => r.Shape == ComboShape.Strain)
            .OrderBy(r => r.Circuit)
            .ToList();

        Assert.Equal(2, strains.Count);
        Assert.Equal(new[] { 0, 1 }, strains.Select(r => r.Circuit));
        Assert.All(strains, r => Assert.Equal("combo.strain-circuit-test", r.ComboId));
    }

    [Fact]
    public void A_six_socket_host_has_one_complete_circuit_and_a_resonance_only_remainder()
    {
        var gem = Gem("fire");
        var fill = new[]
        {
            Fill(0, "", gem), Fill(1, "", gem), Fill(2, "", gem), Fill(3, "", gem),
            Fill(4, "", gem), Fill(5, "", gem),
        };

        var results = CombinationEvaluator.Evaluate(
            Host(6), fill, Resonances.Concat(new[] { CircuitStrain() }).ToList(), Tuning);

        // Circuit 0 (sockets 0-3) is a complete circuit and fires the identity; circuit 1 (4-5) is a
        // two-socket remainder, so it can carry resonance only.
        var identity = Assert.Single(results, r => r.Shape == ComboShape.Strain);
        Assert.Equal(0, identity.Circuit);
        Assert.DoesNotContain(results, r => r.Shape == ComboShape.Strain && r.Circuit == 1);
        Assert.Contains(results, r => r.Shape == ComboShape.Pure && r.Circuit == 1 && r.EffectiveCount == 2);
    }

    [Fact]
    public void A_recipe_is_unordered_within_a_circuit()
    {
        // D41 within one circuit: the same four inserts in any socket order resolve identically.
        var first = new[]
        {
            Fill(0, "", Gem("fire")), Fill(1, "", Gem("ice")),
            Fill(2, "", Gem("earth")), Fill(3, "", Gem("air")),
        };
        var second = new[]
        {
            Fill(0, "", Gem("air")), Fill(1, "", Gem("earth")),
            Fill(2, "", Gem("ice")), Fill(3, "", Gem("fire")),
        };

        var a = CombinationEvaluator.Evaluate(Host(4), first, new[] { CircuitStrain() }, Tuning)
            .Select(r => (r.ComboId, r.Circuit)).OrderBy(x => x.ComboId, StringComparer.Ordinal);
        var b = CombinationEvaluator.Evaluate(Host(4), second, new[] { CircuitStrain() }, Tuning)
            .Select(r => (r.ComboId, r.Circuit)).OrderBy(x => x.ComboId, StringComparer.Ordinal);

        Assert.Equal(a, b);
        Assert.Contains(a, x => x.ComboId == "combo.strain-circuit-test" && x.Circuit == 0);
    }
}
