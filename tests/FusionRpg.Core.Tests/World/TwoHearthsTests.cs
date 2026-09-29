using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Topology;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// L18 acceptance (spec-loam-maps.md): one test per design-target row, each named for the property
/// and each failing if the map drifts. W37's scar: a prediction written down and never enforced is
/// a prediction the map will eventually break. A test is what holds a map to its purpose.
/// </summary>
public class TwoHearthsTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 7);

    [Fact]
    public void Rootbed_scarcity_only_a_few_of_sixteen_sectors_carry_one()
    {
        var w = World();
        var withRootbed = w.Sectors.Count(s => Habitability.For(s));

        Assert.InRange(withRootbed, 3, 6); // "~4 of 16" — a range, the map is authored, not tuned to a number
    }

    [Fact]
    public void Barren_corridors_carry_neither_a_seat_nor_a_rootbed()
    {
        var w = World();
        var barren = w.Sectors.Where(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.NoBase)).ToList();

        Assert.InRange(barren.Count, 5, 8); // "~6"
        Assert.All(barren, s => Assert.False(Habitability.For(s), $"{s.SectorId} is barren but has a rootbed"));
        Assert.All(barren, s => Assert.DoesNotContain(s.Slots, sl => sl.SlotTypeId == SlotTypeCatalog.SeatSlotTypeId));
    }

    [Fact]
    public void A_chaos_gradient_runs_from_calm_capitals_to_a_fierce_midpoint()
    {
        var w = World();
        int IntensityOf(string id) => w.Sectors.Single(s => s.SectorId == id).FractureIntensityMilli;

        var capitals = new[] { IntensityOf("d-home"), IntensityOf("d-flank-1"), IntensityOf("z-flank-1"), IntensityOf("z-home") };
        var midpoint = IntensityOf("hot-ground");

        Assert.All(capitals, i => Assert.True(i < 1000, $"capital intensity {i} is not well below baseline"));
        Assert.True(midpoint > 2000, "the midpoint must be well above baseline");
        Assert.All(capitals, i => Assert.True(midpoint > i, "the midpoint must be fiercer than every capital"));
    }

    [Fact]
    public void A_severable_waist_splits_a_factions_territory_into_two_components()
    {
        var w = World();

        // Cutting Dave's single lane to his outpost must leave him with two components: the capital
        // loop, and the outpost alone.
        var severed = w with
        {
            Lanes = w.Lanes.Select(l => l.LaneId == "l-df2-do" ? l with { State = LaneState.Severed } : l).ToList()
        };

        var components = TerritoryComponents.For(severed, "dave");
        Assert.Equal(2, components.Count);
        Assert.Contains(components, c => c.Contains("d-outpost") && c.Count == 1);
    }

    [Fact]
    public void A_hot_sector_between_the_capitals_carries_several_rootbeds_at_high_intensity()
    {
        var w = World();
        var hot = w.Sectors.Single(s => s.SectorId == "hot-ground");
        var rootbedCount = hot.Slots.Count(sl => sl.SlotTypeId == SlotTypeCatalog.RootbedSlotTypeId);

        Assert.True(rootbedCount >= 2, "the hot sector must carry several rootbeds, not just one");
        Assert.True(hot.FractureIntensityMilli > 2000, "the hot sector must be fiercer than ordinary corridor ground");
    }

    [Fact]
    public void Two_capitals_both_habitable_dense_and_losable_as_clusters_not_home_flags()
    {
        var w = World();

        var daveCapital = new[] { "d-home", "d-flank-1" };
        var zombossCapital = new[] { "z-flank-1", "z-home" };

        foreach (var id in daveCapital.Concat(zombossCapital))
        {
            var sector = w.Sectors.Single(s => s.SectorId == id);
            Assert.True(Habitability.For(sector), $"{id} must be habitable — it is a capital sector");
        }

        // "Capital" means a cluster, not a second Flags.Home — the validator still enforces exactly one.
        var homeSectors = w.Sectors.Where(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home)).ToList();
        Assert.Single(homeSectors);
        Assert.Equal("d-home", homeSectors[0].SectorId);

        // Zomboss's capital is losable precisely because it is ordinary owned ground, not a
        // homeworld — nothing structural protects it, which is the point.
        Assert.Equal("zomboss", w.Sectors.Single(s => s.SectorId == "z-home").OwnerFactionId);
    }

    [Fact]
    public void At_least_two_articulation_points_are_measured_not_assumed()
    {
        var w = World();
        var graph = LaneGraph.Build(w);
        var cuts = ArticulationPoints.Find(graph);

        Assert.True(cuts.Count >= 2, $"expected at least 2 articulation points, measured {cuts.Count}");
        // The two waists must be among them — they are the sharpest cuts on the map by design.
        Assert.Contains("d-flank-2", cuts);
        Assert.Contains("z-flank-2", cuts);
    }

    [Fact]
    public void Two_hearths_builds_deterministically_and_in_stable_order()
    {
        var a = World();
        var b = World();

        Assert.Equal(WorldCanonical.Write(a), WorldCanonical.Write(b));
        Assert.Equal(a.Sectors.Select(s => s.SectorId).OrderBy(x => x, StringComparer.Ordinal), a.Sectors.Select(s => s.SectorId));
        Assert.Equal(a.Lanes.Select(l => l.LaneId).OrderBy(x => x, StringComparer.Ordinal), a.Lanes.Select(l => l.LaneId));
        Assert.Equal(a.Entities.Select(e => e.EntityId).OrderBy(x => x, StringComparer.Ordinal), a.Entities.Select(e => e.EntityId));
    }
}
