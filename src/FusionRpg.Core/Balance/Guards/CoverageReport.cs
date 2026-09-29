namespace FusionRpg.Core.Balance.Guards;

/// <summary>
/// class-system-todo.md P5.2 — what a <see cref="DominanceReport"/> actually measured, alongside the
/// verdict (spec-balance-guard.md §2.1: "the guard must print coverage alongside verdict... a red row
/// must read as 'the live part of these builds is unbalanced' and never as 'this design is
/// unbalanced'"). Two independent, MEASURED reasons the dominance result overstates the problem, per
/// that section — not estimated, traced to what Phase 4 actually built and what the shipped tuning
/// actually carries:
/// </summary>
/// <param name="ElementAxis">Which element axis was live. <see cref="Analytic.StrikeMixture"/>'s own
/// doc comment (P4.1) is explicit that it is omni-only ("elements are out of scope... mirrors
/// OverlayCombatCalculator's own Components.Count == 0 branch") — every corner in this guard's matrix
/// is a 1-D slice of what a live element axis would make a 2-D matchup space.</param>
/// <param name="ReservedFamilies">Channel families registered in the shipped roster/catalog with no
/// reader yet — confirmed present in `gk-core/data/tuning/aptitudes.v1.json`'s own edges this session, not
/// assumed: `resource.efficiency.*` (spec-action-costs.md §1: registered, "has no reader" until A3
/// ships) and `move.range` (the action layer's own, unbuilt).
///
/// `skill.cooldown.*` / `skill.effectiveness.*` are a different case as of 2026-09-04 and are listed
/// here for a narrower reason: they DO have readers now (combat-unification `species-skills` S2/S3 —
/// `CooldownLedger.Start` and `OverlayCombatRequest.EffectivenessMultiplier`, both live on the shipped
/// basic attack), but those readers are on the BATTLE path, and this guard predicts with a closed-form
/// duel model that does not run one. So they remain reserved *for prediction* while no longer being
/// unbuilt — a distinction worth keeping, because "the predictor cannot see it" and "nothing reads it"
/// are different problems with different fixes.
///
/// Points spent on any of these are live in the roster but invisible to this guard's prediction.</param>
public readonly record struct CoverageReport(
    string ElementAxis,
    IReadOnlyList<string> ReservedFamilies)
{
    /// <summary>The exact framing spec-balance-guard.md §2.1 requires a red verdict to carry — an
    /// UPPER BOUND on severity, never a verdict on the design itself.</summary>
    public const string UpperBoundNote =
        "coverage is partial (see ElementAxis/ReservedFamilies) -- this is an UPPER BOUND on severity, not a verdict on the design.";
}
