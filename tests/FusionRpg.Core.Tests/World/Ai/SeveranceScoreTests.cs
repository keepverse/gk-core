using FusionRpg.Core.Tests.World.Topology;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// L30 (spec-loam-ai.md §8.7/§12.3): "how much would cutting this cost the enemy" —
/// <c>ReconnectionCost.For</c> pointed at a target faction's *believed* holdings instead of the
/// viewer's own. Scouting-gated by construction, not by accident: this must read near-zero until
/// the viewer has actually surveyed the target's territory as enemy-owned.
/// </summary>
public class SeveranceScoreTests
{
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

    /// <summary>Dave's own belief: every sector in <paramref name="scouted"/> is fully-surveyed Zomboss ground.</summary>
    static IWorldView FullyScoutedEnemy(WorldState world, IEnumerable<string> owned, IEnumerable<string>? scouted = null)
    {
        var ownedSet = owned.ToHashSet(StringComparer.Ordinal);
        var scoutedSet = (scouted ?? owned).ToHashSet(StringComparer.Ordinal);

        var dressed = world with
        {
            Sectors = world.Sectors
                .Select(s => ownedSet.Contains(s.SectorId) ? s with { OwnerFactionId = Zomboss } : s)
                .ToList()
        };

        var snapshots = dressed.Sectors
            .Where(s => scoutedSet.Contains(s.SectorId))
            .Select(s => new IntelSnapshot
            {
                SectorId = s.SectorId,
                LastSeenTurn = 0,
                Detail = SectorSight.Full,
                OwnerFactionId = s.OwnerFactionId,
                Phase = s.Phase,
                Climate = s.Climate,
                DangerBand = s.DangerBand
            })
            .OrderBy(s => s.SectorId, StringComparer.Ordinal)
            .ToList();

        return new BelievedWorldView(
            dressed with { Intel = new[] { new FactionIntel { FactionId = Dave, Sectors = snapshots } } },
            Dave);
    }

    [Fact]
    public void A_genuine_articulation_point_scores_higher_than_a_redundant_sector()
    {
        // "d" is the neck joining two triangle clusters — cutting it splits the enemy's territory
        // in two. "a" is one corner of the west triangle: b-c already connects directly, so losing
        // "a" lengthens nothing.
        var world = Fixture(GraphShapes.Barbell());
        var everySector = world.Sectors.Select(s => s.SectorId).ToList();
        var view = FullyScoutedEnemy(world, everySector);

        var neck = SeveranceScore.For(view, Zomboss, "d");
        var corner = SeveranceScore.For(view, Zomboss, "a");

        Assert.True(neck > 0, "an articulation point should score above zero once scouted");
        Assert.True(neck > corner, $"the neck ({neck}) should score higher than a redundant corner ({corner})");
    }

    [Fact]
    public void A_mostly_unscouted_enemy_territory_reads_near_zero()
    {
        // Accepted, not a bug (spec-loam-ai.md): ReconnectionCost gates itself below three sectors
        // in scope, so scouting only one of Zomboss's five holdings is the degenerate case, and the
        // score must read zero there — a passing test, not a bug report.
        var world = Fixture(GraphShapes.Barbell());
        var everySector = world.Sectors.Select(s => s.SectorId).ToList();
        var view = FullyScoutedEnemy(world, everySector, scouted: new[] { "d" });

        Assert.Equal(0, SeveranceScore.For(view, Zomboss, "d"));
    }

    [Fact]
    public void A_sector_the_target_does_not_hold_scores_zero()
    {
        var world = Fixture(GraphShapes.Barbell());
        var everySector = world.Sectors.Select(s => s.SectorId).ToList();
        var view = FullyScoutedEnemy(world, everySector);

        Assert.Equal(0, SeveranceScore.For(view, "nobody", "d"));
    }

    [Fact]
    public void Two_sectors_can_never_be_severed_from_each_other()
    {
        var world = Fixture(GraphShapes.From("x-y"));
        var view = FullyScoutedEnemy(world, new[] { "x", "y" });

        Assert.Equal(0, SeveranceScore.For(view, Zomboss, "x"));
        Assert.Equal(0, SeveranceScore.For(view, Zomboss, "y"));
    }
}
