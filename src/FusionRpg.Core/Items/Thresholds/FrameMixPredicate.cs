namespace FusionRpg.Core.Items.Thresholds;

/// <summary>One filled core role on a hybrid body: which role, and which frame's ladder it came from.</summary>
public readonly record struct EquippedRoleFrame(ItemRole Role, ItemFrame Frame);

/// <summary>
/// D3's frame-mix predicate: the budget-weighted minority of a hybrid body's twelve core roles, and
/// the recovery curve that prices it.
///
/// <para><b>The count is a MIN over two buckets, not a count over one predicate.</b> A set predicate
/// asks "is this equipped item a member of set S?" — one predicate, one count. D3's asks for the
/// smaller of two independent sums, and that inversion is what makes cherry-picking and the bonus
/// mutually exclusive: taking the best base type in ten of twelve roles drives the minority sum to
/// almost nothing and parks the body at the floor.</para>
///
/// <para><b>And the count is per-mille of conceded BUDGET, never per item.</b> The six cheapest of
/// D3's twelve core roles are <c>jewel-minor-a</c> 15 + <c>jewel-minor-b</c> 15 + <c>retinue</c> 40 +
/// <c>footing</c> 50 + <c>infusion</c> 50 + <c>girdle</c> 60 = <b>230‰</b>, so an unweighted 6/6 split
/// concedes 230 of 800‰ — 28.75%, not half — and D3's "parity, bought with per-slot quality" would be
/// false as written (item-ideal.md §2g, Watch). Weighting by <c>budgetWeightMilli</c> makes the stated
/// mechanism true, and it re-orders the recovery: a 10/2 body conceding the two HEAVIEST roles beats a
/// 7/5 body conceding the five lightest.</para>
/// </summary>
public static class FrameMixPredicate
{
    /// <summary>The two buckets the <see cref="ThresholdReducer.Min"/> reduces over, in a fixed order.</summary>
    public static readonly IReadOnlyList<string> Buckets = new[] { "humanoid", "plant" };

    public static string BucketOf(ItemFrame frame) => frame == ItemFrame.Humanoid ? "humanoid" : "plant";

    /// <summary>
    /// The twelve roles a hybrid body hosts, read off the registry rather than transcribed. D3 drops
    /// <c>ward-array</c>, <c>head-guard</c> and <c>sense</c>; the surviving twelve sum to exactly 800‰.
    /// </summary>
    public static IReadOnlyDictionary<ItemRole, long> HybridCoreBudget(IReadOnlyList<ItemRoleDef> registry)
    {
        var map = new Dictionary<ItemRole, long>();
        foreach (var def in registry)
        {
            if (def.Role == ItemRole.Standard) continue;   // commander budget, never the body's
            if (!def.HybridEligible) continue;
            map[def.Role] = def.BudgetWeightMilli;
        }
        return map;
    }

    // The minority count is WEIGHTED by role budget, never by item count. long, not int: budgets are
    // permille today, but this feeds a magnitude path and docs/architecture/numeric-types.md's rule is long for anything
    // contentScale can touch. Overflow throws (checked); it never wraps.
    /// <summary>
    /// <c>min( Σ budgetWeightMilli over humanoid-equipped core roles ,
    ///         Σ budgetWeightMilli over plant-equipped core roles )</c>, in 0..parity.
    ///
    /// <para>A role outside the hybrid core contributes nothing — the twelve are enumerated from the
    /// registry, never inferred. A role filled twice (once per frame, which the set corpus does author)
    /// counts once per frame bucket, because each of those is a real concession on its own side.</para>
    /// </summary>
    public static long MinorityMilli(
        IEnumerable<EquippedRoleFrame> equipped, IReadOnlyDictionary<ItemRole, long> hybridCoreBudget)
    {
        long humanoid = 0, plant = 0;
        foreach (var e in equipped)
        {
            if (!hybridCoreBudget.TryGetValue(e.Role, out var weight)) continue;
            checked
            {
                if (e.Frame == ItemFrame.Humanoid) humanoid += weight;
                else plant += weight;
            }
        }
        return Math.Min(humanoid, plant);   // MIN over two buckets, not a count over one
    }

    /// <summary>
    /// The recovery curve, evaluated by exact integer piecewise-linear interpolation between knots.
    ///
    /// <para><b>Integer arithmetic, range-safe.</b> The interpolation is done in <c>long</c> per-mille:
    /// it widens before multiplying and divides exactly once, last (integer division truncates, so it
    /// must come last); between knots it floors, which is exact at every knot. This is a design choice
    /// for exact per-mille results, not a floating-point ban (that ban was removed 2026-09-15).</para>
    ///
    /// <para><b><paramref name="minorityMilli"/> above parity throws and is never clamped.</b> It is
    /// impossible by construction — the twelve core roles sum to 800, so the smaller of two disjoint
    /// sums is at most 400 — so reaching it means the role table changed underneath. A clamp would hide
    /// exactly that (AGENTS.md: an absolute bound is derived and throws, it never clamps silently).</para>
    /// </summary>
    public static long EffectiveBudgetMilli(long minorityMilli, FrameMixTuning tuning)
    {
        if (minorityMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(minorityMilli),
                $"minorityMilli {minorityMilli} is negative — a sum of non-negative role budgets cannot be");

        if (minorityMilli > tuning.ParityMinorityMilli)
            throw new ArgumentOutOfRangeException(nameof(minorityMilli),
                $"minorityMilli {minorityMilli} exceeds parity {tuning.ParityMinorityMilli}. This is " +
                "impossible by construction: the hybrid core roles sum to " +
                $"{tuning.HybridCoreBudgetTotalMilli}‰, so the smaller of two disjoint sums over them is " +
                "at most half that. Reaching it means the role table changed and the invariant broke — " +
                "which is exactly what a silent clamp would hide.");

        var knots = tuning.Knots;
        for (var i = 1; i < knots.Count; i++)
        {
            var lo = knots[i - 1];
            var hi = knots[i];
            if (minorityMilli > hi.MinorityMilli) continue;

            var spanX = hi.MinorityMilli - lo.MinorityMilli;
            var spanY = hi.EffectiveBudgetMilli - lo.EffectiveBudgetMilli;
            var offset = minorityMilli - lo.MinorityMilli;

            // Widen before multiplying (both operands are already long); divide LAST, exactly once.
            checked { return lo.EffectiveBudgetMilli + (spanY * offset) / spanX; }
        }

        return knots[^1].EffectiveBudgetMilli;
    }

    /// <summary>
    /// D3's bonus as a threshold consumer — the third instantiation of the one machine, sharing every
    /// line of <see cref="ThresholdEvaluator"/> with sets and charms.
    /// </summary>
    public static ThresholdConsumer<EquippedRoleFrame> Consumer(
        FrameMixTuning tuning, IReadOnlyDictionary<ItemRole, long> hybridCoreBudget) =>
        new(
            SourceKey: ThresholdContainerIds.FrameMixSource,
            BucketKey: e => hybridCoreBudget.ContainsKey(e.Role) ? BucketOf(e.Frame) : null,
            Reducer: ThresholdReducer.Min,
            Weight: e => hybridCoreBudget.TryGetValue(e.Role, out var w) ? w : 0,
            Breakpoints: tuning.TierBreakpoints(),
            Buckets: Buckets,
            Priority: tuning.TierPriority);
}
