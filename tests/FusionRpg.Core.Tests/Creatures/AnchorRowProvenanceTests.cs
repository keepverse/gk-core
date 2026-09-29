using FusionRpg.Core.Creatures.Generation;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// threat-band-fill T3: the reader surfaces the anchor's own threatBand provenance stamp for
/// the quality report's scored/default/authored split. The stamp is read-only here — nothing
/// downstream decides on it — and absent stamps read as null (pre-fill corpus), never a refusal.
/// </summary>
public class AnchorRowProvenanceTests
{
    const string Prefix = """[{"speciesId":"x","rarity":"fused","threatBand":"raider","aptitudePrimary":"Might","aptitudeSecondary":"none","pure":true,"attackTempo":"steady","reach":"short","variants":[],"side":"plant","gameTypeId":1,"elementPrimary":"earth","elementSecondary":"none","deployMode":"field","acquisition":[],"traits":[],"targetPreference":"frontline",""";

    [Theory]
    [InlineData("\"scored\"", "scored")]
    [InlineData("\"default\"", "default")]
    [InlineData("\"high\"", "high")]
    [InlineData("\"deterministic-fallback\"", "deterministic-fallback")]
    public void ThreatBandConfidence_reads_the_stamp_verbatim(string stampJson, string expected)
    {
        var row = Assert.Single(AnchorRowReader.ReadAll(
            Prefix + "\"_provenance\":{\"confidence\":{\"threatBand\":" + stampJson + "}}}]"));
        Assert.Equal(expected, row.ThreatBandConfidence);
    }

    [Fact]
    public void ThreatBandConfidence_is_null_when_the_anchor_carries_no_stamp()
    {
        var row = Assert.Single(AnchorRowReader.ReadAll(Prefix + "\"_provenance\":{}}]"));
        Assert.Null(row.ThreatBandConfidence);
    }
}
