namespace FusionRpg.Core.Match.Ai;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.7, spec-lawn-cast-trigger.md §7) — the per-frame
/// decision budget, with the overflow carried rather than dropped.
///
/// <para>More due actors than the budget is the normal case at 300 zombies, so the surplus is not
/// discarded: the due set is a FIFO queue ordered by <c>(due tick, ordinal ptr)</c>, the front
/// <c>b</c> are served this frame, and the rest are served on the frames that follow. An ordinal-only
/// order would starve high-address ptrs forever — the kind of bug that only shows up at scale — so the
/// due tick leads and the ordinal key is only the tie-break. Nobody waits more than
/// <c>ceil(due / budget)</c> frames.</para>
///
/// <para>Pure: no clock, no allocation once warm, and the caller owns the output list.</para>
/// </summary>
public sealed class LawnDecisionBudget
{
    /// <summary>Structural, NOT a balance number (tunables-ssot.md §1): it is a per-frame WORK cap —
    /// the class that section exempts and requires to say so, exactly as
    /// <c>KernelDriveHost</c>'s own budget constants do. Changing it does not change how the game
    /// feels; it changes whether the frame holds. The machine-varying half is a different quantity and
    /// lives in tuning: <c>lawn.ai.decide</c>'s share of the frame, in
    /// <c>gk-core/data/tuning/lawn-perf-budget.v1.json</c>, which this module reads and does not author.</summary>
    public const int DecisionsPerFrame = 8;

    readonly int _decisionsPerFrame;
    readonly List<Entry> _queue = new();
    readonly HashSet<string> _queued = new(StringComparer.Ordinal);

    readonly struct Entry
    {
        public Entry(long dueTick, string actorKey)
        {
            DueTick = dueTick;
            ActorKey = actorKey;
        }

        public long DueTick { get; }
        public string ActorKey { get; }
    }

    /// <param name="decisionsPerFrame">Defaults to the structural <see cref="DecisionsPerFrame"/>.</param>
    public LawnDecisionBudget(int decisionsPerFrame = DecisionsPerFrame)
    {
        if (decisionsPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(decisionsPerFrame));
        _decisionsPerFrame = decisionsPerFrame;
    }

    /// <summary>How many actors are waiting to be served.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Offers a due actor. Idempotent per actor: an actor already waiting keeps its original place in
    /// line, so a caller that re-offers every frame does not reorder the queue or duplicate entries.
    /// </summary>
    /// <param name="dueTick">The lawn tick the actor first became due — the FIFO's primary key.</param>
    public void Offer(string actorKey, long dueTick)
    {
        if (string.IsNullOrEmpty(actorKey)) throw new ArgumentException("actorKey must not be empty", nameof(actorKey));
        if (!_queued.Add(actorKey)) return;

        // Insertion into sorted position: no per-frame sort, no comparer allocation, and a total order
        // (unique keys) so the sequence is deterministic.
        var index = _queue.Count;
        while (index > 0 && Precedes(new Entry(dueTick, actorKey), _queue[index - 1])) index--;
        _queue.Insert(index, new Entry(dueTick, actorKey));
    }

    /// <summary>
    /// Serves up to the budget from the front of the queue and appends their keys to
    /// <paramref name="into"/> (the caller owns the list and may reuse it; clear it first if it is
    /// being recycled). Returns how many were served.
    /// </summary>
    public int Take(List<string> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        var served = 0;
        while (served < _decisionsPerFrame && _queue.Count > 0)
        {
            var entry = _queue[0];
            _queue.RemoveAt(0);
            _queued.Remove(entry.ActorKey);
            into.Add(entry.ActorKey);
            served++;
        }

        return served;
    }

    /// <summary>Drops an actor from the queue — the death path, or an actor whose trigger state went
    /// with it. Returns whether it was waiting.</summary>
    public bool Remove(string actorKey)
    {
        if (!_queued.Remove(actorKey)) return false;
        for (var i = 0; i < _queue.Count; i++)
        {
            if (!string.Equals(_queue[i].ActorKey, actorKey, StringComparison.Ordinal)) continue;
            _queue.RemoveAt(i);
            return true;
        }

        return false;
    }

    /// <summary>The board edge, and the kill switch turning off mid-match.</summary>
    public void Clear()
    {
        _queue.Clear();
        _queued.Clear();
    }

    /// <summary>The queue's own total order: earlier due tick first, then the ordinal ptr. Ordinal, never
    /// culture-sensitive — a culture-sensitive compare would reorder the queue between machines.</summary>
    static bool Precedes(Entry a, Entry b) =>
        a.DueTick != b.DueTick
            ? a.DueTick < b.DueTick
            : string.CompareOrdinal(a.ActorKey, b.ActorKey) < 0;
}
