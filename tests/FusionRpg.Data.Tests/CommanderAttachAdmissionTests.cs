using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>`commander-roster` EP3.8's Data half (spec-legion-commander.md's check table): the role and
/// the specimen's base are STORE facts, so they are admitted here moments before Core's own Snapshot
/// re-validation. A dropped attach is recorded by name and never gates the turn.</summary>
public class CommanderAttachAdmissionTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly EmpireRef _empire;

    public CommanderAttachAdmissionTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _empire = new EmpireRef(new SaveId(_playerId), _store.HumanEmpireOf(_playerId));
    }

    public void Dispose() => _testStore.Dispose();

    WorldCommand Attach(string instanceId) => new()
    {
        CommandId = "c-attach",
        Kind = WorldCommandKinds.AttachCommander,
        EntityId = "legion-1",
        CommanderId = _playerId.ToString(),
        MemberInstanceId = instanceId,
        MemberSpeciesId = "",
        MemberLevel = 0,
    };

    [Fact]
    public void A_specimen_without_the_role_is_dropped_as_commander_role_missing()
    {
        var plain = _store.CreateUniqueActor(_playerId, "plant", 3);

        var kept = _store.ValidateCommanderAttachCommands("w-1", new[] { Attach(plain.InstanceId) });

        Assert.Empty(kept);
    }

    [Fact]
    public void A_commander_that_is_not_at_base_is_dropped()
    {
        // The role is real; the specimen has no creature profile / is not at base, which is the second
        // Data-side reason. (A seated or deployed specimen takes the same path.)
        var actor = _store.CreateUniqueActor(_playerId, "plant", 4);
        Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);

        var kept = _store.ValidateCommanderAttachCommands("w-1", new[] { Attach(actor.InstanceId) });

        Assert.Empty(kept);
    }

    [Fact]
    public void A_non_attach_command_passes_through_untouched()
    {
        var other = new WorldCommand
        {
            CommandId = "c-move",
            Kind = WorldCommandKinds.Move,
            EntityId = "legion-1",
            CommanderId = _playerId.ToString(),
        };

        var kept = _store.ValidateCommanderAttachCommands("w-1", new[] { other });

        Assert.Same(other, Assert.Single(kept));
    }
}
