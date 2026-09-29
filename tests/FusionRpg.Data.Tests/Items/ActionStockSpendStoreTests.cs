using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// <c>RpgStore.TrySpendStock</c> and <see cref="RpgStoreStockLedger"/> — the commit half of the
/// <c>holdsStock</c> precondition, against a real SQLite store rather than a mock.
///
/// <para>The properties that matter are the ones a fake cannot prove: the decrement is conditional
/// (so it cannot go negative and cannot silently succeed on an empty stack, which
/// <c>AdjustStock</c>'s <c>MAX(0, …)</c> would), and a multi-demand spend is one transaction (so a
/// shortfall on the second line rolls the first back).</para>
/// </summary>
public class ActionStockSpendStoreTests : IDisposable
{
    const string Player = "player-1";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActionStockSpendStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static StockDemand[] One(string id, long qty = 1) => new[] { new StockDemand(id, qty) };

    // ---- (a) a firing action really decrements rpg_item_stock ------------------------------------

    [Fact]
    public void Spending_a_held_stack_decrements_it_by_the_demanded_quantity()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 3);

        var result = _store.TrySpendStock(Player, One("consumable.k1-001"));

        Assert.True(result.IsSpent);
        Assert.Equal(2, _store.StockQty(Player, "consumable.k1-001"));
    }

    [Fact]
    public void A_demand_above_one_takes_exactly_that_many()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 10);

        Assert.True(_store.TrySpendStock(Player, One("consumable.k1-001", 4)).IsSpent);
        Assert.Equal(6, _store.StockQty(Player, "consumable.k1-001"));
    }

    // ---- (b) an exhausted stack refuses, and nothing moves ---------------------------------------

    [Fact]
    public void An_exhausted_stack_refuses_by_name_and_leaves_the_row_alone()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 1);
        Assert.True(_store.TrySpendStock(Player, One("consumable.k1-001")).IsSpent);

        var second = _store.TrySpendStock(Player, One("consumable.k1-001"));

        Assert.Equal(StockSpendOutcome.MissingStock, second.Outcome);
        Assert.Equal("consumable.k1-001", second.ShortfallStockId);
        Assert.Equal(0, _store.StockQty(Player, "consumable.k1-001")); // never negative
    }

    [Fact]
    public void A_stack_the_player_has_never_held_refuses_rather_than_creating_a_row()
    {
        var result = _store.TrySpendStock(Player, One("consumable.k9-999"));

        Assert.Equal(StockSpendOutcome.MissingStock, result.Outcome);
        Assert.Equal(0, _store.StockQty(Player, "consumable.k9-999"));
    }

    [Fact]
    public void A_partial_stack_refuses_whole_rather_than_taking_what_is_there()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 2);

        Assert.False(_store.TrySpendStock(Player, One("consumable.k1-001", 5)).IsSpent);
        Assert.Equal(2, _store.StockQty(Player, "consumable.k1-001"));
    }

    // ---- one transaction, so a shortfall rolls the whole spend back ------------------------------

    [Fact]
    public void A_shortfall_on_the_second_demand_rolls_the_first_decrement_back()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 5);
        _store.AdjustStock(Player, "consumable.k2-002", 1);

        var result = _store.TrySpendStock(Player, new[]
        {
            new StockDemand("consumable.k1-001", 1),
            new StockDemand("consumable.k2-002", 4),
        });

        Assert.Equal(StockSpendOutcome.MissingStock, result.Outcome);
        Assert.Equal("consumable.k2-002", result.ShortfallStockId);
        Assert.Equal(5, _store.StockQty(Player, "consumable.k1-001")); // the first line is intact
        Assert.Equal(1, _store.StockQty(Player, "consumable.k2-002"));
    }

    [Fact]
    public void Two_affordable_demands_both_commit()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 5);
        _store.AdjustStock(Player, "consumable.k2-002", 5);

        Assert.True(_store.TrySpendStock(Player, new[]
        {
            new StockDemand("consumable.k1-001", 2),
            new StockDemand("consumable.k2-002", 3),
        }).IsSpent);

        Assert.Equal(3, _store.StockQty(Player, "consumable.k1-001"));
        Assert.Equal(2, _store.StockQty(Player, "consumable.k2-002"));
    }

    [Fact]
    public void An_empty_demand_list_is_a_no_op_success()
    {
        Assert.True(_store.TrySpendStock(Player, Array.Empty<StockDemand>()).IsSpent);
    }

    [Fact]
    public void A_non_positive_demand_throws_rather_than_granting_stock()
    {
        // PredicateCompiler already refuses minQty < 1 at load, so reaching here is a caller bug --
        // and a negative "spend" through the same decrement would be a free grant.
        _store.AdjustStock(Player, "consumable.k1-001", 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _store.TrySpendStock(Player, One("consumable.k1-001", -3)));
        Assert.Equal(1, _store.StockQty(Player, "consumable.k1-001"));
    }

    // ---- AdjustStock is NOT a spend path, proven rather than asserted in a comment ----------------

    [Fact]
    public void AdjustStock_clamps_and_therefore_could_never_have_been_the_spend_path()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 1);
        _store.AdjustStock(Player, "consumable.k1-001", -5);

        // No error, no negative, no way for the caller to learn it overspent by four. That is exactly
        // why TrySpendStock exists instead of a call to this.
        Assert.Equal(0, _store.StockQty(Player, "consumable.k1-001"));
    }

    // ---- the IStockLedger seam Core holds --------------------------------------------------------

    [Fact]
    public void The_ledger_adapter_spends_through_the_same_store_path()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 2);
        IStockLedger ledger = new RpgStoreStockLedger(_store);

        Assert.True(ledger.TrySpend(Player, "skill.potion", One("consumable.k1-001")).IsSpent);
        Assert.Equal(1, _store.StockQty(Player, "consumable.k1-001"));

        Assert.True(ledger.TrySpend(Player, "skill.potion", One("consumable.k1-001")).IsSpent);
        Assert.Equal(0, _store.StockQty(Player, "consumable.k1-001"));

        var refused = ledger.TrySpend(Player, "skill.potion", One("consumable.k1-001"));
        Assert.Equal(UsabilityReason.MissingStock, refused.AsRefusal().Reason);
    }

    [Fact]
    public void The_ledger_adapter_maps_an_actor_key_to_the_paying_player()
    {
        _store.AdjustStock(Player, "consumable.k1-001", 1);
        IStockLedger ledger = new RpgStoreStockLedger(_store, actorKey => Player);

        Assert.True(ledger.TrySpend("specimen-42", "skill.potion", One("consumable.k1-001")).IsSpent);
        Assert.Equal(0, _store.StockQty(Player, "consumable.k1-001"));
    }

    // ---- the draught manifest still uses the same one decrement ----------------------------------

    [Fact]
    public void The_draught_manifest_and_the_action_spend_share_one_decrement()
    {
        // TrySpendDraughts was refactored onto TryDecrementStockUnlocked; its own insufficient-stack
        // behaviour must be byte-for-byte what it was, which RunDraughtStoreTests also covers. Kept
        // here too so a future change to the shared helper fails on BOTH callers, not one.
        _store.AdjustStock(Player, "consumable.k1-001", 1);

        var ok = _store.TrySpendDraughts(Player, "expedition", 1,
            new[] { new Core.Items.Consumables.DraughtManifestEntry("consumable.k1-001", 1) });
        Assert.True(ok.Ok);
        Assert.Equal(0, _store.StockQty(Player, "consumable.k1-001"));

        var short2 = _store.TrySpendDraughts(Player, "expedition", 2,
            new[] { new Core.Items.Consumables.DraughtManifestEntry("consumable.k1-001", 1) });
        Assert.False(short2.Ok);
        Assert.Equal("stock.insufficient", short2.Reason);
    }
}
