namespace FusionRpg.Core.Battle.Timeline;

/// <summary>One HP delta that just landed, before any pending death is resolved.</summary>
public readonly record struct HpDeltaEvent(string OwnerKey, long SignedDelta, long HpBefore, long HpAfter, long Tick);

/// <summary>What a listener decided about the HP delta it just observed.</summary>
public readonly record struct TriggerDecision(bool VetoDeath)
{
    public static readonly TriggerDecision Continue = new(false);
    public static readonly TriggerDecision Veto = new(true);
}

/// <summary>
/// One post-apply listener. <c>Priority</c> is the sort key <see cref="TriggerPhase"/> orders on —
/// lower fires first — with registration order as the tiebreak, the same shape
/// <see cref="ActionSlots.SortContenders"/> already uses for exactly the same reason: `List&lt;T&gt;.Sort`
/// is unstable, so an explicit tiebreak is required for replay-stable ordering, not optional.
/// </summary>
public interface ITriggerListener
{
    int Priority { get; }
    TriggerDecision OnHpDelta(in HpDeltaEvent ev);
}

/// <summary>
/// B8 / T2f — the post-apply trigger phase (spec-turn-fsm.md "The post-apply trigger phase"). A
/// slot-free, FSM-neutral dispatch point fired after every HP delta, letting content express
/// "something happened, respond" — <c>immortal</c> (death veto), <c>soul-eater</c> (on-kill),
/// <c>coward</c> (threshold crossing) — as listeners rather than inline engine branches at fixed
/// round offsets.
///
/// <para><b>This module owns only the seam.</b> It never touches <see cref="ActorTurnMachine"/> or
/// <see cref="ActionSlots"/> itself — real <c>immortal</c>/<c>soul-eater</c>/<c>coward</c> behavior
/// belongs to the trait/status layer, which is why this type is provable with fake listeners and
/// zero real actions, matching the rest of the kernel's stated bar. The caller that drives
/// <c>Downed → Dead</c> is responsible for checking <see cref="Fire"/>'s return value before
/// committing that transition — a veto here does not itself stop anything.</para>
/// </summary>
public sealed class TriggerPhase
{
    sealed class Entry
    {
        public ITriggerListener Listener = null!;
        public int RegisteredAt;
    }

    readonly List<Entry> _entries = new();
    bool _sorted = true;

    /// <summary>Adds a listener. Order among equal-<see cref="ITriggerListener.Priority"/> listeners
    /// is registration order, forever — re-registering does not move a listener.</summary>
    public void Register(ITriggerListener listener)
    {
        if (listener is null) throw new ArgumentNullException(nameof(listener));
        _entries.Add(new Entry { Listener = listener, RegisteredAt = _entries.Count });
        _sorted = false;
    }

    /// <summary>
    /// Fires every registered listener, in deterministic order, for one HP delta. Every listener
    /// runs regardless of an earlier veto — a listener does not get to suppress another's chance to
    /// observe the same event, only the eventual death decision.
    /// </summary>
    public bool Fire(in HpDeltaEvent ev)
    {
        EnsureSorted();
        var veto = false;
        for (var i = 0; i < _entries.Count; i++)
            if (_entries[i].Listener.OnHpDelta(ev).VetoDeath) veto = true;
        return veto;
    }

    void EnsureSorted()
    {
        if (_sorted) return;
        _entries.Sort((a, b) =>
        {
            var byPriority = a.Listener.Priority.CompareTo(b.Listener.Priority);
            return byPriority != 0 ? byPriority : a.RegisteredAt.CompareTo(b.RegisteredAt);
        });
        _sorted = true;
    }
}
