using System.Text.Json;
using Xunit;

namespace FusionRpg.Core.Tests.Stats.Aptitudes;

using FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// Web-match log rows carry the squad's <c>BattleHubInputs</c> as setupJson, and the boot
/// sweep re-resolves them — so every HubInput member must survive a JSON round-trip. A
/// summoned specimen's squad actor carries a non-null <c>Aptitude</c>, which made every
/// sweep after a summon test crash with <c>NotSupportedException</c> instead of healing.
/// </summary>
public class AptitudeAllocationJsonTests
{
    static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    [Fact]
    public void Empty_round_trips_to_empty()
    {
        var back = RoundTrip(AptitudeAllocation.Empty);
        Assert.Equal(0, back.GrandTotal());
    }

    [Fact]
    public void A_single_allocation_round_trips_point_for_point()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 7)
            + AptitudeAllocation.Single(AllocationScope.Commander, "Focus", 3);

        var back = RoundTrip(allocation);

        Assert.Equal(7, back.PointsAt(AllocationScope.UniqueCreature, "Might"));
        Assert.Equal(3, back.PointsAt(AllocationScope.Commander, "Focus"));
        Assert.Equal(0, back.PointsAt(AllocationScope.Aspect, "Might"));
        Assert.Equal(10, back.GrandTotal());
    }

    [Fact]
    public void A_legacy_empty_object_reads_as_empty()
    {
        // Before round-trip support, the type serialized as `{}` (no public properties).
        // Rows written in that shape must still read back, as empty.
        var back = JsonSerializer.Deserialize<AptitudeAllocation>("{}")!;
        Assert.Equal(0, back.GrandTotal());
    }
}
