using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Aura;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Aura;

/// <summary>aura-skill T14 (audit D4): the CALLER `CostLedger` never had. Mirrors
/// `CostLedgerTests`' own established fixture shape exactly (`Snapshot`/`MakeLedger`/`Costs`,
/// `Rung=1` inert multipliers) — this is the SAME ledger, aura-scoped, not a second payment
/// mechanism.</summary>
public class AuraUpkeepDriverTests
{
    const string ActorKey = "commander:dave";
    const int Rung = 1; // RungPolicy's shipped row 1: CostMulti=1000, CdMulti=1000 (both inert)

    static ActorDerivedSnapshot Snapshot(params (string resourceId, double max, double regen)[] resources)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        var mods = new List<DerivedModifier>
        {
            new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, 0, SourceId: "test"),
            new(DerivedStatChannels.ProgressionRealm, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
        };
        foreach (var (id, max, regen) in resources)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, max, SourceId: "test"));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, regen, SourceId: "test"));
        }
        return composer.Compose(mods);
    }

    static CostLedger MakeLedger(
        IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> costs, ActorResourcePools pools, ActorDerivedSnapshot derived, long nowTick = 0) =>
        new(costs, _ => pools, _ => derived, (_, _) => Rung, () => nowTick);

    static IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> Costs(string auraId, params ActionCostRow[] rows) =>
        new Dictionary<string, IReadOnlyList<ActionCostRow>> { [auraId] = rows };

    [Fact]
    public void An_affordable_upkeep_charges_every_pool_in_the_auras_list_and_leaves_the_aura_active()
    {
        var derived = Snapshot(("qi", 100, 0), ("stamina", 50, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("aura:ember",
            new ActionCostRow("aura:ember", "qi", ValueSpec.Of(10), ActionCostTiming.PerTick),
            new ActionCostRow("aura:ember", "stamina", ValueSpec.Of(5), ActionCostTiming.PerTick));
        var driver = new AuraUpkeepDriver(MakeLedger(costs, pools, derived));
        var runtime = new AuraRuntime(1, _ => true);
        runtime.Enable("aura:ember");

        var result = driver.ChargeTick(ActorKey, "aura:ember", runtime);

        Assert.True(result.Charged);
        Assert.False(result.Disabled);
        Assert.Equal(90, pools.Resolve("qi", 0, derived));
        Assert.Equal(45, pools.Resolve("stamina", 0, derived));
        Assert.True(runtime.IsActive("aura:ember"));
    }

    [Fact]
    public void A_shortfall_names_which_pool_blocked_it()
    {
        var derived = Snapshot(("qi", 100, 0), ("stamina", 3, 0)); // stamina too low
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("aura:ember",
            new ActionCostRow("aura:ember", "qi", ValueSpec.Of(10), ActionCostTiming.PerTick),
            new ActionCostRow("aura:ember", "stamina", ValueSpec.Of(5), ActionCostTiming.PerTick));
        var driver = new AuraUpkeepDriver(MakeLedger(costs, pools, derived));
        var runtime = new AuraRuntime(1, _ => true);
        runtime.Enable("aura:ember");

        var result = driver.ChargeTick(ActorKey, "aura:ember", runtime);

        Assert.False(result.Charged);
        Assert.Equal(UsabilityReason.CannotAfford, result.Reason);
        Assert.Equal("stamina", result.ResourceId);
    }

    [Fact]
    public void Running_dry_disables_the_aura_through_the_interrupt_path_typed_and_visible()
    {
        var derived = Snapshot(("qi", 3, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("aura:ember", new ActionCostRow("aura:ember", "qi", ValueSpec.Of(10), ActionCostTiming.PerTick));
        var driver = new AuraUpkeepDriver(MakeLedger(costs, pools, derived));
        var runtime = new AuraRuntime(1, _ => true);
        runtime.Enable("aura:ember");
        Assert.True(runtime.IsActive("aura:ember"));

        var result = driver.ChargeTick(ActorKey, "aura:ember", runtime);

        Assert.True(result.Disabled);
        Assert.False(runtime.IsActive("aura:ember")); // the aura is genuinely off, not merely reported off
    }

    [Fact]
    public void Payment_is_validate_all_then_consume_all_a_shortfall_on_the_second_row_spends_nothing_on_the_first()
    {
        var derived = Snapshot(("qi", 100, 0), ("stamina", 2, 0)); // stamina too low, checked second
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("aura:ember",
            new ActionCostRow("aura:ember", "qi", ValueSpec.Of(10), ActionCostTiming.PerTick),
            new ActionCostRow("aura:ember", "stamina", ValueSpec.Of(5), ActionCostTiming.PerTick));
        var driver = new AuraUpkeepDriver(MakeLedger(costs, pools, derived));
        var runtime = new AuraRuntime(1, _ => true);
        runtime.Enable("aura:ember");

        driver.ChargeTick(ActorKey, "aura:ember", runtime);

        Assert.Equal(100, pools.Resolve("qi", 0, derived)); // untouched -- pass 1 found the shortfall before pass 2 ever ran
    }

    [Fact]
    public void An_hp_cost_floors_at_1_refusing_rather_than_letting_upkeep_kill_the_actor()
    {
        var derived = Snapshot(("hp", 1, 0)); // exactly 1 hp
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("aura:sacrifice", new ActionCostRow("aura:sacrifice", "hp", ValueSpec.Of(1), ActionCostTiming.PerTick));
        var driver = new AuraUpkeepDriver(MakeLedger(costs, pools, derived));
        var runtime = new AuraRuntime(1, _ => true);
        runtime.Enable("aura:sacrifice");

        // Paying exactly 1 hp from a pool of 1 would bring hp to 0 -- refused by the floor, not paid.
        var result = driver.ChargeTick(ActorKey, "aura:sacrifice", runtime);

        Assert.True(result.Disabled);
        Assert.Equal("hp", result.ResourceId);
        Assert.Equal(1, pools.Resolve("hp", 0, derived)); // untouched -- the floor refused before spending
    }

    [Fact]
    public void An_aura_that_explicitly_opts_into_lethality_can_pay_hp_down_to_zero()
    {
        var derived = Snapshot(("hp", 1, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = Costs("aura:blood-pact",
            new ActionCostRow("aura:blood-pact", "hp", ValueSpec.Of(1), ActionCostTiming.PerTick, AllowLethal: true));
        var driver = new AuraUpkeepDriver(MakeLedger(costs, pools, derived));
        var runtime = new AuraRuntime(1, _ => true);
        runtime.Enable("aura:blood-pact");

        var result = driver.ChargeTick(ActorKey, "aura:blood-pact", runtime);

        Assert.True(result.Charged);
        Assert.Equal(0, pools.Resolve("hp", 0, derived));
    }

    [Fact]
    public void An_aura_with_zero_cost_rows_is_charged_successfully_forever_a_named_content_authoring_gap()
    {
        // Not a defect in this driver -- CostLedger.TryPay's own early return for "no rows for this
        // actionId" means an aura authored with NO upkeep cost is treated as always-affordable. The
        // termination invariant ("nothing free", decisions.md, blocking) means this can never actually
        // ship -- T16 (content authoring) must guarantee every aura has at least one PerTick row.
        // Recorded here as a named boundary this driver cannot itself enforce (it has no visibility
        // into "should this aura have a cost" -- only into the cost rows it was given), not hidden.
        var derived = Snapshot();
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = new Dictionary<string, IReadOnlyList<ActionCostRow>>(); // no rows authored at all
        var driver = new AuraUpkeepDriver(MakeLedger(costs, pools, derived));
        var runtime = new AuraRuntime(1, _ => true);
        runtime.Enable("aura:free-forever");

        var result = driver.ChargeTick(ActorKey, "aura:free-forever", runtime);

        Assert.True(result.Charged);
        Assert.True(runtime.IsActive("aura:free-forever"));
    }
}
