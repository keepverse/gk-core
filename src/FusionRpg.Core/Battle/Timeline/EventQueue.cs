namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// One scheduled occurrence. <c>Kind</c> and <c>Tag</c> are opaque integers here — the FSM (T2)
/// assigns their meaning, which is what keeps this module testable with no game attached.
/// </summary>
public readonly record struct ScheduledEvent(long DueTick, long Seq, string OwnerKey, int Kind, long Tag);

/// <summary>
/// Identifies a scheduled event for cancel/reschedule. Stable for the event's lifetime.
///
/// Carries the id of the queue that issued it: handles are bare integers, and without an owner a
/// handle from one queue silently addresses a <i>different</i> event in another — cancelling
/// something unrelated with no error. One queue per battle and per replay makes that a real
/// confusion, so the queue rejects foreign handles instead.
/// </summary>
public readonly record struct EventHandle(long QueueId, long Seq);

/// <summary>
/// The Future Event List: an <b>indexed</b> binary min-heap ordered by <c>(DueTick, Seq)</c>.
///
/// "Indexed" is the load-bearing word. Alongside the heap it keeps <c>seq → heap position</c>,
/// maintained by every swap, which is the standard structure for a scheduler that must cancel and
/// re-time events (the same shape as a decrease-key priority queue). It gives:
///
/// <list type="bullet">
/// <item><c>Schedule</c> / <c>PopDue</c> / <c>Cancel</c> / <c>Reschedule</c> all O(log n).</item>
/// <item>Heap size exactly equals live event count — nothing accumulates.</item>
/// <item><c>Count</c> is simply the heap's length, so it cannot drift out of step.</item>
/// </list>
///
/// The first draft used lazy deletion instead: reschedule pushed a <i>duplicate</i> entry and
/// marked the old one stale. That was the wrong pattern here — it made re-timing O(n) per call
/// with permanent heap growth (measured: 12 000 reschedules over 200 live events left a 12 200-entry
/// heap), and it required a whole staleness apparatus whose subtlest case was two live-looking
/// copies that could only be told apart by retiring the id on fire. Maintaining the index removes
/// the cost *and* deletes that entire correctness class: there are no duplicates to distinguish.
///
/// <b>Single-threaded by design.</b> No locking, and <see cref="ComparisonCount"/> is a plain
/// increment. Every consumer is expected to be one owner on one thread — a battle resolving
/// server-side, or the Unity main thread. Sharing one queue across threads would corrupt both the
/// heap and the index map, and the fix would be an owner, not a lock.
///
/// Ordering rules that byte-identical replay depends on:
/// <list type="bullet">
/// <item>Total order is <c>(DueTick, Seq)</c>; <c>Seq</c> is unique, so ties never arise between
/// distinct events and the heap need not be stable.</item>
/// <item><c>Reschedule</c> <b>preserves</b> <c>Seq</c> — re-timing must not reorder unrelated events.</item>
/// </list>
/// </summary>
public sealed class EventQueue
{
    static long _nextQueueId;

    readonly long _queueId = System.Threading.Interlocked.Increment(ref _nextQueueId);
    readonly List<ScheduledEvent> _heap;
    readonly Dictionary<long, int> _indexOf;   // seq → position in _heap
    long _nextSeq;

    /// <param name="expectedEvents">
    /// Pre-sizes both structures. The kernel ticks inside the Unity frame, so a mid-battle resize
    /// is an allocation and a copy at exactly the wrong moment; sizing once at battle start keeps
    /// the steady state allocation-free. Growing beyond it still works — it is a hint, not a cap.
    /// </param>
    public EventQueue(int expectedEvents = 64)
    {
        if (expectedEvents < 0) throw new ArgumentOutOfRangeException(nameof(expectedEvents));
        _heap = new List<ScheduledEvent>(expectedEvents);
        _indexOf = new Dictionary<long, int>(expectedEvents);
    }

    /// <summary>Live events queued. Structurally exact: it is the heap's length.</summary>
    public int Count => _heap.Count;

    /// <summary>
    /// Order comparisons performed — the cost of keeping the heap sorted. Complexity is asserted
    /// by <b>counting</b> rather than by stopwatch, since a timing test in CI measures the build
    /// agent while a count rises only when the algorithm actually degrades.
    /// </summary>
    internal long ComparisonCount { get; private set; }

    /// <summary>
    /// Element <i>lookups</i> performed — the cost of finding an event, as opposed to ordering it.
    ///
    /// This is a separate counter because <see cref="ComparisonCount"/> alone is blind to the
    /// regression these counters exist to catch. Deleting the index map and finding a heap slot by
    /// linear scan leaves the sift work — and therefore the comparison count — <b>byte-identical</b>
    /// while wall-clock blows up several-fold. Counting probes is what makes an O(n) lookup visible.
    /// </summary>
    internal long ProbeCount { get; private set; }

    public EventHandle Schedule(long dueTick, string ownerKey, int kind, long tag)
    {
        var seq = _nextSeq++;
        _heap.Add(new ScheduledEvent(dueTick, seq, ownerKey, kind, tag));
        var i = _heap.Count - 1;
        _indexOf[seq] = i;
        SiftUp(i);
        return new EventHandle(_queueId, seq);
    }

    /// <summary>Removes the event outright. False if it already fired, was cancelled, or is foreign.</summary>
    public bool Cancel(EventHandle handle)
    {
        if (!TryIndex(handle, out var i)) return false;
        RemoveAt(i);
        return true;
    }

    /// <summary>
    /// Re-times a pending event in place, keeping its <c>Seq</c> so tie-break order with unrelated
    /// events is untouched. Sifts in whichever direction the new tick requires.
    /// </summary>
    public bool Reschedule(EventHandle handle, long newDueTick)
    {
        if (!TryIndex(handle, out var i)) return false;
        _heap[i] = _heap[i] with { DueTick = newDueTick };
        // Exactly one of these does work; the other returns immediately.
        SiftUp(i);
        SiftDown(_indexOf[handle.Seq]);
        return true;
    }

    /// <summary>The earliest due tick, or null when nothing is queued.</summary>
    public long? PeekDueTick() => _heap.Count > 0 ? _heap[0].DueTick : null;

    /// <summary>
    /// Drains every event with <c>DueTick &lt;= now</c> into a caller-owned buffer, in
    /// <c>(DueTick, Seq)</c> order. The buffer is appended to, never cleared — callers own their
    /// scratch (the same contract as <c>ShieldRuntime.DrainEvents</c>).
    /// </summary>
    public int PopDue(long now, List<ScheduledEvent> into) => PopDue(now, into, int.MaxValue);

    /// <summary>
    /// Bounded form: drains at most <paramref name="max"/> due events, leaving the rest queued in
    /// order. Everything the unbounded overload guarantees still holds for what it does drain.
    ///
    /// <para><b>Why a bound exists at all.</b> The unbounded loop is correct server-side, where a
    /// battle resolves in one call and nobody is waiting on a frame. Driven per-frame in the injector
    /// it is a spike waiting to happen: a backlog of any size runs to completion inside one Unity
    /// frame on the main thread. Capping the pop keeps the scratch buffer bounded too, so a long
    /// hitch cannot make the drive allocate — which is the property the whole allocation contract
    /// rests on.</para>
    ///
    /// <para>Deferring is safe because simulated time is decoupled from wall-clock: the remaining
    /// events keep their <c>(DueTick, Seq)</c> order and fire in exactly the sequence they would
    /// have, just later. Nothing is dropped — a dropped shield expiry is a correctness bug, unlike a
    /// dropped telemetry row.</para>
    /// </summary>
    public int PopDue(long now, List<ScheduledEvent> into, int max)
    {
        if (into == null) throw new ArgumentNullException(nameof(into));
        if (max < 0) throw new ArgumentOutOfRangeException(nameof(max));
        var drained = 0;
        while (drained < max && _heap.Count > 0 && _heap[0].DueTick <= now)
        {
            into.Add(_heap[0]);
            RemoveAt(0);
            drained++;
        }

        return drained;
    }

    /// <summary>
    /// T8 (spec-turn-order-forecast.md) — the next <paramref name="max"/> events in true pop order,
    /// <b>without touching this queue</b>. Appends to <paramref name="into"/> and returns the count.
    ///
    /// <para><b>Why a sorted copy rather than k pops off a cloned heap.</b> A binary heap's array
    /// order is not pop order — only the root is guaranteed — so reading the first k slots would be
    /// wrong. Sorting a copy by the queue's own total order is O(n log n) against the pop loop's
    /// O(k log n), and that is the right trade here: a forecast runs at UI cadence, not per frame,
    /// and the sorted copy reuses the SAME `(DueTick, Seq)` comparison the heap itself is ordered by
    /// rather than restating it. Duplicating the ordering rule is exactly how a projection starts
    /// disagreeing with the thing it projects.</para>
    ///
    /// <para>Cancelled events cannot appear: <c>Cancel</c> removes them from the heap, so a copy of
    /// the heap contains only live events by construction.</para>
    /// </summary>
    internal int ProjectNext(int max, List<ScheduledEvent> into)
    {
        if (into == null) throw new ArgumentNullException(nameof(into));
        if (max < 0) throw new ArgumentOutOfRangeException(nameof(max));
        if (max == 0 || _heap.Count == 0) return 0;

        var copy = new List<ScheduledEvent>(_heap);
        copy.Sort(static (a, b) =>
        {
            var byTick = a.DueTick.CompareTo(b.DueTick);
            return byTick != 0 ? byTick : a.Seq.CompareTo(b.Seq);
        });

        var take = Math.Min(max, copy.Count);
        for (var i = 0; i < take; i++) into.Add(copy[i]);
        return take;
    }

    /// <summary>
    /// Resolves a handle to its live heap position. The map lookup is the whole liveness test:
    /// fired and cancelled events are removed from it, and a foreign or never-issued id is simply
    /// absent — so a bogus handle cannot mutate anything.
    /// </summary>
    bool TryIndex(EventHandle handle, out int index)
    {
        index = -1;
        ProbeCount++;   // one probe per lookup; a linear-scan implementation would count n
        return handle.QueueId == _queueId && _indexOf.TryGetValue(handle.Seq, out index);
    }

    // ---- indexed binary heap on (DueTick, Seq) ----

    bool Less(in ScheduledEvent a, in ScheduledEvent b)
    {
        ComparisonCount++;
        return a.DueTick != b.DueTick ? a.DueTick < b.DueTick : a.Seq < b.Seq;
    }

    void RemoveAt(int i)
    {
        _indexOf.Remove(_heap[i].Seq);
        var last = _heap.Count - 1;
        if (i == last)
        {
            _heap.RemoveAt(last);
            return;
        }

        _heap[i] = _heap[last];
        var movedSeq = _heap[i].Seq;      // captured BEFORE sifting: after SiftUp, slot i holds
        _indexOf[movedSeq] = i;           // a different element, so re-reading _heap[i] would
        _heap.RemoveAt(last);             // sift the wrong node and silently corrupt the heap.

        // The relocated element may belong above or below its new position; one of these is a no-op.
        SiftUp(i);
        SiftDown(_indexOf[movedSeq]);
    }

    void Swap(int a, int b)
    {
        (_heap[a], _heap[b]) = (_heap[b], _heap[a]);
        _indexOf[_heap[a].Seq] = a;
        _indexOf[_heap[b].Seq] = b;
    }

    void SiftUp(int i)
    {
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (!Less(_heap[i], _heap[parent])) break;
            Swap(i, parent);
            i = parent;
        }
    }

    void SiftDown(int i)
    {
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, smallest = i;
            if (l < _heap.Count && Less(_heap[l], _heap[smallest])) smallest = l;
            if (r < _heap.Count && Less(_heap[r], _heap[smallest])) smallest = r;
            if (smallest == i) break;
            Swap(i, smallest);
            i = smallest;
        }
    }
}
