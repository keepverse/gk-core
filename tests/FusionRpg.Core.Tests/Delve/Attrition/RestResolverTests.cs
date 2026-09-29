using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Attrition;

/// <summary>D2.20 (spec-delve-attrition.md §5) — `RestResolver`: heal ‰, `restRelief`, and the
/// activation pass-through. `DungeonTuningHub` is configured for the whole assembly by
/// `Dungeon.DungeonHubTestBootstrap`'s module initializer.</summary>
public class RestResolverTests
{
    static readonly DungeonTuning Tuning = DungeonTuningHub.Tuning;
    static readonly DifficultyRungTuning Hard = Tuning.Rungs["hard"]; // the identity row -- RestHealMultMilli 1000

    static ActorDerivedSnapshot Snapshot(long max = 1000, long regenPerTick = 0) =>
        ActorDerivedSnapshot.FromValues(DerivedStatChannels.ResourceIds.SelectMany(id => new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax(id), max),
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceRegen(id), regenPerTick),
        }));

    static IReadOnlyDictionary<string, long> FullPools(long value) =>
        DerivedStatChannels.ResourceIds.ToDictionary(id => id, _ => value, StringComparer.Ordinal);

    // ---- Heal: golden at the identity rung, against the real shipped tuning ----

    [Fact]
    public void Heal_at_the_identity_rung_golden()
    {
        // 1000 * 500 * 1000 / 1_000_000 = 500
        var pools = FullPools(200);
        var healed = RestResolver.Heal(pools, Tuning.RestHealsPools, Tuning.AttritionRestHealMilli, Hard, Snapshot(), atTick: 0);

        foreach (var id in Tuning.RestHealsPools)
            Assert.Equal(700, healed[id]); // 200 + 500
    }

    [Fact]
    public void Heal_only_touches_the_named_pools_never_the_others()
    {
        var pools = FullPools(200);
        var healed = RestResolver.Heal(pools, Tuning.RestHealsPools, Tuning.AttritionRestHealMilli, Hard, Snapshot(), atTick: 0);

        foreach (var id in DerivedStatChannels.ResourceIds.Except(Tuning.RestHealsPools))
            Assert.Equal(200, healed[id]); // untouched
    }

    [Fact]
    public void Heal_clamps_at_max_never_overshoots()
    {
        var pools = FullPools(900); // max is 1000, heal would be +500 -> 1400 uncapped
        var healed = RestResolver.Heal(pools, Tuning.RestHealsPools, Tuning.AttritionRestHealMilli, Hard, Snapshot(max: 1000), atTick: 0);

        foreach (var id in Tuning.RestHealsPools)
            Assert.Equal(1000, healed[id]); // the pool's own [0, max] rail stops it, not a second clamp here
    }

    [Fact]
    public void A_rung_with_a_reduced_RestHealMultMilli_heals_less_never_refilling_to_max_what_the_rung_reduced()
    {
        var dampened = Hard with { RestHealMultMilli = 750 }; // e.g. very-hard+'s own shape
        var pools = FullPools(0);
        var healed = RestResolver.Heal(pools, Tuning.RestHealsPools, Tuning.AttritionRestHealMilli, dampened, Snapshot(), atTick: 0);

        // 1000 * 500 * 750 / 1_000_000 = 375 -- the reduced amount, not clamped to anything special
        foreach (var id in Tuning.RestHealsPools)
            Assert.Equal(375, healed[id]);
    }

    [Fact]
    public void No_overflow_at_a_large_max()
    {
        var pools = FullPools(0);
        var healed = RestResolver.Heal(pools, Tuning.RestHealsPools, Tuning.AttritionRestHealMilli, Hard, Snapshot(max: 1_000_000), atTick: 0);

        foreach (var id in Tuning.RestHealsPools)
            Assert.Equal(500_000, healed[id]); // 1_000_000 * 500 * 1000 / 1_000_000
    }

    [Fact]
    public void Heal_null_arguments_throw()
    {
        var pools = FullPools(0);
        Assert.Throws<ArgumentNullException>(() => RestResolver.Heal(null!, Tuning.RestHealsPools, 500, Hard, Snapshot(), 0));
        Assert.Throws<ArgumentNullException>(() => RestResolver.Heal(pools, null!, 500, Hard, Snapshot(), 0));
        Assert.Throws<ArgumentNullException>(() => RestResolver.Heal(pools, Tuning.RestHealsPools, 500, null!, Snapshot(), 0));
        Assert.Throws<ArgumentNullException>(() => RestResolver.Heal(pools, Tuning.RestHealsPools, 500, Hard, null!, 0));
    }

    // ---- StacksAfterRelief ----

    [Theory]
    [InlineData(5, 2, 3)]
    [InlineData(3, 2, 1)]
    [InlineData(1, 2, 0)] // floored at zero -- never negative
    [InlineData(0, 2, 0)]
    public void StacksAfterRelief_floors_at_zero_never_negative(int stacks, long relief, int expected)
    {
        Assert.Equal(expected, RestResolver.StacksAfterRelief(stacks, relief));
    }

    [Fact]
    public void StacksAfterRelief_the_real_shipped_restRelief_golden()
    {
        Assert.Equal(2, Tuning.AttritionNerve.RestRelief); // the real starting shape
        Assert.Equal(3, RestResolver.StacksAfterRelief(5, Tuning.AttritionNerve.RestRelief));
    }

    [Fact]
    public void StacksAfterRelief_negative_relief_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RestResolver.StacksAfterRelief(5, -1));
    }

    // ---- Resolve: the whole rest room, composed ----

    [Fact]
    public void Resolve_composes_heal_relief_and_activations_into_one_result()
    {
        var pools = FullPools(200) as Dictionary<string, long> ?? new Dictionary<string, long>(FullPools(200));
        pools["spirit"] = 0; // spirit is one of the real healsPools ids -- start it drained

        var result = RestResolver.Resolve(
            pools, nerveStacks: 5, Tuning.RestHealsPools, Tuning.AttritionRestHealMilli, Hard,
            Tuning.AttritionNerve.RestRelief, Tuning.RestActivations, Snapshot(), atTick: 0);

        Assert.Equal(700, result.Pools["hp"]);      // healed (200 + 500)
        Assert.Equal(500, result.Pools["spirit"]);  // healed from empty (0 + 500)
        Assert.Equal(200, result.Pools["qi"]);      // not a healsPools id -- untouched
        Assert.Equal(3, result.NerveStacks);        // 5 - restRelief(2)
        Assert.Equal(Tuning.RestActivations, result.Outcome.Activations);
        Assert.False(result.Outcome.Ambushed);      // default -- no deck draw happened here
    }

    [Fact]
    public void Resolve_passes_an_explicit_ambushed_flag_through_verbatim()
    {
        var pools = FullPools(500);
        var result = RestResolver.Resolve(
            pools, nerveStacks: 0, Tuning.RestHealsPools, Tuning.AttritionRestHealMilli, Hard,
            Tuning.AttritionNerve.RestRelief, Tuning.RestActivations, Snapshot(), atTick: 0, ambushed: true);

        Assert.True(result.Outcome.Ambushed);
    }

    // ---- the verify line's own headline: "hunger is relieved only at a rest" ----

    [Fact]
    public void Hunger_drains_across_ordinary_rooms_and_is_relieved_only_when_a_rest_room_resolves()
    {
        var pools = new Dictionary<string, long>(FullPools(1000));
        var snapshot = Snapshot(max: 1000);

        // Two ordinary (non-rest) rooms drain hunger -- HungerCharge is the only thing touching it.
        pools["hunger"] -= HungerCharge.ForRoom("light", 1000, Tuning.HazardBandHungerPerMille, Hard);
        pools["hunger"] -= HungerCharge.ForRoom("heavy", 1000, Tuning.HazardBandHungerPerMille, Hard);
        Assert.Equal(870, pools["hunger"]); // 1000 - 40 - 90, matches HungerChargeTests' own golden shape

        // Every OTHER pool is untouched by hunger draining -- only "hunger" itself ever moved.
        Assert.Equal(1000, pools["hp"]);

        // Now a rest room resolves. Hunger IS one of the real healsPools ids, so it is relieved here --
        // and only here; no ordinary room ever calls RestResolver.Heal.
        var rested = RestResolver.Heal(pools, Tuning.RestHealsPools, Tuning.AttritionRestHealMilli, Hard, snapshot, atTick: 0);
        Assert.Equal(1000, rested["hunger"]); // 830 + 500 heal, clamped at max -- relieved, not reset by a rest bypass
    }

    // ---- real, shipped tuning sanity (fails loudly if the shipped file's shape ever moves) ----

    [Fact]
    public void The_real_shipped_rest_tuning_carries_exactly_these_starting_values()
    {
        Assert.Equal(500, Tuning.AttritionRestHealMilli);
        Assert.Equal(new[] { "hp", "hunger", "spirit" }, Tuning.RestHealsPools);
        // spec-delve-attrition.md's own Tunables table names "3" as rest.activations' starting shape;
        // the real shipped dungeon.v1.json carries 4 -- a real, minor spec-vs-data drift, trusting the
        // committed data over the spec's prose (this repo's own standing rule).
        Assert.Equal(4, Tuning.RestActivations);
        Assert.Equal(1000, Hard.RestHealMultMilli); // the identity rung
    }
}
