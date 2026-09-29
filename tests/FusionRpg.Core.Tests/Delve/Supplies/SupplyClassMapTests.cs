using FusionRpg.Core.Delve.Supplies;
using FusionRpg.Core.Items.Consumables;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Supplies;

/// <summary>D3.25 (spec-supplies-and-objects.md §1, §11.5, §3, §8) — `SupplyClassMap`.</summary>
public class SupplyClassMapTests
{
    // ---- AllowedAtomKinds: §1's own literal per-class table ----

    [Theory]
    [InlineData(ConsumableClass.Restore, "resource.delta")]
    [InlineData(ConsumableClass.Ward, "shield.grant")]
    [InlineData(ConsumableClass.Revive, "resource.delta")]
    [InlineData(ConsumableClass.Utility, "status.clear")]
    [InlineData(ConsumableClass.Draught, "stat.derived")]
    public void AllowedAtomKinds_matches_section1s_own_table(ConsumableClass classId, string expectedKind)
    {
        Assert.Equal(new[] { expectedKind }, SupplyClassMap.AllowedAtomKinds(classId));
    }

    [Fact]
    public void Board_has_no_allowed_executor_at_all()
    {
        Assert.Empty(SupplyClassMap.AllowedAtomKinds(ConsumableClass.Board));
    }

    // ---- Validate: the "one instantiation golden per class" the todo's own Verify line asks for ----

    [Fact]
    public void Ration_class_restore_with_resource_delta_is_a_legal_row()
    {
        SupplyClassMap.Validate(ConsumableClass.Restore, new[] { "resource.delta" }, "consumable.ration");
    }

    [Fact]
    public void Ward_class_with_shield_grant_is_a_legal_row()
    {
        SupplyClassMap.Validate(ConsumableClass.Ward, new[] { "shield.grant" }, "consumable.ward");
    }

    [Fact]
    public void Charm_class_draught_with_stat_derived_is_a_legal_row()
    {
        SupplyClassMap.Validate(ConsumableClass.Draught, new[] { "stat.derived" }, "consumable.charm");
    }

    [Fact]
    public void Key_and_bait_class_utility_with_zero_atoms_is_the_legal_override_only_shape()
    {
        SupplyClassMap.Validate(ConsumableClass.Utility, Array.Empty<string>(), "consumable.key");
        SupplyClassMap.Validate(ConsumableClass.Utility, Array.Empty<string>(), "consumable.bait");
    }

    // ---- refusals (§8) ----

    [Fact]
    public void Board_class_refuses_outright_regardless_of_atoms()
    {
        var ex = Assert.Throws<SupplyClassMapRejection>(() =>
            SupplyClassMap.Validate(ConsumableClass.Board, Array.Empty<string>(), "consumable.lawn-thing"));
        Assert.Contains("supply.board-in-delve", ex.Message);
    }

    [Fact]
    public void Antidote_class_utility_with_status_clear_refuses_the_wiring_gap_by_name()
    {
        // §3's own "wiring gap, named": status.clear is not on OnActivate today, so an antidote's own
        // legal executor is unreachable until effect-atom-map.md's row lands.
        var ex = Assert.Throws<SupplyClassMapRejection>(() =>
            SupplyClassMap.Validate(ConsumableClass.Utility, new[] { "status.clear" }, "consumable.antidote"));
        Assert.Contains("consumable.trigger-not-allowed", ex.Message);
    }

    [Fact]
    public void Status_clear_refuses_regardless_of_which_class_declares_it()
    {
        var ex = Assert.Throws<SupplyClassMapRejection>(() =>
            SupplyClassMap.Validate(ConsumableClass.Restore, new[] { "status.clear" }, "consumable.weird"));
        Assert.Contains("consumable.trigger-not-allowed", ex.Message);
    }

    [Fact]
    public void A_wrong_executor_for_the_class_refuses_naming_both()
    {
        var ex = Assert.Throws<SupplyClassMapRejection>(() =>
            SupplyClassMap.Validate(ConsumableClass.Restore, new[] { "shield.grant" }, "consumable.mismatched"));
        Assert.Contains("supply.wrong-executor", ex.Message);
        Assert.Contains("consumable.mismatched", ex.Message);
        Assert.Contains("restore", ex.Message);
    }

    [Fact]
    public void Null_and_invalid_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => SupplyClassMap.Validate(ConsumableClass.Restore, null!, "x"));
        Assert.Throws<ArgumentException>(() => SupplyClassMap.Validate(ConsumableClass.Restore, Array.Empty<string>(), " "));
    }
}
