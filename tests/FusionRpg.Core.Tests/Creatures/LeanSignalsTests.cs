using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// EP2.4 (spec-per-species-lean.md "Signals — a closed set" and tests 3–4): per-mille ranks within a
/// side, bounded for any input, and a missing signal neutral and recorded. The population here is the
/// caller's list — the filter lives in `BuildFavourMeasurer.RosterPopulation`, once.
/// </summary>
public class LeanSignalsTests
{
    static AnchorRow Anchor(
        string speciesId, string side = "plant", int gameTypeId = 1,
        string? threatBand = "raider", bool pure = false) => new(
        SpeciesId: speciesId, Rarity: "chaff", ThreatBand: threatBand,
        AptitudePrimary: "Might", AptitudeSecondary: null, Pure: pure,
        AttackTempo: "steady", Reach: "melee", Variants: Array.Empty<string>(),
        Side: side, GameTypeId: gameTypeId, ElementPrimary: "fire", ElementSecondary: null,
        DeployMode: "PlantAvatar", Acquisition: new[] { "Summonable" }, Traits: Array.Empty<string>(),
        TargetPreference: "frontline");

    static BaseStatRow Stat(string side, int typeId, double hp, double attack) => new(side, typeId, hp, attack);

    static readonly IReadOnlyList<string> TenRungIds = new[]
    {
        "nuisance", "pest", "marauder", "raider", "warden",
        "scourge", "tyrant", "harbinger", "cataclysm", "calamity"
    };

    static readonly IReadOnlyList<ThreatRung> TenRungs = TenRungIds
        .Select((id, i) => new ThreatRung(i + 1, id, null, (i + 1) * 4))
        .ToArray();

    // ── ranks: bounded, within a side, symmetric ────────────────────────────────────────────────

    [Fact]
    public void A_wall_and_a_glass_cannon_specialise_equally_and_a_mirror_is_neutral()
    {
        // Low attack + high hp and its mirror must read the same |Δrank|; the middling pair 0.
        var population = new[]
        {
            Anchor("wall", gameTypeId: 1), Anchor("cannon", gameTypeId: 2),
            Anchor("middle-a", gameTypeId: 3), Anchor("middle-b", gameTypeId: 4)
        };
        var stats = new[]
        {
            Stat("plant", 1, hp: 1000, attack: 10),
            Stat("plant", 2, hp: 10, attack: 1000),
            Stat("plant", 3, hp: 100, attack: 90),
            Stat("plant", 4, hp: 110, attack: 95)
        };

        var inputs = LeanSignals.Compute(population, stats, TenRungs);
        var byId = inputs.Sets.ToDictionary(s => s.SpeciesId);

        Assert.Equal(byId["wall"].Specialisation, byId["cannon"].Specialisation);
        Assert.True(byId["wall"].Specialisation > 900, $"wall should be a specialist, got {byId["wall"].Specialisation}");
        Assert.True(byId["middle-a"].Specialisation < 250,
            $"a middling shooter should not be a specialist, got {byId["middle-a"].Specialisation}");
        Assert.Empty(inputs.Missing);
    }

    [Fact]
    public void Ranks_are_taken_within_a_side_not_across_sides()
    {
        // Both sides carry the same two extreme profiles. Within its own side each is max-rank on one
        // stat and min on the other, so the specialisation is 1000; pooled across sides the tied
        // extremes would share a rank and read 666, so this number is the difference.
        var population = new[]
        {
            Anchor("p-wall", side: "plant", gameTypeId: 1, threatBand: "raider"),
            Anchor("p-cannon", side: "plant", gameTypeId: 2, threatBand: "raider"),
            Anchor("z-wall", side: "zombie", gameTypeId: 1, threatBand: "raider"),
            Anchor("z-cannon", side: "zombie", gameTypeId: 2, threatBand: "raider")
        };
        var stats = new[]
        {
            Stat("plant", 1, 1000, 10), Stat("plant", 2, 10, 1000),
            Stat("zombie", 1, 1000, 10), Stat("zombie", 2, 10, 1000)
        };

        var inputs = LeanSignals.Compute(population, stats, TenRungs);

        Assert.All(inputs.Sets, s => Assert.Equal(1000, s.Specialisation));
        Assert.Empty(inputs.Missing);
    }

    // ── bounded for any input, including extreme hp ─────────────────────────────────────────────

    [Theory]
    [InlineData(1e15)]
    [InlineData(0)]
    [InlineData(640000)]
    [InlineData(-5)]
    public void Every_signal_lies_in_the_band_for_any_input(double extreme)
    {
        var population = new[] { Anchor("a", gameTypeId: 1, threatBand: "raider"),
                                 Anchor("b", gameTypeId: 2, threatBand: "raider"),
                                 Anchor("c", gameTypeId: 3, threatBand: "raider") };
        var stats = new[]
        {
            Stat("plant", 1, hp: extreme, attack: extreme),
            Stat("plant", 2, hp: 300, attack: 7200),
            Stat("plant", 3, hp: 1, attack: 0)
        };

        var inputs = LeanSignals.Compute(population, stats, TenRungs);

        Assert.Equal(population.Length, inputs.Sets.Count);
        foreach (var set in inputs.Sets)
        {
            Assert.InRange(set.Specialisation, 0, 1000);
            Assert.InRange(set.Pure, 0, 1000);
            Assert.InRange(set.ThreatRung, 0, 1000);
        }
    }

    [Fact]
    public void Pure_reads_1000_or_0_and_is_never_missing()
    {
        var population = new[]
        {
            Anchor("pure", gameTypeId: 1, threatBand: "raider", pure: true),
            Anchor("mixed", gameTypeId: 2, threatBand: "raider", pure: false)
        };
        var stats = new[] { Stat("plant", 1, 300, 20), Stat("plant", 2, 400, 30) };

        var inputs = LeanSignals.Compute(population, stats, TenRungs);
        var byId = inputs.Sets.ToDictionary(s => s.SpeciesId);

        Assert.Equal(1000, byId["pure"].Pure);
        Assert.Equal(0, byId["mixed"].Pure);
        Assert.DoesNotContain(inputs.Missing, m => m.EndsWith(":pure", StringComparison.Ordinal));
    }

    // ── the rung ordinal, never thetaOffset ─────────────────────────────────────────────────────

    [Fact]
    public void The_threat_signal_reads_the_rung_ordinal_and_never_the_theta_offset()
    {
        // ThetaOffsets deliberately absurd: if the signal read them, these numbers would appear.
        var ladder = new[]
        {
            new ThreatRung(1, "nuisance", null, 0),
            new ThreatRung(2, "raider", null, 1),
            new ThreatRung(3, "calamity", null, 999_999)
        };
        var population = new[] { Anchor("a", gameTypeId: 1, threatBand: "nuisance"),
                                 Anchor("b", gameTypeId: 2, threatBand: "raider"),
                                 Anchor("c", gameTypeId: 3, threatBand: "calamity") };
        var stats = new[] { Stat("plant", 1, 300, 20), Stat("plant", 2, 300, 20), Stat("plant", 3, 300, 20) };

        var inputs = LeanSignals.Compute(population, stats, ladder);
        var byId = inputs.Sets.ToDictionary(s => s.SpeciesId);

        Assert.Equal(0, byId["a"].ThreatRung);
        Assert.Equal(500, byId["b"].ThreatRung);
        Assert.Equal(1000, byId["c"].ThreatRung);
        Assert.Empty(inputs.Missing);
    }

    // ── missing input is neutral and recorded, and never re-filtered ────────────────────────────

    [Fact]
    public void A_species_with_no_base_stat_row_gets_500_and_is_listed()
    {
        // Two ranked siblings so the side has a spread at all (a lone ranked species has none).
        var population = new[]
        {
            Anchor("known-a", gameTypeId: 1, threatBand: "raider"),
            Anchor("known-b", gameTypeId: 2, threatBand: "raider"),
            Anchor("unknown", gameTypeId: 99, threatBand: "raider")
        };
        var stats = new[] { Stat("plant", 1, 300, 20), Stat("plant", 2, 640000, 7200) };

        var inputs = LeanSignals.Compute(population, stats, TenRungs);
        var byId = inputs.Sets.ToDictionary(s => s.SpeciesId);

        Assert.Equal(LeanSignals.NeutralPermille, byId["unknown"].Specialisation);
        Assert.Contains("unknown:specialisation", inputs.Missing);
        Assert.DoesNotContain(inputs.Missing, m => m.StartsWith("known-", StringComparison.Ordinal));
        // The neutral value is opaque: it is NOT an "average rank" invented from the other rows.
        Assert.NotEqual(byId["known-a"].Specialisation, byId["unknown"].Specialisation);
    }

    [Fact]
    public void An_unresolved_band_gets_500_and_is_listed()
    {
        var population = new[]
        {
            Anchor("unclassified", gameTypeId: 1, threatBand: null),
            Anchor("unknown-band", gameTypeId: 2, threatBand: "not-a-rung")
        };
        var stats = new[] { Stat("plant", 1, 300, 20), Stat("plant", 2, 400, 30) };

        var inputs = LeanSignals.Compute(population, stats, TenRungs);
        var byId = inputs.Sets.ToDictionary(s => s.SpeciesId);

        Assert.Equal(LeanSignals.NeutralPermille, byId["unclassified"].ThreatRung);
        Assert.Equal(LeanSignals.NeutralPermille, byId["unknown-band"].ThreatRung);
        Assert.Contains("unclassified:threatRung", inputs.Missing);
        Assert.Contains("unknown-band:threatRung", inputs.Missing);
    }

    [Fact]
    public void The_population_is_the_callers_and_is_never_re_filtered()
    {
        // The excluded row is passed in ON PURPOSE: this type must return a set for every row it was
        // given. Dropping it here would be a second filter, and the spec's rule is that the measure
        // owns the one filter (`BuildFavourMeasurer.RosterPopulation`).
        var population = new[] { Anchor("kept", gameTypeId: 1, threatBand: "raider"),
                                 Anchor("phantom", gameTypeId: 2, threatBand: "raider") };
        var stats = new[] { Stat("plant", 1, 300, 20), Stat("plant", 2, 640000, 0) };

        var inputs = LeanSignals.Compute(population, stats, TenRungs);

        Assert.Equal(2, inputs.Sets.Count);
        Assert.Contains(inputs.Sets, s => s.SpeciesId == "phantom");
    }

    // ── the closed set ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_signal_set_is_closed_and_pinned_with_its_reason()
    {
        // A CLOSED vocabulary the code owns and a human changes by review (spec-per-species-lean.md
        // "Ask first: a new signal (it is a closed set)"), so a literal pin is legitimate here — and
        // it is also what the tuning loader validates `leanSignalWeights`' keys against.
        Assert.Equal(new[] { "specialisation", "pure", "threatRung" }, LeanSignals.Names);

        var set = new LeanSignalSet("s", 1, 2, 3);
        Assert.Equal(1, set.For("specialisation"));
        Assert.Equal(2, set.For("pure"));
        Assert.Equal(3, set.For("threatRung"));
        // `crowding` is deliberately absent: it is today's per-primary term and keeps its own
        // published key (`crowdingFactor`), never a `leanSignalWeights` member.
        Assert.Throws<ArgumentOutOfRangeException>(() => set.For("crowding"));
    }

    [Fact]
    public void The_dump_parser_reads_the_nested_statsJson_layer()
    {
        const string dump = """
            {"dumpFormatVersion":1,"entries":[
              {"side":"plant","typeId":0,"typeName":"Peashooter",
               "statsJson":"{\"side\":\"plant\",\"typeId\":0,\"hpBase\":300,\"attackBase\":20}"}
            ]}
            """;

        var row = Assert.Single(BaseStatDump.Parse(dump));

        Assert.Equal("plant", row.Side);
        Assert.Equal(0, row.GameTypeId);
        Assert.Equal(300, row.HpBase);
        Assert.Equal(20, row.AttackBase);
    }
}
