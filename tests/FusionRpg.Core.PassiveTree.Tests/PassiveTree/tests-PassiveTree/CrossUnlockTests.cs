using FusionRpg.Core.PassiveTree;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree;

/// <summary>Task D4 — `CrossUnlock` (spec-tree-resolve.md §4). `credit(i) = max{base(j)}`, exactly
/// one lender, never a sum.</summary>
public class CrossUnlockTests
{
    static readonly IReadOnlyDictionary<string, string?> OneStance = new Dictionary<string, string?>
    {
        ["might"] = "aggressive", ["fortitude"] = "aggressive", ["cunning"] = "aggressive", ["grace"] = "aggressive",
    };

    [Fact] // "Three mates at 40/30/20 credit 40, never 90 -- exactly one lender"
    public void Three_mates_at_40_30_20_credit_40_never_90()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 0, ["fortitude"] = 40, ["cunning"] = 30, ["grace"] = 20 };
        var stance = new Dictionary<string, string?> { ["might"] = "aggressive", ["fortitude"] = "aggressive", ["cunning"] = "aggressive", ["grace"] = "aggressive" };

        var credit = CrossUnlock.Credit("might", baseByTree, stance);

        Assert.Equal(40, credit);
        Assert.NotEqual(90, credit); // the sum every "full" rule would have produced
    }

    [Fact] // "The same mate vector run through max and through sum gives DIFFERENT answers and the
           // resolver returns max (a swap is invisible on a one-mate fixture)" -- needs >=2 mates
    public void Max_and_sum_disagree_on_a_multi_mate_vector_and_the_resolver_returns_max()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 0, ["fortitude"] = 40, ["cunning"] = 30 };
        var stance = new Dictionary<string, string?> { ["might"] = "aggressive", ["fortitude"] = "aggressive", ["cunning"] = "aggressive" };

        var maxCredit = CrossUnlock.Credit("might", baseByTree, stance);
        var sumCredit = baseByTree.Where(kv => kv.Key != "might").Sum(kv => kv.Value); // the rejected "full" rule

        Assert.Equal(40, maxCredit);
        Assert.Equal(70, sumCredit);
        Assert.NotEqual(sumCredit, maxCredit); // the swap IS visible here, unlike the one-mate fixture
    }

    [Fact] // "A four-of-one-stance build's total credit is bounded by its own largest tree"
    public void A_four_of_one_stance_builds_credit_is_bounded_by_its_own_largest_OTHER_tree()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 25, ["fortitude"] = 40, ["cunning"] = 30, ["grace"] = 20 };

        // Every tree's credit is bounded by the LARGEST of the OTHER three -- never their sum, and
        // never even its own base (credit excludes self by construction).
        Assert.Equal(40, CrossUnlock.Credit("might", baseByTree, OneStance));      // others: 40,30,20 -> max=40
        Assert.Equal(30, CrossUnlock.Credit("fortitude", baseByTree, OneStance));  // others: 25,30,20 -> max=30
        Assert.Equal(40, CrossUnlock.Credit("cunning", baseByTree, OneStance));    // others: 25,40,20 -> max=40
        Assert.Equal(40, CrossUnlock.Credit("grace", baseByTree, OneStance));      // others: 25,40,30 -> max=40
    }

    [Fact] // "A tree the catalog gives no stance group gets credit = 0"
    public void A_tree_with_no_stance_group_gets_credit_zero()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 0, ["fortitude"] = 999 };
        var stance = new Dictionary<string, string?> { ["fortitude"] = "aggressive" }; // "might" has none

        Assert.Equal(0, CrossUnlock.Credit("might", baseByTree, stance));
    }

    [Fact]
    public void A_lone_tree_with_no_mates_in_its_stance_credits_zero()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 999 };
        var stance = new Dictionary<string, string?> { ["might"] = "aggressive" };
        Assert.Equal(0, CrossUnlock.Credit("might", baseByTree, stance));
    }

    [Fact]
    public void A_mate_in_a_different_stance_group_never_lends()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 0, ["fortitude"] = 999 };
        var stance = new Dictionary<string, string?> { ["might"] = "aggressive", ["fortitude"] = "defensive" };
        Assert.Equal(0, CrossUnlock.Credit("might", baseByTree, stance));
    }

    [Fact]
    public void Gate_is_base_plus_credit()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 15, ["fortitude"] = 40 };
        var stance = new Dictionary<string, string?> { ["might"] = "aggressive", ["fortitude"] = "aggressive" };

        Assert.Equal(55, CrossUnlock.Gate("might", baseByTree, stance)); // 15 + 40
    }

    [Fact]
    public void Gate_of_a_tree_with_no_base_and_no_mate_is_zero()
    {
        var baseByTree = new Dictionary<string, long>();
        var stance = new Dictionary<string, string?>();
        Assert.Equal(0, CrossUnlock.Gate("might", baseByTree, stance));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        var baseByTree = new Dictionary<string, long>();
        var stance = new Dictionary<string, string?>();
        Assert.Throws<ArgumentNullException>(() => CrossUnlock.Credit("might", null!, stance));
        Assert.Throws<ArgumentNullException>(() => CrossUnlock.Credit("might", baseByTree, null!));
        Assert.Throws<ArgumentNullException>(() => CrossUnlock.Gate("might", null!, stance));
    }

    // ---- D5: Lender -- WHICH tree lent the credit, not just how much -----------------------------

    [Fact] // Lender must name the SAME tree Credit's number came from -- three mates at 40/30/20
    public void Lender_names_the_same_tree_Credit_measured()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 0, ["fortitude"] = 40, ["cunning"] = 30, ["grace"] = 20 };
        var stance = new Dictionary<string, string?> { ["might"] = "aggressive", ["fortitude"] = "aggressive", ["cunning"] = "aggressive", ["grace"] = "aggressive" };

        var credit = CrossUnlock.Credit("might", baseByTree, stance);
        var lender = CrossUnlock.Lender("might", baseByTree, stance);

        Assert.Equal(40, credit);
        Assert.Equal("fortitude", lender);
    }

    [Fact] // Credit == 0 and Lender == null must always agree -- no stance group
    public void No_stance_group_gives_zero_credit_and_a_null_lender()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 0, ["fortitude"] = 999 };
        var stance = new Dictionary<string, string?> { ["fortitude"] = "aggressive" };

        Assert.Equal(0, CrossUnlock.Credit("might", baseByTree, stance));
        Assert.Null(CrossUnlock.Lender("might", baseByTree, stance));
    }

    [Fact] // Credit == 0 and Lender == null must also agree when every mate sits at base 0
    public void Mates_that_all_sit_at_base_zero_give_zero_credit_and_a_null_lender()
    {
        var baseByTree = new Dictionary<string, long> { ["might"] = 0, ["fortitude"] = 0, ["cunning"] = 0 };
        var stance = new Dictionary<string, string?> { ["might"] = "aggressive", ["fortitude"] = "aggressive", ["cunning"] = "aggressive" };

        Assert.Equal(0, CrossUnlock.Credit("might", baseByTree, stance));
        Assert.Null(CrossUnlock.Lender("might", baseByTree, stance));
    }

    [Fact]
    public void Lender_null_arguments_are_refused()
    {
        var baseByTree = new Dictionary<string, long>();
        var stance = new Dictionary<string, string?>();
        Assert.Throws<ArgumentNullException>(() => CrossUnlock.Lender("might", null!, stance));
        Assert.Throws<ArgumentNullException>(() => CrossUnlock.Lender("might", baseByTree, null!));
    }
}
