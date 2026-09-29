using FusionRpg.Core.Diagnostics;

namespace FusionRpg.Core.Stats;

public sealed class StatSystem
{
    readonly object _gate = new();
    readonly Dictionary<string, EntityBaseline> _baselines = new(StringComparer.Ordinal);
    readonly ModifierBag _sessionBag = new();
    bool _dirty;
    StatScope? _lastDirtyScope;

    public ModifierPluginRegistry Plugins { get; } = new();
    public StatContextFactory Contexts { get; } = new();
    public StatModifierFactory Modifiers { get; } = new();
    public StatComposer Composer { get; }

    /// <summary>Raised when Invalidate is called. Injector may coalesce and re-Resolve living entities.</summary>
    public event Action<StatScope?>? Invalidated;

    public StatSystem(IComposeStrategy? strategy = null) =>
        Composer = new StatComposer(strategy);

    /// <summary>Capture Y0 once per entity key; never overwrite with scaled fields.</summary>
    public EntityBaseline CaptureOrGet(string entityKey, Func<EntityBaseline> capture)
    {
        if (string.IsNullOrEmpty(entityKey)) throw new ArgumentException("entityKey required");
        lock (_gate)
        {
            if (_baselines.TryGetValue(entityKey, out var existing))
                return existing;
            var b = capture() ?? throw new InvalidOperationException("capture returned null");
            _baselines[entityKey] = b;
            return b;
        }
    }

    public bool TryGetBaseline(string entityKey, out EntityBaseline baseline)
    {
        lock (_gate) return _baselines.TryGetValue(entityKey, out baseline!);
    }

    public void ForgetEntity(string entityKey)
    {
        lock (_gate) _baselines.Remove(entityKey);
    }

    public void ClearBaselines()
    {
        lock (_gate) _baselines.Clear();
    }

    /// <summary>
    /// Session data-plane Upsert (no plugin). Survives until WithdrawSource.
    /// Plugin-owned sources should change feature state and Contribute instead.
    /// </summary>
    public void Upsert(StatModifier mod)
    {
        if (mod == null) throw new ArgumentNullException(nameof(mod));
        lock (_gate) _sessionBag.Upsert(mod);
        Invalidate();
    }

    public void Upsert(IEnumerable<StatModifier> mods)
    {
        if (mods == null) throw new ArgumentNullException(nameof(mods));
        lock (_gate)
        {
            foreach (var m in mods)
                _sessionBag.Upsert(m);
        }
        Invalidate();
    }

    public void Invalidate(StatScope? scope = null)
    {
        lock (_gate)
        {
            _dirty = true;
            _lastDirtyScope = scope;
        }
        Invalidated?.Invoke(scope);
    }

    public bool IsDirty
    {
        get { lock (_gate) return _dirty; }
    }

    /// <summary>Returns true once if dirty since last consume; clears the flag.</summary>
    public bool ConsumeDirty(out StatScope? scope)
    {
        lock (_gate)
        {
            scope = _lastDirtyScope;
            if (!_dirty)
                return false;
            _dirty = false;
            _lastDirtyScope = null;
            return true;
        }
    }

    /// <summary>Removes session-bag mods only. Does not stop plugins from re-emitting on Contribute.</summary>
    public void WithdrawSource(string sourceKind, string sourceId)
    {
        lock (_gate) _sessionBag.Withdraw(sourceKind, sourceId);
        Invalidate();
    }

    /// <summary>True when the session bag has any mod with the given SourceKind (e.g. Foundation <c>effect</c>).</summary>
    public bool HasSessionModsBySourceKind(string sourceKind)
    {
        lock (_gate)
            return _sessionBag.All.Any(m =>
                string.Equals(m.SourceKind, sourceKind, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Snapshot session mods (optionally filtered by SourceKind) for debug / LIVE assert.</summary>
    public IReadOnlyList<StatModifier> ListSessionMods(string? sourceKind = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(sourceKind))
                return _sessionBag.All.ToList();
            return _sessionBag.All
                .Where(m => string.Equals(m.SourceKind, sourceKind, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>Remove all session mods for a SourceKind (e.g. clear all effect:* after match reset).</summary>
    public void WithdrawAllBySourceKind(string sourceKind)
    {
        lock (_gate)
        {
            var remove = _sessionBag.All
                .Where(m => string.Equals(m.SourceKind, sourceKind, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Key)
                .ToList();
            foreach (var k in remove)
                _sessionBag.RemoveKey(k);
        }
        Invalidate();
    }

    /// <summary>Refresh plugins into a bag for this context, then compose from Y0.</summary>
    public EntityFinal Resolve(StatContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        using var _perf = PerfProbe.Measure(PerfSection.StatsResolve);
        var bag = new ModifierBag();
        foreach (var plugin in Plugins.Ordered())
            plugin.Contribute(ctx, bag);

        lock (_gate)
        {
            foreach (var m in _sessionBag.All)
            {
                if (!StatApplyScope.Matches(m.ApplyOwnerKey, ctx))
                    continue;
                bag.Upsert(m);
            }
        }

        return Composer.Compose(ctx.Baseline, bag, ctx.ApplyStats);
    }

    /// <summary>Preserve current/max ratio when max changes after resolve.</summary>
    public static long ScaleCurrentHp(long previousHp, long previousMax, long newMax)
    {
        if (previousMax <= 0) return Math.Max(1L, newMax);
        var ratio = previousHp / (double)previousMax;
        return Math.Max(1L, (long)Math.Round(newMax * ratio));
    }

    /// <summary>
    /// pushScales / reapply keep Unity-owned current HP (ratio-remap when max changes).
    /// Spawn / absolute / ForceSet keep composed <c>y.Hp</c>.
    /// </summary>
    public static bool PreserveLiveCurrentHp(string? source)
    {
        if (string.IsNullOrEmpty(source)) return false;
        if (source.Contains("absolute", StringComparison.OrdinalIgnoreCase)) return false;
        return source.Contains("pushScales", StringComparison.OrdinalIgnoreCase)
               || source.Contains("reapply", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Current HP for Writer. Preserve path never uses composed snapshot HP.</summary>
    public static long CurrentHpForWrite(
        string? source, long liveHp, long previousMax, long composedHp, long newMax) =>
        CurrentHpForWrite(PreserveLiveCurrentHp(source), liveHp, previousMax, composedHp, newMax);

    public static long CurrentHpForWrite(
        bool preserveLive, long liveHp, long previousMax, long composedHp, long newMax)
    {
        if (!preserveLive)
            return Math.Max(1L, composedHp);
        var scaled = ScaleCurrentHp(liveHp, previousMax, newMax);
        var cap = Math.Max(1L, newMax);
        return scaled > cap ? cap : scaled;
    }
}
