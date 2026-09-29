namespace FusionRpg.Core.Actions.Rungs;

/// <summary>
/// One authored rung (spec-rung-table.md §2). Every multiplier is a per-mille integer — the
/// exponent forms documenting how these were derived (`1.75^((r-1)/2)` etc.) are never evaluated at
/// runtime; a human computed them once.
/// </summary>
/// <param name="PowerBudgetMilli">
/// A-G1 (spec-tier-access-gate.md §3.1): the most power a container reached through this rung may
/// spend, in the same per-mille unit <see cref="Effects.Atoms.Power.PowerVector"/> prices in.
/// Derived once, published, never re-derived here (`data/tuning/action-rungs.v{n}.json`'s own
/// `_meta.powerBudgetDerivation`) — this record only reads the number.
///
/// <para><c>null</c> for a table loaded before this column existed (`action-rungs.v1.json` and every
/// inline test fixture that predates it) — never <c>0</c>, which would read as "budgets nothing"
/// rather than "no ceiling data source loaded". A caller that needs the budget skips a rung reporting
/// <c>null</c>, the same "skip, do not guess" rule the rarity-keyed budget check already uses for a
/// missing ceiling.</para>
/// </param>
public sealed record RungRow(
    int Rung,
    int MinTier,
    int MaxTier,
    int PoolRolls,
    int QPowerMilli,
    int CostMulti,
    int CdMulti,
    IReadOnlyList<string> StructureBudget,
    long? PowerBudgetMilli = null);
