using System.Linq;
using System.Text.Json;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// T30 (spec-action-catalog.md §2): rows → runtime form, once, at load. Validate (reject, never
/// coerce) → structure-budget (R1) → compile (targeting, condition, cost bounds). Every stage can
/// fail; the first failure wins and nothing partial is ever handed back — the same "validate
/// everything before committing anything" shape this whole program already uses (T17's
/// <c>CostLedger</c>, T20's <c>UnlockDiscardService</c>).
/// </summary>
public static class ActionCompiler
{
    public static (ActionRejection Rejection, CompiledAction? Compiled) Compile(
        ActionRow row,
        IReadOnlyList<ActionCostRow> costs,
        IReadOnlyList<ActionScopeRow> scopes,
        IReadOnlyCollection<string>? containerAtomIds,
        bool boardAvailable,
        RungTable rungTable,
        Func<string, int>? statusBit = null,
        Func<string, int>? elementId = null,
        Func<string, int>? stockBit = null,
        ActionBindMode mode = ActionBindMode.Battle)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(rungTable);

        var actionCheck = ActionValidator.ValidateAction(row, containerAtomIds, boardAvailable);
        if (!actionCheck.IsOk) return (actionCheck, null);

        foreach (var cost in costs)
        {
            var costCheck = ActionValidator.ValidateCost(cost);
            if (!costCheck.IsOk) return (costCheck, null);
        }

        foreach (var scope in scopes)
        {
            // containerAtomIds is non-null here -- ValidateAction above already rejected a null one.
            var scopeCheck = ActionValidator.ValidateScope(scope, containerAtomIds!);
            if (!scopeCheck.IsOk) return (scopeCheck, null);
        }

        var structureCheck = StructureBudgetGuard.Check(row, costs, scopes, rungTable);
        if (!structureCheck.IsOk) return (structureCheck, null);

        // The pre-scale's reader is gone (ST2 contract 5): costs compile at authored amount and
        // CostLedger scales once at pay time. StructureBudgetGuard above still takes rungTable.

        var conditionResult = CompileCondition(row, statusBit, elementId, stockBit, mode);
        if (!conditionResult.Rejection.IsOk) return (conditionResult.Rejection, null);

        // ST2 (spec-holder-rung-pricing.md contract 5): no pre-scale here. Costs compile at their
        // authored amount; CostLedger applies the holder's rung exactly once, with rungOf's reading.
        var compiledCosts = new List<CompiledActionCost>(costs.Count);
        foreach (var cost in costs)
            compiledCosts.Add(new CompiledActionCost(cost.ResourceId, cost.AmountSpec, cost.When));

        var compiled = new CompiledAction(
            row.ActionId, row.Kind, row.Rung, row.Tags, row.Enabled, row.Revision, row.Grantable, row.DefaultAttackEligible,
            row.ContainerId, row.Envelope, TargetSpecCompiler.Compile(row.Targeting),
            row.MinRange, row.MaxRange, row.RangeChannel, row.RequiresLineOfSight,
            conditionResult.Compiled, compiledCosts, scopes, RungBand: row.RungBand, Category: row.Category, ProjectilePenalties: row.ProjectilePenalties,
            conditionResult.StockDemands);

        return (ActionRejection.Ok, compiled);
    }

    static (ActionRejection Rejection, ICompiledPredicate Compiled, IReadOnlyList<StockDemand>? StockDemands) CompileCondition(
        ActionRow row, Func<string, int>? statusBit, Func<string, int>? elementId, Func<string, int>? stockBit, ActionBindMode mode)
    {
        if (string.IsNullOrWhiteSpace(row.ConditionsJson))
            return (ActionRejection.Ok, PredicateCompiler.Always, null);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(row.ConditionsJson); }
        catch (JsonException ex)
        {
            return (Fail(row.ActionId, ex.Message), PredicateCompiler.Always, null);
        }

        using (doc)
        {
            var readRejection = AtomJson.TryReadPredicate(doc.RootElement, out var tree);
            if (!readRejection.IsOk)
                return (Fail(row.ActionId, readRejection.Detail), PredicateCompiler.Always, null);

            if (tree is null)
                return (ActionRejection.Ok, PredicateCompiler.Always, null);

            // T10 mode matrix: PvZ lawn is a stateless observer and never reads current inventory --
            // a consumable action (one whose condition authors holdsStock) is simply not bindable
            // there. Checked on the parsed TREE, before compiling, so the refusal names the mode
            // rather than silently compiling a leaf that could never answer true.
            if (mode == ActionBindMode.Lawn && ContainsHoldsStock(tree))
                return (ActionRejection.Fail(ActionRejectionReason.ConsumableUnsupportedInMode,
                    $"{row.ActionId}: holdsStock is not bindable in {mode} mode -- the overlay never reads current inventory"),
                    PredicateCompiler.Always, null);

            // The stock demands, lifted from the TREE for the same reason the mode check reads the
            // tree: PredicateCompiler interns each stockId to a 0-3 slot, so after compiling nothing
            // downstream can still name what the action requires -- and a spend has to be nameable.
            var demands = new List<StockDemand>();
            if (!TryCollectStockDemands(tree, conjunctive: true, demands, out var ungated))
                return (ActionRejection.Fail(ActionRejectionReason.ConsumableStockDemandNotGuaranteed,
                    $"{row.ActionId}: holdsStock leaf '{ungated}' sits under an 'or'/'not', so the action " +
                    "can fire without it holding and there is no defined quantity to spend at commit. " +
                    "A holdsStock leaf must be in conjunctive position (the root, or reachable through " +
                    "'and' alone)"),
                    PredicateCompiler.Always, null);

            var compileRejection = PredicateCompiler.TryCompile(tree, statusBit, out var compiled, elementId, stockBit);
            return !compileRejection.IsOk
                ? (Fail(row.ActionId, compileRejection.Detail), PredicateCompiler.Always, null)
                : (ActionRejection.Ok, compiled, demands.Count == 0 ? null : demands);
        }
    }

    static bool ContainsHoldsStock(PredicateNode node) => node switch
    {
        PredicateNode.And a => a.Children.Any(ContainsHoldsStock),
        PredicateNode.Or o => o.Children.Any(ContainsHoldsStock),
        PredicateNode.Not n => ContainsHoldsStock(n.Child),
        PredicateNode.Leaf l => l.Id == LeafId.HoldsStock,
        _ => false,
    };

    /// <summary>
    /// Walks the tree once, collecting every <c>holdsStock</c> leaf in CONJUNCTIVE position — the
    /// root, or reachable from it through <c>and</c> alone. Those are the leaves a firing action has
    /// proven true, so they are exactly the ones it may be charged for.
    ///
    /// <para>Returns false the moment a <c>holdsStock</c> leaf is found anywhere else, naming it: an
    /// <c>or</c> branch or a <c>not</c> means the action can fire while the leaf is false, and
    /// charging for a stack the player was never required to hold is as wrong as charging nothing.
    /// </para>
    ///
    /// <para>Duplicate ids collapse to the STRICTEST demand rather than summing: two leaves asking
    /// for 1 and 2 of the same stack are jointly satisfied by holding 2, so 2 is what was required
    /// and 2 is what is taken. Summing would charge 3 for a condition 2 satisfies. First-appearance
    /// order is preserved so two logs of one action list its demands identically.</para>
    /// </summary>
    static bool TryCollectStockDemands(
        PredicateNode node, bool conjunctive, List<StockDemand> into, out string? ungatedStockId)
    {
        ungatedStockId = null;
        switch (node)
        {
            case PredicateNode.And a:
                foreach (var child in a.Children)
                    if (!TryCollectStockDemands(child, conjunctive, into, out ungatedStockId)) return false;
                return true;

            case PredicateNode.Or o:
                foreach (var child in o.Children)
                    if (!TryCollectStockDemands(child, conjunctive: false, into, out ungatedStockId)) return false;
                return true;

            case PredicateNode.Not n:
                return TryCollectStockDemands(n.Child, conjunctive: false, into, out ungatedStockId);

            case PredicateNode.Leaf { Id: LeafId.HoldsStock } l:
                if (!conjunctive)
                {
                    ungatedStockId = l.Text;
                    return false;
                }
                // A malformed leaf (no stockId, minQty below 1) is PredicateCompiler's refusal to
                // make, and it runs right after this walk. Skipping it here keeps one owner for that
                // message instead of two that could drift apart.
                if (!string.IsNullOrWhiteSpace(l.Text) && l.Value >= 1) AddDemand(into, l.Text!, l.Value);
                return true;

            default:
                return true;
        }
    }

    static void AddDemand(List<StockDemand> into, string stockId, int minQty)
    {
        for (var i = 0; i < into.Count; i++)
        {
            if (!string.Equals(into[i].StockId, stockId, StringComparison.Ordinal)) continue;
            if (minQty > into[i].MinQty) into[i] = into[i] with { MinQty = minQty };
            return;
        }
        into.Add(new StockDemand(stockId, minQty));
    }

    static ActionRejection Fail(string actionId, string detail) =>
        ActionRejection.Fail(ActionRejectionReason.BadConditionsJson, $"{actionId}: {detail}");
}
