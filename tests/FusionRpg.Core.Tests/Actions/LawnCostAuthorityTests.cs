using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// combat-ai `lawn-cost-authority` (module 17, CAI4.5, spec-lawn-cost-authority.md §1) — the union of the
/// lawn's two cost-row sources. The acceptance's own words are the subject: *"Adding held-action keys
/// leaves the basic-attack id's rows and charged amount identical"*, **proven not argued**.
///
/// <para><b>Where these assertions sit, and why.</b> The row puts the row source in the Injector while
/// putting its test in this Core project, which cannot reference the injector. Resolved by building the
/// held rows HERE (`LawnCostRows` needs only Core types) and leaving the injector's file as the caller
/// that reads module 16's sets. So the row-level half is proven here, and the CHARGED-amount half is
/// proven at the ledger's own keying: `CostLedger` is built per `actionId`, and the union hands a
/// pre-existing key's list through by reference — asserted with <c>Assert.Same</c>, which is as strong as
/// "unchanged" gets in a test.</para>
/// </summary>
public class LawnCostAuthorityTests
{
    const string BasicId = "act.basic";
    const string HeldId = "act.skill";

    static ActionCostRow Row(string actionId, string resourceId, int amount, ActionCostTiming when = ActionCostTiming.OnCommit) =>
        new(actionId, resourceId, ValueSpec.Of(amount), when);

    static Dictionary<string, IReadOnlyList<ActionCostRow>> BasicRows() => new(StringComparer.Ordinal)
    {
        [BasicId] = new[] { Row(BasicId, "stamina", 5) }
    };

    /// <summary>The primitive pair the union takes — the caller passes `(a.ActionId, a.Costs)` per held
    /// action, so no test has to hand-build a fourteen-member `CompiledAction`.</summary>
    static (string ActionId, IReadOnlyList<CompiledActionCost> Costs) HeldAction(
        string actionId, params CompiledActionCost[] costs) => (actionId, costs);

    static CompiledActionCost Cost(string resourceId, ValueSpec amount, ActionCostTiming when = ActionCostTiming.OnCommit) =>
        new(resourceId, amount, when);

    [Fact]
    public void Union_never_rewrites_an_existing_keys_rows()
    {
        var basic = BasicRows();

        var union = LawnCostRows.Union(
            basic,
            new[] { HeldAction(HeldId, Cost("stamina", ValueSpec.Of(40), ActionCostTiming.OnCommit)) });

        // The basic attack's own list is the SAME instance it was: not "equal after a copy", unchanged.
        Assert.Same(basic[BasicId], union[BasicId]);
        Assert.Single(union[BasicId]);
        Assert.Equal(5, union[BasicId][0].AmountSpec.Min);
    }

    [Fact]
    public void A_held_action_gets_one_row_per_compiled_cost_in_the_authored_order()
    {
        var union = LawnCostRows.Union(
            BasicRows(),
            new[]
            {
                HeldAction(
                    HeldId,
                    Cost("qi", ValueSpec.Of(30), ActionCostTiming.OnCommit),
                    Cost("poise", ValueSpec.Of(2), ActionCostTiming.PerTick))
            });

        var rows = union[HeldId];
        Assert.Equal(2, rows.Count);
        Assert.Equal("qi", rows[0].ResourceId);
        Assert.Equal(ActionCostTiming.OnCommit, rows[0].When);
        Assert.Equal("poise", rows[1].ResourceId);
        Assert.Equal(ActionCostTiming.PerTick, rows[1].When);
        Assert.All(rows, r => Assert.Equal(HeldId, r.ActionId));
    }

    [Fact]
    public void An_empty_held_set_returns_the_basic_rows_untouched()
    {
        var basic = BasicRows();

        var union = LawnCostRows.Union(basic, Array.Empty<(string ActionId, IReadOnlyList<CompiledActionCost> Costs)>());

        Assert.Single(union);
        Assert.Same(basic[BasicId], union[BasicId]);
    }

    /// <summary>
    /// A held action that reuses a basic-attack id IS the basic attack: the union never rewrites what it
    /// did not create, so the basic-attack row source keeps its authority over its own id.
    /// </summary>
    [Fact]
    public void A_held_action_reusing_a_basic_id_does_not_take_the_key_over()
    {
        var basic = BasicRows();

        var union = LawnCostRows.Union(
            basic,
            new[] { HeldAction(BasicId, Cost("stamina", ValueSpec.Of(999), ActionCostTiming.OnCommit)) });

        Assert.Same(basic[BasicId], union[BasicId]);
        Assert.Equal(5, union[BasicId][0].AmountSpec.Min);
    }

    [Fact]
    public void Union_refuses_a_null_argument_rather_than_returning_a_partial_map()
    {
        Assert.Throws<ArgumentNullException>(() => LawnCostRows.Union(null!, Array.Empty<(string ActionId, IReadOnlyList<CompiledActionCost> Costs)>()));
        Assert.Throws<ArgumentNullException>(() => LawnCostRows.Union(BasicRows(), null!));
    }
}
