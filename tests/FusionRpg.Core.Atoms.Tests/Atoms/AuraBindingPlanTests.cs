using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// backlog-clear BP2 (`spec-aura-binding-producer.md` §5, §6): the pure reconcile, table-tested
/// without a database. Each case is one row of the spec's own testing table.
/// </summary>
public class AuraBindingPlanTests
{
    [Fact]
    public void Enable_with_no_existing_binding_adds_it()
    {
        var plan = AuraBindingPlan.Compute(
            activeAuraIds: new[] { "Might" },
            existingBindings: Array.Empty<AuraBoundRow>());

        Assert.Equal(new[] { "Might" }, plan.ToBind);
        Assert.Empty(plan.ToWithdraw);
    }

    [Fact]
    public void Enabling_the_same_aura_twice_is_a_no_op()
    {
        var plan = AuraBindingPlan.Compute(
            activeAuraIds: new[] { "Might" },
            existingBindings: new[] { new AuraBoundRow("Might", "b1") });

        Assert.Empty(plan.ToBind);
        Assert.Empty(plan.ToWithdraw);
    }

    [Fact]
    public void Disabling_an_active_aura_withdraws_only_its_own_binding()
    {
        var plan = AuraBindingPlan.Compute(
            activeAuraIds: new[] { "Fortitude" },
            existingBindings: new[]
            {
                new AuraBoundRow("Might", "b-might"),
                new AuraBoundRow("Fortitude", "b-fortitude"),
            });

        Assert.Empty(plan.ToBind);
        Assert.Equal(new[] { "b-might" }, plan.ToWithdraw);
    }

    [Fact]
    public void Eviction_withdraws_the_evicted_aura_and_binds_the_new_one_in_one_reconcile()
    {
        // The runtime already evicted Might by the time this is called -- activeAuraIds is its
        // post-eviction set, never re-derived here (spec S3.2).
        var plan = AuraBindingPlan.Compute(
            activeAuraIds: new[] { "Vigor" },
            existingBindings: new[] { new AuraBoundRow("Might", "b-might") });

        Assert.Equal(new[] { "Vigor" }, plan.ToBind);
        Assert.Equal(new[] { "b-might" }, plan.ToWithdraw);
    }

    [Fact]
    public void Equipped_but_never_enabled_produces_no_binding()
    {
        // "Equipped" never reaches this function at all -- only AuraRuntime.ActiveAuraIds does, and
        // an equipped-but-inactive aura is absent from it by construction. Asserted here as the empty
        // case: nothing active, nothing existing, nothing to do.
        var plan = AuraBindingPlan.Compute(
            activeAuraIds: Array.Empty<string>(),
            existingBindings: Array.Empty<AuraBoundRow>());

        Assert.Empty(plan.ToBind);
        Assert.Empty(plan.ToWithdraw);
    }

    [Fact]
    public void A_duplicate_binding_for_a_still_active_aura_self_heals_to_one()
    {
        var plan = AuraBindingPlan.Compute(
            activeAuraIds: new[] { "Might" },
            existingBindings: new[]
            {
                new AuraBoundRow("Might", "b-2"),
                new AuraBoundRow("Might", "b-1"),
            });

        Assert.Empty(plan.ToBind);
        Assert.Equal(new[] { "b-2" }, plan.ToWithdraw); // lowest id ("b-1") kept, the rest withdrawn
    }

    [Fact]
    public void Output_is_ordered_for_deterministic_writes()
    {
        var plan = AuraBindingPlan.Compute(
            activeAuraIds: new[] { "Vigor", "Agility", "Might" },
            existingBindings: Array.Empty<AuraBoundRow>());

        Assert.Equal(new[] { "Agility", "Might", "Vigor" }, plan.ToBind);
    }
}
