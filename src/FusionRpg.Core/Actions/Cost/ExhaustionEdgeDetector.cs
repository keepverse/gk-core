namespace FusionRpg.Core.Actions.Cost;

/// <summary>Which edge an observation of a resource pool crossed — or no edge at all.</summary>
public enum ExhaustionEdge
{
    /// <summary>The pool's exhausted/not-exhausted answer did not change. The common case: a hundred
    /// refused swings inside one window are a hundred observations and still one event.</summary>
    None = 0,
    Entered = 1,
    Recovered = 2,
}

/// <summary>
/// One exhaustion edge, carrying everything the wire payload and the observer's per-actor row need:
/// who (ptr), what (resource id), which edge, when (tick), the pool's resolved value at the edge, and
/// — on the recovering edge only — how long the window lasted.
/// </summary>
/// <param name="ExhaustedForMs">How long the window lasted, in milliseconds, computed from the SAME
/// tick grid the edge was observed on. <c>null</c> on the entering edge (it has no duration yet).
/// <c>exhaustedForMs</c> is what turns "is this a rhythm or an annoyance" into a readable number
/// instead of an opinion.</param>
public readonly record struct ExhaustionTransition(
    string HostPtr,
    string ResourceId,
    ExhaustionEdge Edge,
    long Tick,
    long PoolValue,
    long? ExhaustedForMs)
{
    public bool IsEdge => Edge != ExhaustionEdge.None;
}

/// <summary>
/// Edge detection for resource exhaustion — <b>transitions, never refusals</b>
/// (<c>lawn-playable/spec-exhaustion-event.md</c>, defect E1). Lives beside
/// <see cref="ExhaustionPolicy"/>: pure, Unity-free, no I/O, so the injector's charge path can call it
/// from a hot loop and the whole property is testable without a game.
///
/// <para><b>Why E1 happened and cannot here.</b> The observer's counter incremented once per refused
/// swing, so a clean-player run measured <c>ExhaustionEvents 591</c> over <c>totalHits 551</c> — more
/// events than hits, which is only possible if "still exhausted" was counted as an event. This type
/// answers the question the counter could not: <see cref="Observe"/> returns an edge only when
/// <see cref="ExhaustionPolicy.IsExhausted"/> <i>changes</i> for one <c>(ptr, resourceId)</c>.</para>
///
/// <para><b>Keyed by match as well as actor.</b> The key is
/// <c>(matchKey, ptr, resourceId)</c>, so a new match is a new window by construction rather than
/// because some lifecycle hook remembered to clear it — and IL2CPP pointer reuse inside one match is
/// handled by <see cref="Forget"/>, which the injector's own death edge
/// (<c>InjectorEntityRegistry.Remove</c>) already calls.</para>
///
/// <para><b>Fail closed.</b> An observation with no pointer or no resource id returns
/// <see cref="ExhaustionEdge.None"/> and never emits — the charger already refuses an empty attacker
/// ptr for the same reason: an event with no actor identity is not an event.</para>
///
/// <para><b>Not here, and named:</b> the debounce (<c>minEdgeIntervalMs</c>) is the one tunable this
/// module owns, and it lives in <c>data/tuning/mode-profiles.v{n}.json</c>'s lawn row — the file
/// `mode-profile` (this program's `LW2.2`) creates. Until it lands there is nothing to read; a pool
/// oscillating across zero at the charge cadence is the one shape it debounces, and no shipped number
/// changes without it.</para>
/// </summary>
public sealed class ExhaustionEdgeDetector
{
    /// <summary>The kernel's tick length in milliseconds — structural, not a tunable
    /// (<c>tunables-ssot.md</c> T2, so it carries this comment rather than a tuning row): it is the
    /// sub-tick unit's own denominator, not a number a balance pass would move. Two independent sites
    /// already fix it at 100 ms — <c>BattleModels.TicksPerSecond = 10</c>
    /// (<c>gk-core/src/FusionRpg.Core/Battle/BattleModels.cs</c>, itself documented as structural) and the
    /// injector's <c>KernelDriveHost</c> 100 ms grid (<c>NowTicks / 100</c>) — and changing it would
    /// re-derive every per-mille-per-tick rate in this repo.</summary>
    public const long MilliPerTick = 100L;

    readonly record struct Window(bool Exhausted, long EnteredTick);

    // ptr -> "matchKey\u001fresourceId" -> window. Two levels so Forget(ptr) is one dictionary removal
    // on the death edge, which runs per death in a 300-zombie wave.
    readonly Dictionary<string, Dictionary<string, Window>> _byPtr = new(StringComparer.Ordinal);

    /// <summary>How many actors have a window being tracked — a bound reading for tests and for the
    /// "address reuse cannot grow this without bound" property; never a contract. A dictionary
    /// <c>Count</c>, not an enumeration: this type lives in the action layer, whose purity guard forbids
    /// enumerating a dictionary (order is not guaranteed there).</summary>
    public int TrackedActors => _byPtr.Count;

    /// <summary>Windows currently tracked, maintained on the two mutations that can change the count —
    /// the <c>_byPtr[ptr] = windows</c> insert (which adds none) and the new-key insert in
    /// <see cref="Observe"/>, plus the removals in <see cref="Forget"/>/<see cref="Clear"/>. Kept as a
    /// field rather than summed on demand because a SUM needs to enumerate the outer dictionary, and
    /// dictionary enumeration is a purity violation for this directory
    /// (<c>ActionsPurityGuardTests</c> — unspecified order, so any fold over it is nondeterministic by
    /// construction). O(1) instead of O(pointers), which is also what a per-death caller wants.</summary>
    int _windowCount;

    /// <summary>How many <c>(ptr, match, resource)</c> windows are being tracked — a bound reading for
    /// tests and for the "address reuse cannot grow this without bound" property; never a contract.</summary>
    public int WindowCount => _windowCount;

    /// <summary>
    /// Observe one resolved pool value and return the edge it crossed, if any.
    ///
    /// <para>An absent window means <b>not exhausted</b>: an actor first seen with an empty pool has
    /// just entered the window, and that is a real transition (a freshly seeded pool starts full, so
    /// this is the reused-pointer case rather than the common one). Subsequent observations at an
    /// unchanged answer return <see cref="ExhaustionEdge.None"/> — which is the whole of defect E1's
    /// fix.</para>
    /// </summary>
    public ExhaustionTransition Observe(
        string? hostPtr, string resourceId, string? matchKey, long resolvedValue, long nowTick)
    {
        if (string.IsNullOrWhiteSpace(hostPtr) || string.IsNullOrWhiteSpace(resourceId))
            return default; // no actor identity -- fail closed, never emit (the charger's own rule)

        var ptr = hostPtr!;
        if (!_byPtr.TryGetValue(ptr, out var windows))
        {
            windows = new Dictionary<string, Window>(StringComparer.Ordinal);
            _byPtr[ptr] = windows;
        }

        var key = (matchKey ?? "") + "\u001f" + resourceId;
        var exhausted = ExhaustionPolicy.IsExhausted(resolvedValue);

        if (!windows.TryGetValue(key, out var window))
        {
            windows[key] = new Window(exhausted, exhausted ? nowTick : 0L);
            _windowCount++;
            return exhausted
                ? new ExhaustionTransition(ptr, resourceId, ExhaustionEdge.Entered, nowTick, resolvedValue, null)
                : default;
        }

        if (window.Exhausted == exhausted)
            return default; // same answer as last time -- still exhausted, or still fine; no event

        if (exhausted)
        {
            windows[key] = new Window(true, nowTick);
            return new ExhaustionTransition(ptr, resourceId, ExhaustionEdge.Entered, nowTick, resolvedValue, null);
        }

        windows[key] = new Window(false, 0L);
        var forMs = window.EnteredTick > 0 && nowTick > window.EnteredTick
            ? (long?)((nowTick - window.EnteredTick) * MilliPerTick)
            : null;
        return new ExhaustionTransition(ptr, resourceId, ExhaustionEdge.Recovered, nowTick, resolvedValue, forMs);
    }

    /// <summary>Whether this actor's window for one resource is currently open — a read for callers
    /// that need the state without observing (never a second copy of it).</summary>
    public bool IsExhausted(string? hostPtr, string resourceId, string? matchKey)
    {
        if (string.IsNullOrWhiteSpace(hostPtr)) return false;
        return _byPtr.TryGetValue(hostPtr!, out var windows)
            && windows.TryGetValue((matchKey ?? "") + "\u001f" + resourceId, out var window)
            && window.Exhausted;
    }

    /// <summary>Drop every window for one pointer — the death / pointer-reuse edge (IL2CPP reuses
    /// addresses, so a new actor must not inherit a stranger's window).</summary>
    public void Forget(string? hostPtr)
    {
        if (string.IsNullOrWhiteSpace(hostPtr)) return;
        if (_byPtr.Remove(hostPtr!, out var dropped)) _windowCount -= dropped.Count;
    }

    /// <summary>Drop everything — the board-start / match-end barrier, matching
    /// <c>InjectorEntityRegistry.Clear</c>'s own flush.</summary>
    public void Clear()
    {
        _byPtr.Clear();
        _windowCount = 0;
    }
}

/// <summary>
/// The two event kinds this module emits, and the payload it emits with them — pure, so the payload's
/// shape is asserted by a Core test rather than by reading the injector.
/// </summary>
public static class ExhaustionEdgeEvents
{
    public const string ExhaustedKind = "actor.exhausted";
    public const string RecoveredKind = "actor.recovered";

    /// <summary>The kind for one edge. A non-edge has no event, so asking for one is a programming
    /// error rather than a silent no-op.</summary>
    public static string KindFor(ExhaustionEdge edge) => edge switch
    {
        ExhaustionEdge.Entered => ExhaustedKind,
        ExhaustionEdge.Recovered => RecoveredKind,
        _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "no exhaustion event exists for a non-edge"),
    };

    /// <summary>
    /// The payload both events carry, plus <c>exhaustedForMs</c> on the recovering edge only.
    /// <c>side</c> and <c>matchKey</c> come from the record that produced the read (the effect event
    /// already carries both), never from a second lookup.
    /// </summary>
    public static Dictionary<string, object> Build(ExhaustionTransition transition, string? side, string? matchKey)
    {
        if (!transition.IsEdge)
            throw new ArgumentException("a non-edge has no exhaustion payload", nameof(transition));

        var payload = new Dictionary<string, object>
        {
            ["ptr"] = transition.HostPtr,
            ["side"] = side ?? "",
            ["resourceId"] = transition.ResourceId,
            ["tick"] = transition.Tick,
            ["poolValue"] = transition.PoolValue,
            ["matchKey"] = matchKey ?? "",
        };

        if (transition.ExhaustedForMs is { } forMs)
            payload["exhaustedForMs"] = forMs;

        return payload;
    }
}
