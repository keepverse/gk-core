using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests;

/// <summary>`commander-roster` EP3.9 — `ParentOf(instanceId)` and the two child admissions that consult
/// it (spec-legion-commander.md "The parent rule (R-C1)"). A child starts from HOME or from a STATIONED
/// legion, never from one that is marching; the parent is read from the world graph, not from a flag.
/// </summary>
public class ParentOfTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly EmpireRef _empire;

    public ParentOfTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _empire = new EmpireRef(new SaveId(_playerId), _store.HumanEmpireOf(_playerId));
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>A committed world holding one legion whose only member is <paramref name="instanceId"/> —
    /// stationed (on ground) or marching (on a lane), the two states the rule separates.</summary>
    string WorldWithLegion(string instanceId, bool marching)
    {
        var worldId = marching ? "w-march" : "w-stationed";
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 99, worldId: worldId);
        var lane = built.Lanes.First();
        var legion = new WorldEntity
        {
            EntityId = "legion-1",
            Kind = WorldEntityKind.Legion,
            OwnerFactionId = built.Factions.First().FactionId,
            AtSectorId = marching ? null : built.Sectors.First().SectorId,
            OnLaneId = marching ? lane.LaneId : null,
            OnLaneTowardSectorId = marching ? lane.ToSectorId : null,
            Members = new[]
            {
                new WorldEntityMember
                {
                    InstanceId = instanceId,
                    SpeciesId = "fumeshroom",
                    Level = 3,
                    Hp = 100,
                    Role = WorldEntityMemberRole.Commander,
                },
            },
        };

        var (ok, reason, _) = _store.CreateWorld(_playerId, built with
        {
            Entities = built.Entities.Append(legion).ToList(),
        });
        Assert.True(ok, reason);
        return worldId;
    }

    [Fact]
    public void A_specimen_in_no_legion_is_at_home()
    {
        var parent = _store.ParentOf("spec-loose");
        Assert.True(parent.IsHome);
        Assert.True(parent.AdmitsChild);
    }

    [Fact]
    public void A_stationed_legion_is_reported_as_a_legion_that_admits_a_child()
    {
        WorldWithLegion("spec-1", marching: false);

        var parent = _store.ParentOf("spec-1");

        Assert.False(parent.IsHome);
        Assert.Equal("legion-1", parent.LegionEntityId);
        Assert.True(parent.Stationed);
        Assert.True(parent.AdmitsChild);
    }

    [Fact]
    public void A_marching_legion_is_reported_as_a_legion_that_admits_no_child()
    {
        WorldWithLegion("spec-1", marching: true);

        var parent = _store.ParentOf("spec-1");

        Assert.False(parent.IsHome);
        Assert.False(parent.Stationed);
        Assert.False(parent.AdmitsChild);
    }

    [Fact]
    public void Lawn_deploy_admits_from_home_and_refuses_from_a_marching_legion()
    {
        var fromHome = _store.CreateUniqueActor(_playerId, "plant", 3);
        Assert.True(_store.TryBeginUniqueDeploy(fromHome.InstanceId, "corr-home").Ok);

        var stationed = _store.CreateUniqueActor(_playerId, "plant", 4);
        WorldWithLegion(stationed.InstanceId, marching: false);
        Assert.True(_store.TryBeginUniqueDeploy(stationed.InstanceId, "corr-stationed").Ok);

        var marching = _store.CreateUniqueActor(_playerId, "plant", 5);
        // A second world carrying the marching legion for THIS specimen.
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 7, worldId: "w-second");
        var lane = built.Lanes.First();
        var legion = new WorldEntity
        {
            EntityId = "legion-2",
            Kind = WorldEntityKind.Legion,
            OwnerFactionId = built.Factions.First().FactionId,
            AtSectorId = null,
            OnLaneId = lane.LaneId,
            OnLaneTowardSectorId = lane.ToSectorId,
            Members = new[]
            {
                new WorldEntityMember
                {
                    InstanceId = marching.InstanceId, SpeciesId = "fumeshroom", Level = 1, Hp = 100,
                    Role = WorldEntityMemberRole.Commander,
                },
            },
        };
        Assert.True(_store.CreateWorld(_playerId, built with
        {
            Entities = built.Entities.Append(legion).ToList(),
        }).Ok);

        var refused = _store.TryBeginUniqueDeploy(marching.InstanceId, "corr-marching");

        Assert.False(refused.Ok);
        Assert.Equal("not-at-base", refused.Reason);
    }

    [Fact]
    public void The_seat_admits_from_a_stationed_legion_and_refuses_a_marching_one()
    {
        var previous = CommanderDirectoryHub.Current;
        var authored = DataCommanderDirectory.Parse(File.ReadAllText(Path.Combine(
            KeepverseRoots.Content(), "data", "seed", "commanders", "_registry", "default-commanders.v1.json")));
        CommanderDirectoryHub.Configure(authored.WithSource(new UniqueCommanderSource(authored, _store)));
        try
        {
            var stationed = _store.CreateUniqueActor(_playerId, "plant", 6);
            Assert.True(_store.GrantCommanderRole(_empire, stationed.InstanceId).Ok);
            WorldWithLegion(stationed.InstanceId, marching: false);
            var seated = _store.SeatLeadingCommander(
                UniqueCommanderSource.UniquePrefix + stationed.InstanceId, "m-stationed");
            Assert.True(seated.Seated, seated.Reason);

            var marching = _store.CreateUniqueActor(_playerId, "plant", 7);
            Assert.True(_store.GrantCommanderRole(_empire, marching.InstanceId).Ok);
            WorldWithLegion(marching.InstanceId, marching: true);
            var refused = _store.SeatLeadingCommander(
                UniqueCommanderSource.UniquePrefix + marching.InstanceId, "m-march");
            Assert.False(refused.Seated);
            Assert.Equal(RpgStore.SeatNotAtBase, refused.Reason);
        }
        finally
        {
            CommanderDirectoryHub.Configure(previous);
        }
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    [Fact]
    public void Expedition_admission_refuses_a_marching_legion_and_does_not_refuse_home_or_a_stationed_one()
    {
        // The parent rule is checked FIRST in the squad loop, so a marching legion's member is refused
        // for THAT reason even though this fixture's specimens have no contract/profile — which is
        // exactly why the other two cases assert "refused for something else, not for the parent rule".
        var marching = _store.CreateUniqueActor(_playerId, "plant", 12);
        WorldWithLegion(marching.InstanceId, marching: true);
        var refused = _store.DispatchExpedition(_playerId, "exp-marching", "scout-30m", new[] { marching.InstanceId }, seed: 3);
        Assert.False(refused.Ok);
        Assert.Equal("not-at-base", refused.Reason);

        var home = _store.CreateUniqueActor(_playerId, "plant", 13);
        var homeOutcome = _store.DispatchExpedition(_playerId, "exp-home", "scout-30m", new[] { home.InstanceId }, seed: 3);
        Assert.False(homeOutcome.Ok);
        Assert.NotEqual("not-at-base", homeOutcome.Reason);

        var stationed = _store.CreateUniqueActor(_playerId, "plant", 14);
        WorldWithLegion(stationed.InstanceId, marching: false);
        var stationedOutcome = _store.DispatchExpedition(_playerId, "exp-stationed", "scout-30m", new[] { stationed.InstanceId }, seed: 3);
        Assert.False(stationedOutcome.Ok);
        Assert.NotEqual("not-at-base", stationedOutcome.Reason);
    }
}
