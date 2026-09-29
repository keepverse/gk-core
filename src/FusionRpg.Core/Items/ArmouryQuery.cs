using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items;

/// <summary>Whether a container's copies are interchangeable (item-ideal.md, `armoury`, I13 §4.3).</summary>
public enum StorageGrade
{
    /// <summary>Every copy is identical — <c>prefix_rolls = 0 AND suffix_rolls = 0</c>. Stored as a counter.</summary>
    Stock,

    /// <summary>Carries at least one rolled value — unique by construction, cannot stack.</summary>
    Rolled,
}

public static class StorageGrading
{
    /// <summary>
    /// Storage grade is DERIVED, never authored (ssot-inventory.md §2.2). An authored flag could
    /// disagree with the container it describes; a derived one cannot. Stock items are a counter
    /// because every copy is provably identical — that is what makes 48 specimens × 15 slots 720
    /// cells and never 720 decisions.
    /// </summary>
    public static StorageGrade GradeOf(ContainerRow c) =>
        c.PrefixRolls == 0 && c.SuffixRolls == 0 ? StorageGrade.Stock : StorageGrade.Rolled;
}

/// <summary>
/// One row the query surface operates over — assembled by the caller (the DAL joins
/// <c>rpg_item</c>/<c>effect_instance</c>/<c>effect_container</c>; this type carries no SQL and no
/// database access, so it is the same shape whether it is being unit-tested or served live).
/// </summary>
public sealed record ArmouryEntry(
    string InstanceId,
    string ContainerId,
    string Role,
    string Frame,
    int RarityOrdinal,
    string AcquiredUtc,
    bool Assigned,
    bool Locked,
    bool Unseen,
    bool Stale,
    int RollQualityMilli);

public enum ArmourySortKey
{
    Acquired,
    RarityOrdinal,
    Role,
    RollQualityMilli,
    AssignedTo,
    Locked,
    Unseen,
}

/// <summary>I13 §5.9's filter set. Every field is optional — an unset field imposes no constraint.</summary>
public sealed record ArmouryFilter(
    string? Role = null,
    string? Frame = null,
    int? RarityMin = null,
    int? RarityMax = null,
    bool? Assigned = null,
    bool? Locked = null,
    bool? Unseen = null,
    bool? Stale = null);

/// <summary>Keyset page request. Offset paging is refused by construction — there is no <c>Offset</c> field.</summary>
public sealed record ArmouryPageRequest(int Limit, string? AfterKey = null);

public sealed record ArmouryPage(IReadOnlyList<ArmouryEntry> Items, string? NextAfterKey);

/// <summary>
/// The query surface — filter, sort, page. No SQL anywhere in this file (`guard-dal.ps1`): it
/// operates purely over an already-materialised <see cref="ArmouryEntry"/> sequence, which is what
/// keeps it testable without a database and keeps the DAL the only place SQL is allowed to live.
/// </summary>
public static class ArmouryQuery
{
    /// <summary>Largest page one call may return. ⚠ <b>A per-request runtime cap — exempt from
    /// AGENTS.md's no-hard-ceilings rule, and saying so here is the rule's own condition.</b> It
    /// bounds one response, never what a player may own: the collection itself is unlimited, and its
    /// only ceiling is <c>RpgStore.InventoryCeiling</c>, an abuse guard with its own exemption note.
    /// Paging past this is the <c>after</c> cursor's job, so a larger armoury costs more calls, never
    /// fewer items.
    ///
    /// <para><b>Structural, not a tunable</b> (<c>tunables-ssot.md</c>): a balance pass has no reason
    /// to change a page size — changing it moves how much arrives per call, not how the game
    /// feels.</para></summary>
    const int MaxLimit = 200;

    public static IEnumerable<ArmouryEntry> ApplyFilter(IEnumerable<ArmouryEntry> entries, ArmouryFilter filter)
    {
        var q = entries;
        if (filter.Role is { } role) q = q.Where(e => string.Equals(e.Role, role, StringComparison.Ordinal));
        if (filter.Frame is { } frame) q = q.Where(e => string.Equals(e.Frame, frame, StringComparison.Ordinal));
        if (filter.RarityMin is { } rMin) q = q.Where(e => e.RarityOrdinal >= rMin);
        if (filter.RarityMax is { } rMax) q = q.Where(e => e.RarityOrdinal <= rMax);
        if (filter.Assigned is { } assigned) q = q.Where(e => e.Assigned == assigned);
        if (filter.Locked is { } locked) q = q.Where(e => e.Locked == locked);
        if (filter.Unseen is { } unseen) q = q.Where(e => e.Unseen == unseen);
        if (filter.Stale is { } stale) q = q.Where(e => e.Stale == stale);
        return q;
    }

    /// <summary>Rarity always sorts on the ORDINAL, never the label (I13 §5.9) — a label sort would
    /// put "almanac" before "chaff" alphabetically, which is backwards.</summary>
    public static IEnumerable<ArmouryEntry> ApplySort(IEnumerable<ArmouryEntry> entries, ArmourySortKey key) => key switch
    {
        ArmourySortKey.Acquired => entries.OrderByDescending(e => e.AcquiredUtc, StringComparer.Ordinal),
        ArmourySortKey.RarityOrdinal => entries.OrderByDescending(e => e.RarityOrdinal),
        ArmourySortKey.Role => entries.OrderBy(e => e.Role, StringComparer.Ordinal),
        ArmourySortKey.RollQualityMilli => entries.OrderByDescending(e => e.RollQualityMilli),
        ArmourySortKey.AssignedTo => entries.OrderByDescending(e => e.Assigned),
        ArmourySortKey.Locked => entries.OrderByDescending(e => e.Locked),
        ArmourySortKey.Unseen => entries.OrderByDescending(e => e.Unseen),
        _ => entries,
    };

    /// <summary>
    /// Keyset page over an already sorted, already filtered sequence. <paramref name="afterKey"/> is
    /// the opaque <c>"instance_id"</c> composite this page contract promises (the sort value itself is
    /// not needed here because the caller already sorted; this only needs to find where the previous
    /// page ended and take the next slice) — offset paging is refused by construction: there is no
    /// numeric skip anywhere in this method.
    /// </summary>
    public static ArmouryPage ApplyPage(IReadOnlyList<ArmouryEntry> sorted, ArmouryPageRequest page)
    {
        var limit = Math.Clamp(page.Limit, 1, MaxLimit);

        var start = 0;
        if (!string.IsNullOrEmpty(page.AfterKey))
        {
            var idx = -1;
            for (var i = 0; i < sorted.Count; i++)
                if (string.Equals(sorted[i].InstanceId, page.AfterKey, StringComparison.Ordinal)) { idx = i; break; }
            start = idx < 0 ? sorted.Count : idx + 1;
        }

        var slice = sorted.Skip(start).Take(limit).ToList();
        var next = slice.Count == limit && start + limit < sorted.Count ? slice[^1].InstanceId : null;
        return new ArmouryPage(slice, next);
    }
}
