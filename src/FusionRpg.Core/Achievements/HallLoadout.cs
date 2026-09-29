namespace FusionRpg.Core.Achievements;

// Empire Hall loadout math (spec-empire-titles.md). Pure: no I/O, no Unity.
// Titles contribute atom families; shares come from tuning per family.
// Stacking: additive across variants, one-per-group within (family, variant) —
// the container group default — keeping max Tier, ties broken by container id
// (deterministic; never binding_id, which is generated per grant).
// Soft caps squash rationally: cap*sum/(cap+sum) — bounded, never Min/clamp.
// P2: a title raising yield without an upkeep term refuses naming the container.
public sealed record HallTitleInput(string ContainerId, string FamilyId, string Variant, int Tier);

public sealed record HallIntent(long YieldShareMilli, long UpkeepShareMilli);

public static class HallLoadout
{
    // Structural capacity (T2 rationale comment): 3 slots bound the Hall layout and the
    // slot grammar hall-{1,2,3} — a correctness property of the loadout shape, not a feel
    // dial. Balance lives in the share/cap tunables, never in this number.
    public const int HallSlots = 3;

    public static bool IsHallSlot(string? slot, out int index)
    {
        index = 0;
        if (slot is null || !slot.StartsWith("hall-", StringComparison.Ordinal)) return false;
        return int.TryParse(slot["hall-".Length..], out index) && index >= 1 && index <= HallSlots;
    }

    /// <summary>
    /// Resolve a Hall loadout. Inputs are flat title atoms; they are grouped by container
    /// (one group = one title) before the P2 rule runs: a TITLE raising yield without
    /// carrying upkeep refuses naming its container. Families absent from the share maps
    /// contribute 0 (only tuned families move the economy — stated, never silent).
    /// </summary>
    public static (HallIntent? Intent, string? Cause) Resolve(
        IReadOnlyList<HallTitleInput> titles, AchievementTitlesTuning tuning)
    {
        foreach (var g in titles.GroupBy(t => t.ContainerId))
        {
            var titleYield = g.Sum(t => TitleYield(t, tuning));
            var titleUpkeep = g.Sum(t => TitleUpkeep(t, tuning));
            if (titleYield > 0 && titleUpkeep == 0)
                return (null, $"hall: title '{g.Key}' raises yield without an upkeep term");
        }
        // One-per-group: same (family, variant) from two titles keeps max tier.
        var winners = titles
            .GroupBy(t => (t.FamilyId, t.Variant))
            .Select(g => g.OrderByDescending(t => t.Tier)
                .ThenBy(t => t.ContainerId, StringComparer.Ordinal).First())
            .ToList();
        long yield = 0, upkeep = 0;
        foreach (var w in winners)
        {
            yield += TitleYield(w, tuning);
            upkeep += TitleUpkeep(w, tuning);
        }
        return (new HallIntent(
            Squash(yield, tuning.HallYieldCapMilli),
            Squash(upkeep, tuning.HallUpkeepCapMilli)), null);
    }

    static long TitleYield(HallTitleInput t, AchievementTitlesTuning tuning) =>
        tuning.EquipShareMilli.TryGetValue(t.FamilyId, out var v) ? v : 0;

    static long TitleUpkeep(HallTitleInput t, AchievementTitlesTuning tuning) =>
        tuning.UpkeepShareMilli.TryGetValue(t.FamilyId, out var v) ? v : 0;

    static long Squash(long sum, long cap) =>
        cap <= 0 || sum <= 0 ? sum : cap * sum / (cap + sum);
}
