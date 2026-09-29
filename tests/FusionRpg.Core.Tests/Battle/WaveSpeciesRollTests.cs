using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// species-gear-chain T6 (spec-wave-species-roll.md Testing strategy) against synthetic rosters —
/// no shipped species is named, no population count asserted. The roster is a population that
/// grows; the contract (seeded, filtered, windowed) is closed and pinned.
/// </summary>
public class WaveSpeciesRollTests
{
    readonly ITestOutputHelper _out;

    public WaveSpeciesRollTests(ITestOutputHelper output) => _out = output;

    static int _id;
    static CreatureSpeciesDef Species(
        string speciesId, CreatureRarity rarity,
        CreatureAcquisition acquisition = CreatureAcquisition.Summonable) =>
        new()
        {
            SpeciesId = speciesId,
            BaseRarity = rarity,
            Acquisition = acquisition,
            CreatureTypeId = 10000 + (_id++),
            ElementPrimary = ElementTypeId.Fire,
        };

    static WavePick Pick(
        List<CreatureSpeciesDef> pool, int count, long capMilli = 0) =>
        new(pool, count,
            pool.Select(s => s.BaseRarity).Distinct()
                .ToDictionary(r => r.ToId(), _ => 100),
            capMilli);

    static List<CreatureSpeciesDef> Pool(params (string Id, CreatureRarity Rarity, CreatureAcquisition Acq)[] rows) =>
        rows.Select(r => Species(r.Id, r.Rarity, r.Acq)).ToList();

    // ── determinism ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_wave_reproduces_the_same_species_twice()
    {
        var pool = Pool(("b", CreatureRarity.Fused, CreatureAcquisition.Summonable),
            ("a", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
            ("c", CreatureRarity.Heirloom, CreatureAcquisition.Summonable));
        var first = WaveCatalog.Enemies(1, "w", Pick(pool, 3)).Select(e => e.SpeciesId).ToList();
        var second = WaveCatalog.Enemies(1, "w", Pick(pool, 3)).Select(e => e.SpeciesId).ToList();
        Assert.Equal(first, second);
    }

    [Fact]
    public void The_draw_is_stable_across_a_shuffled_pool_order()
    {
        // The ordinal sort is the determinism substrate: a differently-ordered input pool must
        // not move the draw, on any platform's hash order.
        var ordered = Pool(("a", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
            ("b", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
            ("c", CreatureRarity.Chaff, CreatureAcquisition.Summonable));
        var shuffled = ordered.AsEnumerable().Reverse().ToList();
        var fromOrdered = WaveCatalog.Enemies(1, "w", Pick(ordered, 3)).Select(e => e.SpeciesId).ToList();
        var fromShuffled = WaveCatalog.Enemies(1, "w", Pick(shuffled, 3)).Select(e => e.SpeciesId).ToList();
        Assert.Equal(fromOrdered, fromShuffled);
    }

    // ── the acquisition filter is a RULE ─────────────────────────────────────────────

    [Fact]
    public void A_synthetic_summonable_eventonly_species_is_refused()
    {
        // Zero shipped species carry both flags — without this synthetic case the filter would be
        // EventOnly-safe by population luck, not by rule.
        var pool = WaveCatalog.Band(CreatureRarity.Chaff, CreatureRarity.Almanac,
            Pool(("zzz", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
                ("mmm", CreatureRarity.Chaff, CreatureAcquisition.Summonable | CreatureAcquisition.EventOnly)));
        Assert.DoesNotContain(pool, s => s.SpeciesId == "mmm");
        Assert.Contains(pool, s => s.SpeciesId == "zzz");
    }

    [Fact]
    public void An_eventonly_species_first_alphabetically_is_still_absent()
    {
        var pool = WaveCatalog.Band(CreatureRarity.Chaff, CreatureRarity.Almanac,
            Pool(("aaa-event", CreatureRarity.Chaff, CreatureAcquisition.EventOnly),
                ("zzz", CreatureRarity.Fused, CreatureAcquisition.Summonable)));
        var drawn = WaveCatalog.Enemies(1, "w", Pick(pool, 1)).Select(e => e.SpeciesId).ToList();
        Assert.DoesNotContain("aaa-event", drawn);
    }

    [Fact]
    public void Captureonly_is_refused_in_a_wave_but_summonable_captureonly_is_admitted()
    {
        var pool = WaveCatalog.Band(CreatureRarity.Chaff, CreatureRarity.Almanac,
            Pool(("cap", CreatureRarity.Chaff, CreatureAcquisition.CaptureOnly),
                ("both", CreatureRarity.Chaff, CreatureAcquisition.Summonable | CreatureAcquisition.CaptureOnly),
                ("ord", CreatureRarity.Chaff, CreatureAcquisition.Summonable)));
        Assert.DoesNotContain(pool, s => s.SpeciesId == "cap");
        Assert.Contains(pool, s => s.SpeciesId == "both");
    }

    // ── draw shape ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_replacement_holds_within_a_pick_when_the_cap_is_zero()
    {
        var pool = Pool(("a", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
            ("b", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
            ("c", CreatureRarity.Chaff, CreatureAcquisition.Summonable));
        var drawn = WaveCatalog.Enemies(1, "w", Pick(pool, 3, capMilli: 0)).Select(e => e.SpeciesId).ToList();
        Assert.Equal(3, drawn.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_window_covering_an_empty_rung_draws_from_the_rest()
    {
        // Only Chaff is occupied; the window spans Chaff..Fused with weight on both.
        var pool = WaveCatalog.Band(CreatureRarity.Chaff, CreatureRarity.Fused,
            Pool(("a", CreatureRarity.Chaff, CreatureAcquisition.Summonable),
                ("b", CreatureRarity.Chaff, CreatureAcquisition.Summonable)));
        var weights = new Dictionary<string, int>(StringComparer.Ordinal)
            { ["chaff"] = 100, ["sprout"] = 100, ["grafted"] = 100, ["cultivated"] = 100, ["fused"] = 100 };
        var drawn = WaveCatalog.Enemies(1, "w", new WavePick(pool, 2, weights, 0));
        Assert.Equal(2, drawn.Count);
    }

    [Fact]
    public void More_draws_than_distinct_species_is_a_content_error_not_a_silent_repeat()
    {
        var pool = Pool(("a", CreatureRarity.Chaff, CreatureAcquisition.Summonable));
        var ex = Assert.Throws<WaveCatalogRejection>(() => WaveCatalog.Enemies(1, "w", Pick(pool, 2, capMilli: 0)));
        Assert.Contains("w", ex.Message);
    }

    // ── tuning contract ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_loader_refuses_an_unknown_rung_id()
    {
        var ex = Assert.Throws<WaveCatalogRejection>(() => WaveCatalogLoader.Parse(
            """{"waves": [{"waveId": "x", "name": "X", "contentIndex": 1, "picks": [{"rarityFrom": "mythic", "rarityTo": "chaff", "count": 1, "sameSpeciesMaxMilli": 0, "weights": {"mythic": 100}}]}]}"""));
        Assert.Contains("mythic", ex.Message);
    }

    [Fact]
    public void The_loader_refuses_an_inverted_window()
    {
        var ex = Assert.Throws<WaveCatalogRejection>(() => WaveCatalogLoader.Parse(
            """{"waves": [{"waveId": "x", "name": "X", "contentIndex": 1, "picks": [{"rarityFrom": "fused", "rarityTo": "chaff", "count": 1, "sameSpeciesMaxMilli": 0, "weights": {"chaff": 100, "fused": 100}}]}]}"""));
        Assert.Contains("inverted", ex.Message);
    }

    // ── rank floor (spec-species-rank.md §6, creature-seed Task 11) ─────────────────────

    [Fact]
    public void At_the_shipped_bottom_floor_a_skipped_rank_is_admitted_and_nothing_narrows()
    {
        // The compiled floors sit at the bottom rung, which admits every rung AND a skipped rank — so
        // the band is exactly what the window plus the admission rule already produced. That is also
        // why every other test in this file still passes unchanged: all of its synthetic species carry
        // no rank at all.
        var source = new[]
        {
            Species("a", CreatureRarity.Chaff),
            Species("b", CreatureRarity.Chaff) with { Rank = CreatureRank.Chaff },
            Species("c", CreatureRarity.Fused) with { Rank = CreatureRank.Cultivated },
        };

        var pool = WaveCatalog.Band(CreatureRarity.Chaff, CreatureRarity.Almanac, source);

        Assert.Equal(new[] { "a", "b", "c" }, pool.Select(s => s.SpeciesId));
    }

    [Fact]
    public void A_raised_wave_band_floor_excludes_below_floor_and_skipped_ranks_only()
    {
        var source = new[]
        {
            Species("below", CreatureRarity.Chaff) with { Rank = CreatureRank.Chaff },
            Species("skipped", CreatureRarity.Chaff),
            Species("at", CreatureRarity.Chaff) with { Rank = CreatureRank.Heirloom },
            Species("above", CreatureRarity.Chaff) with { Rank = CreatureRank.Almanac },
        };

        WithFloorsAt(CreatureRank.Heirloom, () =>
        {
            var pool = WaveCatalog.Band(CreatureRarity.Chaff, CreatureRarity.Almanac, source);
            // `skipped` is dropped too: a null rank maps to the BOTTOM rung at the gate, which is
            // below a raised floor — never silently admitted as if it were the top rung. Order is the
            // band's own SpeciesId ordinal ("above" < "at").
            Assert.Equal(new[] { "above", "at" }, pool.Select(s => s.SpeciesId));
        });
    }

    [Fact]
    public void A_raised_floor_never_adds_an_acquisition_rule_of_its_own()
    {
        // Rank can only narrow. The acquisition rule stays exactly `CreatureAdmission.ForWave`'s, at a
        // raised floor as much as at the bottom: a CaptureOnly species is refused on the same terms as
        // before (and an EventOnly one too), NOT because of rank — each of these carries the top rank.
        var source = new[]
        {
            Species("capture", CreatureRarity.Chaff, CreatureAcquisition.CaptureOnly)
                with { Rank = CreatureRank.Almanac },
            Species("event", CreatureRarity.Chaff, CreatureAcquisition.EventOnly)
                with { Rank = CreatureRank.Almanac },
            Species("plain", CreatureRarity.Chaff) with { Rank = CreatureRank.Almanac },
        };

        WithFloorsAt(CreatureRank.Almanac, () =>
        {
            var pool = WaveCatalog.Band(CreatureRarity.Chaff, CreatureRarity.Almanac, source);
            Assert.Equal(new[] { "plain" }, pool.Select(s => s.SpeciesId));
        });
    }

    /// <summary>Configure every gate at <paramref name="floor"/>, run the body, then put the policy back
    /// to unconfigured (behaviourally the shipped bottom rung), so no later test inherits a raised gate.</summary>
    static void WithFloorsAt(CreatureRank floor, Action body)
    {
        CreatureRankFloors.Configure(new CreatureRankTuning(
            1, CreatureRarityLadder.All.Select(r => r.ToId()).ToList(), Array.Empty<CreatureRankCell>(),
            CreatureRankFloors.DeclaredGates.ToDictionary(g => g, _ => floor.ToId(), StringComparer.Ordinal)));
        try { body(); }
        finally { CreatureRankFloors.ResetToUnconfigured(); }
    }

    // ── report, printed never asserted ───────────────────────────────────────────────

    [Fact]
    public void Report_distinct_species_reachable_across_all_compiled_waves()
    {
        var distinct = WaveCatalog.Build().SelectMany(w => w.Enemies).Select(e => e.SpeciesId)
            .Distinct(StringComparer.Ordinal).Count();
        _out.WriteLine($"distinct species reachable across all compiled waves: {distinct}");
    }
}
