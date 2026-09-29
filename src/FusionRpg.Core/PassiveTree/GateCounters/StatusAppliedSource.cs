using FusionRpg.Core.PassiveTree.State;

namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// spec-gate-counters.md §5.2/§5.3 — task G4's <see cref="IGateQuantitySource"/> for
/// <c>status_applied</c>. Converts the raw lifetime count <c>RpgStore.LoadGateCounter</c> /
/// <c>LoadGateCountersForOwner</c> already persist (task G1's table, task G2's store methods) into
/// aptitude-point-equivalents through <see cref="MasteryIndex"/> (this task's own square-root
/// transform, §9) at <c>gateCounters.statusMasteryRatePoints</c> — never the class system's per-scope
/// allocation scopes, never a synthesized allocation row (D35). <see cref="GateCounterBoundaryGuardTests"/>
/// is extended to scan this file for exactly that.
///
/// <para><b>Why a delegate and not an <c>RpgStore</c> reference.</b> <c>FusionRpg.Core</c> does not —
/// and must not — reference <c>FusionRpg.Data</c> (the DAL boundary <c>guard-dal.ps1</c> enforces), so
/// this class takes the raw-count READ as a delegate, the same shape
/// <see cref="StatusAppliedCounter"/>'s own <c>resolveOwner</c> constructor parameter already uses for
/// the identical reason. The composition root (task G6, server startup) wires the real closure:
/// <c>(owner, subjectId) => store.LoadGateCounter(owner.Kind, owner.Key, "status_applied", subjectId)</c>.</para>
/// </summary>
public sealed class StatusAppliedSource : IGateQuantitySource
{
    public string Family => StatusAppliedCounter.Quantity;

    readonly Func<GateOwnerKey, string, long> _readCount;
    readonly GateCountersTuning _tuning;

    /// <param name="readCount"><c>(owner, subjectId) -&gt; raw lifetime count</c>, <c>0</c> for a key
    /// with no row (§4.1's sparsity — never an error, never an invented default). Called at most once
    /// per <see cref="AptitudePointEquivalents"/>.</param>
    /// <param name="tuning">The already-validated <c>gateCounters</c> tuning block (§13,
    /// <c>PassiveTreeTuningLoader</c>) — this source reads
    /// <see cref="GateCountersTuning.StatusMasteryRatePoints"/> and the shared mastery-curve pair,
    /// never a private copy of either.</param>
    public StatusAppliedSource(Func<GateOwnerKey, string, long> readCount, GateCountersTuning tuning)
    {
        _readCount = readCount ?? throw new ArgumentNullException(nameof(readCount));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    /// <summary>Registered into a <see cref="GateQuantityRegistry"/>, this is only ever called for
    /// <c>id.Family == "status_applied"</c> — the guard below is defensive, not a normal path.</summary>
    public long AptitudePointEquivalents(GateQuantityId id, GateActorContext actor)
    {
        if (id.Family != Family)
            throw new ArgumentException(
                $"StatusAppliedSource answers '{Family}', not '{id.Family}'", nameof(id));

        var count = _readCount(actor.Owner, id.SubjectId);
        return MasteryIndex.Equivalents(count, _tuning.StatusMasteryRatePoints, _tuning);
    }
}
