using FusionRpg.Core.PassiveTree.State;

namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// spec-gate-counters.md §5.2/§5.3 — task G4's <see cref="IGateQuantitySource"/> for
/// <c>element_mastery</c>. Same shape as <see cref="StatusAppliedSource"/> and the same reason: convert
/// the raw lifetime count the store already persists into aptitude-point-equivalents through
/// <see cref="MasteryIndex"/>, at its OWN rate key — <c>gateCounters.elementMasteryRatePoints</c>.
///
/// <para><b>Never routed through the class system's per-scope aptitude-point budget calculator, keyed
/// to its Aspect allocation scope.</b> §5.3 (OQ2, closed 2026-09-05) resolved this in favour of
/// <c>element_mastery</c> owning its own tunable rather than sharing the creature program's unmeasured
/// Aspect rate — spec-gate-counters.md §10's project-structure table still names the superseded route
/// in its one-line description; this file implements the CLOSED decision in §5.3, not that stale table
/// row. <see cref="GateCounterBoundaryGuardTests"/> is extended to scan this file for exactly that
/// machinery, same as every other gate-counter production file (with <see cref="MasteryIndex"/> itself
/// excluded from that scan for the reason stated at its own scanned-file list entry).</para>
///
/// <para>Delegate-based count read for the same DAL-boundary reason
/// <see cref="StatusAppliedSource"/>'s own doc comment gives — <c>FusionRpg.Core</c> cannot reference
/// <c>FusionRpg.Data</c>.</para>
/// </summary>
public sealed class ElementMasterySource : IGateQuantitySource
{
    public string Family => ElementMasteryCounter.Quantity;

    readonly Func<GateOwnerKey, string, long> _readCount;
    readonly GateCountersTuning _tuning;

    /// <param name="readCount"><c>(owner, subjectId) -&gt; raw lifetime count</c>, <c>0</c> for a key
    /// with no row. The composition root (task G6) wires
    /// <c>(owner, subjectId) => store.LoadGateCounter(owner.Kind, owner.Key, "element_mastery", subjectId)</c>.</param>
    /// <param name="tuning">The already-validated <c>gateCounters</c> tuning block — this source reads
    /// <see cref="GateCountersTuning.ElementMasteryRatePoints"/>, never
    /// <see cref="GateCountersTuning.StatusMasteryRatePoints"/> and never a scope-keyed table.</param>
    public ElementMasterySource(Func<GateOwnerKey, string, long> readCount, GateCountersTuning tuning)
    {
        _readCount = readCount ?? throw new ArgumentNullException(nameof(readCount));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    public long AptitudePointEquivalents(GateQuantityId id, GateActorContext actor)
    {
        if (id.Family != Family)
            throw new ArgumentException(
                $"ElementMasterySource answers '{Family}', not '{id.Family}'", nameof(id));

        var count = _readCount(actor.Owner, id.SubjectId);
        return MasteryIndex.Equivalents(count, _tuning.ElementMasteryRatePoints, _tuning);
    }
}
