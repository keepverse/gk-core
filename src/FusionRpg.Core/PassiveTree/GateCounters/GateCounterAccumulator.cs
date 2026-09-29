namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// spec-gate-counters.md §4.3 — the "no write on the hot path" half of the module. A credit is an
/// in-memory dictionary increment; nothing here touches SQL, a clock, or a thread (AGENTS.md's
/// Core/Data boundary — this type lives in <c>FusionRpg.Core</c> precisely so <c>guard-dal.ps1</c>'s
/// "SQL only inside FusionRpg.Data" claim stays true of it by construction, not by discipline).
///
/// <para><b>Who calls <see cref="DrainAndClear"/> and when</b> is deliberately NOT this type's
/// concern. §4.3 names the timer (<c>gateCounters.flushIntervalMs</c>, default 5000 — the same window
/// <c>PerfProbe</c> already uses) and the match-end trigger, but both are host-side scheduling
/// (server/injector), not Core arithmetic; this type only has to make "drain what accumulated since the
/// last flush, and start the next window at zero" a single atomic call so a host can wire either trigger
/// without inventing its own bookkeeping.</para>
///
/// <para><b>Not thread-safe, by the same reasoning <see cref="Status.StatusRuntime"/> already isn't</b>
/// — the lawn's single-threaded per-tick model is the caller's guarantee, not this accumulator's.
/// Serializing access across a real flush timer and the hot-path credit call is the host's job.</para>
/// </summary>
public sealed class GateCounterAccumulator
{
    readonly Dictionary<GateCounterKey, long> _deltas = new();

    /// <summary>Adds <paramref name="amount"/> (default 1 — §2.2c: "each element component credits
    /// exactly 1... a count is a count") to this key's pending delta. `long` and `checked`
    /// (docs/architecture/numeric-types.md): a counter is a magnitude, and an accumulate that would overflow throws rather than
    /// wrapping a player's progress.</summary>
    public void Credit(GateCounterKey key, long amount = 1)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "a gate counter credit must be positive");

        _deltas.TryGetValue(key, out var existing);
        checked { _deltas[key] = existing + amount; }
    }

    /// <summary>Read-only peek, no mutation — for a test or a diagnostic surface that must not affect
    /// the next flush.</summary>
    public IReadOnlyDictionary<GateCounterKey, long> Snapshot() =>
        new Dictionary<GateCounterKey, long>(_deltas);

    /// <summary>The flush call: take exactly what accumulated since the last drain, and reset this
    /// window to empty, in one step — so a crash between the drain and the store write loses at most
    /// one window (§4.3: "a lost window costs a little progress, never correctness"), never double-counts
    /// a window that was already handed to the store.</summary>
    public IReadOnlyDictionary<GateCounterKey, long> DrainAndClear()
    {
        if (_deltas.Count == 0)
            return new Dictionary<GateCounterKey, long>();

        var snapshot = new Dictionary<GateCounterKey, long>(_deltas);
        _deltas.Clear();
        return snapshot;
    }
}
