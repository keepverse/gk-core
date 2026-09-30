using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// Task 9's Delve clause (spec-species-rank.md §6, "Delve encounter display" row): rank reaches Delve
/// through the <see cref="ConcreteAnchor"/> corpus join, and the filter logic plus the null-threat
/// throw are unchanged by it. Reconciled against the ANCHOR TREE — the independent artifact Task 4's
/// regen wrote — so a join that dropped or defaulted the field fails here instead of quietly handing a
/// later gate a rung nobody derived.
/// </summary>
public class ConcreteAnchorRankJoinTests
{
    [Fact]
    public void The_real_corpus_join_carries_each_anchors_own_rank()
    {
        var anchorRank = new Dictionary<string, string?>(StringComparer.Ordinal);
        var seedRoot = Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species");
        foreach (var file in Directory.GetFiles(seedRoot, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            foreach (var anchor in AnchorRowReader.ReadAll(File.ReadAllText(file)))
                anchorRank[anchor.SpeciesId] = anchor.Rank;
        }
        Assert.NotEmpty(anchorRank);

        var rows = RealAnchorCorpusFixture.All;
        Assert.NotEmpty(rows);

        var mismatches = new List<string>();
        foreach (var row in rows)
        {
            if (!anchorRank.TryGetValue(row.SpeciesId, out var expected)) continue;
            var actual = row.Rank?.ToId();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                mismatches.Add($"{row.SpeciesId}: join={actual ?? "(null)"} anchor={expected ?? "(null)"}");
        }

        Assert.True(mismatches.Count == 0,
            "the corpus join lost, defaulted or invented a rank: " + string.Join(", ", mismatches.Take(5)));
        // ... and the field is genuinely populated (a corpus where every row were null would satisfy the
        // loop above trivially). No count is pinned: today every resolved anchor carries a rank.
        Assert.Contains(rows, r => r.Rank is not null);
    }

    [Fact]
    public void A_skipped_rank_joins_as_null_never_a_bottom_rung()
    {
        // Assumption 4 at the join: the anchor has no rank, so the row has none either. Defaulting it
        // to the bottom rung here would fabricate a rung for a species nobody classified.
        var anchor = new AnchorRow(
            "test.norank", "cultivated", "raider", "Onslaught", null, true, "steady", "melee",
            new[] { "normal" }, "plant", 0, "earth", null, "PlantAvatar",
            new[] { "Summonable" }, Array.Empty<string>(), "frontline", Rank: null);
        var species = new ConcreteSpecies
        {
            SpeciesId = "test.norank", Rarity = CreatureRarity.Cultivated, Rank = null,
        };

        var row = ConcreteAnchor.From(anchor, species, RealAnchorCorpusFixture.ThreatTuning);

        Assert.Null(row.Rank);
    }

    [Fact]
    public void A_ranked_pair_joins_its_rank_verbatim()
    {
        var anchor = new AnchorRow(
            "test.ranked", "cultivated", "raider", "Onslaught", null, true, "steady", "melee",
            new[] { "normal" }, "plant", 0, "earth", null, "PlantAvatar",
            new[] { "Summonable" }, Array.Empty<string>(), "frontline", Rank: "heirloom");
        var species = new ConcreteSpecies
        {
            SpeciesId = "test.ranked", Rarity = CreatureRarity.Cultivated, Rank = CreatureRank.Heirloom,
        };

        var row = ConcreteAnchor.From(anchor, species, RealAnchorCorpusFixture.ThreatTuning);

        Assert.Equal(CreatureRank.Heirloom, row.Rank);
    }
}
