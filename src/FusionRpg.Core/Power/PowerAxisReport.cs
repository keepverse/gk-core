namespace FusionRpg.Core.Power;

/// <summary>One ladder's contribution to a Θ composition (spec-power-index.md §2.4, PS-6).</summary>
public sealed record PowerAxisContribution(string AxisId, long Milli, long Whole, int SharePermille);

/// <summary>
/// PS-6 made measurable: the per-axis breakdown behind a composed Θ, not just the total. Shares are
/// per-mille integers summing to 1000 ± rounding drift (asserted ≤ 1‰ by the caller's own tests) —
/// an assertion with a metric behind it, never a constant trusted forever.
/// </summary>
public sealed record PowerAxisReport(int Total, IReadOnlyList<PowerAxisContribution> Axes);
