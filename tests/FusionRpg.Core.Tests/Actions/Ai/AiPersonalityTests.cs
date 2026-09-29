using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md §3): D5's bounded,
/// seeded personality draw — reproducible, never stored, never asserted at a specific drawn value
/// (that would be generated content; the contract is "reproducible and in-bounds").
/// </summary>
public class AiPersonalityTests
{
    static AiPersonalityBounds Bounds(int aggression = 5, int recklessness = 10, int focus = 8, int thrift = 300) =>
        new(new Dictionary<PersonalityAxis, int>
        {
            [PersonalityAxis.Aggression] = aggression, [PersonalityAxis.Recklessness] = recklessness,
            [PersonalityAxis.Focus] = focus, [PersonalityAxis.Thrift] = thrift,
        });

    static AiPersonalityBounds AllZero() => Bounds(0, 0, 0, 0);

    [Fact]
    public void Same_instance_id_gives_the_same_personality()
    {
        var a = AiPersonalityFactory.ForUnique("unique:abc", Bounds());
        var b = AiPersonalityFactory.ForUnique("unique:abc", Bounds());
        Assert.Equal(a.OffsetByAxis, b.OffsetByAxis);

        // Identical across different match seeds too -- a unique's personality is a pure function of
        // its instance id ALONE, never the match.
        var c = AiPersonalityFactory.ForUnique("unique:abc", Bounds());
        Assert.Equal(a.OffsetByAxis, c.OffsetByAxis);
    }

    [Fact]
    public void Same_match_seed_and_actor_key_give_the_same_personality()
    {
        var a = AiPersonalityFactory.ForGeneral(matchSeed: 777UL, "general:1", Bounds());
        var b = AiPersonalityFactory.ForGeneral(matchSeed: 777UL, "general:1", Bounds());
        Assert.Equal(a.OffsetByAxis, b.OffsetByAxis);
    }

    [Fact]
    public void Different_actor_keys_draw_independently()
    {
        var a = AiPersonalityFactory.ForGeneral(matchSeed: 1UL, "general:1", Bounds());
        var b = AiPersonalityFactory.ForGeneral(matchSeed: 1UL, "general:2", Bounds());
        Assert.NotEqual(a.OffsetByAxis, b.OffsetByAxis);
    }

    [Fact]
    public void Every_offset_is_inside_its_bound()
    {
        var bounds = Bounds(aggression: 2, recklessness: 30, focus: 1, thrift: 1000);
        for (var i = 0; i < 50; i++)
        {
            var p = AiPersonalityFactory.ForGeneral(matchSeed: (ulong)i, $"actor:{i}", bounds);
            Assert.InRange(p.OffsetOf(PersonalityAxis.Aggression), -2, 2);
            Assert.InRange(p.OffsetOf(PersonalityAxis.Recklessness), -30, 30);
            Assert.InRange(p.OffsetOf(PersonalityAxis.Focus), -1, 1);
            Assert.InRange(p.OffsetOf(PersonalityAxis.Thrift), -1000, 1000);
        }
    }

    /// <summary>The draw-order contract, asserted directly: every axis draws unconditionally, in
    /// declaration order, from ONE stream — so widening one axis's bound never shifts another axis's
    /// drawn value for the SAME identity. Without this test the unconditional-draw rule is a comment
    /// nobody enforces.</summary>
    [Fact]
    public void Changing_one_axis_bound_does_not_shift_another_axis()
    {
        var narrow = AiPersonalityFactory.ForUnique("unique:xyz", Bounds(recklessness: 5));
        var wide = AiPersonalityFactory.ForUnique("unique:xyz", Bounds(recklessness: 500));

        // Recklessness's OWN offset may differ (a wider span changes what NextInt(span) can return),
        // but every OTHER axis -- drawn before/after it in the SAME fixed order -- is untouched.
        Assert.Equal(narrow.OffsetOf(PersonalityAxis.Aggression), wide.OffsetOf(PersonalityAxis.Aggression));
        Assert.Equal(narrow.OffsetOf(PersonalityAxis.Focus), wide.OffsetOf(PersonalityAxis.Focus));
        Assert.Equal(narrow.OffsetOf(PersonalityAxis.Thrift), wide.OffsetOf(PersonalityAxis.Thrift));
    }

    [Fact]
    public void All_bounds_zero_is_byte_identical()
    {
        var p = AiPersonalityFactory.ForUnique("unique:zero", AllZero());
        foreach (PersonalityAxis axis in Enum.GetValues(typeof(PersonalityAxis)))
            Assert.Equal(0, p.OffsetOf(axis));
    }

    [Fact]
    public void Applied_offsets_are_clamped_to_the_profile_range()
    {
        // A wide recklessness draw still cannot push weightRisk below its own natural floor of 0.
        var scoring = new AiScoringBlock(70, 50, 15, 10, 10, 1, 120, 2, 32); // weightRisk = 120
        var profile = MinimalProfile(scoring);

        var personality = new AiPersonality(new[] { 0, 500, 0, 0 }); // recklessness offset way beyond weightRisk
        var applied = AiPersonalityApply.Apply(profile, personality);

        Assert.Equal(0, applied.Scoring.WeightRisk); // 120 - 500 clamped to 0, never negative
    }

    [Fact]
    public void Recklessness_lowers_weightRisk_and_never_below_zero()
    {
        var profile = MinimalProfile(new AiScoringBlock(70, 50, 15, 10, 10, 1, 120, 2, 32));
        var applied = AiPersonalityApply.Apply(profile, new AiPersonality(new[] { 0, 20, 0, 0 }));
        Assert.Equal(100, applied.Scoring.WeightRisk); // 120 - 20
    }

    [Fact]
    public void Focus_raises_weightLowHp_and_weightKill_together()
    {
        var profile = MinimalProfile(new AiScoringBlock(70, 50, 15, 10, 10, 1, 120, 2, 32));
        var applied = AiPersonalityApply.Apply(profile, new AiPersonality(new[] { 0, 0, 5, 0 }));
        Assert.Equal(15, applied.Scoring.WeightLowHp); // 10 + 5
        Assert.Equal(20, applied.Scoring.WeightKill);  // 15 + 5
    }

    [Fact]
    public void Thrift_shifts_every_reserve_floor_clamped_to_0_1000()
    {
        var reserves = new[] { new AiReserveFloor("stamina", 900), new AiReserveFloor("poise", 50) };
        var profile = MinimalProfile(new AiScoringBlock(70, 50, 15, 10, 10, 1, 120, 2, 32), reserves);

        var hoarding = AiPersonalityApply.Apply(profile, new AiPersonality(new[] { 0, 0, 0, 300 }));
        Assert.Equal(1000, hoarding.Reserves[0].FloorMilliOfMax); // 900 + 300 clamped to 1000
        Assert.Equal(350, hoarding.Reserves[1].FloorMilliOfMax); // 50 + 300

        var spending = AiPersonalityApply.Apply(profile, new AiPersonality(new[] { 0, 0, 0, -300 }));
        Assert.Equal(600, spending.Reserves[0].FloorMilliOfMax); // 900 - 300
        Assert.Equal(0, spending.Reserves[1].FloorMilliOfMax);   // 50 - 300 clamped to 0
    }

    static CombatAiProfile MinimalProfile(AiScoringBlock scoring, IReadOnlyList<AiReserveFloor>? reserves = null) => new(
        "test/default", AiPlace.Battle, AiRole.Default, TierOverride: null,
        new Dictionary<AiActorClass, AiTier> { [AiActorClass.Unique] = AiTier.Smart, [AiActorClass.General] = AiTier.Performance },
        new[] { new AiProfileRow(TargetSelector.Nearest, AiRowCondition.Always, 0, "", AiCensusCondition.None, 0, new AiActionFilter(null, null, null, null)) },
        scoring, new AiSelectionBlock(SelectionMode.Argmax, 1000, "ai.select"),
        reserves ?? Array.Empty<AiReserveFloor>(), new AiWasteGuards(1, 0, 0), new AiAntiRepeat(0, 0, 0), Trigger: null,
        new AiPersonalityBounds(new Dictionary<PersonalityAxis, int>
        {
            [PersonalityAxis.Aggression] = 0, [PersonalityAxis.Recklessness] = 0,
            [PersonalityAxis.Focus] = 0, [PersonalityAxis.Thrift] = 0,
        }));

    [Fact] public void PersonalityAxis_has_four_members() =>
        // Aggression/Recklessness/Focus/Thrift -- append-only (module 2 states this), closed here.
        Assert.Equal(4, Enum.GetValues(typeof(PersonalityAxis)).Length);
}
