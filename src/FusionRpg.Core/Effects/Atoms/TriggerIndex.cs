namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// One runner entry as it arrived on an actor. The entry is the unit of behaviour; the binding is
/// bookkeeping for how it got there (definitions §0 — items have no behaviour, actors do).
/// </summary>
/// <param name="Priority">Primary sort key of the actor effect list, descending.</param>
public sealed record RunnerBinding(string BindingId, int Priority, string OwnerKey, RunnerEntry Entry);

/// <summary>
/// Trigger → the bindings listening to it, resolved once at build time (spec-atom-runner.md, E15).
///
/// <para><b>No scan and no string hashing on the hot path.</b> The trigger vocabulary is closed at
/// seven, so a trigger is interned to its ordinal in <see cref="AtomTriggers.All"/> at build time and
/// the lookup becomes one array index. What the index stores is <b>slots</b> — positions in
/// <see cref="Bindings"/> — so per-binding state lives in flat arrays sized once, never in a
/// dictionary keyed by a binding id.</para>
///
/// <para>Order within a trigger is <c>(priority DESC, bindingId ASC)</c>, the order
/// <c>InMemoryEffectGrantStore.Sorted()</c> already uses. That is what makes two atoms rolling on one
/// hit consume the stream reproducibly no matter when their bindings arrived.</para>
/// </summary>
public sealed class TriggerIndex
{
    static readonly int[] NoSlots = Array.Empty<int>();

    readonly int[][] _slotsByTrigger;

    TriggerIndex(RunnerBinding[] bindings, int[][] slotsByTrigger)
    {
        Bindings = bindings;
        _slotsByTrigger = slotsByTrigger;
    }

    /// <summary>Slot → binding. A slot is stable for the life of the index.</summary>
    public RunnerBinding[] Bindings { get; }

    public int Count => Bindings.Length;

    public static readonly TriggerIndex Empty =
        new(Array.Empty<RunnerBinding>(), NewBuckets());

    /// <summary>The trigger's ordinal, or -1 when it is not one of the seven.</summary>
    public static int Ordinal(string? trigger)
    {
        if (trigger is null) return -1;
        var all = AtomTriggers.All;
        for (var i = 0; i < all.Length; i++)
            if (string.Equals(all[i], trigger, StringComparison.Ordinal)) return i;
        return -1;
    }

    public static TriggerIndex Build(IEnumerable<RunnerBinding> bindings)
    {
        var ordered = bindings
            .OrderByDescending(b => b.Priority)
            .ThenBy(b => b.BindingId, StringComparer.Ordinal)
            .ToArray();

        var buckets = NewBuckets();
        var lists = new List<int>?[AtomTriggers.All.Length];

        for (var slot = 0; slot < ordered.Length; slot++)
        {
            var trigger = ordered[slot].Entry.Trigger;
            var ordinal = Ordinal(trigger);

            // A triggerless entry is a permanent modifier, and E7 emits those as compiled Passive
            // grants. One reaching the runner means the compiler and the classifier disagree — loud,
            // because silently bucketing it would leave a modifier that never applies.
            if (ordinal < 0)
                throw new ArgumentException(
                    $"runner binding '{ordered[slot].BindingId}' carries trigger " +
                    $"'{trigger ?? "(none)"}', which is not one of the seven");

            (lists[ordinal] ??= new List<int>()).Add(slot);
        }

        for (var i = 0; i < lists.Length; i++)
            if (lists[i] is { } l) buckets[i] = l.ToArray();

        return new TriggerIndex(ordered, buckets);
    }

    /// <summary>Slots listening to a trigger ordinal, already in evaluation order.</summary>
    public ReadOnlySpan<int> SlotsFor(int triggerOrdinal) =>
        triggerOrdinal >= 0 && triggerOrdinal < _slotsByTrigger.Length
            ? _slotsByTrigger[triggerOrdinal]
            : ReadOnlySpan<int>.Empty;

    /// <summary>Convenience for callers holding a name. Not the hot path — that one takes an ordinal.</summary>
    public ReadOnlySpan<int> SlotsFor(string? trigger) => SlotsFor(Ordinal(trigger));

    static int[][] NewBuckets()
    {
        var buckets = new int[AtomTriggers.All.Length][];
        for (var i = 0; i < buckets.Length; i++) buckets[i] = NoSlots;
        return buckets;
    }
}
