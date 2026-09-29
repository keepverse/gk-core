using System.Text.Json;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-stage W12 (fog defect A): `Audience` is new on `TurnReportEntry`, and `report_json` rows
/// committed before it existed must still deserialize — the read path this module could otherwise
/// silently break for every already-persisted turn.
/// </summary>
public class TurnReportEntryTests
{
    [Fact]
    public void A_json_row_with_no_audience_property_deserializes_and_reads_as_no_audience()
    {
        // The exact shape `RpgStore.WorldTurns.cs`'s serializer produced before `Audience` existed —
        // no options, default `System.Text.Json` PascalCase names, no `Audience` property at all.
        const string oldRowJson = """
            [{"Phase":"pressure","Kind":"event","Subject":"dave","Detail":"loam.handicap:500","SectorId":null}]
            """;

        var entries = JsonSerializer.Deserialize<List<TurnReportEntry>>(oldRowJson);

        var entry = Assert.Single(entries!);
        Assert.Equal("pressure", entry.Phase);
        Assert.Equal("dave", entry.Subject);
        Assert.Equal("loam.handicap:500", entry.Detail);
        Assert.Null(entry.Audience);
    }

    [Fact]
    public void A_round_trip_through_the_same_serializer_preserves_a_set_audience()
    {
        var entry = new TurnReportEntry("pressure", "event", "dave", "loam.handicap:500", SectorId: null, Audience: "dave");
        var json = JsonSerializer.Serialize(new List<TurnReportEntry> { entry });

        var roundTripped = Assert.Single(JsonSerializer.Deserialize<List<TurnReportEntry>>(json)!);
        Assert.Equal("dave", roundTripped.Audience);
    }
}
