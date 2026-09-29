using FusionRpg.Core.Creatures.Generation;
using System.Text.Json;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>EP1.7 (spec-specimen-respec-price.md "Tunables") — `SpeciesBuildTuningLoader`'s load
/// contract for the three new `uniqueRespec*` keys, and the shipped v2 file's `UniqueRespec` view.</summary>
public class SpeciesBuildTuningTests
{
    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    static string ShippedJson() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "species-build.v6.json"));

    /// <summary>One version's document by number. T4 keeps every superseded version on disk for revert,
    /// so each version's own contract is pinned against the file it left behind, never against
    /// whichever file happens to be current when the pin moves.</summary>
    static string VersionJson(int version) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", $"species-build.v{version}.json"));

    [Fact]
    public void The_shipped_file_parses_and_carries_the_unique_respec_view()
    {
        var tuning = SpeciesBuildTuningLoader.Parse(ShippedJson());
        Assert.Equal(50, tuning.UniqueRespec.BasePrice);
        Assert.Equal(500, tuning.UniqueRespec.EscalationPermille);
        Assert.Equal(3, tuning.UniqueRespec.DecayDays);
        // Working values equal the species keys (spec: "a specimen re-spec starts at the price a
        // player already knows") -- same numbers, still two independently-tunable parameter sets.
        Assert.Equal(tuning.SpeciesRespec, tuning.UniqueRespec);
    }

    const string DocWithoutUniqueKeys = """
        {
          "schemaVersion": 1,
          "version": 2,
          "parityFloorPermille": 50,
          "parityCeilingPermille": 200,
          "leanMinPermille": 350,
          "leanMaxPermille": 600,
          "crowdingFactor": 633,
          "secondarySharePermille": 300,
          "maxAptitudesPerSpecies": 5,
          "minAptitudesPerSpecies": 2,
          "respecBasePrice": 50,
          "respecEscalationPermille": 500,
          "respecDecayDays": 3
        }
        """;

    [Fact]
    public void A_missing_uniqueRespecBasePrice_is_a_load_rejection_naming_the_key()
    {
        var ex = Assert.Throws<SpeciesBuildTuningRejection>(() => SpeciesBuildTuningLoader.Parse(DocWithoutUniqueKeys));
        Assert.Contains("uniqueRespecBasePrice", ex.Message);
    }

    // ── EP2.5: `leanSignalWeights`, a weight per signal in the closed set ────────────────────────

    /// <summary>A full document at <paramref name="version"/> plus whatever trailing keys a case needs
    /// (the caller writes the leading comma), so a case varies only what it is about.</summary>
    static string Doc(int version, string tail) => $$"""
        {
          "schemaVersion": 1,
          "version": {{version}},
          "parityFloorPermille": 50,
          "parityCeilingPermille": 200,
          "leanMinPermille": 350,
          "leanMaxPermille": 600,
          "crowdingFactor": 633,
          "secondarySharePermille": 300,
          "maxAptitudesPerSpecies": 5,
          "minAptitudesPerSpecies": 2,
          "respecBasePrice": 50,
          "respecEscalationPermille": 500,
          "respecDecayDays": 3,
          "uniqueRespecBasePrice": 50,
          "uniqueRespecEscalationPermille": 500,
          "uniqueRespecDecayDays": 3{{tail}}
        }
        """;

    const string WeightsAtZero = "\"leanSignalWeights\": { \"specialisation\": 0, \"pure\": 0, \"threatRung\": 0 }";

    static string V3(string weightsBlock) => Doc(3, "," + weightsBlock);

    [Fact]
    public void The_v2_file_predates_the_signals_and_reads_as_all_zero()
    {
        // v2 has no block at all, and absent there means the shipped value: every penalty 0, which is
        // what the mechanism landed as. A v3+ file is held to the opposite rule below.
        Assert.Equal(LeanSignalWeights.Zero, SpeciesBuildTuningLoader.Parse(VersionJson(2)).LeanSignalWeights);
    }

    [Fact]
    public void The_v3_file_publishes_the_weights_at_zero()
    {
        // EP2.6 (H7): the publish and its readers move in one commit. v3 shipped the mechanism with
        // every weight at 0, so it was byte-identical to the pre-signal formula.
        var tuning = SpeciesBuildTuningLoader.Parse(VersionJson(3));

        Assert.Equal(3, tuning.Version);
        Assert.Equal(LeanSignalWeights.Zero, tuning.LeanSignalWeights);
        Assert.Equal(0, tuning.LeanSignalWeights.For(LeanSignals.ThreatRung));
    }

    [Fact]
    public void The_v4_file_publishes_the_working_values_with_threatRung_at_zero()
    {
        // EP2.7's balance publish, chosen against the measure artifact and labelled "working values"
        // in the file's own `_meta` (never "balance"): the per-species signals now move the lean,
        // `threatRung` stays at 0 (R6: a later publish turns it on), and the crowding term is reduced
        // rather than deleted (R-Q8: `crowdingFactor` stays in the file).
        var tuning = SpeciesBuildTuningLoader.Parse(VersionJson(4));
        var before = SpeciesBuildTuningLoader.Parse(VersionJson(3));

        Assert.Equal(4, tuning.Version);
        Assert.True(tuning.LeanSignalWeights.Specialisation > 0);
        Assert.True(tuning.LeanSignalWeights.Pure > 0);
        Assert.Equal(0, tuning.LeanSignalWeights.ThreatRung);
        Assert.True(tuning.CrowdingFactor < before.CrowdingFactor,
            $"crowdingFactor should fall (was {before.CrowdingFactor}, now {tuning.CrowdingFactor})");
        // The clamp band the parity gate was sized for is untouched by this publish.
        Assert.Equal(before.LeanMinPermille, tuning.LeanMinPermille);
        Assert.Equal(before.LeanMaxPermille, tuning.LeanMaxPermille);
    }

    [Fact]
    public void A_v3_document_without_the_weights_block_is_refused_naming_it()
    {
        // v3+ is held to the strict rule: the block is not optional any more.
        var ex = Assert.Throws<SpeciesBuildTuningRejection>(() => SpeciesBuildTuningLoader.Parse(Doc(3, "")));
        Assert.Contains("leanSignalWeights", ex.Message);
    }

    [Fact]
    public void The_weights_read_per_signal_and_every_name_in_the_closed_set_answers()
    {
        var weights = SpeciesBuildTuningLoader.Parse(
            V3("\"leanSignalWeights\": { \"specialisation\": 120, \"pure\": 40, \"threatRung\": 0 }")).LeanSignalWeights;

        // `For` and the record's own fields must agree for EVERY name in the closed set, so adding a
        // signal cannot leave one of the three readers behind.
        foreach (var name in LeanSignals.Names)
            Assert.Equal(weights.For(name), weights.GetType().GetProperty(FieldFor(name))!.GetValue(weights));
        Assert.Equal(120, weights.Specialisation);
        Assert.Equal(40, weights.Pure);
        Assert.Equal(0, weights.ThreatRung);
    }

    static string FieldFor(string signal) => signal switch
    {
        "specialisation" => "Specialisation",
        "pure" => "Pure",
        "threatRung" => "ThreatRung",
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };

    [Theory]
    // `crowding` is today's term and keeps its own `crowdingFactor`; it is deliberately not a member.
    [InlineData("\"leanSignalWeights\": { \"crowding\": 100 }", "crowding")]
    [InlineData("\"leanSignalWeights\": { \"specialisation\": -1, \"pure\": 0, \"threatRung\": 0 }", "specialisation")]
    [InlineData("\"leanSignalWeights\": { \"specialisation\": 0, \"pure\": 0 }", "threatRung")]
    [InlineData("\"leanSignalWeights\": { \"specialisation\": \"lots\", \"pure\": 0, \"threatRung\": 0 }", "specialisation")]
    public void A_bad_weights_block_is_refused_naming_the_key(string block, string named)
    {
        var ex = Assert.Throws<SpeciesBuildTuningRejection>(() => SpeciesBuildTuningLoader.Parse(V3(block)));
        Assert.Contains(named, ex.Message);
    }

    // ── EP2.8: the lead and shape caps — tuning fields and their load contract, NO gate yet ──────

    [Fact]
    public void The_shipped_file_publishes_the_caps_chosen_from_the_current_measure()
    {
        // Working values, and the two relations that make them mean something: the corpus FAILS the
        // lead half today (which is exactly what the relabel pass exists to fix) and already meets the
        // shape half. Nothing gates on them here — Phase 4 lands with the relabelled corpus.
        // This reads the SHIPPED file, so the version assertion moves with each publish (6 =
        // respec-free-counter's `freeRespecsPerEmpireLevel`, published 2026-09-21); the per-version
        // contracts of v1..v5 are pinned by the `VersionJson` cases instead.
        var tuning = SpeciesBuildTuningLoader.Parse(ShippedJson());
        using var measureDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "data", "generated", "creatures", "_species-build-measure.json")));
        var measure = measureDoc.RootElement;

        Assert.Equal(6, tuning.Version);
        Assert.Equal(250L, tuning.LeadCapPermille!.Value);
        Assert.Equal(50L, tuning.LeadCapTolerancePermille!.Value);
        Assert.Equal(300L, tuning.ShapeCapPermille!.Value);
        // The one comparison Phase 4 makes: cap + tolerance.
        Assert.Equal(300L, tuning.LeadRefusalPermille!.Value);

        // EP2.13/EP2.14 turned the lead relation around, and that is the whole point of the pair: before
        // the relabel pass the corpus FAILED the lead half (413 permille against 300), and after it the
        // corpus MEETS both halves — which is what lets Phase 4's gate be green on main. The relation is
        // asserted, never a literal, so a later balance publish only has to keep it true.
        Assert.True(tuning.LeadRefusalPermille.Value >= measure.GetProperty("maxLeadPermille").GetInt64(),
            "the relabelled corpus should now be inside the working lead threshold");
        Assert.True(tuning.ShapeCapPermille.Value >= measure.GetProperty("largestShapePermille").GetInt64(),
            "the working shape cap should be met by the corpus");
        Assert.Equal(measure.GetProperty("maxLeadAptitude").GetString(), "Onslaught");
    }

    [Fact]
    public void The_v4_file_predates_the_caps_and_states_their_absence()
    {
        // Absence is STATED as absence, never defaulted to a number nobody published: a v4 document
        // loads, and the gate that will read these must refuse to gate on a document with none (T4
        // keeps v4 on disk for revert).
        var tuning = SpeciesBuildTuningLoader.Parse(VersionJson(4));

        Assert.Equal(4, tuning.Version);
        Assert.Null(tuning.LeadCapPermille);
        Assert.Null(tuning.LeadCapTolerancePermille);
        Assert.Null(tuning.ShapeCapPermille);
        Assert.Null(tuning.LeadRefusalPermille);
    }

    [Theory]
    [InlineData("\"leadCapPermille\": 250, \"leadCapTolerancePermille\": 50", "shapeCapPermille")]
    [InlineData("\"leadCapTolerancePermille\": 50, \"shapeCapPermille\": 300", "leadCapPermille")]
    [InlineData("\"leadCapPermille\": 250, \"shapeCapPermille\": 300", "leadCapTolerancePermille")]
    [InlineData("\"leadCapPermille\": -1, \"leadCapTolerancePermille\": 50, \"shapeCapPermille\": 300", "leadCapPermille")]
    public void A_v5_document_missing_or_soiling_a_cap_is_refused_naming_it(string caps, string named)
    {
        var ex = Assert.Throws<SpeciesBuildTuningRejection>(() => SpeciesBuildTuningLoader.Parse(
            Doc(5, "," + WeightsAtZero + ", " + caps)));
        Assert.Contains(named, ex.Message);
    }

    // ---- respec-free-counter EP4.4: `freeRespecsPerEmpireLevel` ----------------------------------

    /// <summary>The shipped document with one top-level key removed or replaced, rebuilt through
    /// `System.Text.Json` so the fixture stays valid JSON whatever the key's own comma placement is.</summary>
    static string ShippedWith(string key, string? rawValue)
    {
        using var doc = JsonDocument.Parse(ShippedJson());
        var parts = doc.RootElement.EnumerateObject()
            .Select(p => p.Name == key
                ? (rawValue is null ? (string?)null : $"\"{p.Name}\":{rawValue}")
                : $"\"{p.Name}\":{p.Value.GetRawText()}")
            .Where(part => part is not null);
        return "{" + string.Join(",", parts) + "}";
    }

    [Fact]
    public void The_shipped_v6_file_publishes_the_free_empire_respec_grant()
    {
        var tuning = SpeciesBuildTuningLoader.Parse(ShippedJson());
        Assert.Equal(1, tuning.FreeRespecsPerEmpireLevel);
        // R-Q3's fixed `respecFreeCount` 25 was never built, and its name must never come back as a key
        // or as a member: the grant belongs to the empire LEVEL, so the key is named for the level.
        Assert.DoesNotContain("respecFreeCount", ShippedJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(SpeciesBuildTuning).GetProperties(),
            p => string.Equals(p.Name, "respecFreeCount", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_v6_document_missing_the_grant_is_refused_naming_it()
    {
        var json = ShippedWith("freeRespecsPerEmpireLevel", rawValue: null);
        using (var rebuilt = JsonDocument.Parse(json))
            Assert.False(rebuilt.RootElement.TryGetProperty("freeRespecsPerEmpireLevel", out _));
        var rejection = Assert.Throws<SpeciesBuildTuningRejection>(() => SpeciesBuildTuningLoader.Parse(json));
        Assert.Contains("freeRespecsPerEmpireLevel", rejection.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void A_v6_document_soiling_the_grant_is_refused(string raw)
    {
        // 0 is LEGAL (levels pay nothing) -- the two refused shapes are "fewer than none" and a
        // fractional count, which is a balance mistake rather than a value to round away.
        Assert.Throws<SpeciesBuildTuningRejection>(() =>
            SpeciesBuildTuningLoader.Parse(ShippedWith("freeRespecsPerEmpireLevel", raw)));
        Assert.Equal(0, SpeciesBuildTuningLoader.Parse(ShippedWith("freeRespecsPerEmpireLevel", "0"))
            .FreeRespecsPerEmpireLevel);
    }

    [Fact]
    public void A_pre_v6_document_without_the_grant_still_loads_and_states_the_absence()
    {
        // v1..v5 stay on disk for revert, so they must keep loading -- and the absence is null, never a
        // defaulted number nobody published (the same discipline the caps use).
        var tuning = SpeciesBuildTuningLoader.Parse(VersionJson(5));
        Assert.Null(tuning.FreeRespecsPerEmpireLevel);
    }
}
