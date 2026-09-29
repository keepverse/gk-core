using FusionRpg.Core.Delve.Supplies;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Consumables;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Supplies;

/// <summary>D3.26 (spec-supplies-and-objects.md §3) — `SupplyUse.Use`.</summary>
public class SupplyUseTests
{
    static InstanceRow Supply(string containerId = "item.ration") => new()
    {
        InstanceId = "inst-1", ContainerId = containerId, RollSeed = 1, CatalogRevision = 0,
        CreatedUtc = "2026-09-06T00:00:00Z", Origin = InstanceOrigin.Drop, ThetaContent = 20, ContentScaleMilli = 1000,
        Atoms = new[] { new InstanceAtomRow(1, "atom.ration-restore", "{}", null) },
    };

    static Func<string, long, bool> StockOf(params (string ContainerId, long Qty)[] pack) =>
        (id, qty) => pack.Any(p => p.ContainerId == id && p.Qty >= qty);

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SupplyUse.Use("m", 0, false, null!, ConsumableClass.Restore, UseContext.Rest, StockOf()));
        Assert.Throws<ArgumentNullException>(() =>
            SupplyUse.Use("m", 0, false, Supply(), ConsumableClass.Restore, UseContext.Rest, null!));
    }

    [Fact]
    public void Menu_context_refuses_in_a_delve_before_the_stock_check()
    {
        // Even with the item genuinely held, Menu is refused first (§8: "before GateManifest").
        var outcome = SupplyUse.Use("m", 0, false, Supply(), ConsumableClass.Restore, UseContext.Menu,
            StockOf(("item.ration", 1)));
        Assert.False(outcome.Ok);
        Assert.Equal("supply.menu-in-delve", outcome.Reason);
    }

    [Fact]
    public void A_supply_not_in_the_pack_refuses_supply_not_held()
    {
        var outcome = SupplyUse.Use("m", 0, false, Supply(), ConsumableClass.Restore, UseContext.Rest, StockOf());
        Assert.False(outcome.Ok);
        Assert.Equal("supply.not-held", outcome.Reason);
    }

    [Fact]
    public void Rest_use_of_a_held_restore_supply_fires_and_names_the_decrement()
    {
        var outcome = SupplyUse.Use("creature-a", 2, false, Supply(), ConsumableClass.Restore, UseContext.Rest,
            StockOf(("item.ration", 1)));
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("item.ration", outcome.DecrementContainerId);
        Assert.NotNull(outcome.Atoms);
        Assert.Single(outcome.Atoms!);
        Assert.NotNull(outcome.Decision);
    }

    [Fact]
    public void Curio_use_of_a_held_supply_also_fires_the_same_way_as_rest()
    {
        var outcome = SupplyUse.Use("creature-a", 0, false, Supply(), ConsumableClass.Restore, UseContext.Curio,
            StockOf(("item.ration", 1)));
        Assert.True(outcome.Ok, outcome.Reason);
    }

    // ---- revive: only on a Downed member, never at curio ----

    [Fact]
    public void Revive_on_a_downed_member_at_rest_fires()
    {
        var outcome = SupplyUse.Use("creature-a", 0, memberDowned: true, Supply("item.revive-tonic"),
            ConsumableClass.Revive, UseContext.Rest, StockOf(("item.revive-tonic", 1)));
        Assert.True(outcome.Ok, outcome.Reason);
    }

    [Fact]
    public void Revive_on_a_member_not_downed_refuses_target_not_downed()
    {
        var outcome = SupplyUse.Use("creature-a", 0, memberDowned: false, Supply("item.revive-tonic"),
            ConsumableClass.Revive, UseContext.Rest, StockOf(("item.revive-tonic", 1)));
        Assert.False(outcome.Ok);
        Assert.Equal("supply.target-not-downed", outcome.Reason);
    }

    [Fact]
    public void Revive_at_curio_refuses_even_on_a_downed_member()
    {
        var outcome = SupplyUse.Use("creature-a", 0, memberDowned: true, Supply("item.revive-tonic"),
            ConsumableClass.Revive, UseContext.Curio, StockOf(("item.revive-tonic", 1)));
        Assert.False(outcome.Ok);
        Assert.Equal("supply.revive-not-at-curio", outcome.Reason);
    }

    [Fact]
    public void Revive_at_battle_on_a_downed_member_fires()
    {
        var outcome = SupplyUse.Use("creature-a", 0, memberDowned: true, Supply("item.revive-tonic"),
            ConsumableClass.Revive, UseContext.Battle, StockOf(("item.revive-tonic", 1)));
        Assert.True(outcome.Ok, outcome.Reason);
    }

    // ---- D3.24's flag, read here: battle waits on A3 unless the supply is a Revive ----

    [Fact]
    public void Battle_use_of_a_non_revive_supply_refuses_while_the_item_cost_row_is_unbuilt()
    {
        Assert.False(FusionRpg.Core.Actions.CrossProgramLandedFlags.ItemCostRowLanded); // the precondition this test relies on
        var outcome = SupplyUse.Use("creature-a", 0, false, Supply(), ConsumableClass.Restore, UseContext.Battle,
            StockOf(("item.ration", 1)));
        Assert.False(outcome.Ok);
        Assert.Equal("supply.battle-cost-row-unbuilt", outcome.Reason);
    }

    [Fact]
    public void Battle_revive_is_exempt_from_the_item_cost_gate_it_never_rides_the_action_layer()
    {
        var outcome = SupplyUse.Use("creature-a", 0, memberDowned: true, Supply("item.revive-tonic"),
            ConsumableClass.Revive, UseContext.Battle, StockOf(("item.revive-tonic", 1)));
        Assert.True(outcome.Ok, outcome.Reason);
    }

    // ---- the todo's own Verify line: a use decrements a PACK cell, stock is never read/touched ----

    [Fact]
    public void The_holdsStock_delegate_is_the_only_stock_source_consulted_never_rpg_item_stock()
    {
        var calls = new List<(string, long)>();
        Func<string, long, bool> spy = (id, qty) => { calls.Add((id, qty)); return true; };

        SupplyUse.Use("creature-a", 0, false, Supply(), ConsumableClass.Restore, UseContext.Rest, spy);

        var call = Assert.Single(calls);
        Assert.Equal(("item.ration", 1L), call);
    }
}
