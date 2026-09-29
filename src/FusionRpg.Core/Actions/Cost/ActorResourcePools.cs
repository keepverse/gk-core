using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Actions.Cost;

/// <summary>
/// All six resource pools for one actor (spec-action-costs.md §1 — <c>hp</c>, <c>stamina</c>,
/// <c>hunger</c>, <c>spirit</c>, <c>qi</c>, <c>poise</c>). Indexed by
/// <see cref="DerivedStatChannels.ResourceIds"/>' fixed order — array-backed, no dictionary
/// allocation on <see cref="Resolve"/>. <c>max</c>/<c>ratePerMilleTick</c> are read fresh from the actor's
/// derived snapshot on every call rather than cached, since a buff or exhaustion debuff can move
/// either between reads (spec §10: exhaustion "re-evaluates on read, not only on write").
/// </summary>
public sealed class ActorResourcePools
{
    static readonly IReadOnlyList<string> Ids = DerivedStatChannels.ResourceIds;

    readonly ResourcePoolState[] _states = new ResourcePoolState[Ids.Count];

    /// <summary>All six pools start at max — the sane default for an actor with no prior run state
    /// (T18 owns loading a persisted value in its place).</summary>
    public static ActorResourcePools CreateFull(ActorDerivedSnapshot derived, long atTick)
    {
        var pools = new ActorResourcePools();
        for (var i = 0; i < Ids.Count; i++)
            pools._states[i] = new ResourcePoolState(ResourceChannelReader.Max(derived, Ids[i]), atTick);
        return pools;
    }

    /// <summary>Seeds every pool from a caller-supplied stored value (e.g. a persisted run-pool row,
    /// or a test fixture wanting a partially-drained start) — every one of the six ids must be
    /// supplied, matching the closed-set contract the rest of this module holds to.</summary>
    public static ActorResourcePools FromStored(IReadOnlyDictionary<string, long> stored, long atTick)
    {
        var pools = new ActorResourcePools();
        for (var i = 0; i < Ids.Count; i++)
        {
            if (!stored.TryGetValue(Ids[i], out var value))
                throw new ArgumentException($"missing stored value for resource id '{Ids[i]}'", nameof(stored));
            pools._states[i] = new ResourcePoolState(value, atTick);
        }
        return pools;
    }

    static int IndexOf(string resourceId)
    {
        for (var i = 0; i < Ids.Count; i++)
            if (Ids[i] == resourceId) return i;
        throw new ArgumentOutOfRangeException(nameof(resourceId), resourceId, "not one of the six registered resource ids");
    }

    public long Resolve(string resourceId, long nowTick, ActorDerivedSnapshot derived)
    {
        var idx = IndexOf(resourceId);
        return _states[idx].Resolve(nowTick, ResourceChannelReader.RegenPerMilleTick(derived, resourceId), ResourceChannelReader.Max(derived, resourceId));
    }

    /// <summary>
    /// Spends <paramref name="amount"/> from one pool if (and only if) it can afford it. On failure
    /// this pool is left byte-for-byte unchanged — not even the read-time clock anchor moves — which
    /// is what makes a caller's "validate all, consume all, roll back on any failure" (spec-action-
    /// costs.md §3) reduce to "never call Spend until every pool has already been peeked affordable":
    /// nothing here needs undoing because nothing partial is ever written.
    /// </summary>
    public bool TrySpend(string resourceId, long amount, long nowTick, ActorDerivedSnapshot derived)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), amount, "a cost is never negative");

        var idx = IndexOf(resourceId);
        var max = ResourceChannelReader.Max(derived, resourceId);
        var rate = ResourceChannelReader.RegenPerMilleTick(derived, resourceId);
        var current = _states[idx].Resolve(nowTick, rate, max);

        if (current < amount) return false;

        var settled = _states[idx].Settle(nowTick, rate, max);
        _states[idx] = settled with { Stored = settled.Stored - amount };
        return true;
    }

    /// <summary>
    /// Adds <paramref name="amount"/> to one pool — the signed-delta complement to
    /// <see cref="TrySpend"/>, for a caller that is not gating an affordability check (a cost) but
    /// applying a generic delta (E28 fix #1, spec-param-parity.md §3 row 1: a <c>resource.delta</c>
    /// atom, which can restore or drain any of the six resources, not only spend one against a
    /// pre-checked cost). <paramref name="amount"/> may be positive or negative; either way the pool
    /// is first settled at <paramref name="nowTick"/> (so regen accrued since the last touch is
    /// folded in before the delta lands, exactly like <see cref="TrySpend"/> does) and the result is
    /// clamped to <c>[0, max]</c> — never a raw dictionary write that could push a pool past its own
    /// rails. Unlike <see cref="TrySpend"/> this never refuses: a drain larger than the current value
    /// clamps to 0 rather than leaving the pool untouched, matching <see cref="ResourcePoolState.Resolve"/>'s
    /// own clamp for a decayed value nothing has settled in a while.
    /// </summary>
    public long Add(string resourceId, long amount, long nowTick, ActorDerivedSnapshot derived)
    {
        var idx = IndexOf(resourceId);
        var max = ResourceChannelReader.Max(derived, resourceId);
        var rate = ResourceChannelReader.RegenPerMilleTick(derived, resourceId);
        var settled = _states[idx].Settle(nowTick, rate, max);

        var next = settled.Stored + amount;
        // Hitting a rail discards the sub-unit carry too (S10.1): a pool clamped full must not bank
        // a windfall it hands back the instant something spends from it.
        if (next < 0) { _states[idx] = settled with { Stored = 0, Carry = 0 }; return 0; }
        if (next > max) { _states[idx] = settled with { Stored = max, Carry = 0 }; return max; }

        _states[idx] = settled with { Stored = next };
        return next;
    }

    /// <summary>Resolves and anchors every pool at <paramref name="nowTick"/>, returning the
    /// battle-end persistence shape: a bare id→value map with no clock attached (spec §2 —
    /// "lastTick is dropped"). Also advances this instance's own state, so a caller that keeps using
    /// the same <see cref="ActorResourcePools"/> after settling reads consistently from the new
    /// anchor rather than re-accruing from the old one.</summary>
    public IReadOnlyDictionary<string, long> SettleAll(long nowTick, ActorDerivedSnapshot derived)
    {
        var result = new Dictionary<string, long>(Ids.Count, StringComparer.Ordinal);
        for (var i = 0; i < Ids.Count; i++)
        {
            var settled = _states[i].Settle(nowTick, ResourceChannelReader.RegenPerMilleTick(derived, Ids[i]), ResourceChannelReader.Max(derived, Ids[i]));
            _states[i] = settled;
            result[Ids[i]] = settled.Stored;
        }
        return result;
    }
}
