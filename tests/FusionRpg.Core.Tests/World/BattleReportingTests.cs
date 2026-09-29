using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-stage W12 (fog defect A): a battle line reached everybody because `BattleReporting.Fight`
/// never named a sector (`VisibleTo` reads a null `SectorId` as "shown to everyone"). A sector-kind
/// battle now carries its ground; a lane-kind crossing must NOT carry `LocationId` there — its
/// `LocationId` is a lane id (`BattleSeam.cs:34`), and putting a lane id in the sector slot is
/// exactly the class of bug world-stage W13 exists to fix elsewhere.
/// </summary>
public class BattleReportingTests
{
    static WorldEntity Fighter(string id, string owner, string atSectorId) => new()
    {
        EntityId = id, Kind = WorldEntityKind.Legion, OwnerFactionId = owner, AtSectorId = atSectorId,
        Members = new[] { new WorldEntityMember { SpeciesId = "grunt", Hp = 100 } }
    };

    static WorldState World(params WorldEntity[] entities) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[]
        {
            new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" },
            new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Zomboss" }
        },
        Sectors = new[] { new WorldSector { SectorId = "s1", OwnerFactionId = "dave", Phase = SectorPhase.Held } },
        Entities = entities
    };

    [Fact]
    public void A_sector_battle_carries_its_own_sector_id()
    {
        var world = World(
            Fighter("e-dave", "dave", "s1"),
            Fighter("e-zomboss", "zomboss", "s1"));
        var request = new BattleRequest
        {
            BattleId = "b1", Kind = BattleKinds.Sector, LocationId = "s1",
            AttackerEntityId = "e-dave", DefenderEntityId = "e-zomboss"
        };

        var report = new TurnReport();
        BattleReporting.Fight(world, request, DistrictAssaultResolver.Instance, report, "battles", seed: 1);

        var line = Assert.Single(report.Entries, e => e.Kind == TurnReportKinds.Battle);
        Assert.Equal("s1", line.SectorId);
    }

    [Fact]
    public void A_lane_crossing_battle_does_not_put_its_lane_id_in_the_sector_slot()
    {
        var world = World(
            Fighter("e-dave", "dave", null!) with { OnLaneId = "l-1", OnLaneTowardSectorId = "s1" },
            Fighter("e-zomboss", "zomboss", null!) with { OnLaneId = "l-1", OnLaneTowardSectorId = "s1" });
        var request = new BattleRequest
        {
            BattleId = "b1", Kind = BattleKinds.Lane, LocationId = "l-1",
            AttackerEntityId = "e-dave", DefenderEntityId = "e-zomboss"
        };

        var report = new TurnReport();
        BattleReporting.Fight(world, request, DistrictAssaultResolver.Instance, report, "battles", seed: 1);

        var line = Assert.Single(report.Entries, e => e.Kind == TurnReportKinds.Battle);
        Assert.Null(line.SectorId);
        // The lane id is still legible in Detail, which the client already reads for a crossing.
        Assert.Contains("l-1", line.Detail);
    }
}
