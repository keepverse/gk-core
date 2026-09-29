namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `decision-inspector` (module 10, CAI2.5, spec-decision-inspector.md §4): the lawn's
/// bounded decision ring — the Core LOGIC, deliberately free of the injector so CI exercises it
/// (the program's own rule 4: *"Logic lives in Core, because CI never builds the injector"*; the
/// injector holds only the adapter that feeds membership edges in).
///
/// <para><b>Ring plus a last-per-actor index, and the pairing is the point.</b> A flat ring is the
/// wrong instrument on the lawn: with up to ten smart-tier uniques plus general creatures, one busy
/// actor evicts every other actor's last decision, and the first question anyone asks the inspector is
/// *"why did <b>this</b> creature do that"*. This is the same `Ring` + `_last` pairing
/// <c>CombatDebugObservability</c> already ships for overlay dumps.</para>
///
/// <para><b>The key set of the index moves on SPAWN and DEATH, not only when a decision happens.</b>
/// That is the class of miss `DESIGN-GATE.md` §2.16 logs three shipped instances of, so it is
/// enumerated rather than assumed: a recorded decision upserts; <see cref="ForgetActor"/> removes on
/// either membership edge; <see cref="Clear"/> empties both when the match ends or the run state is
/// cleared. The switch being turned off mid-match stops new records; existing entries stay readable
/// until cleared — that is the injector adapter's half and is deliberately not modelled here.</para>
///
/// <para><b>IL2CPP reuses pointers.</b> <c>entity:{ptr}</c> state must be withdrawn on death before
/// reuse (<c>overlay-control-loops.md</c> §6 rule 4). A stale index entry would attribute a new actor's
/// decisions to the old one — a fabrication by accident, which is the failure mode this module is most
/// exposed to. Entries already IN THE RING keep their original ptr and tick and are never rewritten: a
/// historical record is not falsified by a later reuse, only the live index is cleared.</para>
///
/// <para><b>Thread safety.</b> One gate, held for the whole mutation or read — the lawn records from the
/// Unity main thread but the debug surface can read from another, and a half-updated index would be
/// exactly the fabrication above.</para>
/// </summary>
public sealed class AiDecisionRing
{
    /// <summary>Structural (`tunables-ssot.md` T2) — an instrument's ring-buffer size, not a balance
    /// number, and the same shape `CombatDebugObservability.Cap` already carries.</summary>
    public const int Cap = 8;

    readonly object _gate = new();
    readonly Queue<AiDecisionRecord> _ring = new();
    readonly Dictionary<string, AiDecisionRecord> _lastByActor = new(StringComparer.Ordinal);

    /// <summary>Recent decisions, oldest first — a fresh list of the records the ring holds, so a caller
    /// can neither evict from the ring nor reorder it. The records themselves are the ones the recorder
    /// passed in and are never mutated after insertion.</summary>
    public IReadOnlyList<AiDecisionRecord> Recent()
    {
        lock (_gate) return new List<AiDecisionRecord>(_ring);
    }

    /// <summary>The last decision indexed for one actor, or <c>null</c> when none is — never a
    /// synthesised record. Four of the five fields the spec names for the lawn come from here.</summary>
    public AiDecisionRecord? LastFor(string actorKey)
    {
        if (actorKey is null) throw new ArgumentNullException(nameof(actorKey));
        lock (_gate) return _lastByActor.TryGetValue(actorKey, out var record) ? record : null;
    }

    /// <summary>One decision: upserts the per-actor index and pushes onto the ring, evicting the oldest
    /// beyond <see cref="Cap"/>. Eviction never drops the index — that is what makes "why did THIS
    /// creature do that" answerable for an actor whose entries have aged out of the ring.</summary>
    public void Record(in AiDecisionRecord record)
    {
        lock (_gate)
        {
            _lastByActor[record.ActorKey] = record;
            _ring.Enqueue(record);
            while (_ring.Count > Cap) _ring.Dequeue();
        }
    }

    /// <summary>The membership edge. Call on death AND before a reused pointer's first new decision:
    /// both orders must reach the same state, which is why this takes no "which edge" argument — the
    /// only correct action on either edge is the same one.</summary>
    public void ForgetActor(string actorKey)
    {
        if (actorKey is null) throw new ArgumentNullException(nameof(actorKey));
        lock (_gate) _lastByActor.Remove(actorKey);
    }

    /// <summary>Match ends / run state cleared: both the ring and the index.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _ring.Clear();
            _lastByActor.Clear();
        }
    }
}
