namespace FusionRpg.Core.Match;

/// <summary>
/// One creature specimen the plant-side deploy-event UI may offer — enough to identify it and let the
/// client resolve display info from its own already-loaded species catalog (`GET /api/creatures/catalog`),
/// not a duplicate of the full roster DTO.
/// </summary>
public sealed record LawnDeployRosterEntry(string InstanceId, string SpeciesId);

/// <summary>
/// creature-lawn-deploy T2.1 (spec-lawn-deploy-events.md Correction 2) — frozen, deploy-eligible roster
/// for one lawn match, captured at <c>board.start</c>. Mirrors <see cref="Commanders.MatchCommanderSnapshot"/>'s
/// own Hot/Cold shape exactly: a Cold-plane read (the roster, and who currently holds the Patron
/// designation) is resolved ONCE outside the hit path and frozen for the match's own duration, so the
/// trigger evaluator (T2.2) never touches Cold-plane data directly.
///
/// <para><see cref="Eligible"/> already excludes the active Patron — the reconciliation happens at
/// snapshot-build time (<see cref="LawnDeployRosterSessionCache.BuildFromSessionCache"/>), not per-read,
/// so the trigger evaluator's own job stays a pure function over already-resolved data. Commander is not
/// separately excluded here for the same reason `lawn-deploy-core`'s own T1.1 refusal does not check it:
/// `CommanderId` (`Core/Commanders/CommanderId.cs`) has no creature-instance binding today.</para>
///
/// <para>A roster change mid-match (a fusion completing, a new summon) is NOT reflected here until the
/// NEXT match's own snapshot — by design (spec's own acceptance line), not a staleness bug.</para>
/// </summary>
public sealed record LawnDeployRosterSnapshot(IReadOnlyList<LawnDeployRosterEntry> Eligible, long SnapshotRevision)
{
    public static readonly LawnDeployRosterSnapshot Empty = new(Array.Empty<LawnDeployRosterEntry>(), 0);
}
