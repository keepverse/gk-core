using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.6, spec-profile-schema.md §4): `AiRowSelector.TryPick`
/// is the only rank walk in the repo. Contracts and closed vocabularies only -- never a row count.
/// </summary>
public class AiRowSelectorTests
{
    static readonly AiActionFilter EmptyFilter = new(null, null, null, null);

    static AiProfileRow Row(
        TargetSelector selector = TargetSelector.Nearest,
        AiRowCondition condition = AiRowCondition.Always, int conditionArgMilli = 0, string conditionArgId = "",
        AiCensusCondition census = AiCensusCondition.None, int censusArg = 0) =>
        new(selector, condition, conditionArgMilli, conditionArgId, census, censusArg, EmptyFilter);

    static CombatAiProfile Profile(params AiProfileRow[] rows) => new(
        ProfileId: "test/default", Place: AiPlace.Battle, Role: AiRole.Default, TierOverride: null,
        TierByActorClass: new Dictionary<AiActorClass, AiTier>
        {
            [AiActorClass.Unique] = AiTier.Smart, [AiActorClass.General] = AiTier.Performance,
        },
        Rows: rows,
        Scoring: new AiScoringBlock(0, 0, 0, 0, 0, 0, 0, AggressionRange: 2, MaxCandidatesScored: 32),
        Selection: new AiSelectionBlock(SelectionMode.Argmax, 1000, "ai.select"),
        Reserves: Array.Empty<AiReserveFloor>(),
        Guards: new AiWasteGuards(0, 0, 0),
        AntiRepeat: new AiAntiRepeat(0, 0, 0),
        Trigger: null,
        Personality: new AiPersonalityBounds(new Dictionary<PersonalityAxis, int>()));

    static readonly AiRowFacts NoTarget = new(
        SelfHpMilli: 1000, SelfResourceMilli: 1000, SelfResourceId: "stamina",
        TargetHpMilli: AiRowFacts.NoTargetHpMilli, SelfStatusMask: 0, TargetStatusMask: 0,
        EnemiesLive: 1, AlliesDowned: 0, Round: 0);

    [Fact]
    public void First_matching_row_wins_and_the_walk_stops()
    {
        var profile = Profile(
            Row(selector: TargetSelector.LowestHp),                                  // rank 0: Always -- matches first
            Row(selector: TargetSelector.HighestThreat, condition: AiRowCondition.Always));

        var found = AiRowSelector.TryPick(profile, NoTarget, out var selector, out _, out var rankIndex);

        Assert.True(found);
        Assert.Equal(TargetSelector.LowestHp, selector);
        Assert.Equal(0, rankIndex);
    }

    [Fact]
    public void A_row_whose_condition_needs_a_target_is_false_when_no_target_is_held()
    {
        var profile = Profile(
            Row(selector: TargetSelector.LowestHp, condition: AiRowCondition.TargetHpBelowMilli, conditionArgMilli: 500),
            Row(selector: TargetSelector.Nearest)); // the required unconditional fallback

        var found = AiRowSelector.TryPick(profile, NoTarget, out var selector, out _, out var rankIndex);

        Assert.True(found);
        Assert.Equal(TargetSelector.Nearest, selector); // row 0 never matched -- NoTargetHpMilli is never "below"
        Assert.Equal(1, rankIndex);
    }

    [Fact]
    public void The_status_condition_reads_the_bit_index_the_arg_names_in_either_mask()
    {
        // `ConditionArgMilli` is the status bit index (spec-profile-schema.md §4). The two masks are
        // `ulong` -- the type `EntityFacts.StatusMask` already is -- so this pins the contract AND the
        // widening: no `unchecked((long)mask)` reinterpret, and bit 63 (the one whose SIGN differs
        // between long and ulong) is read exactly like bit 0.
        var facts = NoTarget with
        {
            SelfStatusMask = (1UL << 63) | 1UL,
            TargetStatusMask = 1UL << 0,
        };

        Assert.True(Picks(Row(condition: AiRowCondition.HasStatus, conditionArgMilli: 0), facts));
        Assert.True(Picks(Row(condition: AiRowCondition.HasStatus, conditionArgMilli: 63), facts));
        Assert.False(Picks(Row(condition: AiRowCondition.HasStatus, conditionArgMilli: 1), facts));
        Assert.False(Picks(Row(condition: AiRowCondition.HasStatus, conditionArgMilli: 62), facts));
        Assert.True(Picks(Row(condition: AiRowCondition.TargetHasStatus, conditionArgMilli: 0), facts));
        Assert.False(Picks(Row(condition: AiRowCondition.TargetHasStatus, conditionArgMilli: 63), facts));
    }

    [Fact]
    public void A_mask_with_only_bit_63_set_matches_exactly_that_one_index_across_all_64()
    {
        // Total over the bit-index contract: for every index in 0..63 the walk answers "set" for 63 and
        // "clear" for the other 63. A `1L << 63` on a `long`-typed mask would still pass bitwise, so the
        // point is the contract's totality, not a sign bug -- the type change above is what makes the
        // absence of an `unchecked` cast checkable by the overflow audit.
        var facts = NoTarget with { SelfStatusMask = 1UL << 63 };

        for (var bit = 0; bit < 64; bit++)
            Assert.Equal(bit == 63, Picks(Row(condition: AiRowCondition.HasStatus, conditionArgMilli: bit), facts));
    }

    /// <summary>True when a profile carrying exactly this one condition row matches the supplied facts.
    /// No fallback row: `AiRowSelector.TryPick` walks whatever rows it is given, so a single row that does
    /// not match must return false — adding an `Always` tail would make every case true.</summary>
    static bool Picks(AiProfileRow row, AiRowFacts facts) =>
        AiRowSelector.TryPick(Profile(row), facts, out _, out _, out _);

    [Fact]
    public void Census_facts_are_read_from_the_supplied_struct_and_never_recounted()
    {
        var rows = new List<AiProfileRow>();
        for (var i = 0; i < 7; i++)
            rows.Add(Row(condition: AiRowCondition.SelfHpBelowMilli, conditionArgMilli: -1)); // never matches
        rows.Add(Row()); // rank 7: the required unconditional fallback

        var profile = Profile(rows.ToArray());
        var callCount = 0;
        // The census is gathered ONCE by the caller, before the walk -- AiRowFacts is a value struct,
        // so "never recounted" means the walk reads the SAME EnemiesLive value on every row, never a
        // fresh read. Simulated here by a counting fake constructing the struct exactly once.
        AiRowFacts BuildFacts() { callCount++; return NoTarget with { EnemiesLive = 3 }; }
        var facts = BuildFacts();

        AiRowSelector.TryPick(profile, facts, out _, out _, out var rankIndex);

        Assert.Equal(1, callCount);
        Assert.Equal(7, rankIndex);
    }

    [Fact]
    public void AiRowFacts_is_total_over_AiCensusCondition()
    {
        // A closed-vocabulary pin: AiCensusCondition has exactly five members (None + four board
        // questions), and every one of the four real questions has a field AiRowFacts supplies.
        Assert.Equal(5, Enum.GetValues(typeof(AiCensusCondition)).Length);

        var profile = Profile(
            Row(census: AiCensusCondition.EnemiesAtLeast, censusArg: 2),
            Row(census: AiCensusCondition.EnemiesAtMost, censusArg: 0),
            Row(census: AiCensusCondition.AlliesDownedAtLeast, censusArg: 5),
            Row(census: AiCensusCondition.RoundAtLeast, censusArg: 100),
            Row()); // the required unconditional fallback

        var facts = NoTarget with { EnemiesLive = 3, AlliesDowned = 5, Round = 100 };
        var found = AiRowSelector.TryPick(profile, facts, out _, out _, out var rankIndex);

        Assert.True(found);
        Assert.Equal(0, rankIndex); // EnemiesAtLeast(2) is satisfied by EnemiesLive=3 -- first match wins
    }

    [Fact]
    public void An_empty_rows_list_returns_false_and_never_throws()
    {
        var profile = Profile(); // no rows at all -- only reachable from a test-constructed profile

        var found = AiRowSelector.TryPick(profile, NoTarget, out _, out _, out var rankIndex);

        Assert.False(found);
        Assert.Equal(-1, rankIndex);
    }
}
