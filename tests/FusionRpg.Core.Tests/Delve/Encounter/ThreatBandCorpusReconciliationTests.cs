using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// TB-H1 (tasks/creature-seed-todo.md, filed from `party-dungeon` F1's blocked landing gate): every
/// `threatBand` the corpus actually produced must map to a row in `creature-threat.v2.json`, and a rung
/// with no row must be refused LOUDLY rather than classifying to nothing. Read over the ANCHOR TREE — the
/// artifact the classifier wrote — and reconciled against the same table the encounter join reads.
///
/// <para>This is a reconciliation/closure assertion, not a population count: it stays valid however many
/// species ship, and fails the moment a classifier produces a rung the table cannot resolve.</para>
/// </summary>
public class ThreatBandCorpusReconciliationTests
{
    static readonly CreatureThreatTuning ThreatTuning = CreatureThreatTuningLoader.Parse(
        File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "creature-threat.v2.json")));

    static Dictionary<string, AnchorRow> RealAnchors()
    {
        var seedRoot = Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species");
        var byId = new Dictionary<string, AnchorRow>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(seedRoot, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            foreach (var anchor in AnchorRowReader.ReadAll(File.ReadAllText(file)))
                byId[anchor.SpeciesId] = anchor;
        }
        return byId;
    }

    static AnchorRow Anchor(string speciesId, string? threatBand) => new(
        speciesId, "cultivated", threatBand, "Onslaught", null, true, "steady", "melee",
        new[] { "normal" }, "plant", 0, "earth", null, "PlantAvatar",
        new[] { "Summonable" }, Array.Empty<string>(), "frontline");

    [Fact]
    public void Every_produced_threatBand_maps_to_a_real_row_in_the_table()
    {
        var anchors = RealAnchors();
        Assert.NotEmpty(anchors);

        var produced = anchors.Values
            .Select(a => a.ThreatBand)
            .Where(b => b is not null and not "unresolved")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The corpus does classify: a zero here would be a reading about the corpus, never a pass.
        Assert.NotEmpty(produced);
        var orphans = produced.Where(b => !ThreatTuning.RungIds.Contains(b, StringComparer.Ordinal)).ToList();
        Assert.True(orphans.Count == 0,
            "these produced threatBand rungs have no row in creature-threat.v2.json: " + string.Join(", ", orphans));

        // And every produced rung resolves through the SAME reader the Θ offset path takes, not merely by
        // id membership in a list.
        foreach (var rung in produced) ThreatTuning.OffsetFor(rung);
    }

    [Fact]
    public void A_rung_with_no_row_is_refused_loudly_by_the_corpus_join()
    {
        // The failure mode TB-H1 names: a produced rung the table cannot resolve must fail with a message
        // naming the rung and the file, never "classify to nothing" — a silent zero-offset species is
        // worse than a refusal because nothing downstream can tell it apart from a real `nuisance`.
        var anchor = Anchor("test.badrung", "not-a-rung");
        var species = new ConcreteSpecies { SpeciesId = "test.badrung", Rarity = CreatureRarity.Cultivated };

        var refusal = Assert.Throws<InvalidOperationException>(
            () => ConcreteAnchor.From(anchor, species, ThreatTuning));

        Assert.Contains("not-a-rung", refusal.Message);
        // Pins the PRODUCTION refusal message verbatim — it names the file the C# join still loads
        // (v1, TB-H2's denied half); this test's own parse above uses the current version.
        Assert.Contains("creature-threat.v1.json", refusal.Message);
    }

    [Fact]
    public void An_absent_threatBand_takes_the_tables_sanctioned_default_instead_of_refusing()
    {
        // The OTHER case, deliberately different and worth pinning beside the refusal above: an anchor
        // that was never classified has a sanctioned fallback (the table's own `inferredDefaultRung`),
        // which is why the Θ offset reader falls back while a produced-but-unknown rung is refused. The
        // corpus join itself fabricates nothing either way.
        var species = new ConcreteSpecies { SpeciesId = "test.norung", Rarity = CreatureRarity.Cultivated };
        var row = ConcreteAnchor.From(Anchor("test.norung", null), species, ThreatTuning);

        Assert.Null(row.ThreatBand);
        Assert.Null(row.ThreatRung);

        var defaultOffset = ThreatTuning.Thresholds
            .Single(t => t.Rung == ThreatTuning.InferredDefaultRung).ThetaOffset;
        Assert.Equal(defaultOffset, ThreatTuning.OffsetFor(null));
    }
}
