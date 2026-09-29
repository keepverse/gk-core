namespace FusionRpg.Core.Actions.Cost;

/// <summary>
/// One stock row an action's condition <b>requires the actor to hold</b>, lifted out of the authored
/// predicate tree at compile time by <see cref="ActionCompiler"/>.
///
/// <para><b>Why this type has to exist at all.</b> <c>LeafId.HoldsStock</c> interns its
/// <c>stockId</c> to a 0-3 slot index at compile time (<c>FactReader</c>'s flat probe), so by the
/// time a compiled action fires, the string naming <i>what</i> it required is gone. The leaf can
/// answer "do I hold enough?" and nothing downstream can answer "then spend what?". Carrying the
/// demand beside the compiled predicate is what makes the spend nameable.</para>
///
/// <para><b><see cref="MinQty"/> is what gets spent</b> — spec-action-costs.md §8 / §3a settle that
/// consuming the item is a <i>precondition</i> and not a cost, so there is no <c>rpg_action_cost</c>
/// row to price it and no second authored number to reach for. The amount the condition demanded is
/// the amount the firing consumes; inventing a separate <c>spendQty</c> would be two sources of truth
/// for one quantity.</para>
///
/// <para><c>long</c> rather than the leaf's <c>int</c>: a stock count is a magnitude, and AGENTS.md's
/// rule is <c>long</c> for every magnitude. Widened once here, at the boundary.</para>
/// </summary>
public readonly record struct StockDemand(string StockId, long MinQty);

/// <summary>Whether a firing action's stock demands were actually taken out of inventory.</summary>
public enum StockSpendOutcome
{
    Spent,

    /// <summary>At least one demand could not be met, so <b>nothing</b> was spent. The shortfall id
    /// is on <see cref="StockSpendResult.ShortfallStockId"/>, matching
    /// <see cref="CostPayResult.ShortfallResourceId"/>'s own single-detail shape.</summary>
    MissingStock,
}

/// <summary>The outcome of one commit-time stock spend. Mirrors <see cref="CostPayResult"/> exactly —
/// one enum, one optional detail, no second discriminated-union type.</summary>
public readonly record struct StockSpendResult(StockSpendOutcome Outcome, string? ShortfallStockId)
{
    public static readonly StockSpendResult Spent = new(StockSpendOutcome.Spent, null);

    public static StockSpendResult Missing(string stockId) => new(StockSpendOutcome.MissingStock, stockId);

    public bool IsSpent => Outcome == StockSpendOutcome.Spent;

    /// <summary>The typed refusal a caller shows the player. ⭐ This is the <b>first and only</b>
    /// raiser of <see cref="UsabilityReason.MissingStock"/>, which spec-usability-conditions.md §2
    /// listed in the result vocabulary and which had been declared and dead ever since — the leaf
    /// refused with the generic <see cref="UsabilityReason.ConditionFailed"/> instead.</summary>
    public UsabilityResult AsRefusal() =>
        IsSpent ? UsabilityResult.Usable : UsabilityResult.Refuse(UsabilityReason.MissingStock, ShortfallStockId);
}

/// <summary>
/// The commit-time counterpart of the <c>holdsStock</c> precondition: gate 5 asks whether the actor
/// holds the stock, and <b>this takes it</b>.
///
/// <para>A seam for the same reason <see cref="IAffordabilityCheck"/> is one — the quantities live in
/// <c>rpg_item_stock</c> and Core touches no store (<c>guard-dal.ps1</c>). <c>FusionRpg.Data</c>
/// supplies the real implementation, which does the whole spend inside one transaction: the
/// conditional decrement <b>is</b> the re-check, so there is no window between "we looked" and "we
/// took".</para>
/// </summary>
public interface IStockLedger
{
    /// <summary>Spend every demand or none of them. Never partial.</summary>
    StockSpendResult TrySpend(string actorKey, string actionId, IReadOnlyList<StockDemand> demands);
}

/// <summary>
/// The inert default: <b>an action that demands stock does not fire</b> when no real ledger is wired.
///
/// <para>⛔ Deliberately the opposite posture from <see cref="AlwaysAffordable"/>. An unwired
/// affordability seam costs the player nothing; an unwired stock seam would hand out unlimited free
/// consumables, which is exactly the defect this whole path exists to close. An action with no
/// demands is unaffected, so every existing call site keeps its current behaviour.</para>
/// </summary>
public sealed class NoStockLedger : IStockLedger
{
    public static readonly NoStockLedger Instance = new();

    public StockSpendResult TrySpend(string actorKey, string actionId, IReadOnlyList<StockDemand> demands) =>
        demands is null || demands.Count == 0
            ? StockSpendResult.Spent
            : StockSpendResult.Missing(demands[0].StockId);
}

/// <summary>
/// <b>The caller the <c>holdsStock</c> leaf never had</b>, in the same sense (and the same shape) as
/// <c>AuraUpkeepDriver</c> is the caller <c>CostLedger</c> never had: the leaf, the probe and the
/// mode matrix all landed 2026-08-28 (<c>action-todo.md</c> T10) and nothing ever decremented a stack
/// afterwards, so a battle-context consumable action fired for free.
///
/// <para><b>At commit, not at landing</b> — the same rule spec-action-costs.md §3 fixes for costs
/// ("committing is what costs, not landing"). One firing spends once whether or not the action hits.
/// </para>
/// </summary>
public sealed class ActionStockCommit
{
    readonly IStockLedger _ledger;

    /// <summary>Counts real ledger calls. Test instrumentation only — no branch depends on it — and
    /// it is how the short-circuit below is proven rather than argued, exactly as
    /// <c>FactReader.Reads</c> proves gate ordering.</summary>
    public int LedgerCalls { get; private set; }

    public ActionStockCommit(IStockLedger ledger) =>
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <summary>
    /// Take <paramref name="action"/>'s authored stock demands out of <paramref name="actorKey"/>'s
    /// inventory. An action that demands nothing never reaches the ledger at all — the overwhelming
    /// majority of actions, so the cost of this call on the ordinary path is one null check.
    /// </summary>
    public StockSpendResult TryCommit(string actorKey, CompiledAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var demands = action.StockDemands;
        if (demands is null || demands.Count == 0) return StockSpendResult.Spent;

        LedgerCalls++;
        return _ledger.TrySpend(actorKey, action.ActionId, demands);
    }
}
