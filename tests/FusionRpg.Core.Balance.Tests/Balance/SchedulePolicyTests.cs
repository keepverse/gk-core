using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>
/// combat-ai `action-schedule-twin` (module 9, CAI2.3, spec-action-schedule-twin.md §2/§3/§4): the
/// identity row and the three things the twin learns. `Greedy` must reproduce the pre-CAI2.3 walk
/// byte-for-byte — that is why the policy is a trailing optional parameter with an identity default
/// rather than a changed walk, and why every existing `ActionScheduleTests` case compiles and passes
/// unedited.
///
/// <para>The parity test against the REAL core policy (`ActionScheduleMatchesCorePolicyTests`) is the
/// module's other half and is not in this file: it needs a live `IIntentSource` fixture.</para>
/// </summary>
public class SchedulePolicyTests
{
    const double Base = 100.0;

    /// <summary>Priority 1: a 100%-share costed option (cost 100). Priority 2: a 30%-share one
    /// (cost 30). Priority 3: the mandatory free fallback.</summary>
    static ActionSchedule.ActionOption[] Options(long rowFloorA = -1, int rowMinTargetsA = 1) =>
    [
        new("heavy", 1, 1.0, "stamina", 1000, rowFloorA, rowMinTargetsA),
        new("light", 2, 1.0, "stamina", 300),
        new("free", 3, 0.5, null, 0),
    ];

    static Dictionary<string, ActionSchedule.PoolState> Pools(double value = 100.0, double max = 100.0, double regen = 0.0) =>
        new(StringComparer.Ordinal) { ["stamina"] = new(value, max, regen) };

    static string[] Sequence(IReadOnlyList<ActionSchedule.RoundOutcome> outcomes) =>
        outcomes.Select(o => o.ActionId).ToArray();

    [Fact]
    public void Walk_with_no_policy_is_byte_identical_to_Walk_with_Greedy()
    {
        var withDefault = ActionSchedule.Walk(Options(), Pools(value: 250, max: 250), Base, rounds: 8);
        var withGreedy = ActionSchedule.Walk(Options(), Pools(value: 250, max: 250), Base, rounds: 8,
            ActionSchedule.SchedulePolicy.Greedy);

        Assert.Equal(Sequence(withDefault), Sequence(withGreedy));
        Assert.Equal(
            withDefault.Select(o => o.DamageMultiplier),
            withGreedy.Select(o => o.DamageMultiplier));
    }

    /// <summary>The pinned identity constants, each with its reason: 0 makes the floor a no-op exactly,
    /// `SkipOverkill` false never reads the predicate, and the tier is the one a "first usable action
    /// after the gates" walk actually is (spec §4).</summary>
    [Fact]
    public void Greedy_is_the_identity_policy()
    {
        Assert.Equal("greedy", ActionSchedule.SchedulePolicy.Greedy.ProfileId);
        Assert.Equal(0, ActionSchedule.SchedulePolicy.Greedy.ReserveFloorMilli);
        Assert.False(ActionSchedule.SchedulePolicy.Greedy.SkipOverkill);
        Assert.Equal(AiTier.Performance, ActionSchedule.SchedulePolicy.Greedy.Tier);
    }

    /// <summary>
    /// The floor is on what REMAINS after paying, not on what is spent — the ideal's own words
    /// (§6.1 step 3). The discriminating case: max 100, cost 30, floor 200 per-mille. A
    /// "may spend at most 20% of max" reading would refuse (30 &gt; 20); "at least 20 must remain"
    /// accepts (`100 - 30 = 70 &gt;= 20`). The costed action is taken, so the reading is the right one.
    /// </summary>
    [Fact]
    public void The_reserve_floor_is_on_what_remains_not_on_what_is_spent()
    {
        var policy = new ActionSchedule.SchedulePolicy("test", AiTier.Performance, ReserveFloorMilli: 200, SkipOverkill: false);

        var outcome = ActionSchedule.Walk(Options(), Pools(), Base, rounds: 1, policy);

        Assert.Equal("light", outcome[0].ActionId); // 30 spent, 70 remains, floor 20 cleared
    }

    /// <summary>A floor the pool can never clear must not starve the walk: the free fallback
    /// short-circuits before the floor is consulted, so floor 1000 still produces `rounds` outcomes and
    /// never throws. A floor that could hang the walk would be a hang, not a balance decision.</summary>
    [Fact]
    public void A_floor_of_1000_never_hangs_and_never_throws()
    {
        var policy = new ActionSchedule.SchedulePolicy("test", AiTier.Performance, ReserveFloorMilli: 1000, SkipOverkill: false);

        var outcome = ActionSchedule.Walk(Options(), Pools(), Base, rounds: 12, policy);

        Assert.Equal(12, outcome.Count);
        Assert.All(Sequence(outcome), id => Assert.Equal("free", id));
    }

    [Fact]
    public void Reserve_floor_minus_one_defers_to_the_policy_and_a_row_value_overrides_it()
    {
        var floor500 = new ActionSchedule.SchedulePolicy("test", AiTier.Performance, ReserveFloorMilli: 500, SkipOverkill: false);

        // Row -1 defers: the 100-cost option cannot leave 50 behind, so the 30-cost one is next...
        // which also cannot (70 - ... wait: 100 - 30 = 70 >= 50, so "light" is affordable).
        var deferred = ActionSchedule.Walk(Options(rowFloorA: -1), Pools(), Base, rounds: 1, floor500);
        Assert.Equal("light", deferred[0].ActionId);

        // Row 0 overrides the policy's 500, so the heavy option (100 spent, 0 remaining) is legal.
        var overridden = ActionSchedule.Walk(Options(rowFloorA: 0), Pools(), Base, rounds: 1, floor500);
        Assert.Equal("heavy", overridden[0].ActionId);
    }

    /// <summary>§3: loud, once, naming the option — never a silent skip and never a silent model.</summary>
    [Fact]
    public void MinTargets_above_one_throws_naming_the_option()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
        {
            ActionSchedule.Walk(Options(rowMinTargetsA: 3), Pools(), Base, rounds: 1);
        });

        Assert.Contains("heavy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("minTargets=3", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>§4: the tier is a closed vocabulary. An unknown value throws naming it and the profile,
    /// rather than falling through to one of the two known tiers.</summary>
    [Fact]
    public void An_out_of_vocabulary_tier_throws_naming_it()
    {
        var bogus = new ActionSchedule.SchedulePolicy("rogue/default", (AiTier)99, ReserveFloorMilli: 0, SkipOverkill: false);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            ActionSchedule.Walk(Options(), Pools(), Base, rounds: 1, bogus);
        });

        Assert.Contains("rogue/default", ex.Message, StringComparison.Ordinal);
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>§2's overkill guard: when the caller says the free option already ends the round, no
    /// costed action is committed — and the predicate is never consulted when the policy is Greedy.</summary>
    [Fact]
    public void SkipOverkill_skips_every_costed_option_when_the_free_one_ends_the_round()
    {
        var overkill = new ActionSchedule.SchedulePolicy("test", AiTier.Performance, ReserveFloorMilli: 0, SkipOverkill: true);

        var guarded = ActionSchedule.Walk(Options(), Pools(), Base, rounds: 4,
            overkill, fightEndsThisRound: _ => true);
        Assert.All(Sequence(guarded), id => Assert.Equal("free", id));

        // The same predicate under Greedy (SkipOverkill false) changes nothing.
        var unguarded = ActionSchedule.Walk(Options(), Pools(value: 250, max: 250), Base, rounds: 1,
            ActionSchedule.SchedulePolicy.Greedy, fightEndsThisRound: _ => true);
        Assert.Equal("heavy", unguarded[0].ActionId);
    }

    /// <summary>§4: the two tiers provably collapse in the duel domain — the smart tier's extra work is
    /// target selection over a candidate set, and a duel has one candidate. The field is provenance.
    /// </summary>
    [Fact]
    public void Smart_and_Performance_tiers_produce_the_identical_sequence()
    {
        var smart = new ActionSchedule.SchedulePolicy("duel/smart", AiTier.Smart, ReserveFloorMilli: 0, SkipOverkill: false);
        var performance = new ActionSchedule.SchedulePolicy("duel/perf", AiTier.Performance, ReserveFloorMilli: 0, SkipOverkill: false);

        Assert.Equal(
            Sequence(ActionSchedule.Walk(Options(), Pools(value: 250, max: 250), Base, rounds: 10, smart)),
            Sequence(ActionSchedule.Walk(Options(), Pools(value: 250, max: 250), Base, rounds: 10, performance)));
    }

    static readonly Predictor.ActionEconomy BasicEconomy = new(
        new[]
        {
            new ActionSchedule.ActionOption("skill-strike", Priority: 1, DamageMultiplier: 1.8, "qi", 300),
            new ActionSchedule.ActionOption("strike", Priority: 2, DamageMultiplier: 1.0, "stamina", 220),
            new ActionSchedule.ActionOption("pass", Priority: 99, DamageMultiplier: 0.0, null, 0),
        },
        new Dictionary<string, ActionSchedule.PoolState> { ["qi"] = new(54, 54, 20), ["stamina"] = new(100, 100, 25) },
        new Dictionary<string, ActionSchedule.PoolState> { ["qi"] = new(54, 54, 20), ["stamina"] = new(100, 100, 25) });

    static Predictor.Actor Duelist(string name) =>
        new(name, new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral),
            Hp: 10_000, BaseDamage: 100, ShieldMaxHp: 0);

    /// <summary>
    /// The reserve floor REACHES the duel predictor: `ActionEconomy` now carries the policy and
    /// `MixedStrike` hands it to `Walk`. At 500 per-mille the 54-cost `skill-strike` cannot leave 27 in
    /// the qi pool, so the mix drops to the 1.0-multiplier `strike` — strictly less attrition per round
    /// than Greedy's `skill-strike` mix, on an apparatus where both sides are floored identically
    /// (so the win share stays an even race).
    /// </summary>
    [Fact]
    public void A_reserve_floor_reaches_the_duel_predictor()
    {
        var a = Duelist("A");
        var b = Duelist("B");

        var greed = Predictor.Predict(a, b, roundLimit: null, BasicEconomy, status: null);
        var flooredEconomy = BasicEconomy with
        {
            Policy = new ActionSchedule.SchedulePolicy("duel/floor", AiTier.Performance, ReserveFloorMilli: 500, SkipOverkill: false),
        };
        var floored = Predictor.Predict(a, b, roundLimit: null, flooredEconomy, status: null);

        Assert.True(floored.NetAttritionA < greed.NetAttritionA,
            $"expected the reserve floor to lower A's attrition ({floored.NetAttritionA}) below Greedy's ({greed.NetAttritionA})");
        Assert.Equal(0.5, floored.WinShareA, 6);
    }

    /// <summary>The new field's identity: `Policy = Greedy` is `Policy = null`, so every existing
    /// `ActionEconomy` and `PredictorTests` case is unchanged by the field's addition.</summary>
    [Fact]
    public void An_explicit_Greedy_policy_is_the_same_economy_as_no_policy()
    {
        var a = Duelist("A");
        var b = Duelist("B");

        Assert.Equal(
            Predictor.Predict(a, b, roundLimit: null, BasicEconomy, status: null),
            Predictor.Predict(a, b, roundLimit: null, BasicEconomy with { Policy = ActionSchedule.SchedulePolicy.Greedy }, status: null));
    }
}
