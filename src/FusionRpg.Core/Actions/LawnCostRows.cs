namespace FusionRpg.Core.Actions;

/// <summary>
/// combat-ai `lawn-cost-authority` (module 17, CAI4.5, spec-lawn-cost-authority.md §1): the UNION of the
/// two row sources the lawn's ONE <c>Cost.CostLedger</c> is built from — the basic-attack rows exactly
/// as they are today, plus one <see cref="ActionCostRow"/> per <see cref="CompiledActionCost"/> on every
/// action in module 16's held sets.
///
/// <para><b>This is in Core, and that is the remedy for a contradiction in the row itself.</b> The row
/// puts the row source at <c>Injector/Effects/LawnCostRowSource.cs</c> while putting its test at
/// <c>gk-core/tests/FusionRpg.Core.Tests/Actions/LawnCostAuthorityTests.cs</c> — a Core test project cannot
/// reference the injector, so as written the acceptance could never be satisfied. The BUILD belongs here
/// because it needs nothing but Core types (<see cref="ActionCostRow"/> is Core; a compiled action's
/// costs are Core), and the injector's file becomes the thin caller that reads module 16's sets and hands
/// them over. Nothing about the lawn's composition changes: one <c>CostLedger</c>, two row sources.</para>
///
/// <para><b>The input is the primitive PAIR, not a <see cref="CompiledAction"/>.</b> The caller holds
/// module 16's compiled actions and passes `(a.ActionId, a.Costs)` per action, which keeps this function
/// free of the compiled action's fourteen other members — and keeps a test from having to hand-build one,
/// which this repo's own `BasicAttackFactoryConstructionSiteTests` polices.</para>
///
/// <para><b>Copied as a SHAPE, not as a second authority</b> — the spec's own words. The construction is
/// `BattleRunState.cs:662`'s line, and what keeps "one cost authority" true is not this function but
/// <c>CostLedger</c>'s own keying: it is built per <c>actionId</c>, so adding keys cannot change the rows
/// any existing key returns. <see cref="Union"/> guarantees the stronger form of that property — a
/// pre-existing key's list is passed through <b>by reference</b> and never replaced, so "the basic
/// attack's rows are unchanged" is a property of this function rather than of how a caller copies it.</para>
/// </summary>
public static class LawnCostRows
{
    /// <summary>
    /// The union, keyed by action id. A key that is ALREADY present keeps its own rows by reference: a
    /// held action that reuses a basic-attack id is that basic attack, and the basic-attack row source is
    /// its authority — the union never rewrites what it did not create.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> Union(
        IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> basicRowsByActionId,
        IEnumerable<(string ActionId, IReadOnlyList<CompiledActionCost> Costs)> heldActions)
    {
        ArgumentNullException.ThrowIfNull(basicRowsByActionId);
        ArgumentNullException.ThrowIfNull(heldActions);

        var union = new Dictionary<string, IReadOnlyList<ActionCostRow>>(basicRowsByActionId, StringComparer.Ordinal);
        foreach (var action in heldActions)
        {
            if (union.ContainsKey(action.ActionId)) continue;

            var costs = action.Costs;
            var rows = new List<ActionCostRow>(costs.Count);
            for (var i = 0; i < costs.Count; i++)
            {
                var cost = costs[i];
                // BattleRunState.cs:662's own line, copied as a shape rather than re-derived.
                rows.Add(new ActionCostRow(action.ActionId, cost.ResourceId, cost.Amount, cost.When));
            }

            union[action.ActionId] = rows;
        }

        return union;
    }
}
