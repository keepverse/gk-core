namespace FusionRpg.Core.Stats.Derived;

/// <summary>Pure derived compose — separate from primary StatComposer.</summary>
public sealed class DerivedComposer
{
    readonly DerivedStatRegistry _registry;

    public DerivedComposer(DerivedStatRegistry? registry = null) =>
        _registry = registry ?? DerivedStatRegistry.CreateDefault();

    public DerivedStatRegistry Registry => _registry;

    static readonly IReadOnlyList<DerivedModifier> NoMods = Array.Empty<DerivedModifier>();

    /// <summary>
    /// ⚡ <b>One pass over the modifiers, then one pass over the channels</b> — O(channels + mods).
    ///
    /// <para>This used to be O(channels × mods): for each of the ~261 registered channels it ran
    /// <c>mods.Where(m =&gt; m.ChannelId == channelId).ToList()</c>, a full scan of every modifier plus a
    /// closure and a fresh <c>List</c> — <b>for every channel, including the ~250 that had no modifier at
    /// all</b>. On the lawn's per-hit path that is the dominant cost of the whole capture pipeline:
    /// measured at 300 zombies, <c>hub.resolveDerived</c> averaged <b>1,624 µs</b> and accounted for
    /// ~85% of <c>effect.onCapture</c>'s 4,214 µs, while the feature's allocation rate ran
    /// <b>724 MB–1.4 GB per five-second window against 142–169 MB with the feature off</b>
    /// (`docs/research/perf/_baseline-lcw-300z-env-*.json`).
    ///
    /// <para><b>Semantics are unchanged, deliberately.</b> Every registered channel is still composed
    /// exactly once, with exactly the modifiers naming it, through the same <see cref="ComposeChannel"/>.
    /// Channel order never mattered — each channel composes independently — and an unregistered channel
    /// could never reach the fold anyway, because <see cref="DerivedStatRegistry.ValidateChannel"/>
    /// throws on it before this loop is reached. A channel with no modifiers now takes a shared empty
    /// list instead of a freshly allocated one.</para>
    /// </summary>
    public ActorDerivedSnapshot Compose(IEnumerable<DerivedModifier>? modifiers = null)
    {
        Dictionary<string, List<DerivedModifier>>? byChannel = null;
        if (modifiers is not null)
        {
            foreach (var m in modifiers)
            {
                _registry.ValidateChannel(m.ChannelId);
                byChannel ??= new Dictionary<string, List<DerivedModifier>>(StringComparer.Ordinal);
                if (!byChannel.TryGetValue(m.ChannelId, out var list))
                    byChannel[m.ChannelId] = list = new List<DerivedModifier>(2);
                list.Add(m);
            }
        }

        var snapshot = new ActorDerivedSnapshot();
        foreach (var def in _registry.AllRegistered)
        {
            IReadOnlyList<DerivedModifier> channelMods = NoMods;
            if (byChannel is not null && byChannel.TryGetValue(def.ChannelId, out var found))
            {
                channelMods = found;
                // Consume it, so what remains below is exactly the OPEN-PREFIX-FAMILY channels — see
                // the loop's own note.
                byChannel.Remove(def.ChannelId);
            }
            snapshot.Set(def.ChannelId, ComposeChannel(def, channelMods));
        }

        // ⚠️ `AllRegistered` lists the CONCRETE defs only. `TryResolveChannel` also resolves the registry's
        // OPEN PREFIX FAMILIES (`status.immune.*` and friends), which have no concrete row until a
        // modifier names one — so a channel can be perfectly valid, pass `ValidateChannel`, and still not
        // appear in `AllRegistered`. Iterating the registered defs alone silently dropped every one of
        // them; `EffectOfflineKitTests.The_four_derived_ops_decide_Full_versus_Partial` caught it on the
        // first full run (a `status.immune.poison` Flag folded to 0 instead of 1). Whatever is still in
        // `byChannel` after the pass above is exactly that set, and it is tiny.
        if (byChannel is not null)
        {
            foreach (var (channelId, channelMods) in byChannel)
            {
                if (!_registry.TryResolveChannel(channelId, out var def))
                    continue;
                snapshot.Set(channelId, ComposeChannel(def, channelMods));
            }
        }

        return snapshot;
    }

    static double ComposeChannel(DerivedStatDef def, IReadOnlyList<DerivedModifier> mods)
    {
        // ⚡ The overwhelming majority of channels have no modifier on any given actor (~250 of ~261 on
        // a lawn resolve). `Cap` still applies, because a capped channel's default must be capped the
        // same way it would be with an empty modifier list — this is a fast path, never a different
        // answer.
        if (mods.Count == 0)
            return def.Compose == DerivedComposeKind.SumIncreased ? Cap(def, def.DefaultValue) : def.DefaultValue;

        return def.Compose switch
        {
            DerivedComposeKind.FlatSum => def.DefaultValue + SumOf(mods, DerivedModifierOp.Flat),
            DerivedComposeKind.FlatReplace => ComposeFlatReplace(def.DefaultValue, mods),
            DerivedComposeKind.SumIncreased => Cap(def, def.DefaultValue + SumOf(mods, DerivedModifierOp.Increased)),
            DerivedComposeKind.MaxPriorityFlag => ComposeMaxFlag(def.DefaultValue, mods),
            _ => def.DefaultValue
        };
    }

    /// <summary>⚡ The allocation-free form of `mods.Where(m =&gt; m.Op == op).Sum(m =&gt; m.Value)` — same
    /// value, no closure, no enumerator, on a path that runs per channel per hit.</summary>
    static double SumOf(IReadOnlyList<DerivedModifier> mods, DerivedModifierOp op)
    {
        double sum = 0;
        for (var i = 0; i < mods.Count; i++)
            if (mods[i].Op == op) sum += mods[i].Value;
        return sum;
    }

    /// <summary>
    /// sim-hub-parity (T15) — the SAME per-channel op-aware fold as <see cref="Compose"/>, but with a
    /// caller-supplied <paramref name="baseline"/> standing in for <see cref="DerivedStatDef.DefaultValue"/>.
    /// <see cref="ActorDerivedLookup"/>'s pinned snapshot value already IS "this actor's value with zero
    /// bound contributions" — reusing this composer's exact <c>ComposeFlatReplace</c>/<c>ComposeMaxFlag</c>
    /// logic against that baseline is "the same op-aware contribution fold as Hub," not a second,
    /// possibly-drifting reimplementation (SOLID L/D, the spec's own boundary). An unregistered channel
    /// returns <paramref name="baseline"/> unchanged — this never widens the registered vocabulary.
    /// </summary>
    public double ComposeChannelWithBaseline(string channelId, double baseline, IReadOnlyList<DerivedModifier> mods)
    {
        if (!_registry.TryResolveChannel(channelId, out var def)) return baseline;
        var channelMods = mods.Where(m => string.Equals(m.ChannelId, channelId, StringComparison.Ordinal)).ToList();
        return def.Compose switch
        {
            DerivedComposeKind.FlatSum => baseline + channelMods.Where(m => m.Op == DerivedModifierOp.Flat).Sum(m => m.Value),
            DerivedComposeKind.FlatReplace => ComposeFlatReplace(baseline, channelMods),
            DerivedComposeKind.SumIncreased => Cap(def, baseline + channelMods.Where(m => m.Op == DerivedModifierOp.Increased).Sum(m => m.Value)),
            DerivedComposeKind.MaxPriorityFlag => ComposeMaxFlag(baseline, channelMods),
            _ => baseline
        };
    }

    static double ComposeFlatReplace(double baseline, IReadOnlyList<DerivedModifier> mods)
    {
        var replaces = mods.Where(m => m.Op == DerivedModifierOp.Replace)
            .OrderByDescending(m => m.Priority)
            .ThenBy(m => m.SourceId, StringComparer.Ordinal)
            .ToList();
        if (replaces.Count > 0)
            return replaces[0].Value;
        var flats = mods.Where(m => m.Op == DerivedModifierOp.Flat).Sum(m => m.Value);
        return baseline + flats;
    }

    static double ComposeMaxFlag(double baseline, IReadOnlyList<DerivedModifier> mods)
    {
        var flags = mods.Where(m => m.Op is DerivedModifierOp.Flag or DerivedModifierOp.Replace or DerivedModifierOp.Increased)
            .Select(m => m.Value)
            .DefaultIfEmpty(baseline);
        var max = flags.Max();
        return max;
    }

    static double Cap(DerivedStatDef def, double value) =>
        def.Cap.HasValue ? Math.Min(value, def.Cap.Value) : value;
}
