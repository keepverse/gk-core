using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Shield;

/// <summary>
/// Shield pool runtime — Core-internal state above the Funnel (shield-system-spec.md).
/// Owns apply/merge/admission (§2.5), the absorb cascade (§2.4), and tick upkeep (§2.6).
/// Never writes Unity state and never enqueues the Funnel; the dispatcher passes damage
/// through <see cref="Absorb"/> and forwards only the remainder.
/// </summary>
public sealed class ShieldRuntime
{
    readonly Dictionary<string, List<ShieldInstance>> _byOwner = new(StringComparer.Ordinal);
    long _nextSeq;
    int _instanceCount;

    public bool HasAnyInstances() => _instanceCount > 0;

    /// <summary>True when Tick has anything to do — instances, queued innates, or undrained events.</summary>
    public bool HasPendingWork => _instanceCount > 0 || _pendingInnate.Count > 0 || _pendingEvents.Count > 0;

    // ---- Event collection — spec §2.6: string-stream observability, host-drained ----

    readonly List<ShieldEventRec> _pendingEvents = new();
    readonly Dictionary<string, int> _absorbAggIndex = new(StringComparer.Ordinal);   // shieldId+owner → open aggregate

    static string ElementIdOf(ShieldInstance s) =>
        s.Element is { } el ? el.ToElementId() : "none";

    void EmitEvent(string kind, ShieldInstance shield)
    {
        _pendingEvents.Add(new ShieldEventRec
        {
            Kind = kind,
            OwnerKey = shield.OwnerKey,
            ShieldId = shield.ShieldId,
            Element = ElementIdOf(shield),
            Amount = 0,
            HitCount = 0,
            Hp = Math.Max(0, shield.Hp),
            MaxHp = shield.MaxHp
        });
    }

    void AggregateAbsorbed(ShieldInstance shield, long spent, long hitCount)
    {
        var key = shield.OwnerKey + "|" + shield.ShieldId;
        if (_absorbAggIndex.TryGetValue(key, out var idx))
        {
            var rec = _pendingEvents[idx];
            rec.Amount += spent;
            rec.HitCount += hitCount;
            rec.Hp = Math.Max(0, shield.Hp);
            _pendingEvents[idx] = rec;
            return;
        }

        _absorbAggIndex[key] = _pendingEvents.Count;
        _pendingEvents.Add(new ShieldEventRec
        {
            Kind = ShieldEventKinds.Absorbed,
            OwnerKey = shield.OwnerKey,
            ShieldId = shield.ShieldId,
            Element = ElementIdOf(shield),
            Amount = spent,
            HitCount = hitCount,
            Hp = Math.Max(0, shield.Hp),
            MaxHp = shield.MaxHp
        });
    }

    /// <summary>
    /// Moves all pending events (aggregates included) into <paramref name="into"/> and clears
    /// the window. The host emits them onto the string event stream.
    ///
    /// APPENDS — it does not clear <paramref name="into"/>. That is deliberate: callers
    /// accumulate several windows into one buffer (see ShieldDeterminismAndPerfTests, which
    /// collects 50 ticks before asserting the sequence). A per-window caller owns clearing its
    /// own scratch, and must clear it on EVERY path — including an early return on a zero drain.
    /// </summary>
    public int DrainEvents(List<ShieldEventRec> into)
    {
        if (_pendingEvents.Count == 0) return 0;
        var n = _pendingEvents.Count;
        into.AddRange(_pendingEvents);
        _pendingEvents.Clear();
        _absorbAggIndex.Clear();
        return n;
    }

    /// <summary>Last tick number seen by <see cref="Tick"/> — expiry base for mid-frame grants.</summary>
    public long LastTick { get; private set; }

    // ---- Innate queue — spec §2.6 barrier: queued at registration, applied on the first
    // shield tick after the owner's derived snapshot is complete (capacity read there).
    // Granted-once markers stop registry resyncs from re-forming a broken innate shield;
    // RemoveAll (death/ptr reuse) clears them so a genuinely new actor gets its shield.

    readonly List<ShieldGrant> _pendingInnate = new();
    readonly HashSet<string> _innateGranted = new(StringComparer.Ordinal);

    public void QueueInnate(ShieldGrant grant)
    {
        if (grant == null) return;
        var marker = grant.OwnerKey + "|" + grant.SourceId;
        if (_innateGranted.Contains(marker))
            return;
        for (var i = 0; i < _pendingInnate.Count; i++)
        {
            if (_pendingInnate[i].OwnerKey == grant.OwnerKey && _pendingInnate[i].SourceId == grant.SourceId)
                return;
        }

        _pendingInnate.Add(grant);
    }

    public ShieldApplyResult Apply(ShieldGrant grant, ActorDerivedSnapshot ownerSnapshot, long nowTick)
    {
        if (grant == null) throw new ArgumentNullException(nameof(grant));
        var capacity = (long)Math.Round(
            CombatDerivedReader.ShieldCapacity(ownerSnapshot, grant.Element), MidpointRounding.AwayFromZero);
        var maxHp = grant.BaseHp + capacity;
        var expiresAt = grant.DurationTicks is { } d ? nowTick + d : (long?)null;

        if (!_byOwner.TryGetValue(grant.OwnerKey, out var stack))
        {
            stack = new List<ShieldInstance>(ShieldPolicy.MaxShieldsPerActor);
            _byOwner[grant.OwnerKey] = stack;
        }

        var elementId = grant.Element is { } el ? el.ToElementId() : "none";
        var shieldId = grant.SourceId + ":" + elementId;

        // Merge on same (sourceId, element) — §2.5.
        for (var i = 0; i < stack.Count; i++)
        {
            var existing = stack[i];
            if (!existing.ShieldId.Equals(shieldId, StringComparison.Ordinal))
                continue;
            if (maxHp <= 0)
            {
                stack.RemoveAt(i);
                _instanceCount--;
                if (stack.Count == 0) _byOwner.Remove(grant.OwnerKey);
                EmitEvent(ShieldEventKinds.Expired, existing);
                return new ShieldApplyResult(ShieldApplyOutcome.MergedRemoved, existing, null);
            }

            existing.MaxHp = maxHp;
            existing.ExpiresAtTick = expiresAt;
            existing.Hp = existing.RefillOnMerge ? maxHp : Math.Min(existing.Hp, maxHp);
            return new ShieldApplyResult(ShieldApplyOutcome.Merged, existing, null);
        }

        if (maxHp <= 0)
            return new ShieldApplyResult(ShieldApplyOutcome.Rejected, null, null);

        ShieldInstance? evicted = null;
        if (stack.Count >= ShieldPolicy.MaxShieldsPerActor)
        {
            // Admission at cap: only if strictly stronger than the weakest CURRENT pool — §2.5.
            var weakestIdx = 0;
            for (var i = 1; i < stack.Count; i++)
            {
                if (stack[i].Hp < stack[weakestIdx].Hp ||
                    (stack[i].Hp == stack[weakestIdx].Hp && stack[i].CreatedSeq > stack[weakestIdx].CreatedSeq))
                    weakestIdx = i;
            }

            if (maxHp <= stack[weakestIdx].Hp)
                return new ShieldApplyResult(ShieldApplyOutcome.DroppedWeaker, null, null);
            evicted = stack[weakestIdx];
            stack.RemoveAt(weakestIdx);
            _instanceCount--;
            // Administrative eviction — expired, never broken (no break VFX). Spec §2.5.
            EmitEvent(ShieldEventKinds.Expired, evicted);
        }

        var instance = new ShieldInstance
        {
            ShieldId = shieldId,
            OwnerKey = grant.OwnerKey,
            Element = grant.Element,
            MaxHp = maxHp,
            Hp = maxHp,
            Priority = grant.Priority,
            CreatedSeq = _nextSeq++,
            ExpiresAtTick = expiresAt,
            SourceId = grant.SourceId,
            RefillOnMerge = grant.RefillOnMerge,
            IsInnate = grant.IsInnate
        };
        InsertInDrainOrder(stack, instance);
        _instanceCount++;
        EmitEvent(ShieldEventKinds.Granted, instance);
        return new ShieldApplyResult(ShieldApplyOutcome.Applied, instance, evicted);
    }

    /// <summary>
    /// Stacks stay sorted in drain order — priority DESCENDING (outer-to-core: aura 30 before
    /// skill 20 before innate 10), CreatedSeq ascending tiebreak — so Absorb walks index 0 first.
    /// </summary>
    static void InsertInDrainOrder(List<ShieldInstance> stack, ShieldInstance instance)
    {
        var idx = stack.Count;
        for (var i = 0; i < stack.Count; i++)
        {
            if (instance.Priority > stack[i].Priority ||
                (instance.Priority == stack[i].Priority && instance.CreatedSeq < stack[i].CreatedSeq))
            {
                idx = i;
                break;
            }
        }

        stack.Insert(idx, instance);
    }

    public IReadOnlyList<ShieldInstance> GetShields(string ownerKey) =>
        _byOwner.TryGetValue(ownerKey, out var stack) ? stack : Array.Empty<ShieldInstance>();

    /// <summary>Walk every owner that currently has a shield stack (HUD / debug census).</summary>
    public void VisitOwners(Action<string, IReadOnlyList<ShieldInstance>> visit)
    {
        if (visit == null) return;
        foreach (var kv in _byOwner)
            visit(kv.Key, kv.Value);
    }

    public (long Hp, long MaxHp) Totals(string ownerKey)
    {
        if (!_byOwner.TryGetValue(ownerKey, out var stack))
            return (0, 0);
        long hp = 0, max = 0;
        for (var i = 0; i < stack.Count; i++)
        {
            hp += stack[i].Hp;
            max += stack[i].MaxHp;
        }

        return (hp, max);
    }

    /// <summary>Death/lifecycle flush — clears every shield on the actor, no events.</summary>
    public void RemoveAll(string ownerKey)
    {
        if (_byOwner.Remove(ownerKey, out var stack))
        {
            _instanceCount -= stack.Count;
            _regenCarryMilliHp.Remove(ownerKey);
        }

        for (var i = _pendingInnate.Count - 1; i >= 0; i--)
        {
            if (_pendingInnate[i].OwnerKey == ownerKey)
                _pendingInnate.RemoveAt(i);
        }

        _innateGranted.RemoveWhere(m => m.StartsWith(ownerKey + "|", StringComparison.Ordinal));
    }

    /// <summary>
    /// Full reset — board/match lifecycle barrier (EffectBag.ClearAll, registry Clear).
    /// Everything goes: instances, innate queue AND granted-once markers (a new match may
    /// legitimately reuse ptrs), regen carries, and undrained events (a reset emits nothing).
    /// </summary>
    public void Clear()
    {
        _byOwner.Clear();
        _instanceCount = 0;
        _pendingInnate.Clear();
        _innateGranted.Clear();
        _regenCarryMilliHp.Clear();
        _pendingEvents.Clear();
        _absorbAggIndex.Clear();
    }

    // ---- Absorb cascade — spec §2.4 ----

    /// <summary>
    /// Cascade |damage| through the owner's stack in drain order; returns what reaches HP.
    /// No-shield fast path is a single dictionary miss with zero allocation. Broken shields
    /// are pruned inline (never revived by same-tick regen); optional out-lists collect
    /// per-shield spends and breaks for events/debug without allocating on the fast path.
    /// </summary>
    public long Absorb(
        string ownerKey,
        long damage,
        long hitCount,
        IReadOnlyList<ElementPayloadComponent> components,
        ActorDerivedSnapshot? attackerSnapshot,
        ActorDerivedSnapshot ownerSnapshot,
        List<(ShieldInstance Shield, long Spent)>? spentOut = null,
        List<ShieldInstance>? brokenOut = null)
    {
        if (damage <= 0) return damage;
        if (!_byOwner.TryGetValue(ownerKey, out var stack) || stack.Count == 0)
            return damage;

        var input = damage;
        var i = 0;
        while (input > 0 && i < stack.Count)
        {
            var shield = stack[i];
            var relPm = ShieldMath.WeightedRelationUnitPm(components, shield.Element);
            var pen = attackerSnapshot == null
                ? 0
                : (long)Math.Round(CombatDerivedReader.ShieldPen(attackerSnapshot, shield.Element),
                    MidpointRounding.AwayFromZero);
            var toughness = (long)Math.Round(CombatDerivedReader.ShieldToughness(ownerSnapshot, shield.Element),
                MidpointRounding.AwayFromZero);

            var layer = ShieldMath.AbsorbLayer(input, shield.Hp, relPm, pen - toughness, hitCount);
            shield.Hp -= layer.Spent;
            if (layer.Spent > 0)
            {
                spentOut?.Add((shield, layer.Spent));
                AggregateAbsorbed(shield, layer.Spent, hitCount);
            }

            if (shield.Hp <= 0)
            {
                stack.RemoveAt(i);
                _instanceCount--;
                brokenOut?.Add(shield);
                // Close the open aggregate so a later same-id shield starts fresh; the
                // absorbed record precedes this broken record by insertion order (spec §2.6).
                _absorbAggIndex.Remove(shield.OwnerKey + "|" + shield.ShieldId);
                EmitEvent(ShieldEventKinds.Broken, shield);
            }
            else
            {
                i++;
            }

            input = layer.Remainder;
        }

        if (stack.Count == 0)
        {
            _byOwner.Remove(ownerKey);
            // The regen carry dies with the stack — a leftover fraction must never leak
            // into a future shield on this key (ptr reuse / re-grant determinism).
            _regenCarryMilliHp.Remove(ownerKey);
        }

        return input;
    }

    // ---- Tick upkeep — spec §2.6: runs AFTER the frame's drain dispatch; regen → expiry/prune ----

    readonly Dictionary<string, long> _regenCarryMilliHp = new(StringComparer.Ordinal);
    readonly List<string> _tickOwnerScratch = new();

    /// <summary>
    /// Front-shield regen + tick expiry. Regen: exactly ONE instance per actor — the first in
    /// drain order with hp &lt; maxHp — gains (regen.omni + regen.{element}) HP/sec, integer
    /// milli-HP carry, capped at maxHp, no spill, total independent of shield count.
    /// </summary>
    public void Tick(
        long nowTick,
        long deltaMs,
        Func<string, ActorDerivedSnapshot> ownerSnapshotResolver,
        List<ShieldInstance>? expiredOut = null)
    {
        LastTick = nowTick;
        if (deltaMs <= 0) return;

        // Innate barrier drain — first tick after registration; capacity read here.
        if (_pendingInnate.Count > 0)
        {
            for (var i = 0; i < _pendingInnate.Count; i++)
            {
                var grant = _pendingInnate[i];
                _innateGranted.Add(grant.OwnerKey + "|" + grant.SourceId);
                Apply(grant, ownerSnapshotResolver(grant.OwnerKey), nowTick);
            }

            _pendingInnate.Clear();
        }

        if (_instanceCount == 0) return;

        // Deterministic owner order: stable-sorted by key (standalone discipline). Reused
        // scratch list — no LINQ, no per-tick allocation in steady state (spec §2.7).
        _tickOwnerScratch.Clear();
        foreach (var key in _byOwner.Keys)
            _tickOwnerScratch.Add(key);
        _tickOwnerScratch.Sort(StringComparer.Ordinal);
        foreach (var ownerKey in _tickOwnerScratch)
        {
            if (!_byOwner.TryGetValue(ownerKey, out var stack))
                continue;

            // Regen — front damaged shield only.
            for (var i = 0; i < stack.Count; i++)
            {
                var shield = stack[i];
                if (shield.Hp >= shield.MaxHp)
                    continue;
                var snap = ownerSnapshotResolver(ownerKey);
                var ratePm = (long)Math.Round(
                    CombatDerivedReader.ShieldRegen(snap, shield.Element) * 1000.0,
                    MidpointRounding.AwayFromZero);
                if (ratePm > 0)
                {
                    // B17 (battle-timeline-todo.md): the carry accumulates the RAW `ratePm * deltaMs`
                    // product, never an already-divided per-call increment. The previous shape —
                    // `carry += ratePm * deltaMs / 1000` — divided BEFORE accumulating, so any call
                    // where `ratePm * deltaMs < 1000` contributed exactly zero and that fractional
                    // regen was gone forever, not merely deferred. At the 1000ms-per-call cadence
                    // this engine has always used that never bit (ratePm*1000 is always >= 1000 for
                    // any ratePm > 0), which is exactly why it went unnoticed — but a caller ticking
                    // at finer granularity (the injector's eventual per-frame drive, B26) would see
                    // regen truncate to zero below 1000‰. Dividing once, only when extracting whole
                    // HP, makes the accumulator granularity-independent and produces the IDENTICAL
                    // result to the old formula at deltaMs=1000 (proven: `ratePm*1000/1_000_000 ==
                    // (ratePm*1000/1000)/1000` for every ratePm, since 1000 divides 1000 exactly).
                    _regenCarryMilliHp.TryGetValue(ownerKey, out var carry);
                    carry += ratePm * deltaMs;
                    var whole = carry / 1_000_000;
                    if (whole > 0)
                    {
                        var gain = Math.Min(whole, shield.MaxHp - shield.Hp);
                        shield.Hp += gain;
                        carry -= whole * 1_000_000;   // no spill: excess whole HP beyond cap is forfeited
                    }

                    _regenCarryMilliHp[ownerKey] = carry;
                }

                break;   // exactly one instance per actor
            }

            // Expiry after regen — an expiring shield still absorbed this frame's dispatch.
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i].ExpiresAtTick is { } at && nowTick >= at)
                {
                    expiredOut?.Add(stack[i]);
                    EmitEvent(ShieldEventKinds.Expired, stack[i]);
                    stack.RemoveAt(i);
                    _instanceCount--;
                }
            }

            if (stack.Count == 0)
            {
                _byOwner.Remove(ownerKey);
                _regenCarryMilliHp.Remove(ownerKey);
            }
        }
    }
}
