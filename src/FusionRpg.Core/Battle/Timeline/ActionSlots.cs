namespace FusionRpg.Core.Battle.Timeline;

/// <summary>Whether the concurrency width counts globally or per side.</summary>
public enum WScope
{
    Global,
    /// <summary>"One actor per side, alternating" — a shape a single global integer cannot express.</summary>
    PerSide
}

/// <summary>
/// The concurrency width W: how many actors may be mid-action at once. `W = 1` is a strictly
/// serialized turn, `W = N` lets many act together, and that integer is most of what separates
/// one battle mode from another.
///
/// Two properties matter more than the counting. **Contention is deterministic** — the
/// longest-waiting eligible actor takes a freed slot, ordered by `(readyTick, seq)`, never by a
/// scan over a dictionary. And **every exit path releases**, because a leaked slot deadlocks a
/// `W = 1` battle outright.
///
/// Note W only *binds* when actions have wind-up: under next-event advance with a strict total
/// order and atomic resolution, a battle is already serialized regardless of W.
/// </summary>
public sealed class ActionSlots
{
    readonly Dictionary<string, int> _heldBySide = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _sideOf = new(StringComparer.Ordinal);
    int _held;

    public ActionSlots(int width, WScope scope = WScope.Global)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width), "W must be at least 1.");
        Width = width;
        Scope = scope;
    }

    public int Width { get; }
    public WScope Scope { get; }
    public int Held => _held;

    public bool HasFreeSlot(string sideId) =>
        Scope == WScope.Global ? _held < Width : HeldBy(sideId) < Width;

    /// <summary>Takes a slot for an actor. False when the width is exhausted.</summary>
    public bool TryAcquire(string actorKey, string sideId)
    {
        if (_sideOf.ContainsKey(actorKey)) return false;   // already holding
        if (!HasFreeSlot(sideId)) return false;

        _sideOf[actorKey] = sideId;
        _heldBySide[sideId] = HeldBy(sideId) + 1;
        _held++;
        return true;
    }

    /// <summary>
    /// Releases a slot. Idempotent, because it is called from every exit path — resolve,
    /// interrupt, fizzle, death, and withdrawal — and double-release must never corrupt the count.
    /// </summary>
    public bool Release(string actorKey)
    {
        if (!_sideOf.TryGetValue(actorKey, out var sideId)) return false;
        _sideOf.Remove(actorKey);
        _heldBySide[sideId] = Math.Max(0, HeldBy(sideId) - 1);
        _held--;
        return true;
    }

    public bool Holds(string actorKey) => _sideOf.ContainsKey(actorKey);

    int HeldBy(string sideId) => _heldBySide.TryGetValue(sideId, out var n) ? n : 0;

    /// <summary>
    /// Orders contenders deterministically: earliest ready tick, then insertion sequence. Sorted
    /// explicitly rather than relying on any collection's enumeration order — a live instance of
    /// order leaking from Dictionary internals into report bytes was found in this codebase.
    /// </summary>
    public static void SortContenders(List<(string ActorKey, long ReadyTick, long Seq)> contenders)
    {
        contenders.Sort((x, y) =>
        {
            var byTick = x.ReadyTick.CompareTo(y.ReadyTick);
            if (byTick != 0) return byTick;
            var bySeq = x.Seq.CompareTo(y.Seq);
            if (bySeq != 0) return bySeq;
            // ActorKey is the final tiebreak, and it is not decoration: List<T>.Sort is introsort,
            // which is UNSTABLE and switches algorithm by length, so any pair equal on the first
            // two keys would land in a length- and pivot-dependent order. Actor keys are unique,
            // which makes this comparator a total order and the sort's instability irrelevant.
            return string.CompareOrdinal(x.ActorKey, y.ActorKey);
        });
    }
}
