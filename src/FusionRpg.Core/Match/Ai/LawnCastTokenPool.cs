namespace FusionRpg.Core.Match.Ai;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.7, spec-lawn-cast-trigger.md §8) — the cast-token
/// pool: a lease, not a mutex.
///
/// <para>Module 18's cast is instantaneous at the Funnel (build → runner → bag → flush, one call), so
/// the pool's real job is not to guard a long-running cast — it is to bound <b>casts per window across
/// all actors</b>, independently of the per-frame <i>decision</i> budget. A decision that finds nothing
/// usable costs no token; a decision that casts does.</para>
///
/// <para>A token is released on completion, actor death, interrupt, and timeout. The documented failure
/// this shape answers is <i>a token never released</i> (Doom 2016), so the timeout is the
/// <b>backstop, not the mechanism</b>: it reclaims a lease whose holder never reported, including one a
/// refusal path dropped without reporting. Release is idempotent, so a double release is a no-op and
/// the count can never go negative.</para>
/// </summary>
public sealed class LawnCastTokenPool
{
    /// <summary>Structural, NOT a balance number (tunables-ssot.md §1): a concurrency LEASE COUNT
    /// bounding casts per window across all actors — the runtime-cap class that section exempts.
    /// Raising it does not make the game more fun; it makes the worst frame worse. The smart-tier
    /// population it bounds is capped at ten by D6, so four is generous rather than tight.</summary>
    public const int CastTokens = 4;

    /// <summary>Structural, and structural in the strictest sense: set too short it reclaims a
    /// legitimate cast's lease and the system is <b>wrong</b>, not differently balanced — which is why
    /// a balance pass has no reason to touch a leak guard. Long enough that a legitimate cast never
    /// trips it, short enough that a leak self-heals in two seconds.</summary>
    public const long CastTokenTimeoutTicks = 20;

    readonly int _capacity;
    readonly long _timeoutTicks;
    readonly Dictionary<string, long> _leasedAtTick = new(StringComparer.Ordinal);

    /// <param name="capacity">Defaults to the structural <see cref="CastTokens"/>.</param>
    /// <param name="timeoutTicks">Defaults to the structural <see cref="CastTokenTimeoutTicks"/>.</param>
    public LawnCastTokenPool(int capacity = CastTokens, long timeoutTicks = CastTokenTimeoutTicks)
    {
        // A zero-capacity pool is not a tuning value, it is a permanently disabled cast path — that is
        // the kill switch's job (module 19's own flag), not a token count's. And a zero timeout would
        // reclaim a legitimate cast's lease at the tick it was taken, which makes the system wrong.
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (timeoutTicks < 1) throw new ArgumentOutOfRangeException(nameof(timeoutTicks));
        _capacity = capacity;
        _timeoutTicks = timeoutTicks;
    }

    /// <summary>Leases currently held — a reading, never a pinned population.</summary>
    public int InUse => _leasedAtTick.Count;

    /// <summary>The lease count this pool was built with.</summary>
    public int Capacity => _capacity;

    public bool IsLeased(string actorKey) => _leasedAtTick.ContainsKey(actorKey);

    /// <summary>
    /// Takes a lease for <paramref name="actorKey"/>, or returns false when every token is out. Leasing
    /// twice for the same actor is the same cast, not a second one: it returns true and consumes
    /// nothing further, because consuming a second token for one cast is exactly the leak this pool
    /// exists to make impossible.
    /// </summary>
    public bool TryLease(string actorKey, long nowTick)
    {
        if (string.IsNullOrEmpty(actorKey)) throw new ArgumentException("actorKey must not be empty", nameof(actorKey));
        if (_leasedAtTick.ContainsKey(actorKey)) return true;
        if (_leasedAtTick.Count >= _capacity) return false;

        _leasedAtTick[actorKey] = nowTick;
        return true;
    }

    /// <summary>Releases <paramref name="actorKey"/>'s lease. Idempotent — releasing a token nobody
    /// holds is a no-op that returns false, never a negative count.</summary>
    public bool Release(string actorKey)
    {
        if (string.IsNullOrEmpty(actorKey)) throw new ArgumentException("actorKey must not be empty", nameof(actorKey));
        return _leasedAtTick.Remove(actorKey);
    }

    /// <summary>
    /// Reclaims every lease whose holder never reported. Reclaims <b>at</b> the timeout, so a lease
    /// taken at tick 100 with a 20-tick timeout is reclaimed on the tick 120 call and not before.
    /// Returns how many were reclaimed.
    /// </summary>
    public int ReclaimExpired(long nowTick)
    {
        List<string>? expired = null;
        foreach (var lease in _leasedAtTick)
        {
            if (nowTick < checked(lease.Value + _timeoutTicks)) continue;
            (expired ??= new List<string>()).Add(lease.Key);
        }

        if (expired is null) return 0;
        foreach (var actorKey in expired) _leasedAtTick.Remove(actorKey);
        return expired.Count;
    }

    /// <summary>The board edge, and the kill switch turning off mid-match: release everything, because a
    /// half-armed AI holding tokens nothing will ever release is the failure this class answers.</summary>
    public void Clear() => _leasedAtTick.Clear();
}
