namespace FusionRpg.Core.Vfx;

/// <summary>One live sustained visual set — (host, statusId) keyed, TTL-bounded.</summary>
public sealed class VfxSustainedSet
{
    public string HostPtr { get; init; } = "";
    public string StatusId { get; init; } = "";
    public string CueId { get; init; } = "";
    public VfxRecipe Recipe { get; init; } = new();
    public bool HasMarker { get; init; }
    public double StartedAt { get; set; }
    public double TtlAt { get; set; }
    /// <summary>Injector-side render handle (aura slot etc.); the tracker never reads it.</summary>
    public object? RenderState { get; set; }
}

/// <summary>
/// Pure sustained-visual state machine — SPEC vfx-v3 M2. Keyed (hostPtr, statusId): re-apply
/// refreshes TTL (never duplicates, never flickers); ends come from expire cues, TTL sweep,
/// host-gone reaping, eviction, or EndAll. Caps: 24 global / 2 per host, marker-priority
/// eviction (marker-bearing sets are gameplay-reactive and survive longest). No Unity types.
/// </summary>
public sealed class VfxStateTracker
{
    readonly Dictionary<string, VfxSustainedSet> _live = new(StringComparer.OrdinalIgnoreCase);

    public int LiveCount => _live.Count;
    public IEnumerable<VfxSustainedSet> Live => _live.Values;

    static string Key(string hostPtr, string statusId) => hostPtr + "|" + statusId;

    /// <summary>
    /// Start or refresh the set for (host, statusId). Returns the started set (null on pure
    /// refresh) and any sets evicted to make room.
    /// </summary>
    public VfxSustainedSet? Start(
        string hostPtr,
        string statusId,
        string cueId,
        VfxRecipe recipe,
        int durationMs,
        double now,
        out List<VfxSustainedSet> evicted)
    {
        evicted = EmptyList;
        var key = Key(hostPtr, statusId);
        if (_live.TryGetValue(key, out var existing))
        {
            existing.TtlAt = TtlFor(durationMs, now);
            return null; // refresh — caller emits nothing, visuals continue
        }

        var set = new VfxSustainedSet
        {
            HostPtr = hostPtr,
            StatusId = statusId,
            CueId = cueId,
            Recipe = recipe,
            HasMarker = recipe.HasMarker,
            StartedAt = now,
            TtlAt = TtlFor(durationMs, now)
        };

        List<VfxSustainedSet>? evictedList = null;
        var perHost = _live.Values.Count(s => string.Equals(s.HostPtr, hostPtr, StringComparison.OrdinalIgnoreCase));
        if (perHost >= VfxSustainedRules.PerHostCap)
            Evict(s => string.Equals(s.HostPtr, hostPtr, StringComparison.OrdinalIgnoreCase), ref evictedList);
        if (_live.Count >= VfxSustainedRules.GlobalCap)
            Evict(_ => true, ref evictedList);

        _live[key] = set;
        if (evictedList != null) evicted = evictedList;
        return set;
    }

    /// <summary>Refresh TTL only (pre-admission fast path for rapid re-applies).</summary>
    public bool Refresh(string hostPtr, string statusId, int durationMs, double now)
    {
        if (!_live.TryGetValue(Key(hostPtr, statusId), out var set)) return false;
        set.TtlAt = TtlFor(durationMs, now);
        return true;
    }

    public VfxSustainedSet? End(string hostPtr, string statusId)
    {
        var key = Key(hostPtr, statusId);
        if (!_live.TryGetValue(key, out var set)) return null;
        _live.Remove(key);
        return set;
    }

    public List<VfxSustainedSet> SweepTtl(double now)
    {
        List<VfxSustainedSet>? ended = null;
        foreach (var kv in _live)
        {
            if (kv.Value.TtlAt <= now) (ended ??= new List<VfxSustainedSet>()).Add(kv.Value);
        }

        if (ended == null) return EmptyList;
        foreach (var s in ended) _live.Remove(Key(s.HostPtr, s.StatusId));
        return ended;
    }

    public List<VfxSustainedSet> EndHost(string hostPtr)
    {
        List<VfxSustainedSet>? ended = null;
        foreach (var kv in _live)
        {
            if (string.Equals(kv.Value.HostPtr, hostPtr, StringComparison.OrdinalIgnoreCase))
                (ended ??= new List<VfxSustainedSet>()).Add(kv.Value);
        }

        if (ended == null) return EmptyList;
        foreach (var s in ended) _live.Remove(Key(s.HostPtr, s.StatusId));
        return ended;
    }

    public List<VfxSustainedSet> EndAll()
    {
        if (_live.Count == 0) return EmptyList;
        var all = _live.Values.ToList();
        _live.Clear();
        return all;
    }

    /// <summary>Evict one set among candidates: non-marker before marker, oldest first.</summary>
    void Evict(Func<VfxSustainedSet, bool> candidate, ref List<VfxSustainedSet>? evicted)
    {
        VfxSustainedSet? victim = null;
        foreach (var s in _live.Values)
        {
            if (!candidate(s)) continue;
            if (victim == null
                || (victim.HasMarker && !s.HasMarker)
                || (victim.HasMarker == s.HasMarker && s.StartedAt < victim.StartedAt))
                victim = s;
        }

        if (victim == null) return;
        _live.Remove(Key(victim.HostPtr, victim.StatusId));
        (evicted ??= new List<VfxSustainedSet>()).Add(victim);
    }

    static double TtlFor(int durationMs, double now) =>
        durationMs > 0
            ? now + durationMs / 1000.0 + VfxSustainedRules.TtlGraceSeconds
            : now + VfxSustainedRules.InfiniteTtlSeconds;

    static readonly List<VfxSustainedSet> EmptyList = new();
}
