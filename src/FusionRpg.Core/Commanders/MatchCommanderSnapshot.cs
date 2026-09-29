using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Commanders;

/// <summary>
/// Frozen commander identity, active aura, and aptitude allocation for one lawn match — captured at
/// <c>board.start</c> (commander-surface <c>match-snapshot</c>).
/// </summary>
public sealed record MatchCommanderSnapshot(
    string LeadingCommanderId,
    string LeadingCommanderDisplayName,
    string? ActiveAuraId,
    string? ActiveAuraDisplayName,
    AptitudeAllocation Allocation,
    long AllocationRevision,
    long SnapshotRevision);
