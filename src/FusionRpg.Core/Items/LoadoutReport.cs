namespace FusionRpg.Core.Items;

/// <summary>What a saved loadout entry resolves to <b>right now</b>, re-checked on every read.
/// <c>spec-armoury.md</c>'s loadout section is explicit: <i>"Entries validate on read, never silently
/// drop. An entry whose item was salvaged returns with a <c>missing</c> marker, so the player sees the
/// hole."</i> A shorter list is the one answer this type exists to refuse.</summary>
public enum LoadoutEntryState
{
    /// <summary>The reference still resolves: the instance is owned, or the stock count is above zero.</summary>
    Present,

    /// <summary>The reference no longer resolves — salvaged, traded away, or spent to the last copy.
    /// The entry is still returned, so the hole is visible rather than inferred from a short list.</summary>
    Missing,
}

/// <summary>One cell of <c>rpg_item_assignment</c> — the <c>(specimen, role)</c> pair that holds an
/// item today. <c>Role</c> is the persisted role id (<see cref="ItemRoles.Id"/>'s spelling), matching
/// what both <c>rpg_item_assignment</c> and <c>rpg_item_loadout_entry</c> store.</summary>
public sealed record LoadoutCell(string SpecimenId, string Role);

/// <summary>One role's entry in a loadout, with the validity the library re-derives on read.</summary>
public sealed record LoadoutEntryStatus(string Role, string RefKind, string RefId, LoadoutEntryState State);

/// <summary>One entry that cannot be applied because a specific copy is already worn somewhere else.
/// <c>HeldBy</c> names the exact cell rather than reporting a count — <i>"listing exactly which cells
/// hold what"</i> is the spec's wording, and a bare count is what makes <i>"why is my other creature
/// naked"</i> unanswerable.</summary>
public sealed record LoadoutConflict(string Role, string RefKind, string RefId, LoadoutCell HeldBy);

/// <summary>The answer to "what would applying this loadout do" — computed and returned <b>before</b>
/// anything is written. Nothing in this file writes; the write is module 4's
/// <c>rpg_item_assignment</c> and is sequenced there.</summary>
/// <param name="Entries">Every entry, in stored order, including the <see cref="LoadoutEntryState.Missing"/>
/// ones. Never filtered — a preset that quietly loses a piece is the defect.</param>
/// <param name="Conflicts">Present entries whose specific copy is worn in another cell.</param>
/// <param name="Stripped">The cells a <c>force</c> apply would empty. Empty when
/// <paramref name="Refused"/> is true, because a refused apply strips nothing.</param>
/// <param name="Refused">True when conflicts exist and <c>force</c> was not asked for — the default.</param>
public sealed record LoadoutPlan(
    IReadOnlyList<LoadoutEntryStatus> Entries,
    IReadOnlyList<LoadoutConflict> Conflicts,
    IReadOnlyList<LoadoutCell> Stripped,
    bool Refused);

/// <summary>
/// The loadout library's judgement half — <c>spec-armoury.md</c> assigns it here, not to the apply
/// path: <i>"The library, <b>the conflict report</b> and G-C ship here, and module 2 needs nothing
/// from module 4 to ship its store or its query surface."</i> Only the <i>write</i> lands with
/// module 4.
///
/// <para><b>Pure and DB-free</b>, the same shape as <see cref="SalvageGuards"/> — the caller assembles
/// the facts, so this stays unit-testable without a database and there is exactly one place the
/// question "would this apply take something off another creature" is answered.</para>
/// </summary>
public static class LoadoutReport
{
    /// <summary>The reference kind that pins one specific copy. The sibling kind, <c>"stock"</c>, is a
    /// <c>container_id</c> and <b>never pins one copy</b> (<c>RpgItemLoadoutEntryRow</c>'s own rule), so
    /// two presets naming the same stock id are not in conflict — they draw from a count, and a count
    /// that has run out is reported as <see cref="LoadoutEntryState.Missing"/> instead.</summary>
    public const string InstanceRefKind = "item";

    /// <param name="entries">Already validated by the reader, so a <c>Missing</c> entry arrives marked
    /// rather than being re-derived here.</param>
    /// <param name="targetSpecimenId">The specimen the loadout is being applied to. A copy already in
    /// this specimen's own cell for the same role is not a conflict — it is already correct.</param>
    /// <param name="heldBy">Where each instance-pinned <c>refId</c> currently sits, or absent when it
    /// sits nowhere. Keyed by <c>refId</c> because the kind is fixed at
    /// <see cref="InstanceRefKind"/>.</param>
    /// <param name="force">Steal the contested copies. Never silent: every cell the steal would empty
    /// comes back in <see cref="LoadoutPlan.Stripped"/>.</param>
    public static LoadoutPlan Plan(
        IEnumerable<LoadoutEntryStatus> entries,
        string targetSpecimenId,
        IReadOnlyDictionary<string, LoadoutCell> heldBy,
        bool force = false)
    {
        var all = entries as IReadOnlyList<LoadoutEntryStatus> ?? entries.ToList();
        var conflicts = new List<LoadoutConflict>();

        foreach (var e in all)
        {
            if (e.State != LoadoutEntryState.Present) continue;
            if (!string.Equals(e.RefKind, InstanceRefKind, StringComparison.Ordinal)) continue;
            if (!heldBy.TryGetValue(e.RefId, out var cell)) continue;

            // The cell this very apply is about to fill is not a conflict with itself.
            if (string.Equals(cell.SpecimenId, targetSpecimenId, StringComparison.Ordinal) &&
                string.Equals(cell.Role, e.Role, StringComparison.Ordinal)) continue;

            conflicts.Add(new LoadoutConflict(e.Role, e.RefKind, e.RefId, cell));
        }

        var refused = conflicts.Count > 0 && !force;

        var stripped = refused || conflicts.Count == 0
            ? Array.Empty<LoadoutCell>()
            : conflicts.Select(c => c.HeldBy).Distinct().ToArray();

        return new LoadoutPlan(all, conflicts, stripped, refused);
    }

    /// <summary>
    /// build-preset BP1.6/X4 (spec-item-loadout-apply.md): a loadout pins an instance-specific copy
    /// as <see cref="InstanceRefKind"/> (<c>"item"</c>); an assignment pins the same copy as
    /// <see cref="EquipRefKinds.Rolled"/> (<c>"rolled"</c>) -- one word apart, the exact defect R2
    /// (2026-09-06, this class's own doc comment on <see cref="EquipRefKinds"/>) already named once.
    /// This is the single place the two vocabularies meet, so an apply loop dispatching by
    /// assignment-side kind can never drift from what a loadout entry actually stored.
    /// </summary>
    public static string AssignmentRefKindFor(string loadoutRefKind) => loadoutRefKind switch
    {
        InstanceRefKind => EquipRefKinds.Rolled,
        EquipRefKinds.Stock => EquipRefKinds.Stock,
        _ => throw new ArgumentOutOfRangeException(nameof(loadoutRefKind), loadoutRefKind, "unknown loadout ref kind"),
    };
}
