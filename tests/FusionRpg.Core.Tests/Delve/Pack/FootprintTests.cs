using FusionRpg.Core.Delve.Pack;
using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Pack;

/// <summary>
/// D3.18 (spec-loot-pack.md §2) — footprint(role, massClass) derivation. `Tuning` below is a
/// TEST-LOCAL `PackTuning`, built from §2's own literal table, the same way `SoulEarnPolicyTests.
/// TuningAt` builds a test-local `PowerTuning` rather than reading the real shipped file — D3.19's own
/// `PackFootprintTable.Build` is the loader that will read `dungeon.v1.json` into this same shape;
/// nothing shipped exists to read from yet.
/// </summary>
public class FootprintTests
{
    // §2's own table, verbatim base cells per role.
    static readonly PackTuning Tuning = new(
        RoleCells: new Dictionary<ItemRole, int>
        {
            [ItemRole.ArmamentPrimary] = 3,
            [ItemRole.ArmamentSecondary] = 2,
            [ItemRole.CoreGuard] = 4,
            [ItemRole.WardArray] = 3,
            [ItemRole.Mantle] = 3,
            [ItemRole.HeadGuard] = 2,
            [ItemRole.Girdle] = 2,
            [ItemRole.Footing] = 2,
            [ItemRole.Manipulator] = 2,
            [ItemRole.Sense] = 1,
            [ItemRole.Infusion] = 1,
            [ItemRole.JewelMajor] = 1,
            [ItemRole.JewelMinorA] = 1,
            [ItemRole.JewelMinorB] = 1,
            [ItemRole.Retinue] = 1,
            // Standard is declared, never generated (D14) -- deliberately absent, see the refusal test below.
        },
        MassSteps: new Dictionary<string, int>
        {
            ["light"] = -1,
            ["medium-light"] = 0,
            ["medium"] = 0,
            ["medium-heavy"] = +1,
            ["heavy"] = +1,
        });

    // ---- CellsFor: the ladder-index clamp ----

    [Theory]
    [InlineData(3, -1, 2)]  // armament-primary, light: 3 -> index2(3) - 1 = index1(2)
    [InlineData(3, 0, 3)]   // medium: unchanged
    [InlineData(3, 1, 4)]   // medium-heavy: 3 -> 4
    [InlineData(1, -1, 1)]  // clamps at the floor -- can't go below the smallest shape
    [InlineData(8, 1, 8)]   // clamps at the ceiling -- can't go past the largest shape
    public void CellsFor_steps_along_the_ladder_and_clamps_at_the_edges(int roleCells, int massStep, int expected)
    {
        Assert.Equal(expected, Footprint.CellsFor(roleCells, massStep));
    }

    [Fact]
    public void CellsFor_rejects_a_role_cell_count_that_is_not_a_ladder_member()
    {
        Assert.Throws<ArgumentException>(() => Footprint.CellsFor(5, 0)); // 5 is not on [1,2,3,4,6,8]
    }

    // ---- Orient: tall (Weapon) vs broad (everything else) ----

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 1, 3)]
    [InlineData(4, 1, 4)]
    [InlineData(6, 2, 3)]
    [InlineData(8, 2, 4)]
    public void Orient_shapes_tall_for_the_weapon_ladder(int cells, int expectedW, int expectedH)
    {
        var (w, h) = Footprint.Orient(cells, ClassLadder.Weapon);
        Assert.Equal((expectedW, expectedH), (w, h));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(6, 3, 2)]
    [InlineData(8, 4, 2)]
    public void Orient_shapes_broad_for_every_other_ladder(int cells, int expectedW, int expectedH)
    {
        foreach (var ladder in new[] { ClassLadder.Armour, ClassLadder.Offhand, ClassLadder.Jewel, ClassLadder.Standard })
        {
            var (w, h) = Footprint.Orient(cells, ladder);
            Assert.Equal((expectedW, expectedH), (w, h));
        }
    }

    [Fact]
    public void Orient_rejects_a_cell_count_off_the_ladder()
    {
        Assert.Throws<ArgumentException>(() => Footprint.Orient(5, ClassLadder.Armour));
    }

    // ---- Derive: the full pipeline, one golden row per (role, massClass) in §2's own table ----

    public static IEnumerable<object[]> Section2Golden()
    {
        // role, massClass, expected (W,H) -- hand-derived from §2's own table: base cells stepped by
        // massClass, clamped, then oriented tall (weapon roles) or broad (everything else).
        (ItemRole Role, string MassClass, int W, int H)[] rows =
        {
            (ItemRole.ArmamentPrimary, "light", 1, 2), (ItemRole.ArmamentPrimary, "medium", 1, 3), (ItemRole.ArmamentPrimary, "medium-heavy", 1, 4),
            (ItemRole.ArmamentSecondary, "light", 1, 1), (ItemRole.ArmamentSecondary, "medium", 1, 2), (ItemRole.ArmamentSecondary, "heavy", 1, 3),
            (ItemRole.CoreGuard, "light", 3, 1), (ItemRole.CoreGuard, "medium-light", 2, 2), (ItemRole.CoreGuard, "medium-heavy", 3, 2),
            (ItemRole.WardArray, "light", 2, 1), (ItemRole.WardArray, "medium", 3, 1), (ItemRole.WardArray, "heavy", 2, 2),
            (ItemRole.Mantle, "light", 2, 1), (ItemRole.Mantle, "medium", 3, 1), (ItemRole.Mantle, "heavy", 2, 2),
            (ItemRole.HeadGuard, "light", 1, 1), (ItemRole.HeadGuard, "medium", 2, 1), (ItemRole.HeadGuard, "heavy", 3, 1),
            (ItemRole.Girdle, "light", 1, 1), (ItemRole.Girdle, "medium", 2, 1), (ItemRole.Girdle, "heavy", 3, 1),
            (ItemRole.Footing, "light", 1, 1), (ItemRole.Footing, "medium", 2, 1), (ItemRole.Footing, "heavy", 3, 1),
            (ItemRole.Manipulator, "light", 1, 1), (ItemRole.Manipulator, "medium", 2, 1), (ItemRole.Manipulator, "heavy", 3, 1),
            (ItemRole.Sense, "light", 1, 1), (ItemRole.Sense, "medium", 1, 1), (ItemRole.Sense, "heavy", 2, 1),
            (ItemRole.Infusion, "light", 1, 1), (ItemRole.Infusion, "medium", 1, 1), (ItemRole.Infusion, "heavy", 2, 1),
            (ItemRole.JewelMajor, "light", 1, 1), (ItemRole.JewelMajor, "medium", 1, 1), (ItemRole.JewelMajor, "heavy", 2, 1),
            (ItemRole.JewelMinorA, "light", 1, 1), (ItemRole.JewelMinorA, "medium", 1, 1), (ItemRole.JewelMinorA, "heavy", 2, 1),
            (ItemRole.JewelMinorB, "light", 1, 1), (ItemRole.JewelMinorB, "medium", 1, 1), (ItemRole.JewelMinorB, "heavy", 2, 1),
            (ItemRole.Retinue, "light", 1, 1), (ItemRole.Retinue, "medium", 1, 1), (ItemRole.Retinue, "heavy", 2, 1),
        };
        foreach (var row in rows)
            yield return new object[] { row.Role, row.MassClass, row.W, row.H };
    }

    [Theory]
    [MemberData(nameof(Section2Golden))]
    public void Derive_matches_section2s_own_table_row_by_row(ItemRole role, string massClass, int expectedW, int expectedH)
    {
        var (w, h) = Footprint.Derive(role, massClass, Tuning);
        Assert.Equal((expectedW, expectedH), (w, h));
    }

    [Fact]
    public void Derive_null_tuning_throws()
    {
        Assert.Throws<ArgumentNullException>(() => Footprint.Derive(ItemRole.Sense, "medium", null!));
    }

    [Fact]
    public void Standard_is_declared_never_generated_and_has_no_footprint_entry()
    {
        // §9: "`standard` in a grant refuses `pack.unknown-role`" -- this Tuning deliberately has no
        // entry for it, matching D14's own "declared; the generator emits nothing into it."
        Assert.Throws<KeyNotFoundException>(() => Footprint.Derive(ItemRole.Standard, "medium", Tuning));
    }
}
