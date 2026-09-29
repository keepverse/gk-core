using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// EP2.1 (spec-favour-detector.md, "The population is real creatures only"): the anchor reader
/// exposes creature-seed's own `speciesKind` mark (CS13, commit <c>be3ac8a6</c>) and the generation
/// layer reads it through one closed vocabulary. The mark reached <see cref="AnchorRow.SpeciesKind"/>
/// already — this file pins the read, not the field, so the favour measure's population filter and
/// every consumer of that population (the lean ranks, stage A) share one rule.
/// </summary>
public class AnchorRowReaderTests
{
    const string Prefix = """[{"speciesId":"x","rarity":"fused","threatBand":"raider","aptitudePrimary":"Might","aptitudeSecondary":"none","pure":true,"attackTempo":"steady","reach":"short","variants":[],"side":"plant","gameTypeId":1,"elementPrimary":"earth","elementSecondary":"none","deployMode":"field","acquisition":[],"traits":[],"targetPreference":"frontline",""";

    static AnchorRow Read(string tail) => Assert.Single(AnchorRowReader.ReadAll(Prefix + tail + "]"));

    [Theory]
    [InlineData("creature")]
    [InlineData("mimic")]
    [InlineData("excluded")]
    public void SpeciesKind_reads_verbatim_from_the_anchor(string kind)
    {
        var row = Read($"\"speciesKind\":\"{kind}\"}}");
        Assert.Equal(kind, row.SpeciesKind);
    }

    [Fact]
    public void A_pre_mark_anchor_reads_null_and_is_never_inferred_excluded()
    {
        // 2,281 of the committed anchors predate the mark. The mark REMOVES a row from play, so an
        // absent one must read as creature — never as excluded.
        var row = Read("\"_provenance\":{}}");
        Assert.Null(row.SpeciesKind);
        Assert.False(AnchorSpeciesKind.IsExcluded(row));
    }

    [Fact]
    public void The_vocabulary_is_the_three_ruled_members_and_nothing_else()
    {
        // A literal pin, legitimate here and only for a CLOSED vocabulary the code owns and a human
        // changes by review (R-CS4 / seedsmith's SPECIES_KIND) — never for the species roster, which
        // is a derived population.
        Assert.Equal(new[] { "creature", "mimic", "excluded" }, AnchorSpeciesKind.All);
    }

    [Theory]
    [InlineData("creature", false)]
    [InlineData("mimic", false)]      // R-CS2: borrows a lawn type id, still counts as roster
    [InlineData("excluded", true)]
    [InlineData("", false)]
    [InlineData("Excluded", false)]   // ordinal equality, never case-folded
    [InlineData("exclud", false)]     // nor a prefix match
    public void The_generation_layer_reads_exclusion_from_the_readers_own_output(string kind, bool excluded)
    {
        Assert.Equal(excluded, AnchorSpeciesKind.IsExcluded(Read($"\"speciesKind\":\"{kind}\"}}")));
    }

    // ---- rank (spec-species-rank.md §1.7, creature-seed Task 5) ---------------------------------------

    [Fact]
    public void Rank_reads_verbatim_from_the_anchor()
    {
        var row = Read("\"rank\":\"fused\"}");
        Assert.Equal("fused", row.Rank);
    }

    [Fact]
    public void A_skipped_rank_reads_null_never_a_default()
    {
        // §1.7's sentinel mapping: seedsmith writes the literal "unresolved" when it skipped rank
        // (an unresolved threatBand or rarity), and C# models the same honest gap as null. An ABSENT
        // key — a pre-rank anchor — says the same thing and must read the same way.
        Assert.Null(Read("\"rank\":\"unresolved\"}").Rank);
        Assert.Null(Read("\"_provenance\":{}}").Rank);
    }
}
