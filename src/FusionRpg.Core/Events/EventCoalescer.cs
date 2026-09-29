namespace FusionRpg.Core.Events;

/// <summary>
/// Drain-entry merge window — event-pipeline-v2-ssot.md §4b.2 and plan Task 5.
/// Pops up to <c>max</c> records from the ring in FIFO order and produces the processed list:
/// 1. A paired taken record (PairId &gt; 0) is suppressed when its dealt partner is in the same
///    window — the causal replacement for the v1 DealtIdentity event-count heuristic.
/// 2. Coalescible records (ChainDepth 0, no SourceGrantId, coalescible kind) with the same
///    key merge into the earliest occurrence: Amount sums, HitCount accumulates. Merging into
///    the earliest position preserves per-target FIFO — dealt records always precede their
///    targets' takens, and non-mergeable records keep their relative order.
/// 3. Chain records, source-grant records, and paired takens whose partner is absent pass
///    through untouched in order.
/// </summary>
public static class EventCoalescer
{
    public static List<GameEventRec> Window(GameEventRing ring, int max = int.MaxValue)
    {
        var output = new List<GameEventRec>(Math.Min(ring.Count, 64));
        if (ring.Count == 0 || max <= 0) return output;

        // Key → index in output of the record to merge into.
        Dictionary<(GameEventKind, int, byte, IntPtr, IntPtr, int, int), int>? mergeIdx = null;
        // Dealt PairIds seen in this window, for taken suppression.
        HashSet<int>? dealtPairs = null;

        var taken = 0;
        while (taken < max && ring.TryPop(out var rec))
        {
            taken++;

            if (rec.PairId > 0 && rec.Kind == GameEventKind.CombatHit)
                (dealtPairs ??= new HashSet<int>()).Add(rec.PairId);

            if (rec.PairId > 0 && IsTakenKind(rec.Kind) && dealtPairs != null && dealtPairs.Contains(rec.PairId))
                continue; // suppressed: dealt partner already in this window

            if (!rec.IsCoalescible || !IsCoalescibleKind(rec.Kind))
            {
                output.Add(rec);
                continue;
            }

            var key = (rec.Kind, rec.MatchKeyIdx, rec.Side, rec.ActorPtr, rec.TargetPtr, rec.TypeId, rec.TargetTypeId);
            mergeIdx ??= new Dictionary<(GameEventKind, int, byte, IntPtr, IntPtr, int, int), int>();
            if (mergeIdx.TryGetValue(key, out var at))
            {
                output[at] = Merge(output[at], rec);
            }
            else
            {
                mergeIdx[key] = output.Count;
                output.Add(rec);
            }
        }

        return output;
    }

    /// <summary>
    /// Session-mode window (I4): no merging — event-for-event fidelity — but pair suppression
    /// still applies. The dealt/taken pair is one physical hit (a causal fact, not a
    /// coalescing optimization); v1 suppressed the taken too, via DealtIdentity.
    /// </summary>
    public static List<GameEventRec> PassthroughWindow(GameEventRing ring)
    {
        var output = new List<GameEventRec>(ring.Count);
        HashSet<int>? dealtPairs = null;
        while (ring.TryPop(out var rec))
        {
            if (rec.PairId > 0 && rec.Kind == GameEventKind.CombatHit)
                (dealtPairs ??= new HashSet<int>()).Add(rec.PairId);
            if (rec.PairId > 0 && IsTakenKind(rec.Kind) && dealtPairs != null && dealtPairs.Contains(rec.PairId))
                continue;
            output.Add(rec);
        }
        return output;
    }

    static bool IsTakenKind(GameEventKind kind) =>
        kind is GameEventKind.PlantDamage or GameEventKind.ZombieDamage;

    static bool IsCoalescibleKind(GameEventKind kind) =>
        kind is GameEventKind.CombatHit
            or GameEventKind.PlantDamage
            or GameEventKind.ZombieDamage
            or GameEventKind.BulletInit;

    static GameEventRec Merge(in GameEventRec into, in GameEventRec add)
    {
        var hits = into.HitCount + add.HitCount;
        if (hits > short.MaxValue) hits = short.MaxValue;
        return new GameEventRec(
            into.Kind, into.Frame, into.Seq,
            into.ActorPtr, into.TargetPtr,
            into.TypeId, into.TargetTypeId, into.Side,
            into.Amount + add.Amount,
            (short)hits,
            into.ChainDepth, into.SourceGrantIdx, into.MatchKeyIdx,
            into.PairId);
    }
}
