using FusionRpg.Contracts;
using FusionRpg.Core.Diagnostics;

namespace FusionRpg.Core.Events;

public struct DrainStats
{
    public int Processed;
    public int Carried;
    public int Generations;
    public int ExpensiveDeferred;
    public long DroppedByDepth;
    /// <summary>v4: worst record age at processing time (frames), when the caller passes its frame.</summary>
    public int MaxLatencyFrames;
    /// <summary>v4: costliest single record this pass (timestamp ticks) — budget overshoot visibility.</summary>
    public long MaxRecordTicks;
}

/// <summary>
/// Frame-budgeted processor for hot-tier event records — event-pipeline-v2 plan Tasks 6–8.
///
/// Storage model (v5 — saturation fix): two tiers. The RING holds only new records from
/// hooks. The CARRY list holds the already-coalesced remainder of previous windows, drained
/// first via a cursor and NEVER re-coalesced — the previous rotate-through-the-ring design
/// re-popped and re-coalesced the whole backlog every frame, making drain cost O(backlog)
/// (v4-600z-war: 2.07M re-carries, 123 MB/5s churn, processing starved). FIFO holds by
/// construction: carry (older) drains before ring windows (newer). A dealt/taken pair still
/// lands in one ring window together (same-frame recording contract), so pair suppression is
/// unaffected. Records appended *during* processing (Unity re-entry) form the next generation,
/// processed in the same pass up to <see cref="GenerationCap"/>.
///
/// Chain containment: records carry ChainDepth; while a record is being processed,
/// <see cref="RecordDepth"/> exposes parent+1 for hooks to stamp. Records at or beyond
/// <see cref="ChainDepthLimit"/> are refused at Record time — the mechanism is hard-coded
/// (spec decision #1); only the limit value is configurable, clamped 1..8.
///
/// Session mode: no budget, no coalescing, no expensive-class deferral — LIVE prove packs
/// see v1 event-for-event behavior.
/// </summary>
public sealed class EventDrain
{
    public const int DefaultChainDepthLimit = 6;
    public const int MinChainDepthLimit = 1;
    public const int MaxChainDepthLimit = 8;

    readonly GameEventRing _ring;
    readonly EventStringInterner _matchKeys = new();
    readonly EventStringInterner _grantIds = new();
    readonly Dictionary<IntPtr, string> _ptrHex = new();
    readonly Dictionary<GameEventKind, double> _costEma = new();
    // v5: persistent already-coalesced remainder, drained from _carryCursor. Never re-coalesced.
    readonly List<GameEventRec> _carry = new();
    int _carryCursor;
    readonly Action<EffectEventDto> _process;
    readonly Func<long> _timestamp;

    int _chainDepthLimit = DefaultChainDepthLimit;
    byte _recordDepth;
    long _droppedByDepth;
    // Re-entrancy guard: Drain/FlushForPtr/FlushAllAndReset share _scratch, and processing a
    // record can fire game hooks that call back into the flush APIs (drained FA10 kills a
    // zombie → death hook → FlushForPtr). Nested entries no-op: the records stay pending and
    // the outer pass (or next frame) handles them. The nested entity's death-flush ordering
    // degrades to next-frame in that rare case — documented in the SSOT.
    bool _active;
    // Set by a nested FlushAllAndReset (match ended mid-drain); completed in Drain's finally.
    bool _resetRequested;
    long _droppedDeathBudget;
    long _droppedDeathBudgetLivingTarget;
    int _currentFrame = -1;

    // v3 A3 (reworked v5): pending-record count per actor/target ptr so FlushForPtr no-ops in
    // O(1) for ptrs with nothing pending. Hook path stays incremental (+1 on Append); drains
    // and flushes rebuild it from carry-remainder + ring at their boundaries — O(pending) dict
    // ops once per structural change, correct by construction (merges/suppression included).
    readonly Dictionary<IntPtr, int> _pendingByPtr = new();

    // lawn-hit-entry (T9a, D8): one swing (a piercing bullet's own ptr, or (ActorPtr,Frame) for
    // melee — the SAME identity ToDto already derives for EffectEventDto.SwingId) fires its
    // per-swing "action trigger" exactly once, however many victim records it carries. Counted
    // down to zero and removed on the record's own kind (CombatHit/ChainSynthetic only — the
    // "dealt" kinds ToDto maps to OnDamageDealt); a ptr-reuse-safe design, unlike a set that only
    // ever grows: once every record for a swing has drained, its entry disappears, so a bullet
    // pointer recycled for a brand-new swing later starts fresh rather than being permanently
    // "already triggered". EventCoalescer.Merge can fold two DIFFERENT swings that happen to share
    // (ActorPtr,TargetPtr) into one record (its own doc: "a coalesced record's swing id degrades to
    // the (ActorPtr,Frame) fallback") — the merged record is then treated as one swing, which can
    // leave one or two orphaned counter entries (never decremented) until the match ends and
    // FlushAllAndReset clears this dictionary outright; the trigger for the merged record's own
    // synthesized key still correctly fires (falls into the "never seen this key" defensive branch
    // below, which answers "yes, first" rather than silently blocking a real hit's trigger).
    readonly struct SwingKey : IEquatable<SwingKey>
    {
        readonly IntPtr _ptr;   // bullet ptr (nonzero) OR ActorPtr when melee (SwingPtr == Zero)
        readonly int _frame;    // meaningful only for the melee (ActorPtr,Frame) shape
        readonly bool _isBullet;

        public SwingKey(IntPtr swingPtr, IntPtr actorPtr, int frame)
        {
            if (swingPtr != IntPtr.Zero) { _ptr = swingPtr; _frame = 0; _isBullet = true; }
            else { _ptr = actorPtr; _frame = frame; _isBullet = false; }
        }

        public bool Equals(SwingKey other) =>
            _isBullet == other._isBullet && _ptr == other._ptr && (_isBullet || _frame == other._frame);
        public override bool Equals(object? obj) => obj is SwingKey k && Equals(k);
        public override int GetHashCode() => _isBullet ? HashCode.Combine(true, _ptr) : HashCode.Combine(false, _ptr, _frame);
    }

    sealed class SwingCounter
    {
        public int Pending;
        public bool Triggered;
    }

    readonly Dictionary<SwingKey, SwingCounter> _swingCounters = new();

    static bool HasSwingIdentity(GameEventKind kind) => kind is GameEventKind.CombatHit or GameEventKind.ChainSynthetic;

    void SwingBump(in GameEventRec rec, int delta)
    {
        if (!HasSwingIdentity(rec.Kind)) return;
        var key = new SwingKey(rec.SwingPtr, rec.ActorPtr, rec.Frame);
        if (delta > 0)
        {
            if (_swingCounters.TryGetValue(key, out var c)) c.Pending++;
            else _swingCounters[key] = new SwingCounter { Pending = 1 };
        }
    }

    /// <summary>Returns true exactly once per swing key — the first record of a swing to reach
    /// this call "wins" the trigger; every subsequent record for the same swing gets false. Not a
    /// swing kind at all (taken-side, OnSpawn, …) always answers true — those triggers never had a
    /// dedupe concept and must keep firing every time, matching their pre-T9 behaviour.</summary>
    bool ConsumeSwingTriggerAndRelease(in GameEventRec rec)
    {
        if (!HasSwingIdentity(rec.Kind)) return true;
        var key = new SwingKey(rec.SwingPtr, rec.ActorPtr, rec.Frame);
        if (!_swingCounters.TryGetValue(key, out var counter))
            return true; // defensive: never block a real hit's trigger on an internal bookkeeping miss
        var isFirst = !counter.Triggered;
        counter.Triggered = true;
        counter.Pending--;
        if (counter.Pending <= 0) _swingCounters.Remove(key);
        return isFirst;
    }

    // lawn-hit-entry (T9c): a nested FlushForPtr (called while `_active` — e.g. a drained record's
    // own action kills a DIFFERENT entity, whose death handler calls FlushForPtr for THAT ptr) must
    // not pull records out of `_carry`/`_ring` while the outer pass is still iterating them — but it
    // must not silently forget the ptr either, or that ptr's still-pending records would drain AFTER
    // its grants withdraw (the exact defect this class exists to prevent). So it queues the ptr
    // instead; whichever call is CURRENTLY active (Drain/FlushForPtr/FlushAllAndReset) drains this
    // queue in its own `finally`, right after `_active` flips back to false and before returning —
    // so by the time a nested caller's own caller resumes (e.g. GameHooks.cs about to withdraw
    // grants), this ptr's records have already been forced through, same as an unnested flush.
    readonly List<IntPtr> _pendingNestedFlushes = new();

    void DrainPendingNestedFlushes()
    {
        if (_pendingNestedFlushes.Count == 0) return;
        var due = _pendingNestedFlushes.ToArray();
        _pendingNestedFlushes.Clear();
        foreach (var ptr in due)
            FlushForPtr(ptr); // no longer nested here — runs for real; may itself re-queue, handled by ITS OWN finally
    }

    void IndexBump(IntPtr ptr, int delta)
    {
        if (ptr == IntPtr.Zero) return;
        _pendingByPtr.TryGetValue(ptr, out var n);
        n += delta;
        if (n <= 0) _pendingByPtr.Remove(ptr);
        else _pendingByPtr[ptr] = n;
    }

    void RebuildIndex()
    {
        _pendingByPtr.Clear();
        for (var i = _carryCursor; i < _carry.Count; i++)
        {
            IndexBump(_carry[i].ActorPtr, +1);
            IndexBump(_carry[i].TargetPtr, +1);
        }
        _ring.ForEach(rec =>
        {
            IndexBump(rec.ActorPtr, +1);
            IndexBump(rec.TargetPtr, +1);
        });
    }

    bool Append(in GameEventRec rec)
    {
        if (!_ring.TryAppend(rec))
        {
            // lawn-hit-entry (T9b, D9): the ring's own fixed capacity used to mean "drop the
            // incoming record, counted" (GameEventRing's own doc, event-pipeline-v2-ssot.md §3.3
            // "drop droppable kinds with a counter"). That was correct while combat.hit had no
            // consumer; every record that reaches Record() now already passed a live-grant gate at
            // the caller (EventDrainHost.TryRecordDealtFromBullet/TryRecordTaken/TryRecordMeleeDealt
            // all refuse to record without HasOnDamageDealtGrant()/HasOnDamageTakenGrant() first),
            // so EVERY record the ring could overflow on is now effect-bearing — dropping it here
            // is exactly the "dropping gameplay, not telemetry" case the spec forbids. Divert to
            // the carry tier instead: it still drains (this pass or a later one), never lost.
            // GameEventRing.Dropped keeps counting this as a backlog-pressure signal (still useful
            // telemetry — a rising rate means the ring is undersized for the load — it just no
            // longer means the record was lost).
            _carry.Add(rec);
            IndexBump(rec.ActorPtr, +1);
            IndexBump(rec.TargetPtr, +1);
            SwingBump(rec, +1);
            return true;
        }
        IndexBump(rec.ActorPtr, +1);
        IndexBump(rec.TargetPtr, +1);
        SwingBump(rec, +1);
        return true;
    }

    /// <summary>Pending records referencing this ptr (test/diagnostic view of the A3 index).</summary>
    public int PendingCountFor(IntPtr ptr) =>
        _pendingByPtr.TryGetValue(ptr, out var n) ? n : 0;

    public EventDrain(Action<EffectEventDto> process, Func<long>? timestamp = null, int capacity = GameEventRing.DefaultCapacity)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _timestamp = timestamp ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _ring = new GameEventRing(capacity);
    }

    public bool SessionMode { get; set; }
    public int GenerationCap { get; set; } = 3;

    /// <summary>A kind whose EMA cost exceeds this fraction of the budget is "expensive": max 1 per pass.
    /// v4: tightened 0.5 → 0.35 — single-record overshoot was the main budget violation under war load.</summary>
    public double ExpensiveBudgetFraction { get; set; } = 0.35;

    public int ChainDepthLimit
    {
        get => _chainDepthLimit;
        set => _chainDepthLimit = Math.Clamp(value, MinChainDepthLimit, MaxChainDepthLimit);
    }

    public int PendingCount => (_carry.Count - _carryCursor) + _ring.Count;
    public long DroppedByOverflow => _ring.Dropped;
    public long DroppedByDepth => _droppedByDepth;

    /// <summary>Depth to stamp on records generated while the drain is executing (0 outside a drain).</summary>
    public byte RecordDepth => _recordDepth;

    public long NextSeq() => _ring.NextSeq();
    public int InternMatchKey(string? key) => _matchKeys.Intern(key);
    public int InternGrantId(string? id) => _grantIds.Intern(id);

    public bool Record(in GameEventRec rec)
    {
        if (rec.ChainDepth >= _chainDepthLimit)
        {
            _droppedByDepth++;
            return false;
        }
        return Append(rec);
    }

    /// <summary>
    /// Per-frame drain. <paramref name="budgetTicks"/> in timestamp units (caller computes
    /// 10% of frame budget). Ignored in session mode. <paramref name="currentFrame"/> (v4)
    /// enables effect-latency measurement (record age at processing time).
    /// </summary>
    public DrainStats Drain(long budgetTicks, int currentFrame = -1)
    {
        _currentFrame = currentFrame;
        var stats = default(DrainStats);
        if (_active) return stats;
        _active = true;
        try
        {
            return DrainCore(budgetTicks, ref stats);
        }
        finally
        {
            _active = false;
            if (_resetRequested)
            {
                _resetRequested = false;
                _ring.Clear();
                _carry.Clear();
                _carryCursor = 0;
                _pendingByPtr.Clear();
                _swingCounters.Clear();
                _pendingNestedFlushes.Clear();
                _matchKeys.Clear();
                _grantIds.Clear();
                _ptrHex.Clear();
            }
            else
            {
                // T9c: any FlushForPtr calls that came in WHILE this Drain was active got queued
                // instead of run (see FlushForPtr) — this is the first safe point to run them,
                // now that _active is false again and DrainCore's own carry/ring iteration is done.
                DrainPendingNestedFlushes();
            }
        }
    }

    DrainStats DrainCore(long budgetTicks, ref DrainStats stats)
    {
        var start = _timestamp();
        var expensiveUsed = false;
        var budgetDead = false;

        // Phase 1 (v5): drain the persistent carry — already coalesced, oldest records first,
        // zero re-coalescing cost. Expensive-deferral does not apply here: these records were
        // already deferred once and must not starve.
        while (_carryCursor < _carry.Count && !_resetRequested)
        {
            if (!SessionMode && stats.Processed > 0 && _timestamp() - start >= budgetTicks)
            {
                budgetDead = true;
                break;
            }
            var carried = _carry[_carryCursor];
            _carryCursor++;
            Process(carried, ref stats);
        }
        if (_carryCursor >= _carry.Count)
        {
            _carry.Clear();
            _carryCursor = 0;
        }

        // Phase 2: new-record windows (coalesced), only when the carry fully drained.
        while (!budgetDead && !_resetRequested && _ring.Count > 0 && stats.Generations < GenerationCap)
        {
            var window = SessionMode
                ? EventCoalescer.PassthroughWindow(_ring)
                : EventCoalescer.Window(_ring);
            stats.Generations++;

            var idx = 0;
            for (; idx < window.Count; idx++)
            {
                if (_resetRequested) break; // match ended mid-drain — remainder belongs to it
                if (!SessionMode && stats.Processed > 0 && _timestamp() - start >= budgetTicks)
                    break;

                var rec = window[idx];
                if (!SessionMode && IsExpensive(rec.Kind, budgetTicks))
                {
                    if (expensiveUsed)
                    {
                        // Defer to the carry (processed next frame, after older remainder).
                        _carry.Add(rec);
                        stats.ExpensiveDeferred++;
                        continue;
                    }
                    expensiveUsed = true;
                }

                Process(rec, ref stats);
            }

            // Budget exhausted mid-window: the remainder becomes carry, in order, exactly once.
            if (!_resetRequested)
                for (; idx < window.Count; idx++)
                    _carry.Add(window[idx]);

            if (_carryCursor < _carry.Count) break; // carry exists — stop consuming generations
            if (SessionMode) continue;              // session mode keeps draining re-entrants
        }

        if (_resetRequested)
        {
            // C1 (review): a match ended inside this drain — nothing of it may resurrect.
            _carry.Clear();
            _carryCursor = 0;
        }

        stats.Carried = _carry.Count - _carryCursor;
        stats.DroppedByDepth = _droppedByDepth;
        RebuildIndex();
        return stats;
    }

    /// <summary>
    /// Death/lifecycle barrier: process pending records referencing <paramref name="ptr"/>
    /// now, keeping all others pending in order. Call before grant-withdraw so OnDeath-adjacent
    /// hits still see entity grants (SSOT §A2).
    /// v4 item 1: <paramref name="budgetTicks"/> bounds the synchronous work per death —
    /// &lt; 0 is unlimited (tests/legacy); once spent, the dying entity's REMAINING records are
    /// dropped with a counter (its grants withdraw immediately after, so late processing would
    /// find nothing — shedding is the honest semantics under extreme kill rates).
    /// Returns timestamp ticks spent, so the host can pool a per-frame death-flush allowance.
    /// T9c: returns <c>-1</c> when called from inside an already-active drain/flush pass — pulling
    /// this ptr's records out of <c>_carry</c>/<c>_ring</c> right now would corrupt the outer
    /// pass's own iteration, so the ptr is queued instead and forced through once that outer call's
    /// own `finally` runs (see <see cref="DrainPendingNestedFlushes"/>) — always before the outer
    /// call returns to ITS caller. A caller that gets <c>-1</c> must not withdraw this ptr's grants
    /// yet; a caller that gets <c>&gt;= 0</c> (handled inline, the overwhelmingly common case) may
    /// withdraw immediately, exactly as before this fix.
    /// </summary>
    public long FlushForPtr(IntPtr ptr, long budgetTicks = -1)
    {
        if (ptr == IntPtr.Zero) return 0;
        if (_active)
        {
            if (PendingCountFor(ptr) > 0 && !_pendingNestedFlushes.Contains(ptr))
                _pendingNestedFlushes.Add(ptr);
            return -1;
        }
        if (PendingCountFor(ptr) == 0) return 0; // A3: O(1) no-op — the common case per death
        var start = _timestamp();
        _active = true;
        try
        {
            var stats = default(DrainStats);

            // v5: matching records live in the carry remainder and/or the ring.
            if (_carryCursor < _carry.Count)
            {
                var keep = new List<GameEventRec>(_carry.Count - _carryCursor);
                for (var i = _carryCursor; i < _carry.Count; i++)
                {
                    var rec = _carry[i];
                    if (rec.ActorPtr == ptr || rec.TargetPtr == ptr)
                    {
                        if (budgetTicks >= 0 && _timestamp() - start > budgetTicks)
                            ShedForDeath(rec, ptr);
                        else
                            Process(rec, ref stats);
                    }
                    else
                    {
                        keep.Add(rec);
                    }
                }
                _carry.Clear();
                _carry.AddRange(keep);
                _carryCursor = 0;
            }

            var flushScratch = new List<GameEventRec>(_ring.Count);
            var n = _ring.Count;
            for (var i = 0; i < n && _ring.TryPop(out var rec); i++)
            {
                if (_resetRequested) break;
                if (rec.ActorPtr == ptr || rec.TargetPtr == ptr)
                {
                    if (budgetTicks >= 0 && _timestamp() - start > budgetTicks)
                        ShedForDeath(rec, ptr); // dying entity, over budget — shed, counted
                    else
                        Process(rec, ref stats);
                }
                else
                {
                    flushScratch.Add(rec);
                }
            }
            if (!_resetRequested)
                foreach (var rec in flushScratch)
                    _ring.TryAppend(rec);
        }
        finally
        {
            _active = false;
            if (_resetRequested)
            {
                // Match ended inside this flush (processed record fired board.end): complete
                // the reset here — nothing of the ended match may survive.
                _resetRequested = false;
                _ring.Clear();
                _carry.Clear();
                _carryCursor = 0;
                _pendingByPtr.Clear();
                _swingCounters.Clear();
                _pendingNestedFlushes.Clear();
                _matchKeys.Clear();
                _grantIds.Clear();
                _ptrHex.Clear();
            }
            else
            {
                RebuildIndex();
                // T9c: same as Drain's own finally — run anything a nested FlushForPtr queued
                // while THIS flush was active, now that it is safe to.
                DrainPendingNestedFlushes();
            }
        }
        return _timestamp() - start;
    }

    /// <summary>v4: records shed by the per-death flush budget (visible, never silent).</summary>
    public long DroppedByDeathBudget => _droppedDeathBudget;

    /// <summary>lawn-combat-wire L-N33: the part of <see cref="DroppedByDeathBudget"/> whose record targets someone other
    /// than the dying ptr — the dying entity was the attacker, so a living victim lost that hit's RPG delta. A shed record
    /// that targets the dying ptr loses nothing: the apply gate refuses a delta on a dead target anyway.</summary>
    public long DroppedByDeathBudgetLivingTarget => _droppedDeathBudgetLivingTarget;

    void ShedForDeath(in GameEventRec rec, IntPtr dyingPtr)
    {
        _droppedDeathBudget++;
        if (rec.TargetPtr != dyingPtr) _droppedDeathBudgetLivingTarget++;
    }

    /// <summary>Lifecycle barrier (board end / match result): drain everything, then reset interned state.</summary>
    public void FlushAllAndReset()
    {
        if (_active)
        {
            // Nested inside a drain/flush (board.end fired by a processed record): drop pending
            // records unprocessed — their match is over. _resetRequested makes the outer pass
            // discard its carry/window remainder and clear interners (C1: nothing from the
            // ended match may resurrect into the next one).
            _ring.Clear();
            _carry.Clear();
            _carryCursor = 0;
            _pendingByPtr.Clear();
            _swingCounters.Clear();
            _pendingNestedFlushes.Clear();
            _resetRequested = true;
            return;
        }
        _active = true;
        try
        {
            var stats = default(DrainStats);
            for (var i = _carryCursor; i < _carry.Count && !_resetRequested; i++)
                Process(_carry[i], ref stats);
            _carry.Clear();
            _carryCursor = 0;
            while (!_resetRequested && _ring.TryPop(out var rec))
                Process(rec, ref stats);
        }
        finally
        {
            _active = false;
            _resetRequested = false;
        }
        _ring.Clear();
        _carry.Clear();
        _carryCursor = 0;
        _pendingByPtr.Clear();
        _swingCounters.Clear();
        _pendingNestedFlushes.Clear();
        _matchKeys.Clear();
        _grantIds.Clear();
        _ptrHex.Clear();
    }

    bool IsExpensive(GameEventKind kind, long budgetTicks) =>
        budgetTicks > 0
        && _costEma.TryGetValue(kind, out var ema)
        && ema >= budgetTicks * ExpensiveBudgetFraction;

    void Process(in GameEventRec rec, ref DrainStats stats)
    {
        if (_currentFrame >= 0 && rec.Frame > 0)
        {
            var age = _currentFrame - rec.Frame;
            if (age > stats.MaxLatencyFrames) stats.MaxLatencyFrames = age;
        }

        // lawn-hit-entry (T9a, D8): consumed once, here, regardless of what the callback does with
        // it — this is the ONE place a record transitions from "pending" to "processed", so it is
        // the only correct place to decide "am I the first of my swing".
        var isFirstOfSwing = ConsumeSwingTriggerAndRelease(rec);

        var t0 = _timestamp();
        _recordDepth = (byte)Math.Min(byte.MaxValue, rec.ChainDepth + 1);
        try
        {
            _process(ToDto(rec, isFirstOfSwing));
        }
        finally
        {
            _recordDepth = 0;
        }

        var cost = _timestamp() - t0;
        if (cost > stats.MaxRecordTicks) stats.MaxRecordTicks = cost;
        _costEma[rec.Kind] = _costEma.TryGetValue(rec.Kind, out var ema)
            ? ema * 0.8 + cost * 0.2
            : cost;
        stats.Processed++;
    }

    string? PtrHex(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        if (_ptrHex.TryGetValue(ptr, out var s)) return s;
        s = ptr.ToString("X");
        _ptrHex[ptr] = s;
        return s;
    }

    /// <summary>Mirror of EffectEventAdapterCore's hot-kind mappings, from values instead of dicts.</summary>
    EffectEventDto ToDto(in GameEventRec rec, bool isFirstOfSwing = true)
    {
        var matchKey = _matchKeys.Get(rec.MatchKeyIdx);
        var sourceGrant = _grantIds.Get(rec.SourceGrantIdx);
        switch (rec.Kind)
        {
            case GameEventKind.CombatHit:
            case GameEventKind.ChainSynthetic:
                // Dealt: DTO Side is the ATTACKER side — opposite of the record's target side.
                return new EffectEventDto
                {
                    Trigger = EffectTriggers.OnDamageDealt,
                    MatchKey = matchKey,
                    Side = rec.Side == GameEventSide.Zombie ? "plant" : "zombie",
                    ActorPtr = PtrHex(rec.ActorPtr),
                    TargetPtr = PtrHex(rec.TargetPtr),
                    TypeId = rec.TypeId,
                    TargetTypeId = rec.TargetTypeId,
                    Damage = rec.Amount,
                    HitCount = rec.HitCount,
                    ChainDepth = rec.ChainDepth,
                    SourceGrantId = sourceGrant,
                    Tick = rec.Seq,
                    // lawn-hit-attribution (T6): the SWING identity, separate from ActorPtr (now the
                    // firing creature). A projectile's own ptr for a bullet hit — one bullet piercing
                    // N victims shares one SwingId across N dealt records; a melee record has no
                    // SwingPtr, so this falls back to (ActorPtr, Frame), the same identity
                    // `_meleePairsByTarget`/`_meleePairsFrame` already scope melee bookkeeping by.
                    SwingId = rec.SwingPtr != IntPtr.Zero
                        ? PtrHex(rec.SwingPtr)
                        : (rec.ActorPtr != IntPtr.Zero ? PtrHex(rec.ActorPtr) + ":" + rec.Frame : null),
                    // lawn-hit-entry (T9a, D8): exactly one of the N records sharing one SwingId
                    // reads true — see ConsumeSwingTriggerAndRelease.
                    IsFirstOfSwing = isFirstOfSwing,
                    // lawn-hit-entry (T9c): threaded straight from the record — DamagePacketBuilder
                    // is the consumer, refusing a proportional rider for this class of hit.
                    InstakillShaped = rec.InstakillShaped
                };
            case GameEventKind.PlantDamage:
            case GameEventKind.ZombieDamage:
                return new EffectEventDto
                {
                    Trigger = EffectTriggers.OnDamageTaken,
                    MatchKey = matchKey,
                    Side = rec.Kind == GameEventKind.PlantDamage ? "plant" : "zombie",
                    ActorPtr = PtrHex(rec.ActorPtr),
                    TargetPtr = PtrHex(rec.TargetPtr),
                    TypeId = rec.TargetTypeId,
                    TargetTypeId = rec.TargetTypeId,
                    Damage = rec.Amount,
                    HitCount = rec.HitCount,
                    ChainDepth = rec.ChainDepth,
                    SourceGrantId = sourceGrant,
                    Tick = rec.Seq
                };
            case GameEventKind.BulletInit:
                return new EffectEventDto
                {
                    Trigger = EffectTriggers.OnSpawn,
                    MatchKey = matchKey,
                    Side = "bullet",
                    ActorPtr = PtrHex(rec.ActorPtr),
                    TypeId = rec.TypeId,
                    HitCount = rec.HitCount,
                    Tick = rec.Seq
                };
            case GameEventKind.StatusHook:
            default:
                return new EffectEventDto
                {
                    Trigger = EffectTriggers.OnDamageTaken,
                    MatchKey = matchKey,
                    Side = rec.Side == GameEventSide.Plant ? "plant" : "zombie",
                    ActorPtr = PtrHex(rec.ActorPtr),
                    TargetPtr = PtrHex(rec.TargetPtr),
                    TypeId = rec.TypeId,
                    TargetTypeId = rec.TargetTypeId,
                    HitCount = rec.HitCount,
                    Tick = rec.Seq
                };
        }
    }
}
