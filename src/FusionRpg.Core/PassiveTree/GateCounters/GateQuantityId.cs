namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// spec-gate-counters.md §5.2's contract type — what <c>tree-resolve</c> asks for and the only thing
/// it ever asks for. <c>Family</c> is <c>"status_applied"</c> or <c>"element_mastery"</c> today (and
/// <c>"aptitude"</c> for the primary/family categories that never route through this registry at
/// all); <c>SubjectId</c> is the 21-status or 6-element id underneath it.
///
/// <para>This is a lookup key, never a stored row -- nothing here is persisted (§4.1 stores raw counts
/// only, keyed by <see cref="GateCounterKey"/>, a different and independently-shaped type for a
/// different job: that one is the accumulator/store's identity for a credit; this one is the gate's
/// question to the registry).</para>
/// </summary>
public readonly record struct GateQuantityId(string Family, string SubjectId);
