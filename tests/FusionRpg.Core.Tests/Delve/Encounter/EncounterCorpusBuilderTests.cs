using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// D4.17 row 6's own real finding (party-dungeon-todo.md, 2026-09-07): `ConcreteAnchor.From` had zero
/// production callers anywhere — <see cref="EncounterCorpusBuilder"/> is the production equivalent of
/// the test-only <see cref="RealAnchorCorpusFixture"/>, proven here to produce the SAME real corpus
/// (mirrors its own already-proven "over 700 anchors join cleanly" assertion) through the identical
/// anchor -&gt; <see cref="SpeciesExpander"/> -&gt; <see cref="ConcreteAnchor.From"/> pipeline, reading
/// real tuning files directly rather than the fixture's own private copies.
/// </summary>
public class EncounterCorpusBuilderTests
{
    static readonly AptitudeTuning RealAptitudes =
        AptitudeTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "aptitudes.v2.json")));
    static readonly PowerTuning RealPower =
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "power-scale.v2.json")));
    static readonly CreatureShapeTuning RealShape =
        CreatureShapeTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "creature-shape.v1.json")));
    static readonly CreatureThreatTuning RealThreat = RealAnchorCorpusFixture.ThreatTuning;

    [Fact]
    public void Build_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => EncounterCorpusBuilder.Build(null!, RealAptitudes, RealPower, RealShape, RealThreat));
        Assert.Throws<ArgumentNullException>(() => EncounterCorpusBuilder.Build(DungeonTestFiles.SpeciesDir(), null!, RealPower, RealShape, RealThreat));
        Assert.Throws<ArgumentNullException>(() => EncounterCorpusBuilder.Build(DungeonTestFiles.SpeciesDir(), RealAptitudes, null!, RealShape, RealThreat));
        Assert.Throws<ArgumentNullException>(() => EncounterCorpusBuilder.Build(DungeonTestFiles.SpeciesDir(), RealAptitudes, RealPower, null!, RealThreat));
        Assert.Throws<ArgumentNullException>(() => EncounterCorpusBuilder.Build(DungeonTestFiles.SpeciesDir(), RealAptitudes, RealPower, RealShape, null!));
    }

    [Fact]
    public void A_missing_directory_returns_empty_never_throws()
    {
        var rows = EncounterCorpusBuilder.Build(
            Path.Combine(DungeonTestFiles.RepoRoot(), "does-not-exist"), RealAptitudes, RealPower, RealShape, RealThreat);
        Assert.Empty(rows);
    }

    [Fact]
    public void Build_joins_the_real_species_corpus_the_same_way_the_test_fixture_already_proves()
    {
        var rows = EncounterCorpusBuilder.Build(DungeonTestFiles.SpeciesDir(), RealAptitudes, RealPower, RealShape, RealThreat);

        Assert.True(rows.Count > 700, $"expected well over 700 anchors, got {rows.Count}");
        Assert.Equal(RealAnchorCorpusFixture.All.Count, rows.Count);
        Assert.Equal(RealAnchorCorpusFixture.All.Select(a => a.SpeciesId), rows.Select(a => a.SpeciesId));
    }

    [Fact]
    public void The_classified_subset_matches_the_already_documented_184_of_841_finding()
    {
        var rows = EncounterCorpusBuilder.Build(DungeonTestFiles.SpeciesDir(), RealAptitudes, RealPower, RealShape, RealThreat);
        var classified = rows.Count(a => a.ThreatBand is not null);
        Assert.Equal(RealAnchorCorpusFixture.All.Count(a => a.ThreatBand is not null), classified);
    }
}
