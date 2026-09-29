using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// L30 (spec-loam-ai.md): `Sever`'s fire threshold, harness-tuned the same way every other constant
/// in this program is — named explicitly, proven against representative shapes rather than picked
/// and hoped for. A genuine articulation point's reconnection cost runs into
/// <c>AllPairsCost.Unreachable</c>-scale territory (the map's cross-cluster pairs go fully
/// unreachable); a redundant sector's is near zero. The gap between the two is enormous by
/// construction, so <see cref="FrontierRulesPolicy.SeveranceThresholdCost"/> only needs to sit
/// somewhere comfortably inside it — this proves it does, across more than one shape.
/// </summary>
public class SeveranceThresholdTests
{
    readonly ITestOutputHelper _out;
    public SeveranceThresholdTests(ITestOutputHelper output) => _out = output;

    const string Dave = "dave";
    const string Zomboss = "zomboss";

    static WorldState Fixture(WorldState shape) => shape with
    {
        Factions = new[]
        {
            new WorldFaction { FactionId = Dave, Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = Zomboss, Kind = WorldFactionKind.Zomboss, Name = "Z" }
        }
    };

    static IWorldView FullyScoutedEnemy(WorldState world, IEnumerable<string> owned)
    {
        var ownedSet = owned.ToHashSet(StringComparer.Ordinal);
        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => ownedSet.Contains(s.SectorId) ? s with { OwnerFactionId = Zomboss } : s)
                .ToList()
        };

        var snapshots = dressed.Sectors
            .Where(s => ownedSet.Contains(s.SectorId))
            .Select(s => new IntelSnapshot
            {
                SectorId = s.SectorId, LastSeenTurn = 0, Detail = SectorSight.Full,
                OwnerFactionId = s.OwnerFactionId, Phase = s.Phase, Climate = s.Climate, DangerBand = s.DangerBand
            })
            .OrderBy(s => s.SectorId, StringComparer.Ordinal)
            .ToList();

        return new BelievedWorldView(
            dressed with { Intel = new[] { new FactionIntel { FactionId = Dave, Sectors = snapshots } } },
            Dave);
    }

    [Fact]
    public void The_threshold_fires_on_the_barbells_neck_and_declines_every_other_sector()
    {
        // "c-d" is the single lane joining the two triangle clusters — a bridge, so *both* of its
        // ends are articulation points: losing either one splits the enemy's holdings in two. Every
        // corner that is not an end of that bridge has a direct alternate edge and cuts nothing.
        var world = Fixture(GraphShapes.Barbell());
        var everySector = world.Sectors.Select(s => s.SectorId).ToList();
        var view = FullyScoutedEnemy(world, everySector);
        var bridgeEnds = new HashSet<string>(StringComparer.Ordinal) { "c", "d" };

        foreach (var sectorId in everySector)
        {
            var score = SeveranceScore.For(view, Zomboss, sectorId);
            var shouldFire = bridgeEnds.Contains(sectorId);
            var fires = score > FrontierRulesPolicy.SeveranceThresholdCost;

            _out.WriteLine($"{sectorId}: severance {score}, fires {fires}");
            Assert.True(fires == shouldFire,
                $"'{sectorId}' scored {score} — expected fires={shouldFire}, got {fires}");
        }
    }

    [Fact]
    public void The_threshold_fires_on_the_stars_hub_and_declines_every_spoke()
    {
        // hub-x, hub-y, hub-z: the hub cuts everything (three now-mutually-unreachable spokes), a
        // spoke cuts nothing (the other two spokes were never routed through it).
        var world = Fixture(GraphShapes.Star());
        var everySector = world.Sectors.Select(s => s.SectorId).ToList();
        var view = FullyScoutedEnemy(world, everySector);

        foreach (var sectorId in everySector)
        {
            var score = SeveranceScore.For(view, Zomboss, sectorId);
            var shouldFire = sectorId == "hub";
            var fires = score > FrontierRulesPolicy.SeveranceThresholdCost;

            _out.WriteLine($"{sectorId}: severance {score}, fires {fires}");
            Assert.True(fires == shouldFire,
                $"'{sectorId}' scored {score} — expected fires={shouldFire}, got {fires}");
        }
    }

    [Fact]
    public void A_ring_has_no_articulation_point_and_the_threshold_never_fires()
    {
        // Every member has a way round (spec-world-topology.md's own framing) — nothing here should
        // ever look like a worthwhile cut.
        var world = Fixture(GraphShapes.Ring());
        var everySector = world.Sectors.Select(s => s.SectorId).ToList();
        var view = FullyScoutedEnemy(world, everySector);

        foreach (var sectorId in everySector)
        {
            var score = SeveranceScore.For(view, Zomboss, sectorId);
            Assert.False(score > FrontierRulesPolicy.SeveranceThresholdCost,
                $"'{sectorId}' scored {score}, above the threshold on a ring with no articulation point");
        }
    }
}
