using System.Text.Json.Nodes;
using FusionRpg.Core.Expeditions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Expeditions;

/// <summary>
/// npc-story-events NR2.19 (plan §4 D4, spec-host-content-theta.md §4, spec-expedition-lead-host.md §6):
/// the published <c>expeditions.v{n}.json</c> carries a danger band per tier and the two encounter
/// chances, and every one of those keys is REQUIRED — deleting it must reject naming its own path, not
/// fall back to a value nobody chose (tunables-ssot.md T5).
///
/// <para>The file is read from <c>gk-core/data/tuning/</c> at its highest published version, so a later
/// <c>publish.py</c> bump moves this test with it instead of leaving it reading a superseded file.</para>
/// </summary>
[Collection(ExpeditionsSequentialCollection.Name)]
public class ExpeditionTuningTests
{
    /// <summary>The CURRENT committed version: the highest <c>expeditions.v{n}.json</c>.</summary>
    static string TuningPath()
    {
        var dir = Path.Combine(RepoRoot(), "data", "tuning");
        return Directory.GetFiles(dir, "expeditions.v*.json")
            .OrderByDescending(path => int.Parse(Path.GetFileNameWithoutExtension(path)[("expeditions.v".Length)..]))
            .First();
    }

    static string TuningText() => File.ReadAllText(TuningPath());

    static ExpeditionTuning Parse() => ExpeditionTuningLoader.Parse(TuningText());

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }

    // ---- the committed file ------------------------------------------------------------------------

    [Fact]
    public void The_committed_file_parses_and_carries_the_tier_danger_bands_and_the_encounter_chances()
    {
        var tuning = Parse();

        Assert.Equal(4, tuning.Tiers.Count);
        Assert.All(tuning.Tiers.Values, tier => Assert.True(tier.DangerBand >= 0, "a danger band is an index, never negative"));
        Assert.True(tuning.Encounter.WildCreatureMetMilli is >= 0 and <= 1000, "a bounded per-mille ratio");
        Assert.True(tuning.Encounter.QuietMilli is >= 0 and <= 1000, "a bounded per-mille ratio");
    }

    [Fact]
    public void The_shipped_starting_shapes_are_the_specs_bands_one_to_four()
    {
        // spec-host-content-theta.md §4: "a tier's danger matches the sector bands its battles
        // resemble — scout-30m 1, forage-4h 2, hunt-8h 3, warpath-20h 4". A rebalance republishes
        // expeditions.v{n+1} through gk-core/tools/tuning/publish.py and moves this test with it.
        var tiers = Parse().Tiers;

        Assert.Equal(1, tiers["scout-30m"].DangerBand);
        Assert.Equal(2, tiers["forage-4h"].DangerBand);
        Assert.Equal(3, tiers["hunt-8h"].DangerBand);
        Assert.Equal(4, tiers["warpath-20h"].DangerBand);
    }

    [Fact]
    public void A_tiers_danger_band_rises_with_its_length()
    {
        // A relation, not a literal: the band is a property of the tier, and a longer sortie into
        // deeper ground is never the safer one.
        var ordered = Parse().Tiers
            .OrderBy(pair => pair.Value.DurationMinutes)
            .Select(pair => (TierId: pair.Key, pair.Value.DangerBand))
            .ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.True(
                ordered[i].DangerBand > ordered[i - 1].DangerBand,
                $"{ordered[i].TierId} (band {ordered[i].DangerBand}) is not more dangerous than "
                + $"{ordered[i - 1].TierId} (band {ordered[i - 1].DangerBand})");
        }
    }

    [Fact]
    public void The_catalog_passes_each_tiers_band_through_from_the_hub()
    {
        // The catalog is a projection of the configured tuning: its own `_byId` cache fills once, at
        // first read (boot-time configuration, not a per-call reload), so the band a consumer sees is
        // the band the hub carries — the chain the file's 1..4 reaches a host through.
        var live = ExpeditionTuningHub.Tuning;

        foreach (var (tierId, numbers) in live.Tiers)
            Assert.Equal(numbers.DangerBand, ExpeditionTierCatalog.Get(tierId).DangerBand);

        Assert.True(live.Tiers.Values.Any(t => t.DangerBand != 0),
            "a hardcoded zero band would make this assertion vacuous");
    }

    // ---- every new key is required, by name --------------------------------------------------------

    [Theory]
    [InlineData("tiers", "scout-30m", "dangerBand", "tiers.scout-30m.dangerBand")]
    [InlineData("tiers", "forage-4h", "dangerBand", "tiers.forage-4h.dangerBand")]
    [InlineData("tiers", "hunt-8h", "dangerBand", "tiers.hunt-8h.dangerBand")]
    [InlineData("tiers", "warpath-20h", "dangerBand", "tiers.warpath-20h.dangerBand")]
    [InlineData("encounter", null, "wildCreatureMetMilli", "encounter.wildCreatureMetMilli")]
    [InlineData("encounter", null, "quietMilli", "encounter.quietMilli")]
    public void Deleting_a_key_rejects_naming_its_path(string container, string? tierId, string leaf, string expectedPath)
    {
        var root = JsonNode.Parse(TuningText())!.AsObject();
        var node = tierId is null
            ? root[container]!.AsObject()
            : root[container]![tierId]!.AsObject();
        node.Remove(leaf);

        var ex = Assert.Throws<ExpeditionTuningRejection>(() => ExpeditionTuningLoader.Parse(root.ToJsonString()));

        Assert.Contains(expectedPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_the_whole_encounter_block_rejects_naming_it()
    {
        var root = JsonNode.Parse(TuningText())!.AsObject();
        root.Remove("encounter");

        var ex = Assert.Throws<ExpeditionTuningRejection>(() => ExpeditionTuningLoader.Parse(root.ToJsonString()));

        Assert.Contains("$.encounter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_superseded_v1_file_can_no_longer_be_parsed_at_all()
    {
        // "every loader reads v2" is not a promise about five call sites: the loader now requires the
        // keys v1 never carried, so a reader left behind on v1 throws naming the missing path. This is
        // what makes the reader switch in this commit mechanically checkable.
        var v1 = Path.Combine(RepoRoot(), "data", "tuning", "expeditions.v1.json");

        var ex = Assert.Throws<ExpeditionTuningRejection>(() => ExpeditionTuningLoader.Parse(File.ReadAllText(v1)));

        Assert.Contains("dangerBand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_integer_danger_band_is_refused_rather_than_coerced()
    {
        var root = JsonNode.Parse(TuningText())!.AsObject();
        root["tiers"]!["hunt-8h"]!["dangerBand"] = "3";

        var ex = Assert.Throws<ExpeditionTuningRejection>(() => ExpeditionTuningLoader.Parse(root.ToJsonString()));

        Assert.Contains("tiers.hunt-8h.dangerBand", ex.Message, StringComparison.Ordinal);
    }
}
