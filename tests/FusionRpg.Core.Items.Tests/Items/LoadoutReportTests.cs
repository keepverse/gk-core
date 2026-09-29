using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `armoury`'s conflict report — the half `spec-armoury.md` assigns to module 2 rather than to the
/// deferred apply path: <i>"The library, the conflict report and G-C ship here, and module 2 needs
/// nothing from module 4 to ship its store or its query surface."</i>
///
/// <para>Nothing here writes. The report answers "what would this do" so a refusal can name cells;
/// the write into <c>rpg_item_assignment</c> stays module 4's and stays sequenced there.</para>
/// </summary>
public class LoadoutReportTests
{
    static LoadoutEntryStatus Present(string role, string refId, string kind = "item")
        => new(role, kind, refId, LoadoutEntryState.Present);

    static IReadOnlyDictionary<string, LoadoutCell> HeldBy(params (string RefId, string Specimen, string Role)[] rows)
        => rows.ToDictionary(r => r.RefId, r => new LoadoutCell(r.Specimen, r.Role), StringComparer.Ordinal);

    /// <summary>`spec-armoury.md`'s own named test: two presets wanting one item.</summary>
    [Fact]
    public void Applying_a_loadout_whose_item_is_held_elsewhere_refuses_with_LoadoutConflict()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("armament-primary", "inst-1") },
            targetSpecimenId: "spec-B",
            HeldBy(("inst-1", "spec-A", "armament-primary")));

        Assert.True(plan.Refused);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("armament-primary", conflict.Role);
        Assert.Equal("inst-1", conflict.RefId);
        Assert.Equal(new LoadoutCell("spec-A", "armament-primary"), conflict.HeldBy);

        // A refused apply strips nothing — the refusal is the whole outcome.
        Assert.Empty(plan.Stripped);
    }

    /// <summary>The conflict names the CELL, not a count. A report that only said "1 conflict" is
    /// exactly the answer "why is my other creature naked" cannot be built from.</summary>
    [Fact]
    public void The_refusal_lists_exactly_which_cells_hold_what()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("armament-primary", "inst-1"), Present("core-guard", "inst-2") },
            "spec-C",
            HeldBy(("inst-1", "spec-A", "manipulator"), ("inst-2", "spec-B", "core-guard")));

        Assert.True(plan.Refused);
        Assert.Equal(2, plan.Conflicts.Count);
        Assert.Contains(plan.Conflicts, c => c.RefId == "inst-1" && c.HeldBy == new LoadoutCell("spec-A", "manipulator"));
        Assert.Contains(plan.Conflicts, c => c.RefId == "inst-2" && c.HeldBy == new LoadoutCell("spec-B", "core-guard"));
    }

    /// <summary>`force = true` steals — and <b>reports what it stripped</b>. Never a silent strip.</summary>
    [Fact]
    public void Force_steals_and_reports_every_cell_it_would_empty()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("armament-primary", "inst-1"), Present("core-guard", "inst-2") },
            "spec-C",
            HeldBy(("inst-1", "spec-A", "armament-primary"), ("inst-2", "spec-A", "core-guard")),
            force: true);

        Assert.False(plan.Refused);
        Assert.Equal(2, plan.Conflicts.Count);
        Assert.Equal(2, plan.Stripped.Count);
        Assert.Contains(new LoadoutCell("spec-A", "armament-primary"), plan.Stripped);
        Assert.Contains(new LoadoutCell("spec-A", "core-guard"), plan.Stripped);
    }

    /// <summary>Two entries pinning the same worn copy strip that one cell once, not twice — a
    /// duplicated strip line reads as two creatures losing gear when only one did.</summary>
    [Fact]
    public void A_cell_contested_twice_is_stripped_once()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("jewel-minor-a", "inst-1"), Present("jewel-minor-b", "inst-1") },
            "spec-C",
            HeldBy(("inst-1", "spec-A", "jewel-minor-a")),
            force: true);

        Assert.Equal(2, plan.Conflicts.Count);
        Assert.Single(plan.Stripped);
    }

    /// <summary>Re-applying a preset to the creature already wearing it is not a conflict with itself.</summary>
    [Fact]
    public void An_item_already_in_the_target_cell_is_not_a_conflict()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("armament-primary", "inst-1") },
            "spec-A",
            HeldBy(("inst-1", "spec-A", "armament-primary")));

        Assert.False(plan.Refused);
        Assert.Empty(plan.Conflicts);
    }

    /// <summary>Same creature, different role still moves the item — so it is still a conflict, and the
    /// cell it would leave is still named.</summary>
    [Fact]
    public void The_same_specimen_wearing_it_in_another_role_is_still_a_conflict()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("armament-secondary", "inst-1") },
            "spec-A",
            HeldBy(("inst-1", "spec-A", "armament-primary")));

        Assert.True(plan.Refused);
        Assert.Equal(new LoadoutCell("spec-A", "armament-primary"), Assert.Single(plan.Conflicts).HeldBy);
    }

    /// <summary>A stock entry names a `container_id` and never pins one copy, so two presets naming
    /// the same stock id are not fighting over an item. Running out is reported as `Missing` by the
    /// reader instead — treating this as a conflict would refuse a legal apply.</summary>
    [Fact]
    public void A_stock_entry_never_conflicts_by_identity()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("core-guard", "item.iron-band", kind: "stock") },
            "spec-B",
            HeldBy(("item.iron-band", "spec-A", "core-guard")));

        Assert.False(plan.Refused);
        Assert.Empty(plan.Conflicts);
    }

    /// <summary>A missing entry is returned and skipped, never dropped and never applied — the two
    /// halves of "never a silently shorter loadout" asserted together.</summary>
    [Fact]
    public void A_missing_entry_is_reported_and_never_becomes_a_conflict()
    {
        var entries = new[]
        {
            new LoadoutEntryStatus("armament-primary", "item", "inst-gone", LoadoutEntryState.Missing),
            Present("core-guard", "inst-2"),
        };

        var plan = LoadoutReport.Plan(entries, "spec-B", HeldBy(("inst-gone", "spec-A", "armament-primary")));

        Assert.Equal(2, plan.Entries.Count);
        Assert.Contains(plan.Entries, e => e.RefId == "inst-gone" && e.State == LoadoutEntryState.Missing);
        Assert.Empty(plan.Conflicts);
        Assert.False(plan.Refused);
    }

    [Fact]
    public void An_unheld_loadout_applies_clean()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("armament-primary", "inst-1"), Present("core-guard", "inst-2") },
            "spec-A",
            HeldBy());

        Assert.False(plan.Refused);
        Assert.Empty(plan.Conflicts);
        Assert.Empty(plan.Stripped);
        Assert.Equal(2, plan.Entries.Count);
    }

    /// <summary>`force` on a clean apply is a no-op, not an excuse to report a strip that never
    /// happened.</summary>
    [Fact]
    public void Force_on_a_clean_apply_strips_nothing()
    {
        var plan = LoadoutReport.Plan(
            new[] { Present("armament-primary", "inst-1") }, "spec-A", HeldBy(), force: true);

        Assert.False(plan.Refused);
        Assert.Empty(plan.Stripped);
    }

    // ---- AssignmentRefKindFor (build-preset BP1.6/X4): the one place the loadout table's "item" ---
    // ---- meets the assignment table's "rolled" ------------------------------------------------

    [Fact]
    public void AssignmentRefKindFor_maps_the_loadout_instance_kind_to_the_assignment_rolled_kind()
    {
        Assert.Equal(EquipRefKinds.Rolled, LoadoutReport.AssignmentRefKindFor(LoadoutReport.InstanceRefKind));
    }

    [Fact]
    public void AssignmentRefKindFor_keeps_the_stock_kind_as_is()
    {
        Assert.Equal(EquipRefKinds.Stock, LoadoutReport.AssignmentRefKindFor(EquipRefKinds.Stock));
    }

    [Fact]
    public void AssignmentRefKindFor_throws_on_an_unknown_loadout_ref_kind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadoutReport.AssignmentRefKindFor("sorcery"));
    }
}
