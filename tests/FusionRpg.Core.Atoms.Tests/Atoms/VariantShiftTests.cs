using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T3.3 (`resolution-order`, Q12): a variant shifts a resolution PARAMETER — it authors nothing.
/// Covers the shift table's own real tuning file (<c>gk-core/data/tuning/variant-shifts.v1.json</c>) plus the
/// pure shift math, including the t5 saturation clamp AGENTS.md's no-hard-caps rule exempts as a
/// structural limit.
/// </summary>
public class VariantShiftTests
{
    static string RealTuningJson()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", "variant-shifts.v1.json");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("data/tuning/variant-shifts.v1.json not found walking up from " + AppContext.BaseDirectory);
    }

    static IReadOnlyDictionary<string, VariantShift> RealTable() => VariantShiftTable.Parse(RealTuningJson());

    [Fact]
    public void The_real_tuning_file_parses_and_names_all_six_variants()
    {
        var table = RealTable();

        Assert.Equal(
            new[] { "ancient", "blessed", "corrupted", "cursed", "mutated", "shiny" },
            table.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Variant_shifts_the_tier_window_and_authors_nothing()
    {
        var ancient = RealTable()["ancient"];

        var (min, max) = ancient.ShiftTierWindow(1, 3);

        Assert.Equal(2, min);
        Assert.Equal(4, max);
        // "Authors nothing": the shift is pure arithmetic over the SAME container's own MinTier/
        // MaxTier — no new ContainerRow, no new AtomRow, nothing written anywhere.
    }

    [Fact]
    public void A_null_window_is_left_unshifted()
    {
        var ancient = RealTable()["ancient"];

        Assert.Equal((null, null), ancient.ShiftTierWindow(null, null));
    }

    [Fact]
    public void Ancient_at_rung_10_saturates_at_t5_not_a_progression_cap()
    {
        // A rung-10 container already sits at the top of the ladder — window [4, 5]. +1 would push
        // the max to 6, which does not exist: the clamp fires because there is no t6 row to select,
        // not because a balance ceiling forbids it (VariantShift.MaxTier's own doc comment names this
        // exemption explicitly, per AGENTS.md's no-hard-caps rule).
        var ancient = RealTable()["ancient"];

        var (min, max) = ancient.ShiftTierWindow(4, 5);

        Assert.Equal(5, min);
        Assert.Equal(5, max);
    }

    [Fact]
    public void A_downward_shift_clamps_at_tier_one_not_zero()
    {
        var mutated = RealTable()["mutated"];

        var (min, max) = mutated.ShiftTierWindow(1, 1);

        Assert.Equal(1, min);
        Assert.Equal(1, max);
    }

    [Fact]
    public void A_uniform_shift_never_inverts_a_valid_window()
    {
        var ancient = RealTable()["ancient"];

        // Every tier pair in the real ladder, shifted, stays non-inverted — the uniform-shift-then-
        // independently-clamp design is what guarantees this (VariantShift.ShiftTierWindow's own doc
        // comment states the invariant; this proves it holds for the whole real range).
        for (var lo = 1; lo <= VariantShift.MaxTier; lo++)
            for (var hi = lo; hi <= VariantShift.MaxTier; hi++)
            {
                var (shiftedLo, shiftedHi) = ancient.ShiftTierWindow(lo, hi);
                Assert.True(shiftedLo <= shiftedHi, $"[{lo},{hi}] -> [{shiftedLo},{shiftedHi}] inverted");
            }
    }

    [Fact]
    public void Blessed_adds_one_prefix_roll_and_leaves_suffix_alone()
    {
        var blessed = RealTable()["blessed"];

        Assert.Equal(3, blessed.ShiftPrefixRolls(2));
        Assert.Equal(1, blessed.ShiftSuffixRolls(1));
    }

    [Fact]
    public void Cursed_adds_a_suffix_roll_and_removes_a_prefix_roll()
    {
        var cursed = RealTable()["cursed"];

        Assert.Equal(0, cursed.ShiftPrefixRolls(1));
        Assert.Equal(2, cursed.ShiftSuffixRolls(1));
    }

    [Fact]
    public void A_roll_count_never_goes_negative()
    {
        var cursed = RealTable()["cursed"];

        // Starting at 0 prefix rolls, -1 would go negative — floored at 0, a domain-validity floor,
        // not a balance cap (VariantShift.ShiftPrefixRolls's own doc comment).
        Assert.Equal(0, cursed.ShiftPrefixRolls(0));
    }

    [Fact]
    public void Shiny_shifts_nothing()
    {
        var shiny = RealTable()["shiny"];

        Assert.Equal((2, 4), shiny.ShiftTierWindow(2, 4));
        Assert.Equal(3, shiny.ShiftPrefixRolls(3));
        Assert.Equal(1, shiny.ShiftSuffixRolls(1));
        Assert.False(shiny.RerollsOneElementSlot);
    }

    [Fact]
    public void Corrupted_is_the_only_variant_that_rerolls_a_slot()
    {
        var table = RealTable();

        Assert.True(table["corrupted"].RerollsOneElementSlot);
        Assert.All(table.Values.Where(v => v.VariantId != "corrupted"), v => Assert.False(v.RerollsOneElementSlot));
    }

    // ---- parser rejections ------------------------------------------------------------------------

    [Fact]
    public void Empty_json_is_rejected()
    {
        Assert.Throws<VariantShiftTuningRejection>(() => VariantShiftTable.Parse(""));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.Throws<VariantShiftTuningRejection>(() => VariantShiftTable.Parse("{ not json"));
    }

    [Fact]
    public void A_missing_variants_object_is_rejected()
    {
        Assert.Throws<VariantShiftTuningRejection>(() => VariantShiftTable.Parse("""{"schemaVersion":1}"""));
    }

    [Fact]
    public void A_variant_missing_a_required_field_is_rejected()
    {
        Assert.Throws<VariantShiftTuningRejection>(() =>
            VariantShiftTable.Parse("""{"variants":{"ancient":{"tierWindowShift":1}}}"""));
    }
}
