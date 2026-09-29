namespace FusionRpg.Core.Achievements;
// Actor title slots + worn selection (spec-actor-titles.md). Pure: no I/O.
// Title equip reuses the existing equip path end to end — effect_binding rows
// (owner unique-actor:{instanceId}, slot title-{1,2,3}) resolve through
// EquippedBoundAtoms.DerivedFromStore → EquipAtomSource → AtomDerivedSubsystem
// with equip:title-{slot}:{instance} SourceIds. No parallel composer, no new
// subsystem: a second fold would be the BattleStatComposer defect class.
// Worn display is derived (highest tier wins + container-id tiebreak), never stored.
public sealed record WornCandidate(string ContainerId, string InstanceId, int MaxTier);

public static class TitleWornSelector
{
    // Structural capacity (T2 rationale comment): 3 title slots mirror the Hall and bound
    // the title-{1,2,3} slot grammar — a correctness property of the loadout shape, never
    // widened through the 15-role gear registry (separate domain by design).
    public const int TitleSlots = 3;

    public static bool IsTitleSlot(string? slot, out int index)
    {
        index = 0;
        if (slot is null || !slot.StartsWith("title-", StringComparison.Ordinal)) return false;
        return int.TryParse(slot["title-".Length..], out index) && index >= 1 && index <= TitleSlots;
    }

    /// <summary>Highest tier wins; ties break on container id (deterministic, content-derived).</summary>
    public static WornCandidate? PickWorn(IReadOnlyList<WornCandidate> equipped)
    {
        WornCandidate? best = null;
        foreach (var c in equipped)
        {
            if (best is null || c.MaxTier > best.MaxTier ||
                (c.MaxTier == best.MaxTier &&
                 string.Compare(c.ContainerId, best.ContainerId, StringComparison.Ordinal) < 0))
                best = c;
        }
        return best;
    }
}

/// <summary>
/// Six-resource rule (spec-actor-titles.md): title atoms touching
/// resource.max|regen|efficiency.* must cover all six resource ids
/// (DerivedStatChannels.ResourceIds). Pure over channel strings; returns the
/// missing ids CSV, or null when the rule passes (nothing touched, or all six).
/// </summary>
public static class TitleResourceRule
{
    public static string? MissingIds(IEnumerable<string?> channels)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in channels)
        {
            if (string.IsNullOrEmpty(c)) continue;
            foreach (var prefix in new[] { "resource.max.", "resource.regen.", "resource.efficiency." })
            {
                if (c.StartsWith(prefix, StringComparison.Ordinal))
                    touched.Add(c[prefix.Length..]);
            }
        }
        if (touched.Count == 0) return null;
        var missing = new List<string>();
        foreach (var id in Stats.Derived.DerivedStatChannels.ResourceIds)
            if (!touched.Contains(id)) missing.Add(id);
        return missing.Count == 0 ? null : string.Join(",", missing);
    }
}
