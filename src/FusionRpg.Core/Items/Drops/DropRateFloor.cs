using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// A tunable, universal floor on how rare a drop-table entry may ever be configured to be —
/// `spec-rate-floor.md`. Applied to EVERY entry in every group, refusing at import rather than
/// clamping (a silent clamp is the exact "your gear stopped mattering with no symptom" failure
/// AGENTS.md's caps section names). A drop-table-ENTRY concept only — never touches the rarity ladder
/// (`item-rarity.v1.json`'s `dropWeightPer100k`) or `bands.v1.json`'s frozen registry; D2 (2026-09-07)
/// deliberately keeps this off the ten-rung ladder.
/// </summary>
public static class DropRateFloor
{
    /// <summary>
    /// Checks one entry's already-computed effective rate against the floor. Takes the
    /// CALLER-COMPUTED effective weight, never `entry.Weight` directly — an entry evaluated at an ilvl
    /// where it is not in-band has effective weight 0 and is not a rate at all (found in review: a
    /// version of this signature that re-derived `entry.Weight` let an implementer evaluate an
    /// out-of-band entry by mistake). `Enabled`/zero-weight are `DropTableModel.EffectiveWeight`'s own
    /// concern upstream — an entry that is already zeroed there is not re-tested here, which is why a
    /// disabled placeholder row with a real authored `Weight` never false-positives against the floor.
    /// </summary>
    public static AtomRejection ValidateEntry(
        DropTableEntryRow entry, long entryEffectiveWeight, long groupTotalWeight, DropRateFloorTuning tuning)
    {
        if (entryEffectiveWeight <= 0) return AtomRejection.Ok;
        if (groupTotalWeight <= 0)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                "a group's total weight must be positive to evaluate a rate floor");

        long ratePerMillion = checked(entryEffectiveWeight * 1_000_000L) / groupTotalWeight;
        if (ratePerMillion < tuning.MinRatePerMillion)
            return AtomRejection.ContentRule("drop.rate-below-floor",
                $"entry '{entry.RefId}' resolves to {ratePerMillion} per million within its group " +
                $"(effective weight {entryEffectiveWeight} / group total {groupTotalWeight}), below the " +
                $"configured floor of {tuning.MinRatePerMillion} per million — widen the weight, shrink " +
                "the group's other weights, or split it into its own table");
        return AtomRejection.Ok;
    }

    /// <summary>
    /// Walks a group across every ilvl breakpoint that could change its composition, checking each
    /// in-band entry there. The breakpoint set is `{every MinIlvl} ∪ {every MaxIlvl + 1}` — NOT
    /// `{MinIlvl, MaxIlvl}` (found in review: `EffectiveWeight`'s band is inclusive at `MaxIlvl`, so
    /// the ilvl where a sibling actually drops out of band, and a surviving entry's share spikes, is
    /// one step past its own `MaxIlvl`, never at it). An entry that declares no ilvl band at all still
    /// gets one static check at ilvl 1, the same value `DropTableEntryRow`'s own unbounded default
    /// represents.
    /// </summary>
    public static IReadOnlyList<AtomRejection> ValidateGroupAcrossBreakpoints(
        IReadOnlyList<DropTableEntryRow> group, DropRateFloorTuning tuning)
    {
        if (group is null) throw new ArgumentNullException(nameof(group));

        var breakpoints = new SortedSet<int>();
        foreach (var e in group)
        {
            if (e.MinIlvl is { } lo) breakpoints.Add(lo);
            if (e.MaxIlvl is { } hi) breakpoints.Add(checked(hi + 1));
        }
        if (breakpoints.Count == 0) breakpoints.Add(1);

        var results = new List<AtomRejection>();
        foreach (var ilvl in breakpoints)
        {
            long groupTotal = 0;
            foreach (var e in group)
                groupTotal = checked(groupTotal + DropTableDraw.EffectiveWeight(e, ilvl));

            foreach (var e in group)
            {
                var eff = DropTableDraw.EffectiveWeight(e, ilvl);
                if (eff <= 0) continue; // not in-band at this breakpoint — nothing to validate here
                results.Add(ValidateEntry(e, eff, groupTotal, tuning));
            }
        }
        return results;
    }
}
