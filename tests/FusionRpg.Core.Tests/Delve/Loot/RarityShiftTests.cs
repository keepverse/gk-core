using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>D3.14 (spec-dungeon-loot.md §4, "Rung reward columns — the floor and the shift"). A small,
/// hand-built 5-rung ladder (ordinals 10-50, weights 1000/300/90/25/7 — the real shipped dropBand
/// weight ladder's own numbers, reused here only as recognizable, hand-verifiable values) rather than
/// the real item-corpus ladder, so every expected delta is computed by hand, not approximated.</summary>
public class RarityShiftTests
{
    static RarityRung Rung(string id, int ordinal, int weight) => new(id, ordinal, 0, 0, 0, 0, weight);

    static readonly IReadOnlyList<RarityRung> Ladder = new[]
    {
        Rung("staple", 10, 1000),
        Rung("frequent", 20, 300),
        Rung("occasional", 30, 90),
        Rung("seldom", 40, 25),
        Rung("exceptional", 50, 7),
    };

    static int NewWeight(IReadOnlyDictionary<int, int> shift, int ordinal, int oldWeight) => oldWeight + shift[ordinal];

    // ---- ComposeFloor ----

    [Fact]
    public void ComposeFloor_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => RarityShift.ComposeFloor(null!, "staple"));
        Assert.Throws<ArgumentNullException>(() => RarityShift.ComposeFloor(Ladder, null!));
    }

    [Fact]
    public void All_null_floors_compose_to_no_floor_at_all()
    {
        Assert.Null(RarityShift.ComposeFloor(Ladder, null, null, null));
    }

    [Fact]
    public void A_single_floor_passes_through()
    {
        Assert.Equal("occasional", RarityShift.ComposeFloor(Ladder, "occasional"));
    }

    [Fact]
    public void The_highest_ordinal_among_several_floors_wins_never_an_average()
    {
        Assert.Equal("seldom", RarityShift.ComposeFloor(Ladder, "staple", "seldom", "occasional", null));
    }

    [Fact]
    public void Equal_floors_compose_to_the_same_floor()
    {
        Assert.Equal("frequent", RarityShift.ComposeFloor(Ladder, "frequent", "frequent"));
    }

    // ---- ToWeightShift: argument validation ----

    [Fact]
    public void ToWeightShift_null_or_empty_ladder_throws()
    {
        Assert.Throws<ArgumentNullException>(() => RarityShift.ToWeightShift(null!, 1));
        Assert.Throws<ArgumentException>(() => RarityShift.ToWeightShift(Array.Empty<RarityRung>(), 1));
    }

    [Fact]
    public void A_single_rung_ladder_with_a_nonzero_shift_throws()
    {
        var oneRung = new[] { Rung("staple", 10, 1000) };
        Assert.Throws<ArgumentException>(() => RarityShift.ToWeightShift(oneRung, 1));
    }

    [Fact]
    public void A_single_rung_ladder_at_n_zero_does_not_throw()
    {
        var oneRung = new[] { Rung("staple", 10, 1000) };
        var shift = RarityShift.ToWeightShift(oneRung, 0);
        Assert.Equal(0, shift[10]);
    }

    // ---- ToWeightShift: n = 0 is empty (spec, verbatim) ----

    [Fact]
    public void N_zero_is_empty_every_ordinal_maps_to_zero()
    {
        var shift = RarityShift.ToWeightShift(Ladder, 0);
        Assert.Equal(5, shift.Count);
        Assert.All(shift.Values, d => Assert.Equal(0, d));
    }

    // ---- ToWeightShift: n = 1, the spec's own precisely-cited case ----

    [Fact]
    public void N_one_sums_to_zero()
    {
        var shift = RarityShift.ToWeightShift(Ladder, 1);
        Assert.Equal(0, shift.Values.Sum());
    }

    [Fact]
    public void N_one_zeroes_exactly_the_bottom_rung()
    {
        var shift = RarityShift.ToWeightShift(Ladder, 1);
        Assert.Equal(0, NewWeight(shift, 10, 1000));
    }

    [Fact]
    public void N_one_moves_every_middle_rungs_own_weight_up_by_one_step()
    {
        var shift = RarityShift.ToWeightShift(Ladder, 1);
        Assert.Equal(1000, NewWeight(shift, 20, 300));  // frequent now carries staple's own old weight
        Assert.Equal(300, NewWeight(shift, 30, 90));    // occasional now carries frequent's
        Assert.Equal(90, NewWeight(shift, 40, 25));     // seldom now carries occasional's
    }

    [Fact]
    public void N_one_the_top_rung_absorbs_rather_than_losing_its_own_weight()
    {
        // "Top absorbing": exceptional keeps ITS OWN old weight (7) AND gains what shifted up from
        // seldom (25) -- 32, not a plain 25-only replacement (which would lose the old 7 to nowhere).
        var shift = RarityShift.ToWeightShift(Ladder, 1);
        Assert.Equal(7 + 25, NewWeight(shift, 50, 7));
    }

    // ---- ToWeightShift: n = 2 generalizes the same property ----

    [Fact]
    public void N_two_also_sums_to_zero()
    {
        var shift = RarityShift.ToWeightShift(Ladder, 2);
        Assert.Equal(0, shift.Values.Sum());
    }

    [Fact]
    public void N_two_zeroes_the_bottom_two_rungs()
    {
        var shift = RarityShift.ToWeightShift(Ladder, 2);
        Assert.Equal(0, NewWeight(shift, 10, 1000));
        Assert.Equal(0, NewWeight(shift, 20, 300));
        Assert.Equal(1000, NewWeight(shift, 30, 90)); // occasional now carries staple's own old weight (2 steps up)
    }

    // ---- ToWeightShift: negative n (this file's own reasoned, not separately spec-cited, extrapolation) ----

    [Fact]
    public void Negative_n_also_sums_to_zero()
    {
        var shift = RarityShift.ToWeightShift(Ladder, -1);
        Assert.Equal(0, shift.Values.Sum());
    }

    [Fact]
    public void Negative_n_zeroes_the_top_rung_by_symmetry()
    {
        var shift = RarityShift.ToWeightShift(Ladder, -1);
        Assert.Equal(0, NewWeight(shift, 50, 7));
    }

    [Fact]
    public void Negative_n_the_bottom_rung_absorbs_by_symmetry()
    {
        var shift = RarityShift.ToWeightShift(Ladder, -1);
        // staple keeps its own 1000 AND gains what shifted down from frequent (300).
        Assert.Equal(1000 + 300, NewWeight(shift, 10, 1000));
    }

    [Fact]
    public void Negative_n_moves_every_middle_rungs_own_weight_down_by_one_step()
    {
        var shift = RarityShift.ToWeightShift(Ladder, -1);
        Assert.Equal(90, NewWeight(shift, 20, 300));  // frequent now carries occasional's own old weight
        Assert.Equal(25, NewWeight(shift, 30, 90));   // occasional now carries seldom's
        Assert.Equal(7, NewWeight(shift, 40, 25));    // seldom now carries exceptional's
    }

    // ---- Apply: the per-table orchestrator (D3.11) ----------------------------------------------------

    static DropTableRow SampleTable(string tableId, string? entryFloor = null) => new(
        tableId, SourceAllow: new[] { "web" }, MinIlvl: null, MaxIlvl: null, Enabled: true, Revision: 1,
        Groups: new[]
        {
            new DropTableGroupRow("main", Seq: 0, Rolls: 1, Entries: new[]
            {
                new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Equipment, RefId: "", Weight: 100, RarityFloor: entryFloor),
            }),
        });

    [Fact]
    public void Apply_null_arguments_throw()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1") };
        Assert.Throws<ArgumentNullException>(() => RarityShift.Apply(null!, Ladder, "t1", 0));
        Assert.Throws<ArgumentNullException>(() => RarityShift.Apply(tables, null!, "t1", 0));
        Assert.Throws<ArgumentNullException>(() => RarityShift.Apply(tables, Ladder, null!, 0));
        Assert.Throws<ArgumentNullException>(() => RarityShift.Apply(tables, Ladder, "t1", 0, null!));
    }

    [Fact]
    public void Apply_on_an_unknown_table_id_returns_the_map_unchanged()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1") };
        var result = RarityShift.Apply(tables, Ladder, "does-not-exist", 0);
        Assert.Same(tables, result);
    }

    [Fact]
    public void Apply_leaves_every_other_table_untouched_same_instance()
    {
        var t1 = SampleTable("t1");
        var t2 = SampleTable("t2");
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = t1, ["t2"] = t2 };

        var result = RarityShift.Apply(tables, Ladder, "t1", 1, "occasional");

        Assert.Same(t2, result["t2"]);
        Assert.NotSame(t1, result["t1"]);
    }

    [Fact]
    public void Apply_composes_the_entrys_own_floor_with_the_callers_own_sources_caller_wins()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1", entryFloor: "frequent") };

        var result = RarityShift.Apply(tables, Ladder, "t1", 0, "occasional", null);

        Assert.Equal("occasional", result["t1"].Groups[0].Entries[0].RarityFloor); // occasional (30) beats frequent (20)
    }

    /// <summary>The other direction of the same claim, load-bearing on its own: with the caller's own
    /// sources all LOWER than the entry's, the entry's own authored floor must still be the one that
    /// wins — proving `Apply` genuinely folds it in, not just whatever the caller happens to supply.
    /// (A caller-floor-always-highest fixture cannot tell "entry floor included" from "entry floor
    /// silently dropped", since the caller's own value would win either way.)</summary>
    [Fact]
    public void Apply_composes_the_entrys_own_floor_with_the_callers_own_sources_entry_wins()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1", entryFloor: "seldom") };

        var result = RarityShift.Apply(tables, Ladder, "t1", 0, "occasional", null);

        Assert.Equal("seldom", result["t1"].Groups[0].Entries[0].RarityFloor); // seldom (40) beats occasional (30)
    }

    [Fact]
    public void Apply_with_no_floor_sources_at_all_leaves_the_entry_unfloored()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1") };

        var result = RarityShift.Apply(tables, Ladder, "t1", 0);

        Assert.Null(result["t1"].Groups[0].Entries[0].RarityFloor);
    }

    [Fact]
    public void Apply_writes_the_same_composed_shift_onto_every_entry_in_the_table()
    {
        var table = SampleTable("t1") with
        {
            Groups = new[]
            {
                new DropTableGroupRow("g1", 0, 1, new[] { new DropTableEntryRow(0, DropEntryKind.Equipment, "", 100) }),
                new DropTableGroupRow("g2", 1, 1, new[] { new DropTableEntryRow(0, DropEntryKind.Equipment, "", 100) }),
            },
        };
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = table };

        var result = RarityShift.Apply(tables, Ladder, "t1", 1);

        var shift1 = result["t1"].Groups[0].Entries[0].RarityWeightShift;
        var shift2 = result["t1"].Groups[1].Entries[0].RarityWeightShift;
        Assert.Equal(RarityShift.ToWeightShift(Ladder, 1), shift1);
        Assert.Equal(shift1, shift2);
    }

    [Fact]
    public void Apply_sums_kind_and_rung_shifts_before_computing_the_weight_shift()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1") };

        var result = RarityShift.Apply(tables, Ladder, "t1", shiftRungs: 1 + 1); // e.g. rung=1, room-kind=1

        Assert.Equal(RarityShift.ToWeightShift(Ladder, 2), result["t1"].Groups[0].Entries[0].RarityWeightShift);
    }

    // ---- ToCeilingShift (D4.12): a ceiling deletes the top end, it never redistributes ----------------

    [Fact]
    public void ToCeilingShift_null_or_empty_ladder_or_null_ceilRung_throws()
    {
        Assert.Throws<ArgumentNullException>(() => RarityShift.ToCeilingShift(null!, "staple"));
        Assert.Throws<ArgumentException>(() => RarityShift.ToCeilingShift(Array.Empty<RarityRung>(), "staple"));
        Assert.Throws<ArgumentNullException>(() => RarityShift.ToCeilingShift(Ladder, null!));
    }

    [Fact]
    public void ToCeilingShift_an_unknown_ceilRung_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => RarityShift.ToCeilingShift(Ladder, "does-not-exist"));
    }

    [Fact]
    public void ToCeilingShift_at_seldom_zeroes_only_exceptional()
    {
        var shift = RarityShift.ToCeilingShift(Ladder, "seldom"); // ordinal 40
        Assert.Equal(1000, NewWeight(shift, 10, 1000)); // at/below the ceiling: unchanged
        Assert.Equal(300, NewWeight(shift, 20, 300));
        Assert.Equal(90, NewWeight(shift, 30, 90));
        Assert.Equal(25, NewWeight(shift, 40, 25));  // the ceiling rung itself is KEPT, never zeroed
        Assert.Equal(0, NewWeight(shift, 50, 7));    // strictly above the ceiling: zeroed outright
    }

    [Fact]
    public void ToCeilingShift_never_redistributes_the_deleted_weight_unlike_ToWeightShift()
    {
        // The load-bearing difference from ToWeightShift's own "top absorbing" conservation: a
        // ceiling's own deltas need not sum to zero -- the deleted weight is gone, not moved.
        var shift = RarityShift.ToCeilingShift(Ladder, "frequent"); // ordinal 20
        Assert.NotEqual(0, shift.Values.Sum());
        Assert.Equal(-(90 + 25 + 7), shift.Values.Sum());
    }

    [Fact]
    public void ToCeilingShift_at_the_top_rung_zeroes_nothing()
    {
        var shift = RarityShift.ToCeilingShift(Ladder, "exceptional"); // the ladder's own top ordinal
        Assert.All(shift.Values, d => Assert.Equal(0, d));
    }

    [Fact]
    public void ToCeilingShift_at_the_bottom_rung_zeroes_everything_above_it()
    {
        var shift = RarityShift.ToCeilingShift(Ladder, "staple"); // the ladder's own bottom ordinal
        Assert.Equal(1000, NewWeight(shift, 10, 1000)); // the ceiling rung itself: kept
        Assert.Equal(0, NewWeight(shift, 20, 300));
        Assert.Equal(0, NewWeight(shift, 30, 90));
        Assert.Equal(0, NewWeight(shift, 40, 25));
        Assert.Equal(0, NewWeight(shift, 50, 7));
    }

    // ---- ApplyWindow (D4.12): the quest-reward composer -- a floor and a ceiling, never a shift ------

    [Fact]
    public void ApplyWindow_null_arguments_throw()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1") };
        Assert.Throws<ArgumentNullException>(() => RarityShift.ApplyWindow(null!, Ladder, "t1", null, "seldom"));
        Assert.Throws<ArgumentNullException>(() => RarityShift.ApplyWindow(tables, null!, "t1", null, "seldom"));
        Assert.Throws<ArgumentNullException>(() => RarityShift.ApplyWindow(tables, Ladder, null!, null, "seldom"));
        Assert.Throws<ArgumentNullException>(() => RarityShift.ApplyWindow(tables, Ladder, "t1", null, null!));
    }

    [Fact]
    public void ApplyWindow_on_an_unknown_table_id_returns_the_map_unchanged()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1") };
        var result = RarityShift.ApplyWindow(tables, Ladder, "does-not-exist", "occasional", "seldom");
        Assert.Same(tables, result);
    }

    [Fact]
    public void ApplyWindow_leaves_every_other_table_untouched_same_instance()
    {
        var t1 = SampleTable("t1");
        var t2 = SampleTable("t2");
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = t1, ["t2"] = t2 };

        var result = RarityShift.ApplyWindow(tables, Ladder, "t1", "occasional", "seldom");

        Assert.Same(t2, result["t2"]);
        Assert.NotSame(t1, result["t1"]);
    }

    [Fact]
    public void ApplyWindow_sets_the_ceiling_shift_exactly_as_ToCeilingShift_would()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1") };

        var result = RarityShift.ApplyWindow(tables, Ladder, "t1", null, "seldom");

        Assert.Equal(RarityShift.ToCeilingShift(Ladder, "seldom"), result["t1"].Groups[0].Entries[0].RarityWeightShift);
    }

    [Fact]
    public void ApplyWindow_composes_the_entrys_own_floor_with_the_composed_floor_stronger_wins()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1", entryFloor: "frequent") };

        var result = RarityShift.ApplyWindow(tables, Ladder, "t1", "occasional", "exceptional");

        Assert.Equal("occasional", result["t1"].Groups[0].Entries[0].RarityFloor); // occasional (30) beats frequent (20)
    }

    /// <summary>Same "entry wins" proof `Apply`'s own pair already established (a composed-floor-always-
    /// highest fixture cannot tell "entry floor included" from "entry floor silently dropped").</summary>
    [Fact]
    public void ApplyWindow_composes_the_entrys_own_floor_with_the_composed_floor_entry_wins()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1", entryFloor: "seldom") };

        var result = RarityShift.ApplyWindow(tables, Ladder, "t1", "occasional", "exceptional");

        Assert.Equal("seldom", result["t1"].Groups[0].Entries[0].RarityFloor); // seldom (40) beats occasional (30)
    }

    [Fact]
    public void ApplyWindow_with_no_composed_floor_and_no_entry_floor_leaves_the_entry_unfloored()
    {
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = SampleTable("t1") };

        var result = RarityShift.ApplyWindow(tables, Ladder, "t1", null, "exceptional");

        Assert.Null(result["t1"].Groups[0].Entries[0].RarityFloor);
    }

    [Fact]
    public void ApplyWindow_writes_the_same_composed_ceiling_onto_every_entry_in_the_table()
    {
        var table = SampleTable("t1") with
        {
            Groups = new[]
            {
                new DropTableGroupRow("g1", 0, 1, new[] { new DropTableEntryRow(0, DropEntryKind.Equipment, "", 100) }),
                new DropTableGroupRow("g2", 1, 1, new[] { new DropTableEntryRow(0, DropEntryKind.Equipment, "", 100) }),
            },
        };
        var tables = new Dictionary<string, DropTableRow> { ["t1"] = table };

        var result = RarityShift.ApplyWindow(tables, Ladder, "t1", null, "occasional");

        var shift1 = result["t1"].Groups[0].Entries[0].RarityWeightShift;
        var shift2 = result["t1"].Groups[1].Entries[0].RarityWeightShift;
        Assert.Equal(RarityShift.ToCeilingShift(Ladder, "occasional"), shift1);
        Assert.Equal(shift1, shift2);
    }
}
