namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// Per-binding runtime state: ICD clocks, charges, hit meters, and per-match caps
/// (spec-atom-runner.md, E15).
///
/// <para><b>Session RAM, never a table.</b> An <c>entity:{ptr}</c> clock is meaningless across a
/// restart and a per-match counter dies with the match by definition, so E6 deliberately created no
/// durable runtime table.</para>
///
/// <para>Flat arrays indexed by the slot <see cref="TriggerIndex"/> assigned — allocated once at
/// build, reused for the life of the index, so the hot path never hashes a binding id.</para>
/// </summary>
public sealed class RunnerState
{
    readonly long[] _icdUntilMs;
    readonly int[] _chargesLeft;
    readonly int[] _hitMeter;
    readonly int[] _capUsed;
    readonly bool[] _capNotified;

    public RunnerState(TriggerIndex index)
    {
        var n = index.Count;
        _icdUntilMs = new long[n];
        _chargesLeft = new int[n];
        _hitMeter = new int[n];
        _capUsed = new int[n];
        _capNotified = new bool[n];
        MatchKey = "";
        ResetAll(index);
    }

    /// <summary>The match the cap counters belong to. Changing it is what clears them.</summary>
    public string MatchKey { get; private set; }

    /// <summary>
    /// Match start. Caps and their one-shot telemetry flags reset; charges reset too, because a
    /// charge is a per-life resource and a new match is a new life.
    /// </summary>
    public void BeginMatch(TriggerIndex index, string matchKey)
    {
        MatchKey = matchKey ?? "";
        ResetAll(index);
    }

    void ResetAll(TriggerIndex index)
    {
        Array.Clear(_icdUntilMs, 0, _icdUntilMs.Length);
        Array.Clear(_hitMeter, 0, _hitMeter.Length);
        Array.Clear(_capUsed, 0, _capUsed.Length);
        Array.Clear(_capNotified, 0, _capNotified.Length);

        for (var slot = 0; slot < index.Count; slot++)
        {
            var limits = index.Bindings[slot].Entry.Limits;
            _chargesLeft[slot] = limits.HasCharges ? limits.Charges : -1;
        }
    }

    // ---- ICD: may this listener try again? --------------------------------------------------------
    //
    // The grant ICD only. StatusRuntime owns whether a STATUS may be re-applied, and its pulse
    // cadence is not an ICD at all — status-ssot.md keeps the three clocks separate and merging any
    // two of them silently changes both.

    public bool IcdReady(int slot, long nowMs) => nowMs >= _icdUntilMs[slot];

    public void StampIcd(int slot, long nowMs, int icdMs)
    {
        if (icdMs > 0) _icdUntilMs[slot] = nowMs + icdMs;
    }

    public long IcdUntil(int slot) => _icdUntilMs[slot];

    // ---- charges ---------------------------------------------------------------------------------

    public bool HasChargeLeft(int slot) => _chargesLeft[slot] != 0;

    public void SpendCharge(int slot)
    {
        if (_chargesLeft[slot] > 0) _chargesLeft[slot]--;
    }

    public int ChargesLeft(int slot) => _chargesLeft[slot];

    // ---- hit meter -------------------------------------------------------------------------------

    /// <summary>
    /// Advance the meter and say whether this is the Nth hit.
    ///
    /// <para>The meter advances even when it does not fire. A pre-proc gate consumes nothing on
    /// failure — but a meter that never ticks can never reach N, so "consumes nothing" means no ICD
    /// stamped and no roll drawn, not a frozen counter. The spec states the principle without
    /// naming this case; this is the reading that leaves <c>everyHits</c> implementable.</para>
    /// </summary>
    public bool AdvanceMeter(int slot, int everyHits)
    {
        var next = _hitMeter[slot] + 1;
        if (next < everyHits)
        {
            _hitMeter[slot] = next;
            return false;
        }
        _hitMeter[slot] = 0;
        return true;
    }

    public int MeterAt(int slot) => _hitMeter[slot];

    // ---- capPerMatch -----------------------------------------------------------------------------

    public bool CapReached(int slot, int cap) => _capUsed[slot] >= cap;

    public void CountDispatch(int slot) => _capUsed[slot]++;

    public int DispatchesThisMatch(int slot) => _capUsed[slot];

    /// <summary>
    /// True exactly once per binding per match. A capped economy atom is hit at board rate, so
    /// emitting a skip record per attempt would bury the event log under the one effect that has
    /// already stopped doing anything.
    /// </summary>
    public bool ClaimCapNotice(int slot)
    {
        if (_capNotified[slot]) return false;
        _capNotified[slot] = true;
        return true;
    }
}
