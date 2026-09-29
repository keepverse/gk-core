using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>D2.2 (spec-encounter-generator.md §2 steps 3-4) — `SlotFill`: count plus a weighted draw
/// with the same-species cap, on named streams.</summary>
public class SlotFillTests
{
    static ConcreteAnchor Anchor(string id, ElementTypeId element) => new()
    {
        SpeciesId = id, ThreatBand = "raider", ThreatRung = 4, AptitudePrimary = "Might",
        Reach = EncounterReach.Short, TargetPreference = TargetPreference.Frontline, ElementPrimary = element,
    };

    // ---- Count ----

    [Fact]
    public void Count_stays_within_the_inclusive_band_across_many_seeds()
    {
        var band = new SlotCountBandTuning(Min: 2, Max: 3);
        for (ulong seed = 0; seed < 200; seed++)
        {
            var n = SlotFill.Count(SeededRng.DeriveStream(seed, "test"), band, delta: 0);
            Assert.InRange(n, 2, 3);
        }
    }

    [Fact]
    public void Count_applies_the_rung_delta_additively()
    {
        var band = new SlotCountBandTuning(Min: 2, Max: 2); // pinned -- delta is the only variable left
        var n = SlotFill.Count(SeededRng.DeriveStream(1, "test"), band, delta: 3);
        Assert.Equal(5, n);
    }

    [Fact]
    public void Count_can_go_negative_when_the_delta_is_negative_the_caller_refuses_it_not_this()
    {
        var band = new SlotCountBandTuning(Min: 1, Max: 1);
        var n = SlotFill.Count(SeededRng.DeriveStream(1, "test"), band, delta: -5);
        Assert.Equal(-4, n); // SlotFill itself never refuses -- confirms the doc comment's own claim
    }

    [Fact]
    public void Count_is_deterministic_same_stream_same_result()
    {
        var band = new SlotCountBandTuning(Min: 1, Max: 7);
        var a = SlotFill.Count(SeededRng.DeriveStream(42, "dungeon:encounter:0:0:slot:0:count"), band, 0);
        var b = SlotFill.Count(SeededRng.DeriveStream(42, "dungeon:encounter:0:0:slot:0:count"), band, 0);
        Assert.Equal(a, b);
    }

    // ---- Draw: shape ----

    [Fact]
    public void Draw_returns_exactly_n_picks()
    {
        var corpus = new[] { Anchor("a", ElementTypeId.Fire), Anchor("b", ElementTypeId.Fire), Anchor("c", ElementTypeId.Fire) };
        var result = SlotFill.Draw(corpus, n: 5, climate: ElementTypeId.Fire, offClimateMilli: 400,
            sameSpeciesMaxMilli: 1000, seed: 7, streamName: "dungeon:encounter:0:0:slot:0");
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Draw_is_deterministic_same_seed_same_stream_same_sequence()
    {
        var corpus = new[] { Anchor("a", ElementTypeId.Fire), Anchor("b", ElementTypeId.Ice), Anchor("c", ElementTypeId.Earth) };
        var first = SlotFill.Draw(corpus, 6, ElementTypeId.Fire, 400, 500, 99, "s");
        var second = SlotFill.Draw(corpus, 6, ElementTypeId.Fire, 400, 500, 99, "s");
        Assert.Equal(first.Select(a => a.SpeciesId), second.Select(a => a.SpeciesId));
    }

    [Fact]
    public void A_different_stream_name_off_the_same_seed_can_draw_differently()
    {
        // Not asserting a SPECIFIC different outcome (that would be asserting on RNG internals) --
        // asserting that the stream name actually namespaces the draw, per the class's own doc
        // comment ("an extra draw in one never shifts another"). If this ever starts flaking, the
        // stream-derivation is not actually independent per name, which is the real defect to chase.
        var corpus = Enumerable.Range(0, 6).Select(i => Anchor($"s{i}", ElementTypeId.Fire)).ToArray();
        var a = SlotFill.Draw(corpus, 6, ElementTypeId.Fire, 400, 1000, 7, "stream-a");
        var b = SlotFill.Draw(corpus, 6, ElementTypeId.Fire, 400, 1000, 7, "stream-b");
        Assert.NotEqual(a.Select(x => x.SpeciesId), b.Select(x => x.SpeciesId));
    }

    // ---- Draw: the same-species cap ----

    [Fact]
    public void The_same_species_cap_is_never_exceeded()
    {
        // Only 2 distinct species, drawing 10 -- with a 500‰ cap, ceil(10*500/1000) = 5 seats each,
        // exactly enough for 2*5=10. Proves the cap partitions the draw rather than starving it.
        var corpus = new[] { Anchor("a", ElementTypeId.Fire), Anchor("b", ElementTypeId.Fire) };
        var result = SlotFill.Draw(corpus, n: 10, climate: ElementTypeId.Fire, offClimateMilli: 400,
            sameSpeciesMaxMilli: 500, seed: 3, streamName: "cap-test");

        var counts = result.GroupBy(a => a.SpeciesId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, counts.Count); // both species had to be used
        Assert.All(counts.Values, c => Assert.True(c <= 5, $"a species held {c} seats, cap was 5"));
    }

    [Fact]
    public void A_single_species_corpus_is_capped_even_below_1000_milli_via_the_ceiling()
    {
        // ceil(3 * 999/1000) = 3 -- rounds UP, so a near-1000 cap on a lone species still admits all 3.
        var corpus = new[] { Anchor("only", ElementTypeId.Fire) };
        var result = SlotFill.Draw(corpus, n: 3, climate: ElementTypeId.Fire, offClimateMilli: 400,
            sameSpeciesMaxMilli: 999, seed: 1, streamName: "single");
        Assert.Equal(3, result.Count);
        Assert.All(result, a => Assert.Equal("only", a.SpeciesId));
    }

    [Fact]
    public void Exhausting_every_species_under_the_cap_throws_EncounterFillExhausted()
    {
        // 1 species, cap 300‰ of 4 = ceil(1.2) = 2 seats -- but n=4 needs 4. Draws 1,2 succeed, the
        // 3rd pick has nothing left to draw from.
        var corpus = new[] { Anchor("only", ElementTypeId.Fire) };

        var ex = Assert.Throws<EncounterFillExhausted>(() =>
            SlotFill.Draw(corpus, n: 4, climate: ElementTypeId.Fire, offClimateMilli: 400,
                sameSpeciesMaxMilli: 300, seed: 1, streamName: "exhaust-test"));
        Assert.Contains("cap", ex.Message);
    }

    // ---- Draw: argument validation ----

    [Fact]
    public void Draw_rejects_empty_candidates_and_n_below_1()
    {
        var corpus = new[] { Anchor("a", ElementTypeId.Fire) };
        Assert.Throws<ArgumentNullException>(() => SlotFill.Draw(null!, 1, null, 0, 1000, 0, "s"));
        Assert.Throws<ArgumentException>(() => SlotFill.Draw(Array.Empty<ConcreteAnchor>(), 1, null, 0, 1000, 0, "s"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlotFill.Draw(corpus, 0, null, 0, 1000, 0, "s"));
    }

    // ---- Draw: element weighting biases the pick, does not filter it ----

    [Fact]
    public void OnClimate_candidates_are_favoured_but_offClimate_ones_still_win_sometimes()
    {
        // Weight 1000 vs 400: on-climate should win roughly 1000/1400 ~ 71% of single draws.
        // Sampled over many independent seeds/streams rather than asserted as an exact ratio.
        var corpus = new[] { Anchor("hot", ElementTypeId.Fire), Anchor("cold", ElementTypeId.Ice) };
        var hotWins = 0;
        const int trials = 300;
        for (ulong seed = 0; seed < trials; seed++)
        {
            var pick = SlotFill.Draw(corpus, 1, ElementTypeId.Fire, 400, 1000, seed, "weight-test:" + seed);
            if (pick[0].SpeciesId == "hot") hotWins++;
        }
        // 1000/1400 = 71.4%; a generous band around it proves the bias exists without pinning the RNG's exact output.
        Assert.InRange(hotWins, (int)(trials * 0.55), (int)(trials * 0.90));
    }
}
