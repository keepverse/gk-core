using FusionRpg.Core.Items;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>SC7, enforced by <see cref="RarityBudgetKeys"/> (spec-rarity-bands.md).</summary>
public class RarityBudgetKeysTests
{
    [Theory]
    [InlineData("promote_from")]
    [InlineData("pity_guarded")]
    [InlineData("drop_weight_default")]
    [InlineData("enhance_cap")]
    [InlineData("power_ceiling")]
    // ⭐ salvage_yield joined the ready set 2026-09-04 when module 14 (`salvage-craft`) decided its
    // shape — one integer per rung, the substrate quantity a salvage of that rung returns before the
    // affix bonus, seeded by RpgStore.SeedSalvageYield from gk-core/data/tuning/materials.v1.json. Moved
    // rather than loosened: the row below still pins the three that ARE still awaiting, so "not
    // decided is not safe-to-seed" is asserted exactly as hard as before.
    [InlineData("salvage_yield")]
    // ⭐ reroll_cost_mult joined the ready set 2026-09-05 when module 15 (`enhance-reroll`) decided
    // its shape — the per-rung integer is the reroll price's RUNG LEG, seeded by
    // RpgStore.SeedRerollCostMult from gk-core/data/tuning/enhancement.v1.json, with §9.7's affix-count
    // requirement met by a second leg that is not a per-rung row. Moved rather than loosened: the
    // row below still pins the two that ARE still awaiting.
    [InlineData("reroll_cost_mult")]
    // ⭐ socket_min and socket_max joined the ready set 2026-09-05 when module 16 (`sockets`) decided
    // their shape — TWO integers per rung, the inclusive window a drop's socket count is rolled from
    // before the base type's own socketMax clamps it, seeded by RpgStore.SeedSocketGrants from
    // gk-core/data/tuning/sockets.v1.json and read by SocketGeometry.SocketsAtDrop.
    [InlineData("socket_min")]
    [InlineData("socket_max")]
    public void The_ready_keys_are_registered(string key) =>
        Assert.True(RarityBudgetKeys.IsRegistered(key));

    [Fact]
    public void Every_listed_key_now_has_a_decided_shape_and_the_undecided_gate_still_bites()
    {
        // ⭐ 2026-09-05: with module 16's two keys decided, EVERY key in the closed list is ready.
        // The "not decided is not safe-to-seed" gate is therefore asserted against a synthetic
        // undecided key rather than a real one — the mechanism has to survive the list happening to
        // be fully decided today, because the NEXT key added will not be.
        Assert.All(RarityBudgetKeys.All, k => Assert.True(k.HasDecidedShape, $"'{k.Key}' is listed but undecided"));

        var undecided = new RarityBudgetKeyDef("hypothetical_key", "nobody (0)", HasDecidedShape: false);
        Assert.False(undecided.HasDecidedShape);
        Assert.False(RarityBudgetKeys.IsRegistered(undecided.Key));
        Assert.Throws<RarityBudgetKeyRejection>(() => RarityBudgetKeys.Validate(undecided.Key));
    }

    [Fact]
    public void An_unknown_key_is_rejected()
    {
        Assert.False(RarityBudgetKeys.IsRegistered("does-not-exist"));
        Assert.Throws<RarityBudgetKeyRejection>(() => RarityBudgetKeys.Validate("does-not-exist"));
    }

    [Fact]
    public void Set_eligible_and_charm_potency_are_not_in_the_key_registry()
    {
        // D15 makes set_eligible vacuous; spec-set-charm-gen.md never reads charm_potency. Both were
        // resolved by DROPPING, not deferring -- a re-add with no consumer is the exact SC7 violation
        // this test pins against regressing.
        Assert.DoesNotContain(RarityBudgetKeys.All, k => k.Key == "set_eligible");
        Assert.DoesNotContain(RarityBudgetKeys.All, k => k.Key == "charm_potency");
        Assert.False(RarityBudgetKeys.IsRegistered("set_eligible"));
        Assert.False(RarityBudgetKeys.IsRegistered("charm_potency"));
    }

    [Fact]
    public void Registered_keys_each_name_a_real_consumer_module()
    {
        foreach (var k in RarityBudgetKeys.All)
            Assert.False(string.IsNullOrWhiteSpace(k.ConsumerModule));
    }
}
