using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// `encounter-generator` D2.7 (spec-encounter-generator.md §8 "Closed-loop metric — cell coverage") —
/// distinct `(postureMultiset, elementSpread, formation)` shapes reached, never raw entries per cell
/// (ideal §11.4). Pure counting over whatever <see cref="EncounterCell"/> sequence the caller already
/// produced (typically 32 seeds × a domain's fight/elite/boss rooms via repeated
/// <see cref="Encounter.Build"/> calls) — this module owns none of that seeding or looping itself.
///
/// <para><b>The pass threshold stays the caller's own number, never read from disk here.</b>
/// `gk-data/packs/fusion/data/seed/dungeon/_plan/budget.v1.json`'s real `dungeon-encounter` row (read in full, 2026-09-06):
/// <c>cells: 9, target: 81, firstShip: 40, tolerance: {under: 0, over: 1}</c> — a real, already-shipped
/// budget row this module's own tests check against directly, but §9's "no store, no I/O" bars
/// <i>this</i> module from reading the file itself; <see cref="MeetsBudget"/> takes the target as a
/// plain parameter, matching every other tuning value this whole module reads as data, not a file.</para>
/// </summary>
public static class EncounterCoverage
{
    /// <summary>How many distinct `(postureMultiset, elementSpread, formation)` shapes appear across
    /// <paramref name="cells"/>. The posture multiset is order-independent but multiplicity-sensitive
    /// (two Bastions and one Force is a different shape from one Bastion and two Forces) — sorted
    /// before comparison so arrival order never matters, matching every other "no dictionary
    /// enumeration order" determinism rule this module already follows.</summary>
    public static int DistinctCells(IEnumerable<EncounterCell> cells)
    {
        if (cells is null) throw new ArgumentNullException(nameof(cells));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cells) seen.Add(Key(c));
        return seen.Count;
    }

    /// <summary><paramref name="tolerance"/> is `budget.v1.json`'s own `tolerance.under` — 0 in the
    /// real row, meaning no shortfall is tolerated; a positive value would allow one.</summary>
    public static bool MeetsBudget(int distinctCells, int target, int tolerance = 0) =>
        distinctCells >= target - tolerance;

    /// <summary>
    /// §8's own "StS sibling rule": two same-kind siblings on one graph row resolving to the identical
    /// cell are a finding here (never a refusal — the spec's own words are "a finding here and a
    /// filed ask on `delve-graph-roll`", the module that would need to change if this fires). Which
    /// encounters are "siblings" is `delve-graph-roll`'s own graph-row concept (D1.x, already built)
    /// — this module reads the caller's own grouping, never re-derives it.
    /// </summary>
    public static IReadOnlyList<string> SiblingCollisions(IEnumerable<(string SiblingGroup, EncounterCell Cell)> encounters)
    {
        if (encounters is null) throw new ArgumentNullException(nameof(encounters));

        var seenPerGroup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var collided = new List<string>();
        foreach (var (group, cell) in encounters)
        {
            var seen = seenPerGroup.TryGetValue(group, out var s) ? s : seenPerGroup[group] = new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(Key(cell)) && !collided.Contains(group))
                collided.Add(group);
        }
        return collided;
    }

    static string Key(EncounterCell c) =>
        string.Join(".", c.PostureMultiset.Select(p => p.ToString()).OrderBy(s => s, StringComparer.Ordinal)) + "|" +
        string.Join(".", c.ElementSpread.Select(e => e.ToString()).OrderBy(s => s, StringComparer.Ordinal)) + "|" + c.Formation;
}
