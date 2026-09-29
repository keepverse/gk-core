using FusionRpg.Contracts;

namespace FusionRpg.Core.Vfx;

/// <summary>Outcome of admission for one cue.</summary>
public sealed class VfxDecision
{
    public bool Admitted { get; init; }
    public string Reason { get; init; } = "";
    public VfxRecipe? Recipe { get; init; }
    /// <summary>Indices into Recipe.Primitives that survived per-kind rate limiting.</summary>
    public IReadOnlyList<int> SpecIndices { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Pure admission engine driven by the injector director — vfx-ssot.md §8 steps 1–3.
/// Owns the per-kind rate limiter (floaters group per anchor ptr, bursts/flashes per cell — §7)
/// and the global cue-per-tick cap. No Unity types; the caller supplies time.
/// </summary>
public sealed class VfxAdmission
{
    readonly VfxCatalog _catalog;
    readonly Dictionary<string, double> _lastAdmit = new(StringComparer.OrdinalIgnoreCase);
    int _admittedThisTick;
    double _lastPrune;

    public VfxAdmission(VfxCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public void BeginTick(double now)
    {
        _admittedThisTick = 0;
        if (now - _lastPrune >= 5.0 && _lastAdmit.Count > 1024)
        {
            var dead = _lastAdmit.Where(kv => now - kv.Value > 5.0).Select(kv => kv.Key).ToList();
            foreach (var k in dead) _lastAdmit.Remove(k);
            _lastPrune = now;
        }
    }

    public void Clear()
    {
        _lastAdmit.Clear();
        _admittedThisTick = 0;
    }

    public VfxDecision Decide(VfxCueDto cue, double now, bool masterOn, Func<string, bool>? isMuted = null)
    {
        if (cue == null || string.IsNullOrWhiteSpace(cue.CueId))
            return Skip(VfxSkipReasons.UnknownCue);
        if (!masterOn)
            return Skip(VfxSkipReasons.Disabled);
        if (isMuted != null && isMuted(cue.CueId))
            return Skip(VfxSkipReasons.Muted);
        if (!_catalog.TryGet(cue.CueId, out var recipe))
            return Skip(VfxSkipReasons.UnknownCue);
        if (_admittedThisTick >= VfxRules.GlobalCuePerTickCap)
            return Skip(VfxSkipReasons.Cap);

        var admitted = new List<int>(recipe.Primitives.Count);
        for (var i = 0; i < recipe.Primitives.Count; i++)
        {
            var spec = recipe.Primitives[i];
            var floater = spec.Kind == VfxPrimitiveKind.Floater;
            var interval = floater ? recipe.RateLimit.FloaterSeconds : recipe.RateLimit.BurstSeconds;
            var group = floater ? PtrGroup(cue) : CellGroup(cue);
            var key = cue.CueId + "|" + (int)spec.Kind + "|" + i + "|" + group;
            if (TryAdmit(key, now, interval))
                admitted.Add(i);
        }

        if (admitted.Count == 0)
            return Skip(VfxSkipReasons.RateLimited);

        _admittedThisTick++;
        return new VfxDecision { Admitted = true, Recipe = recipe, SpecIndices = admitted };
    }

    bool TryAdmit(string key, double now, float minInterval)
    {
        if (minInterval > 0f && _lastAdmit.TryGetValue(key, out var last) && now - last < minInterval)
            return false;
        _lastAdmit[key] = now;
        return true;
    }

    static VfxDecision Skip(string reason) => new() { Admitted = false, Reason = reason };

    /// <summary>Floaters never collapse across distinct units — group per ptr first.</summary>
    static string PtrGroup(VfxCueDto cue) =>
        !string.IsNullOrWhiteSpace(cue.TargetPtr) ? "p:" + cue.TargetPtr : CellGroup(cue);

    static string CellGroup(VfxCueDto cue)
    {
        if (cue.Col.HasValue && cue.Row.HasValue)
            return "c:" + cue.Col.Value + "," + cue.Row.Value;
        if (!string.IsNullOrWhiteSpace(cue.TargetPtr))
            return "p:" + cue.TargetPtr;
        if (cue.WorldX.HasValue && cue.WorldY.HasValue)
            return "w:" + Math.Round(cue.WorldX.Value, 1) + "," + Math.Round(cue.WorldY.Value, 1);
        return "none";
    }
}
