using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>
/// How the per-bucket counts collapse into the one number the breakpoints are looked up against.
///
/// <para><b><see cref="Min"/> is not a convenience.</b> D3's frame-mix bonus keys on the SMALLER of
/// two independent counts, and that inversion is the whole anti-cherry-pick mechanism: a body that
/// takes the best base type in ten of twelve roles drives the minority bucket to nearly nothing and
/// parks at the floor. A literal "count the things matching a predicate" evaluator cannot express it,
/// which is why a consumer supplies a bucket KEY plus a reducer rather than a boolean predicate
/// (spec-threshold-grants.md, "D3's predicate is a min over two counts").</para>
/// </summary>
public enum ThresholdReducer
{
    /// <summary>Sum one bucket — set bonuses and charm resonances.</summary>
    Sum = 0,

    /// <summary>Minimum across the consumer's declared buckets — D3's frame mix.</summary>
    Min,
}

/// <summary>
/// One breakpoint: at <paramref name="At"/> or above, <paramref name="ContainerId"/> is held.
///
/// <para>Grants are <b>cumulative</b> (ssot-sets.md §2): a wearer at four pieces holds the two-piece
/// container as well as the four-piece one. A partial set is a real build, not a consolation prize.</para>
/// </summary>
public readonly record struct ThresholdBreakpoint(long At, string ContainerId);

/// <summary>
/// One consumer of the evaluator: a bucket key over the owner's held things, a reducer, and a
/// breakpoint table. Three of these ship — set bonus, charm resonance, frame mix — and there is no
/// fourth machine anywhere in this module.
/// </summary>
/// <param name="SourceKey">
/// The <c>effect_binding.source</c> tag every binding this consumer produces carries, so the group
/// withdraws together and never collides with another consumer's rows
/// (<c>set:{set_id}</c> / <c>charm-resonance:{axis}</c> / <c>frame-mix</c>).
/// </param>
/// <param name="BucketKey">
/// The bucket a held thing falls into, or <c>null</c> when it falls into none. Not a predicate — see
/// <see cref="ThresholdReducer.Min"/>.
/// </param>
/// <param name="Weight">
/// What one held thing contributes to its bucket. <b><c>long</c>, not <c>int</c></b>: the set and
/// charm consumers pass 1, but the frame-mix consumer passes a role's <c>budgetWeightMilli</c> and
/// this feeds a magnitude path (docs/architecture/numeric-types.md — <c>long</c> for anything <c>contentScale</c> can touch).
/// </param>
/// <param name="Breakpoints">The consumer's own table. Never shared across consumer instances.</param>
/// <param name="Buckets">
/// The buckets <see cref="ThresholdReducer.Min"/> reduces over, in a fixed order. Empty for
/// <see cref="ThresholdReducer.Sum"/>, where the bucket set is whatever the held things produce.
/// </param>
/// <param name="Priority">
/// <c>effect_binding.priority</c>. <b>0</b> for set and frame-mix tiers — identical to an item binding
/// (ssot-sets.md §4.4), so the tiebreak is <c>container_id</c> ordinal and the lower tier resolves
/// first for free — and <b>-100</b> for charm bindings (ssot-charms.md §4.1), so an actor's own gear
/// reads before the account layer.
/// </param>
public sealed record ThresholdConsumer<T>(
    string SourceKey,
    Func<T, string?> BucketKey,
    ThresholdReducer Reducer,
    Func<T, long> Weight,
    IReadOnlyList<ThresholdBreakpoint> Breakpoints,
    IReadOnlyList<string> Buckets,
    int Priority);

/// <summary>What one evaluation wants: the reduced count, and the container ids it implies.</summary>
public readonly record struct ThresholdGrant(long Count, IReadOnlyList<string> WantedContainerIds);

/// <summary>
/// The exact diff between what is bound under a consumer's <c>source</c> and what should be.
///
/// <para>Re-evaluation is <b>total</b> — withdraw-and-rebind, never patch. A partial update is how
/// derived state drifts, and these bindings are derived: the durable truth is the assignment rows.</para>
/// </summary>
public readonly record struct ThresholdReconciliation(
    IReadOnlyList<string> ToBind,
    IReadOnlyList<string> ToWithdraw,
    IReadOnlyList<string> Unchanged);

/// <summary>
/// One mechanism, three consumers. Count the things an owner currently holds that fall into a bucket,
/// look up every breakpoint at or below that count, and make the owner's bindings under this
/// consumer's <c>source</c> equal that set. Nothing else.
///
/// <para>Set bonuses, charm resonances and D3's frame-mix bonus are three PREDICATES OVER ONE MACHINE,
/// not three machines (spec-threshold-grants.md, Objective). Module 16 (`sockets`) reuses this shape at
/// per-item scope; it is not folded in here because its owner is the host item's binding rather than
/// the actor, and merging the two would make the scope a parameter of a thing whose whole identity is
/// its scope.</para>
///
/// <para><b>There is deliberately no cap on how many consumers an owner may be partially through.</b>
/// I5 §3.6: "There is no cap on the number of sets a wearer may be partially in. The slot budget is
/// the cap." A <c>maxActiveSets</c> parameter here would be a hard progression ceiling wearing a
/// balance name (AGENTS.md), and it would silently undo module 13's most important authoring rule —
/// the capability sits at the LOWEST threshold precisely because two partial sets are expected.
/// <c>The_evaluator_carries_no_max_active_sets_parameter</c> pins this by reflection, so it cannot be
/// reintroduced quietly.</para>
///
/// <para>Pure, and it runs on change — a Core function with no I/O that runs with the game closed. It
/// fires on equip / unequip / attune, never per frame.</para>
/// </summary>
public static class ThresholdEvaluator
{
    static ThresholdEvaluator() => ContentRuleNamespaces.Register("threshold");

    /// <summary>Force the static constructor, so the `threshold` namespace is registered before use.</summary>
    public static void EnsureRegistered() => System.Runtime.CompilerServices.RuntimeHelpers
        .RunClassConstructor(typeof(ThresholdEvaluator).TypeHandle);

    /// <summary>Bucket totals for one owner's held things. Declared buckets are present at zero.</summary>
    public static IReadOnlyDictionary<string, long> Count<T>(
        ThresholdConsumer<T> consumer, IEnumerable<T> held)
    {
        if (consumer is null) throw new ArgumentNullException(nameof(consumer));
        if (held is null) throw new ArgumentNullException(nameof(held));

        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var bucket in consumer.Buckets) totals[bucket] = 0;

        foreach (var thing in held)
        {
            var key = consumer.BucketKey(thing);
            if (key is null) continue;

            // Min reduces over a DECLARED bucket list; a key outside it is not this consumer's.
            if (consumer.Reducer == ThresholdReducer.Min && !totals.ContainsKey(key)) continue;

            var w = consumer.Weight(thing);
            if (w < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(held), $"threshold: bucket '{key}' got a negative weight {w}");

            // checked: a weight sum is a magnitude. Overflow throws; it never wraps (AGENTS.md).
            checked { totals[key] = totals.TryGetValue(key, out var prior) ? prior + w : w; }
        }

        return totals;
    }

    /// <summary>The reduced count — `Sum` over every produced bucket, `Min` over the declared ones.</summary>
    public static long Reduce<T>(ThresholdConsumer<T> consumer, IReadOnlyDictionary<string, long> totals)
    {
        if (consumer.Reducer == ThresholdReducer.Min)
        {
            if (consumer.Buckets.Count == 0)
                throw new InvalidOperationException(
                    "threshold: a Min consumer must declare the buckets it reduces over — " +
                    "an empty list would silently reduce to zero and the grant would never fire");

            long min = long.MaxValue;
            foreach (var bucket in consumer.Buckets)
                min = Math.Min(min, totals.TryGetValue(bucket, out var v) ? v : 0);
            return min;
        }

        long sum = 0;
        checked { foreach (var v in totals.Values) sum += v; }
        return sum;
    }

    /// <summary>
    /// Count to wanted container ids. Cumulative and ordered by <c>At</c>, so the ids come back in the
    /// order the actor effect list will resolve them (which the zero pad then preserves ordinally).
    /// </summary>
    public static ThresholdGrant Grant<T>(ThresholdConsumer<T> consumer, IEnumerable<T> held)
    {
        var n = Reduce(consumer, Count(consumer, held));
        var wanted = consumer.Breakpoints
            .Where(b => b.At <= n)
            .OrderBy(b => b.At)
            .ThenBy(b => b.ContainerId, StringComparer.Ordinal)
            .Select(b => b.ContainerId)
            .ToList();
        return new ThresholdGrant(n, wanted);
    }

    /// <summary>
    /// The total reconcile. <paramref name="boundUnderThisSource"/> is exactly the container ids
    /// currently bound with <c>effect_binding.source = consumer.SourceKey</c> — never the owner's whole
    /// binding list, because withdrawing by <c>source</c> as a group is what keeps two partial sets
    /// independent (I5 §3.6: withdrawing one touches nothing of the other).
    /// </summary>
    public static ThresholdReconciliation Reconcile(
        IEnumerable<string> boundUnderThisSource, IReadOnlyList<string> wanted)
    {
        var bound = new HashSet<string>(boundUnderThisSource, StringComparer.Ordinal);
        var want = new HashSet<string>(wanted, StringComparer.Ordinal);

        return new ThresholdReconciliation(
            ToBind: wanted.Where(id => !bound.Contains(id)).ToList(),
            ToWithdraw: bound.Where(id => !want.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            Unchanged: wanted.Where(bound.Contains).ToList());
    }

    /// <summary>Evaluate and reconcile in one call — the shape every consumer actually uses.</summary>
    public static (ThresholdGrant Grant, ThresholdReconciliation Diff) Evaluate<T>(
        ThresholdConsumer<T> consumer, IEnumerable<T> held, IEnumerable<string> boundUnderThisSource)
    {
        var grant = Grant(consumer, held);
        return (grant, Reconcile(boundUnderThisSource, grant.WantedContainerIds));
    }
}
