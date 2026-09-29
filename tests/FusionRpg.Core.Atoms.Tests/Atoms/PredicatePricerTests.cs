using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// `P0.3` (action-todo.md, spec-power-vector.md "predicates ARE priced" — owner decision
/// 2026-08-27): the tree-composition math in <see cref="PredicatePricer"/>, the floored per-leaf
/// chain in <see cref="PowerTables.PredicateFrequencyOf"/>, and the fifth <see
/// cref="CostFunction.Conditionality"/> factor that wires them together.
/// </summary>
public class PredicatePricerTests
{
    static PredicateNode Leaf(LeafId id, string argKey) =>
        new PredicateNode.Leaf(id, Subject.Self, Text: argKey);

    // ---- PowerTables.PredicateFrequencyOf ---------------------------------------------------------

    [Fact]
    public void An_unauthored_leaf_arg_prices_at_1000_the_never_discounted_safe_default()
    {
        var tables = new PowerTables(Array.Empty<PowerCoefficientRow>(), Array.Empty<TriggerFrequencyRow>());

        Assert.Equal(1000L, tables.PredicateFrequencyOf(LeafId.HasStatus, "burn", floorMilli: 400));
    }

    [Fact]
    public void The_four_factor_chain_multiplies_all_four_factors_in_per_mille()
    {
        // reachability x susceptibility x coincidence x uptime, hand-computed:
        // 900 x 900 / 1000 = 810; 810 x 900 / 1000 = 729; 729 x 900 / 1000 = 656.1 -> 656 (round-half-away-from-zero).
        var row = new PredicateFrequencyRow("HasStatus", "burn", 900, 900, 900, 900);
        var tables = new PowerTables(
            Array.Empty<PowerCoefficientRow>(), Array.Empty<TriggerFrequencyRow>(), new[] { row });

        Assert.Equal(656L, tables.PredicateFrequencyOf(LeafId.HasStatus, "burn", floorMilli: 0));
    }

    [Fact]
    public void The_chain_floors_at_the_supplied_floor_rather_than_going_lower()
    {
        // A chain of 100 x 100 x 1000 x 1000 = 10 sits far under a 400 floor.
        var row = new PredicateFrequencyRow("HpBelowMilli", "300", 100, 100, 1000, 1000);
        var tables = new PowerTables(
            Array.Empty<PowerCoefficientRow>(), Array.Empty<TriggerFrequencyRow>(), new[] { row });

        Assert.Equal(400L, tables.PredicateFrequencyOf(LeafId.HpBelowMilli, "300", floorMilli: 400));
    }

    [Fact]
    public void The_floor_never_raises_a_chain_that_already_clears_it()
    {
        var row = new PredicateFrequencyRow("HasStatus", "burn", 900, 900, 900, 900);
        var tables = new PowerTables(
            Array.Empty<PowerCoefficientRow>(), Array.Empty<TriggerFrequencyRow>(), new[] { row });

        Assert.Equal(656L, tables.PredicateFrequencyOf(LeafId.HasStatus, "burn", floorMilli: 400));
    }

    [Fact]
    public void HasStatus_burn_and_HasStatus_freeze_are_independent_rows()
    {
        // hasStatus differs per status -- the exact reason ArgKey joins LeafId in the table's key.
        var tables = new PowerTables(
            Array.Empty<PowerCoefficientRow>(), Array.Empty<TriggerFrequencyRow>(),
            new[]
            {
                new PredicateFrequencyRow("HasStatus", "burn", 600, 1000, 1000, 1000),
                new PredicateFrequencyRow("HasStatus", "freeze", 300, 1000, 1000, 1000),
            });

        Assert.Equal(600L, tables.PredicateFrequencyOf(LeafId.HasStatus, "burn", 0));
        Assert.Equal(300L, tables.PredicateFrequencyOf(LeafId.HasStatus, "freeze", 0));
        Assert.Equal(1000L, tables.PredicateFrequencyOf(LeafId.HasStatus, "chill", 0));
    }

    // ---- PredicatePricer tree composition ----------------------------------------------------------

    static PowerTables TwoLeafTables() => new(
        Array.Empty<PowerCoefficientRow>(), Array.Empty<TriggerFrequencyRow>(),
        new[]
        {
            // reachability is the only non-1000 factor, so the chain equals it exactly.
            new PredicateFrequencyRow("HasStatus", "p600", 600, 1000, 1000, 1000),
            new PredicateFrequencyRow("HasStatus", "p500", 500, 1000, 1000, 1000),
        });

    [Fact]
    public void A_null_tree_prices_unconditional_matching_conditionalitys_own_short_circuit()
    {
        Assert.Equal(1000L, PredicatePricer.PriceTree(null, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void A_bare_leaf_prices_at_its_own_table_row()
    {
        var tree = Leaf(LeafId.HasStatus, "p600");

        Assert.Equal(600L, PredicatePricer.PriceTree(tree, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void And_multiplies_its_two_children_in_per_mille()
    {
        // 600 x 500 / 1000 = 300.
        var tree = new PredicateNode.And(new PredicateNode[] { Leaf(LeafId.HasStatus, "p600"), Leaf(LeafId.HasStatus, "p500") });

        Assert.Equal(300L, PredicatePricer.PriceTree(tree, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void Or_is_the_probabilistic_union_of_its_two_children()
    {
        // 1000 - (1000-600)(1000-500)/1000 = 1000 - 400*500/1000 = 1000 - 200 = 800.
        var tree = new PredicateNode.Or(new PredicateNode[] { Leaf(LeafId.HasStatus, "p600"), Leaf(LeafId.HasStatus, "p500") });

        Assert.Equal(800L, PredicatePricer.PriceTree(tree, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void Not_inverts_its_childs_price()
    {
        var tree = new PredicateNode.Not(Leaf(LeafId.HasStatus, "p600"));

        Assert.Equal(400L, PredicatePricer.PriceTree(tree, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void And_of_three_children_folds_left_to_right_from_the_multiplicative_identity()
    {
        // 1000 x 600 / 1000 = 600; 600 x 500 / 1000 = 300; 300 x 500 / 1000 = 150.
        var tree = new PredicateNode.And(new PredicateNode[]
        {
            Leaf(LeafId.HasStatus, "p600"), Leaf(LeafId.HasStatus, "p500"), Leaf(LeafId.HasStatus, "p500"),
        });

        Assert.Equal(150L, PredicatePricer.PriceTree(tree, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void An_empty_And_prices_as_the_multiplicative_identity_unconditional()
    {
        var tree = new PredicateNode.And(Array.Empty<PredicateNode>());

        Assert.Equal(1000L, PredicatePricer.PriceTree(tree, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void An_empty_Or_prices_as_guaranteed_false_zero()
    {
        var tree = new PredicateNode.Or(Array.Empty<PredicateNode>());

        Assert.Equal(0L, PredicatePricer.PriceTree(tree, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void A_nested_tree_composes_not_inside_and_correctly()
    {
        // Not(p500) = 500. And(p600, 500) = 600*500/1000 = 300.
        var tree = new PredicateNode.And(new PredicateNode[]
        {
            Leaf(LeafId.HasStatus, "p600"), new PredicateNode.Not(Leaf(LeafId.HasStatus, "p500")),
        });

        Assert.Equal(300L, PredicatePricer.PriceTree(tree, TwoLeafTables(), floorMilli: 0));
    }

    [Fact]
    public void A_leaf_below_the_floor_is_clamped_before_it_enters_the_tree_composition()
    {
        // Table row prices at 100‰; a 400 floor lifts the leaf itself, so a bare leaf reads 400 --
        // proving the floor is applied per-leaf (PriceNode), not just once at the top of the tree.
        var tables = new PowerTables(
            Array.Empty<PowerCoefficientRow>(), Array.Empty<TriggerFrequencyRow>(),
            new[] { new PredicateFrequencyRow("HasStatus", "rare", 100, 1000, 1000, 1000) });

        Assert.Equal(400L, PredicatePricer.PriceTree(Leaf(LeafId.HasStatus, "rare"), tables, floorMilli: 400));
    }

    // ---- CostFunction.Conditionality's fifth factor -------------------------------------------------

    static PowerTables TablesWithTriggerAndPredicate() => new(
        Array.Empty<PowerCoefficientRow>(),
        new[] { new TriggerFrequencyRow(AtomTriggers.OnDamageDealt, 60) },
        new[] { new PredicateFrequencyRow("HasStatus", "p600", 600, 1000, 1000, 1000) });

    static JsonElement PredicateJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void A_triggered_atom_with_no_predicate_key_prices_unconditional_for_the_fifth_factor()
    {
        var when = CostFunction.Read($$"""{"trigger":"{{AtomTriggers.OnDamageDealt}}","chance":1000}""");
        var pars = CostFunction.Read("{}");

        var withoutPredicate = CostFunction.Conditionality(when, pars, TablesWithTriggerAndPredicate());
        var noPredicateAtAllTables = CostFunction.Conditionality(
            when, pars, new PowerTables(
                Array.Empty<PowerCoefficientRow>(), new[] { new TriggerFrequencyRow(AtomTriggers.OnDamageDealt, 60) }));

        Assert.Equal(noPredicateAtAllTables, withoutPredicate);
    }

    [Fact]
    public void A_predicate_bearing_triggered_atom_prices_strictly_lower_than_one_without()
    {
        var baseline = CostFunction.Read($$"""{"trigger":"{{AtomTriggers.OnDamageDealt}}","chance":1000}""");
        var withPredicate = CostFunction.Read(
            "{\"trigger\":\"" + AtomTriggers.OnDamageDealt + "\",\"chance\":1000," +
            "\"predicate\":{\"leaf\":\"hasStatus\",\"subject\":\"target\",\"value\":\"p600\"}}");
        var pars = CostFunction.Read("{}");
        var tables = TablesWithTriggerAndPredicate();

        var baselineMilli = CostFunction.Conditionality(baseline, pars, tables);
        var discountedMilli = CostFunction.Conditionality(withPredicate, pars, tables);

        Assert.True(discountedMilli < baselineMilli,
            $"predicate-bearing atom ({discountedMilli}) should price lower than the same atom without " +
            $"a predicate ({baselineMilli})");
        // baseline factor is 1000 (chance) x 1000 (60/min frequency) x 1000 (no ICD) x 1000 (1 target)
        // all combined = baselineMilli itself; the predicate then multiplies by 600/1000 on top.
        Assert.Equal(PowerMath.CombineMilli(baselineMilli, 600), discountedMilli);
    }

    [Fact]
    public void A_predicate_on_a_triggerless_atom_is_out_of_scope_and_does_not_discount()
    {
        // CostFunction.Conditionality's own early return for "no trigger" fires before the predicate
        // is ever read -- deliberately: spec-power-vector.md's reasoning is entirely about event-driven
        // atoms, and this scope boundary was a deliberate choice this session, not an oversight.
        var when = CostFunction.Read("""{"predicate":{"leaf":"hasStatus","subject":"target","value":"p600"}}""");
        var pars = CostFunction.Read("{}");

        Assert.Equal(1000L, CostFunction.Conditionality(when, pars, TablesWithTriggerAndPredicate()));
    }

    [Fact]
    public void A_malformed_predicate_json_prices_unconditional_rather_than_throwing_or_silently_discounting()
    {
        var when = CostFunction.Read(
            "{\"trigger\":\"" + AtomTriggers.OnDamageDealt +
            "\",\"chance\":1000,\"predicate\":{\"leaf\":\"not-a-real-leaf\"}}");
        var pars = CostFunction.Read("{}");
        var tables = TablesWithTriggerAndPredicate();

        var baseline = CostFunction.Conditionality(
            CostFunction.Read($$"""{"trigger":"{{AtomTriggers.OnDamageDealt}}","chance":1000}"""), pars, tables);
        var malformed = CostFunction.Conditionality(when, pars, tables);

        Assert.Equal(baseline, malformed);
    }

    // ---- PowerPredicateTuningLoader -----------------------------------------------------------------

    [Fact]
    public void The_shipped_tuning_file_parses_to_the_documented_400_default()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-predicate.v1.json"));

        var tuning = PowerPredicateTuningLoader.Parse(json);

        Assert.Equal(400, tuning.DiscountFloorMilli);
    }

    [Fact]
    public void A_zero_floor_is_refused_an_uncapped_discount_is_the_exact_defect_the_floor_prevents()
    {
        Assert.Throws<PowerPredicateTuningRejection>(() =>
            PowerPredicateTuningLoader.Parse("""{"discountFloorMilli":0}"""));
    }

    [Fact]
    public void A_floor_above_1000_is_refused()
    {
        Assert.Throws<PowerPredicateTuningRejection>(() =>
            PowerPredicateTuningLoader.Parse("""{"discountFloorMilli":1001}"""));
    }

    [Fact]
    public void Missing_discountFloorMilli_is_refused_rather_than_defaulted_silently()
    {
        Assert.Throws<PowerPredicateTuningRejection>(() => PowerPredicateTuningLoader.Parse("{}"));
    }

    [Fact]
    public void Empty_json_is_refused()
    {
        Assert.Throws<PowerPredicateTuningRejection>(() => PowerPredicateTuningLoader.Parse(""));
    }

    [Fact]
    public void The_hub_defaults_to_400_when_never_configured()
    {
        PowerPredicateTuningHub.Reset();
        try
        {
            Assert.Equal(400, PowerPredicateTuningHub.Current.DiscountFloorMilli);
        }
        finally
        {
            PowerPredicateTuningHub.Reset();
        }
    }

    [Fact]
    public void Configuring_the_hub_changes_Current_and_Reset_restores_the_default()
    {
        PowerPredicateTuningHub.Reset();
        try
        {
            PowerPredicateTuningHub.Configure(new PowerPredicateTuning(DiscountFloorMilli: 500));
            Assert.Equal(500, PowerPredicateTuningHub.Current.DiscountFloorMilli);

            PowerPredicateTuningHub.Reset();
            Assert.Equal(400, PowerPredicateTuningHub.Current.DiscountFloorMilli);
        }
        finally
        {
            PowerPredicateTuningHub.Reset();
        }
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not find repo root from " + AppContext.BaseDirectory);
    }
}
