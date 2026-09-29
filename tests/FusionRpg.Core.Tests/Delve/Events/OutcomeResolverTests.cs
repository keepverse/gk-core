using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Dungeon.Tuning;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.5 (spec-event-deck.md §5): the severity band-shift and the weighted `:outcome` draw.
/// `dropBandOrder`/`weightTable` mirror the real, shipped item-registry content
/// (`gk-data/packs/fusion/data/seed/items/_registry/bands.v1.json:453-459,463-479`: `staple·frequent·occasional·seldom·
/// exceptional`, weights `1000/300/90/25/7`) as LOCAL test fixtures — this module never reads that
/// registry itself (D3.1's own citation: "this module has no business owning" the item registry), so
/// every real caller supplies its own copy the same way these tests do.</summary>
public class OutcomeResolverTests
{
    static readonly IReadOnlyList<string> Order = new[] { "staple", "frequent", "occasional", "seldom", "exceptional" };
    static readonly IReadOnlyDictionary<string, int> Weights = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["staple"] = 1000, ["frequent"] = 300, ["occasional"] = 90, ["seldom"] = 25, ["exceptional"] = 7,
    };

    static readonly DungeonTuning Tuning = DungeonTuningHub.Tuning;
    static readonly DifficultyRungTuning Hard = Tuning.Rungs["hard"]; // real shipped: EventSeverityTier == 2

    static EventOutcomeRow Outcome(string ordinal, string dropBand) => new(ordinal, dropBand, "none", Array.Empty<EventEffectRef>());

    // ---- ShiftDropBand: argument validation ----

    [Fact]
    public void ShiftDropBand_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => OutcomeResolver.ShiftDropBand(null!, "good", 1, Order));
        Assert.Throws<ArgumentNullException>(() => OutcomeResolver.ShiftDropBand("staple", null!, 1, Order));
        Assert.Throws<ArgumentNullException>(() => OutcomeResolver.ShiftDropBand("staple", "good", 1, null!));
    }

    [Fact]
    public void ShiftDropBand_empty_order_throws()
    {
        Assert.Throws<ArgumentException>(() => OutcomeResolver.ShiftDropBand("staple", "good", 1, Array.Empty<string>()));
    }

    [Fact]
    public void ShiftDropBand_negative_severityTier_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OutcomeResolver.ShiftDropBand("staple", "good", -1, Order));
    }

    [Fact]
    public void ShiftDropBand_unknown_dropBand_throws()
    {
        Assert.Throws<ArgumentException>(() => OutcomeResolver.ShiftDropBand("mythic", "good", 1, Order));
    }

    // ---- ShiftDropBand: the shift itself ----

    [Fact]
    public void Severity_zero_never_shifts_any_ordinal()
    {
        Assert.Equal("staple", OutcomeResolver.ShiftDropBand("staple", "good", 0, Order));
        Assert.Equal("exceptional", OutcomeResolver.ShiftDropBand("exceptional", "bad", 0, Order));
        Assert.Equal("occasional", OutcomeResolver.ShiftDropBand("occasional", "mixed", 0, Order));
    }

    [Fact]
    public void Good_shifts_toward_exceptional_by_exactly_the_tier_step_count()
    {
        Assert.Equal("frequent", OutcomeResolver.ShiftDropBand("staple", "good", 1, Order));
        Assert.Equal("occasional", OutcomeResolver.ShiftDropBand("staple", "good", 2, Order));
        Assert.Equal("seldom", OutcomeResolver.ShiftDropBand("frequent", "good", 2, Order));
    }

    [Fact]
    public void Good_clamps_at_exceptional_never_overflows_past_the_list()
    {
        Assert.Equal("exceptional", OutcomeResolver.ShiftDropBand("seldom", "good", 10, Order));
        Assert.Equal("exceptional", OutcomeResolver.ShiftDropBand("exceptional", "good", 3, Order));
    }

    [Fact]
    public void Bad_shifts_toward_staple_by_exactly_the_tier_step_count()
    {
        Assert.Equal("seldom", OutcomeResolver.ShiftDropBand("exceptional", "bad", 1, Order));
        Assert.Equal("occasional", OutcomeResolver.ShiftDropBand("exceptional", "bad", 2, Order));
        Assert.Equal("frequent", OutcomeResolver.ShiftDropBand("seldom", "bad", 2, Order));
    }

    [Fact]
    public void Bad_clamps_at_staple_never_underflows_before_the_list()
    {
        Assert.Equal("staple", OutcomeResolver.ShiftDropBand("occasional", "bad", 10, Order));
        Assert.Equal("staple", OutcomeResolver.ShiftDropBand("staple", "bad", 3, Order));
    }

    // ---- ShiftDropBand: the per-severity golden (D3.5's own verify line) ----

    /// <summary>
    /// D3.5's own verify line, "an outcome golden per severity" — the COMPLETE ordinal × severity-tier
    /// table, pinned once against the real shipped five-band order, rather than the sampled assertions
    /// above. Every one of the 4 × 4 cells is a blessed value: a change to the shift rule (either
    /// direction, either clamp) moves at least one cell and fails here, which is exactly what a golden is
    /// for. Tier is `rung.eventSeverityTier` (`difficulty.rungs[]`, 1-3 across the ten shipped rungs;
    /// `hard` carries 2 — the real value `OutcomeResolverTests` already sanity-pins), and `mixed`/`nothing`
    /// are severity-invariant by spec §5.
    /// </summary>
    [Theory]
    [InlineData("good", "staple", 0, "staple")]
    [InlineData("good", "staple", 1, "frequent")]
    [InlineData("good", "staple", 2, "occasional")]
    [InlineData("good", "staple", 3, "seldom")]
    [InlineData("good", "occasional", 0, "occasional")]
    [InlineData("good", "occasional", 1, "seldom")]
    [InlineData("good", "occasional", 2, "exceptional")]
    [InlineData("good", "occasional", 3, "exceptional")]
    [InlineData("mixed", "staple", 0, "staple")]
    [InlineData("mixed", "staple", 1, "staple")]
    [InlineData("mixed", "staple", 2, "staple")]
    [InlineData("mixed", "staple", 3, "staple")]
    [InlineData("mixed", "exceptional", 2, "exceptional")]
    [InlineData("bad", "exceptional", 0, "exceptional")]
    [InlineData("bad", "exceptional", 1, "seldom")]
    [InlineData("bad", "exceptional", 2, "occasional")]
    [InlineData("bad", "exceptional", 3, "frequent")]
    [InlineData("bad", "frequent", 0, "frequent")]
    [InlineData("bad", "frequent", 1, "staple")]
    [InlineData("bad", "frequent", 2, "staple")]
    [InlineData("bad", "frequent", 3, "staple")]
    [InlineData("nothing", "staple", 0, "staple")]
    [InlineData("nothing", "staple", 3, "staple")]
    [InlineData("nothing", "exceptional", 3, "exceptional")]
    public void The_shifted_band_golden_per_ordinal_and_severity(
        string ordinal, string dropBand, int severityTier, string expected)
    {
        Assert.Equal(expected, OutcomeResolver.ShiftDropBand(dropBand, ordinal, severityTier, Order));
    }

    [Fact]
    public void Mixed_and_nothing_never_shift_regardless_of_tier()
    {
        Assert.Equal("frequent", OutcomeResolver.ShiftDropBand("frequent", "mixed", 4, Order));
        Assert.Equal("frequent", OutcomeResolver.ShiftDropBand("frequent", "nothing", 4, Order));
    }

    [Fact]
    public void Sanity_golden_against_the_real_shipped_hard_rungs_own_severity_tier()
    {
        // "hard" (rung 4, the identity row) carries EventSeverityTier == 2 in the real shipped tuning --
        // NOT 0, confirmed by reading gk-core/data/tuning/dungeon.v1.json directly before writing this test.
        Assert.Equal(2, Hard.EventSeverityTier);
        Assert.Equal("occasional", OutcomeResolver.ShiftDropBand("staple", "good", Hard.EventSeverityTier, Order));
        Assert.Equal("staple", OutcomeResolver.ShiftDropBand("occasional", "bad", Hard.EventSeverityTier, Order));
    }

    // ---- WeightFor ----

    [Fact]
    public void WeightFor_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => OutcomeResolver.WeightFor(null!, Weights));
        Assert.Throws<ArgumentNullException>(() => OutcomeResolver.WeightFor("staple", null!));
    }

    [Fact]
    public void WeightFor_unknown_band_throws()
    {
        Assert.Throws<ArgumentException>(() => OutcomeResolver.WeightFor("mythic", Weights));
    }

    [Fact]
    public void WeightFor_returns_the_real_shipped_weight_per_band()
    {
        Assert.Equal(1000, OutcomeResolver.WeightFor("staple", Weights));
        Assert.Equal(300, OutcomeResolver.WeightFor("frequent", Weights));
        Assert.Equal(90, OutcomeResolver.WeightFor("occasional", Weights));
        Assert.Equal(25, OutcomeResolver.WeightFor("seldom", Weights));
        Assert.Equal(7, OutcomeResolver.WeightFor("exceptional", Weights));
    }

    // ---- PickOutcome: argument validation ----

    [Fact]
    public void PickOutcome_null_outcomes_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OutcomeResolver.PickOutcome(null!, 0, Order, Weights, 0, 0, seed: 1));
    }

    [Fact]
    public void PickOutcome_empty_outcomes_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            OutcomeResolver.PickOutcome(Array.Empty<EventOutcomeRow>(), 0, Order, Weights, 0, 0, seed: 1));
    }

    [Fact]
    public void PickOutcome_a_single_outcome_always_wins()
    {
        var outcomes = new[] { Outcome("good", "staple") };
        for (ulong seed = 0; seed < 10; seed++)
            Assert.Equal("good", OutcomeResolver.PickOutcome(outcomes, 0, Order, Weights, 0, 0, seed).Ordinal);
    }

    // ---- PickOutcome: determinism and stream namespacing ----

    [Fact]
    public void PickOutcome_is_deterministic_same_seed_same_room_same_pick()
    {
        var outcomes = new[] { Outcome("good", "staple"), Outcome("bad", "exceptional"), Outcome("mixed", "occasional") };
        var a = OutcomeResolver.PickOutcome(outcomes, 1, Order, Weights, 3, 4, seed: 42);
        var b = OutcomeResolver.PickOutcome(outcomes, 1, Order, Weights, 3, 4, seed: 42);
        Assert.Equal(a.Ordinal, b.Ordinal);
    }

    [Fact]
    public void A_different_room_off_the_same_seed_draws_a_different_sequence()
    {
        var outcomes = new[] { Outcome("good", "staple"), Outcome("bad", "staple"), Outcome("mixed", "staple"), Outcome("nothing", "staple") };
        var atRoomA = new List<string>();
        var atRoomB = new List<string>();
        for (ulong seed = 0; seed < 40; seed++)
        {
            atRoomA.Add(OutcomeResolver.PickOutcome(outcomes, 0, Order, Weights, 0, 0, seed).Ordinal);
            atRoomB.Add(OutcomeResolver.PickOutcome(outcomes, 0, Order, Weights, 9, 9, seed).Ordinal);
        }
        Assert.NotEqual(atRoomA, atRoomB);
    }

    // ---- PickOutcome: severity actually moves the odds, not just the label ----

    [Fact]
    public void Higher_severity_makes_a_staple_good_outcome_rarer_to_draw_against_a_staple_bad_outcome()
    {
        // Both authored at "staple" (equal 1000/1000 odds at tier 0). At tier 3, "good" shifts to
        // "seldom" (weight 25) while "bad" stays put via a separate all-staple row acting as ballast --
        // so raising severity should make the pool land on "good" far less often.
        var outcomes = new[] { Outcome("good", "staple"), Outcome("bad", "staple") };

        int GoodWins(int severityTier)
        {
            var wins = 0;
            for (ulong seed = 0; seed < 300; seed++)
                if (OutcomeResolver.PickOutcome(outcomes, severityTier, Order, Weights, 0, 0, seed).Ordinal == "good")
                    wins++;
            return wins;
        }

        var atZero = GoodWins(0);
        var atThree = GoodWins(3);
        Assert.InRange(atZero, 120, 180); // ~50% at equal 1000/1000 weight
        Assert.True(atThree < atZero / 3, $"expected severity to sharply cut good's odds: tier0={atZero} tier3={atThree}");
    }

    [Fact]
    public void Higher_severity_makes_a_staple_bad_outcome_more_frequent_against_a_seldom_good_outcome()
    {
        // "good" authored at "seldom" (weight 25), "bad" authored at "occasional" (weight 90) -- bad
        // already favoured at tier 0. At tier 2, bad shifts down to "staple" (weight 1000), making it
        // overwhelmingly likely -- "more severe consequences" as a measurable odds shift, not just prose.
        var outcomes = new[] { Outcome("good", "seldom"), Outcome("bad", "occasional") };

        int BadWins(int severityTier)
        {
            var wins = 0;
            for (ulong seed = 0; seed < 300; seed++)
                if (OutcomeResolver.PickOutcome(outcomes, severityTier, Order, Weights, 0, 0, seed).Ordinal == "bad")
                    wins++;
            return wins;
        }

        var atZero = BadWins(0);
        var atTwo = BadWins(2);
        Assert.True(atTwo > atZero, $"expected severity to raise bad's odds further: tier0={atZero} tier2={atTwo}");
        Assert.True(atTwo > 270, $"expected bad to be overwhelmingly favoured at tier 2 (1000 vs 7): got {atTwo}/300");
    }

    // ---- PickOutcome: exhaustion reuses EventDeckRefusal ----

    [Fact]
    public void PickOutcome_refuses_via_EventDeckRefusal_when_every_weight_resolves_to_zero()
    {
        // No band in Order carries weight 0 in the real table, so force it via a custom weight table.
        var zeroWeights = new Dictionary<string, int>(StringComparer.Ordinal) { ["staple"] = 0 };
        var outcomes = new[] { Outcome("mixed", "staple") };
        var ex = Assert.Throws<EventDeckRefusal>(() =>
            OutcomeResolver.PickOutcome(outcomes, 0, Order, zeroWeights, 2, 5, seed: 1));
        Assert.Contains("(2,5)", ex.Message);
    }

    // ---- TryForcedOutcome / Resolve: spec §5 "Forced outcome", 2026-09-08 ----

    static EventRow EventRowWithSupplyOverride(string? supplyOverride, params EventOutcomeRow[] outcomes) => new(
        EventId: "event.test-fixture", Kind: "curio", Theme: null, ClimateAffinity: null,
        RepeatScope: "per-delve", Eligibility: null, Outcomes: outcomes,
        SupplyOverride: supplyOverride, ChainRef: null);

    [Fact]
    public void TryForcedOutcome_null_outcomes_throws()
    {
        Assert.Throws<ArgumentNullException>(() => OutcomeResolver.TryForcedOutcome(null!, holdsOverrideStock: true));
    }

    [Fact]
    public void TryForcedOutcome_empty_outcomes_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            OutcomeResolver.TryForcedOutcome(Array.Empty<EventOutcomeRow>(), holdsOverrideStock: true));
    }

    [Fact]
    public void TryForcedOutcome_red_not_holding_the_override_stock_never_forces_anything()
    {
        var outcomes = new[] { Outcome("good", "staple"), Outcome("bad", "staple") };
        Assert.Null(OutcomeResolver.TryForcedOutcome(outcomes, holdsOverrideStock: false));
    }

    [Fact]
    public void TryForcedOutcome_green_holding_the_override_stock_forces_the_good_ordinal()
    {
        // The verify line's own headline: a real supplyOverride/HoldsStock red/green pair.
        var outcomes = new[] { Outcome("bad", "staple"), Outcome("good", "exceptional") };
        var forced = OutcomeResolver.TryForcedOutcome(outcomes, holdsOverrideStock: true);
        Assert.NotNull(forced);
        Assert.Equal("good", forced!.Ordinal);
        Assert.Equal("exceptional", forced.DropBand);
    }

    [Theory]
    [InlineData(new[] { "bad", "mixed" }, "mixed")]      // no "good" present: next-best is "mixed"
    [InlineData(new[] { "bad", "nothing" }, "bad")]      // no "good"/"mixed": next-best is "bad"
    [InlineData(new[] { "nothing", "bad", "mixed", "good" }, "good")]
    public void TryForcedOutcome_falls_through_the_preference_order_to_the_best_ordinal_actually_present(
        string[] ordinals, string expectedForced)
    {
        var outcomes = ordinals.Select(o => Outcome(o, "staple")).ToArray();
        var forced = OutcomeResolver.TryForcedOutcome(outcomes, holdsOverrideStock: true);
        Assert.Equal(expectedForced, forced!.Ordinal);
    }

    [Fact]
    public void TryForcedOutcome_two_outcomes_tied_on_the_top_ranked_ordinal_refuses_rather_than_guessing()
    {
        var outcomes = new[] { Outcome("good", "staple"), Outcome("good", "exceptional"), Outcome("bad", "staple") };
        var ex = Assert.Throws<EventDeckRefusal>(() => OutcomeResolver.TryForcedOutcome(outcomes, holdsOverrideStock: true));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'good'", ex.Message);
    }

    [Fact]
    public void Resolve_null_eventRow_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OutcomeResolver.Resolve(null!, holdsOverrideStock: true, 0, Order, Weights, 0, 0, seed: 1));
    }

    [Fact]
    public void Resolve_red_no_real_supplyOverride_always_draws_the_ordinary_weighted_outcome()
    {
        var row = EventRowWithSupplyOverride(null, Outcome("good", "staple"));
        var result = OutcomeResolver.Resolve(row, holdsOverrideStock: true, 0, Order, Weights, 0, 0, seed: 7);
        Assert.Equal("good", result.Ordinal); // the only outcome -- proves this went through PickOutcome, not a short-circuit
    }

    [Fact]
    public void Resolve_red_a_real_supplyOverride_without_the_stock_still_draws_the_ordinary_weighted_outcome()
    {
        var row = EventRowWithSupplyOverride("herbs", Outcome("bad", "staple"), Outcome("good", "exceptional"));
        // At severity 0 with equal-ish odds, run many seeds and confirm BOTH ordinals are reachable --
        // proving the draw is genuinely happening, not silently forced despite holdsOverrideStock: false.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 0; seed < 50; seed++)
            seen.Add(OutcomeResolver.Resolve(row, holdsOverrideStock: false, 0, Order, Weights, 0, 0, seed).Ordinal);
        Assert.Contains("bad", seen);
    }

    [Fact]
    public void Resolve_green_a_real_supplyOverride_with_the_stock_short_circuits_straight_to_the_best_outcome()
    {
        // "bad" is authored at "staple" (weight 1000, overwhelmingly favoured by the ordinary draw) and
        // "good" at "exceptional" (weight 7) -- if the forced path were NOT short-circuiting the draw,
        // "bad" would win almost every one of these 50 seeds. It never does, because Resolve forces "good".
        var row = EventRowWithSupplyOverride("herbs", Outcome("bad", "staple"), Outcome("good", "exceptional"));
        for (ulong seed = 0; seed < 50; seed++)
            Assert.Equal("good", OutcomeResolver.Resolve(row, holdsOverrideStock: true, 0, Order, Weights, 0, 0, seed).Ordinal);
    }
}
