namespace FusionRpg.Core.Items;

/// <summary>I13 §5.7's four guards, in the order they are checked — the first one that fires wins,
/// so the report names exactly one reason per excluded item.</summary>
public enum SalvageExclusionReason
{
    /// <summary>G-A: you cannot destroy what you are wearing.</summary>
    Assigned,

    /// <summary>G-B: lock is absolute or it is decoration — never bypassed, auto-salvage included.</summary>
    Locked,

    /// <summary>G-C: loadout membership implies lock — a preset that quietly loses a piece is worse
    /// than a refused salvage.</summary>
    LoadoutMember,

    /// <summary>G-D: players do not lock what they have not looked at, and that is always the item
    /// they lose — so best-in-role is excluded from bulk selections by default.</summary>
    BestInRole,
}

/// <summary>Everything <see cref="SalvageGuards.Preview"/> needs to know about one item to judge it —
/// assembled by the caller, so this stays a Core, DB-free, unit-testable type.</summary>
public sealed record SalvageCandidate(
    string InstanceId, bool Assigned, bool Locked, bool InAnyLoadout, bool BestInRole);

public sealed record SalvageExclusion(string InstanceId, SalvageExclusionReason Reason);

/// <summary>The exact id list a commit must reuse verbatim — a race that adds an item between preview
/// and commit cannot widen the selection, because commit never re-evaluates the guards.</summary>
public sealed record SalvagePreview(IReadOnlyList<string> Eligible, IReadOnlyList<SalvageExclusion> Excluded);

/// <summary>
/// I13 §5.7's four guards, claimed here because a warning dialog is not a guard — these run on
/// <b>every</b> salvage path, manual bulk and auto-salvage alike, so the same function is the single
/// place "can this item be destroyed" is answered (<c>SC1</c> — never a second mechanism for the same
/// job).
/// </summary>
public static class SalvageGuards
{
    /// <param name="includeBestInRole">The bulk-selection opt-in — G-D excludes by default; this flips
    /// it for one preview call. Never available to auto-salvage, which has no player watching to
    /// confirm the opt-in.</param>
    public static SalvagePreview Preview(IEnumerable<SalvageCandidate> candidates, bool includeBestInRole = false)
    {
        var eligible = new List<string>();
        var excluded = new List<SalvageExclusion>();

        foreach (var c in candidates)
        {
            if (c.Assigned) { excluded.Add(new SalvageExclusion(c.InstanceId, SalvageExclusionReason.Assigned)); continue; }
            if (c.Locked) { excluded.Add(new SalvageExclusion(c.InstanceId, SalvageExclusionReason.Locked)); continue; }
            if (c.InAnyLoadout) { excluded.Add(new SalvageExclusion(c.InstanceId, SalvageExclusionReason.LoadoutMember)); continue; }
            if (c.BestInRole && !includeBestInRole) { excluded.Add(new SalvageExclusion(c.InstanceId, SalvageExclusionReason.BestInRole)); continue; }

            eligible.Add(c.InstanceId);
        }

        return new SalvagePreview(eligible, excluded);
    }
}
