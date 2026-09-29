using FusionRpg.Core.Delve.Events;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.3's low-level draw (spec-event-deck.md §3): "which event wins" over an already-filtered
/// pool. Mirrors `SlotFillTests.cs`'s own template for the identical `DeriveStream` -&gt; `NextULong`
/// -&gt; `WeightedChoice.Pick` two-level pattern -- determinism, per-room stream namespacing, and a
/// sampled weighting-bias band rather than a pinned RNG output.</summary>
public class EventDrawTests
{
    static EventOutcomeRow Outcome() => new("good", "staple", "none", Array.Empty<EventEffectRef>());

    static EventRow Row(string id, string? climateAffinity) => new(
        EventId: id,
        Kind: "curio",
        Theme: null,
        ClimateAffinity: climateAffinity,
        RepeatScope: "per-delve",
        Eligibility: null,
        Outcomes: new[] { Outcome(), Outcome() },
        SupplyOverride: null,
        ChainRef: null);

    // ---- WeightMilliFor ----

    [Fact]
    public void WeightMilliFor_null_row_throws()
    {
        Assert.Throws<ArgumentNullException>(() => EventDraw.WeightMilliFor(null!, "fire", 3000, 1000, 300));
    }

    [Fact]
    public void WeightMilliFor_climate_blind_row_returns_noneMilli_regardless_of_room_climate()
    {
        var row = Row("e1", climateAffinity: null);
        Assert.Equal(1000, EventDraw.WeightMilliFor(row, "fire", matchMilli: 3000, noneMilli: 1000, offMilli: 300));
        Assert.Equal(1000, EventDraw.WeightMilliFor(row, null, matchMilli: 3000, noneMilli: 1000, offMilli: 300));
    }

    [Fact]
    public void WeightMilliFor_matching_affinity_returns_matchMilli()
    {
        var row = Row("e1", climateAffinity: "fire");
        Assert.Equal(3000, EventDraw.WeightMilliFor(row, "fire", matchMilli: 3000, noneMilli: 1000, offMilli: 300));
    }

    [Fact]
    public void WeightMilliFor_mismatched_affinity_returns_offMilli()
    {
        var row = Row("e1", climateAffinity: "fire");
        Assert.Equal(300, EventDraw.WeightMilliFor(row, "ice", matchMilli: 3000, noneMilli: 1000, offMilli: 300));
    }

    [Fact]
    public void WeightMilliFor_affinity_match_is_ordinal_case_sensitive()
    {
        // "Fire" vs "fire" -- a real content-authoring trap, not a hypothetical: this documents that
        // a mismatched case reads as a MISS (offMilli), never a match, so seed content and room
        // climate ids must agree on casing exactly.
        var row = Row("e1", climateAffinity: "Fire");
        Assert.Equal(300, EventDraw.WeightMilliFor(row, "fire", matchMilli: 3000, noneMilli: 1000, offMilli: 300));
    }

    // ---- stream naming: DelveStreams is the one owner (N13), EventDraw must never re-derive it ----

    [Fact]
    public void PickEvent_draws_on_the_DelveStreams_owned_room_stream_not_a_private_copy()
    {
        // Regression for a real defect: EventDraw used to carry its own private `RootStream` method
        // re-deriving "dungeon:event:{r}:{c}" — a second owner of a name `DelveStreams.Event` (the
        // real, single, cited authority, `DelveStreams.cs`) already owns, exactly the N13 pattern
        // ("the same number/name under a second owner") this codebase has already been burned by
        // once. Proven here as an outcome, not a string match on private internals: a pool with two
        // candidates draws identically whether keyed by `DelveStreams.Event(r,c) + ":pick"` computed
        // here in the test or by `PickEvent` itself -- if `PickEvent` ever silently reverts to a
        // private stream name, this equality breaks.
        var pool = new[] { Row("e1", "fire"), Row("e2", "ice") };
        var expectedStreamName = FusionRpg.Core.Delve.Roll.DelveStreams.Event(3, 4) + ":pick";
        var expectedStream = FusionRpg.Core.Battle.SeededRng.DeriveStream(42, expectedStreamName);
        var expectedRollSeed = unchecked((long)expectedStream.NextULong());
        var options = pool.Select(e => new FusionRpg.Core.Actions.Seeding.WeightedOption<EventRow>(e,
            (int)EventDraw.WeightMilliFor(e, "fire", 3000, 1000, 300))).ToList();
        var expected = FusionRpg.Core.Actions.Seeding.WeightedChoice.Pick(options, expectedRollSeed, expectedStreamName);

        var actual = EventDraw.PickEvent(pool, 3, 4, "fire", 3000, 1000, 300, seed: 42);
        Assert.Equal(expected.EventId, actual.EventId);
    }

    // ---- PickEvent: shape and validation ----

    [Fact]
    public void PickEvent_null_pool_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            EventDraw.PickEvent(null!, 0, 0, "fire", 3000, 1000, 300, seed: 1));
    }

    [Fact]
    public void PickEvent_empty_pool_throws_EventDeckRefusal_naming_the_room()
    {
        var ex = Assert.Throws<EventDeckRefusal>(() =>
            EventDraw.PickEvent(Array.Empty<EventRow>(), 1, 2, "fire", 3000, 1000, 300, seed: 1));
        Assert.Contains("(1,2)", ex.Message);
    }

    [Fact]
    public void PickEvent_all_zero_weight_pool_throws_EventDeckRefusal()
    {
        // Every candidate climate-blind, and noneMilli itself is 0 -- WeightedChoice sees weight <= 0
        // for every option and refuses, rewrapped here as the deck's own exception.
        var pool = new[] { Row("e1", null), Row("e2", null) };
        var ex = Assert.Throws<EventDeckRefusal>(() =>
            EventDraw.PickEvent(pool, 0, 0, "fire", climateAffinityMatchMilli: 3000, climateAffinityNoneMilli: 0, climateAffinityOffMilli: 0, seed: 1));
        Assert.Contains("(0,0)", ex.Message);
    }

    [Fact]
    public void A_malformed_milli_value_beyond_int_range_throws_via_the_checked_cast()
    {
        // A genuinely malformed tuning value (a `long` milli past int.MaxValue) throws rather than
        // wrapping -- the repo's own "overflow throws, never wraps" rule. Not a defensive check aimed
        // at real play (every shipped *Milli value is small); the assertion is that it fails LOUD.
        var pool = new[] { Row("e1", "fire") };
        Assert.Throws<OverflowException>(() =>
            EventDraw.PickEvent(pool, 0, 0, "fire", climateAffinityMatchMilli: (long)int.MaxValue + 1, climateAffinityNoneMilli: 1000, climateAffinityOffMilli: 300, seed: 1));
    }

    // ---- PickEvent: determinism ----

    [Fact]
    public void PickEvent_is_deterministic_same_seed_same_room_same_pick()
    {
        var pool = new[] { Row("e1", "fire"), Row("e2", "ice"), Row("e3", null) };
        var a = EventDraw.PickEvent(pool, 3, 4, "fire", 3000, 1000, 300, seed: 777);
        var b = EventDraw.PickEvent(pool, 3, 4, "fire", 3000, 1000, 300, seed: 777);
        Assert.Equal(a.EventId, b.EventId);
    }

    [Fact]
    public void PickEvent_is_deterministic_over_256_seeds()
    {
        // Verify line (todo D3.3): "determinism over 256 seeds" -- proven here as repeatability (same
        // seed twice agrees), not as a fixed golden sequence, since the pool/tuning here are this
        // test's own fixture rather than a shipped seed corpus (no seed-import wiring for events yet).
        var pool = new[] { Row("e1", "fire"), Row("e2", "ice"), Row("e3", null), Row("e4", "fire") };
        for (ulong seed = 0; seed < 256; seed++)
        {
            var a = EventDraw.PickEvent(pool, 1, 1, "fire", 3000, 1000, 300, seed);
            var b = EventDraw.PickEvent(pool, 1, 1, "fire", 3000, 1000, 300, seed);
            Assert.Equal(a.EventId, b.EventId);
        }
    }

    [Fact]
    public void A_different_room_off_the_same_seed_draws_a_different_sequence()
    {
        // Same shape as SlotFillTests' own "different stream name" test, generalized to a sequence of
        // single-pick draws across many seeds: if (row,col) did not actually namespace the stream,
        // these two sequences would be identical for every seed.
        var pool = Enumerable.Range(0, 6).Select(i => Row($"e{i}", i % 2 == 0 ? "fire" : "ice")).ToArray();
        var picksAtRoomA = new List<string>();
        var picksAtRoomB = new List<string>();
        for (ulong seed = 0; seed < 50; seed++)
        {
            picksAtRoomA.Add(EventDraw.PickEvent(pool, 0, 0, "fire", 3000, 1000, 300, seed).EventId);
            picksAtRoomB.Add(EventDraw.PickEvent(pool, 9, 9, "fire", 3000, 1000, 300, seed).EventId);
        }
        Assert.NotEqual(picksAtRoomA, picksAtRoomB);
    }

    // ---- PickEvent: weighting bias ----

    [Fact]
    public void Matching_climate_is_favoured_but_off_climate_still_wins_sometimes()
    {
        // matchMilli 3000 vs offMilli 300 -- match should win roughly 3000/3300 ~ 90.9% of single
        // draws. Sampled over many independent seeds, per SlotFillTests' own precedent, rather than
        // asserted as an exact ratio (that would pin the RNG's internals, not the weighting law).
        var pool = new[] { Row("hot", "fire"), Row("cold", "ice") };
        var hotWins = 0;
        const int trials = 400;
        for (ulong seed = 0; seed < trials; seed++)
        {
            var pick = EventDraw.PickEvent(pool, 0, 0, "fire", climateAffinityMatchMilli: 3000, climateAffinityNoneMilli: 1000, climateAffinityOffMilli: 300, seed);
            if (pick.EventId == "hot") hotWins++;
        }
        Assert.InRange(hotWins, (int)(trials * 0.80), (int)(trials * 0.98));
    }

    [Fact]
    public void Climate_blind_event_uses_noneMilli_regardless_of_room_climate()
    {
        // A climate-blind row (null affinity) against an off-climate row: noneMilli 1000 vs offMilli
        // 300 favours the blind row roughly 1000/1300 ~ 76.9% of single draws.
        var pool = new[] { Row("blind", null), Row("off", "ice") };
        var blindWins = 0;
        const int trials = 400;
        for (ulong seed = 0; seed < trials; seed++)
        {
            var pick = EventDraw.PickEvent(pool, 0, 0, "fire", climateAffinityMatchMilli: 3000, climateAffinityNoneMilli: 1000, climateAffinityOffMilli: 300, seed);
            if (pick.EventId == "blind") blindWins++;
        }
        Assert.InRange(blindWins, (int)(trials * 0.60), (int)(trials * 0.90));
    }
}
