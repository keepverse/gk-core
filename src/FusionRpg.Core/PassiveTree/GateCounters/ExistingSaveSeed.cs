using FusionRpg.Core.PassiveTree.State;

namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// D43 (owner, 2026-09-05) / spec-gate-counters.md §16 OQ1 / passive-tree-todo.md G5 — the one-time
/// seed for an EXISTING save. D43's own text: "D37's counters count forward and there is no event log
/// to replay, so a long-running save would show 27 trees at tier 0 beside primary trees at tier 7 —
/// which reads as broken, not as new. Each existing player gets a starting counter derived from
/// something already persisted. A one-time, auditable, stamped fiction that never runs again for a new
/// player."
///
/// <para><b>The proxy is the player's OWN persisted primary-tree investment</b> — the Commander-scope
/// aptitude-point total already sitting in <c>rpg_aptitude_allocation</c>. Three things make it the
/// right signal rather than an invented one: (1) it IS "something already persisted" (D43's own
/// requirement, no new tracked quantity); (2) it literally means "primary-tree depth"
/// (passive-tree-todo.md G5's acceptance wording — Commander points are what a primary tree's own tier
/// gate reads, spec-gate-counters.md §5.1); (3) it is already denominated in aptitude-point-equivalents,
/// the one unit every gate quantity answers in (§5.2), so no unit conversion is invented here beyond the
/// one this module already owns.</para>
///
/// <para><b>No second curve.</b> The seeded raw count is found by inverting the SAME
/// <see cref="MasteryIndex"/> ladder every organic credit already resolves through, at the family's own
/// rate key (§5.3) — never a private <c>f(commanderPoints)</c>. AGENTS.md's "one power ladder, no
/// private curves" rule, applied to a migration path exactly as strictly as to the live one.</para>
///
/// <para><b>This type never references <c>AllocationScope</c>/<c>AptitudeAllocation</c>/<c>PointBudget</c>
/// itself</b> — it takes the already-extracted `long` total, so it can sit in
/// <see cref="GateCounterBoundaryGuardTests"/>'s scanned file list alongside the rest of this namespace
/// (D35: no gate-counter file references the aptitude-allocation machinery). The actual
/// <c>rpg_aptitude_allocation</c> read is the Data-layer orchestrator's job
/// (<c>RpgStore.GateCounterSeed.cs</c>), which is deliberately NOT in that guard's list — see that
/// file's own doc comment for why reading the existing allocation as a one-time migration proxy is not
/// the thing D35/§14 forbid.</para>
///
/// <para><b>Zero in, zero out — "never runs for a new player" made structural.</b> A commander total of
/// <c>0</c> (a fresh account with no primary-tree investment at all) returns <c>0</c> here, and a
/// caller that never writes a zero-count row (§4.1's sparsity) leaves that player's save
/// byte-identical to one nobody ever seeded.</para>
/// </summary>
public static class ExistingSaveSeed
{
    /// <summary>
    /// The seeded raw lifetime count for one counter-backed subject, given the player's own persisted
    /// Commander-scope total and the FAMILY's own rate key (<c>StatusMasteryRatePoints</c> or
    /// <c>ElementMasteryRatePoints</c> — never the other family's; callers pass the right one exactly as
    /// <see cref="StatusAppliedSource"/>/<see cref="ElementMasterySource"/> already do for the live
    /// path).
    ///
    /// <para><c>index = commanderTotalPoints / ratePoints + 1</c> is the exact integer inverse of
    /// <see cref="MasteryIndex.Equivalents"/>'s <c>(index − 1) × ratePoints</c>, floored rather than
    /// rounded. Flooring under-seeds rather than over-seeds when the division is inexact — the same
    /// "errs strict, the safe direction for a gate" bias spec-gate-counters.md §3.4 states for its own
    /// parity table, applied here to the migration path instead of the live one.</para>
    /// </summary>
    public static long SeededCount(long commanderTotalPoints, long ratePoints, GateCountersTuning tuning)
    {
        if (commanderTotalPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(commanderTotalPoints), commanderTotalPoints,
                "a persisted aptitude-point total cannot be negative");
        if (ratePoints <= 0)
            throw new ArgumentOutOfRangeException(nameof(ratePoints), ratePoints,
                "a gate-counter rate must be positive");
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (commanderTotalPoints == 0) return 0; // a fresh account -- never runs for a new player.

        long index;
        checked { index = commanderTotalPoints / ratePoints + 1; }
        return MasteryIndex.CountToReach(index, tuning);
    }
}
