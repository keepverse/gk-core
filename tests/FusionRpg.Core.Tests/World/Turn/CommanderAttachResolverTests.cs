using FusionRpg.Core.World;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Turn;

/// <summary>`commander-roster` EP3.8 — `attach-commander` / `detach-commander` at resolution
/// (spec-legion-commander.md "Checks split by where the fact lives"). The two store-side reasons
/// (`commander.role.missing`, `commander.not-at-base`) are Data's; these four are the world graph's,
/// re-validated at `Snapshot` so a legion lost the same turn cannot keep a commander.</summary>
public class CommanderAttachResolverTests
{
    static (WorldState World, WorldEntity Legion) WorldWithLegion()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 99, worldId: "w-cmd");
        var legion = new WorldEntity
        {
            EntityId = "legion-test",
            Kind = WorldEntityKind.Legion,
            OwnerFactionId = "f1",
            AtSectorId = world.Sectors.First().SectorId,
            Members = Array.Empty<WorldEntityMember>(),
        };
        return (world with { Entities = world.Entities.Append(legion).ToList() }, legion);
    }

    static WorldCommand Attach(WorldEntity legion, string faction = "f1") => new()
    {
        CommandId = "c-attach",
        Kind = WorldCommandKinds.AttachCommander,
        EntityId = legion.EntityId,
        CommanderId = faction,
        MemberInstanceId = "spec-1",
        MemberSpeciesId = "fumeshroom",
        MemberLevel = 3,
    };

    static WorldState Run(WorldState world, TurnReport report, params WorldCommand[] commands) =>
        CommanderAttachResolver.Run(world, commands, report, TurnEngine.Phases.Snapshot);

    static string? Dropped(TurnReport report) => report.Entries
        .Where(e => e.Kind == TurnReportKinds.CommandDropped)
        .Select(e => e.Detail)
        .FirstOrDefault();

    [Fact]
    public void A_resolved_attach_seats_one_Commander_member_carrying_the_stamped_facts()
    {
        var (world, legion) = WorldWithLegion();
        var report = new TurnReport();

        var next = Run(world, report, Attach(legion));

        var member = Assert.Single(next.Entities.Single(e => e.EntityId == legion.EntityId).Members);
        Assert.Equal(WorldEntityMemberRole.Commander, member.Role);
        Assert.Equal("spec-1", member.InstanceId);
        Assert.Equal("fumeshroom", member.SpeciesId);
        Assert.Equal(3, member.Level);
        Assert.True(member.Hp > 0, "a seated commander carries the world layer's member hp");
        Assert.Equal(0, member.Wounds);
        Assert.Empty(report.Entries.Where(e => e.Kind == TurnReportKinds.CommandDropped));
    }

    [Fact]
    public void A_legion_that_is_gone_is_re_validated_and_drops_by_name()
    {
        var (world, legion) = WorldWithLegion();
        var report = new TurnReport();
        var orphan = Attach(legion) with { EntityId = "no-such-legion" };

        var next = Run(world, report, orphan);

        Assert.Equal(CommanderAttachResolver.LegionGone, Dropped(report));
        Assert.Empty(next.Entities.Single(e => e.EntityId == legion.EntityId).Members);
    }

    [Fact]
    public void Another_factions_legion_drops_legion_not_yours()
    {
        var (world, legion) = WorldWithLegion();
        var report = new TurnReport();

        Run(world, report, Attach(legion, faction: "f2"));

        Assert.Equal(CommanderAttachResolver.LegionNotYours, Dropped(report));
    }

    [Fact]
    public void A_marching_legion_drops_legion_marching()
    {
        var (world, legion) = WorldWithLegion();
        var marching = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == legion.EntityId
                    ? e with { OnLaneId = "lane-1", OnLaneTowardSectorId = "sector-2" }
                    : e)
                .ToList(),
        };
        var report = new TurnReport();

        Run(marching, report, Attach(legion));

        Assert.Equal(CommanderAttachResolver.LegionMarching, Dropped(report));
    }

    [Fact]
    public void One_commander_per_legion_is_a_structural_limit()
    {
        var (world, legion) = WorldWithLegion();
        var first = Run(world, new TurnReport(), Attach(legion));
        var report = new TurnReport();

        var second = Run(first, report, Attach(legion) with { CommandId = "c-2", MemberInstanceId = "spec-2" });

        Assert.Equal(CommanderAttachResolver.LegionHasCommander, Dropped(report));
        // The structural limit is why: a legion has ONE leader; the first commander is untouched.
        var member = Assert.Single(second.Entities.Single(e => e.EntityId == legion.EntityId).Members);
        Assert.Equal("spec-1", member.InstanceId);
    }

    [Fact]
    public void Detach_removes_the_commander_and_is_a_no_op_when_there_is_none()
    {
        var (world, legion) = WorldWithLegion();
        var seated = Run(world, new TurnReport(), Attach(legion));
        var detach = new WorldCommand
        {
            CommandId = "c-detach",
            Kind = WorldCommandKinds.DetachCommander,
            EntityId = legion.EntityId,
            CommanderId = "f1",
        };
        var report = new TurnReport();

        var emptied = Run(seated, report, detach);

        Assert.Empty(emptied.Entities.Single(e => e.EntityId == legion.EntityId).Members);
        Assert.Empty(report.Entries.Where(e => e.Kind == TurnReportKinds.CommandDropped));

        // A second detach has nothing to remove and refuses nothing.
        var again = new TurnReport();
        var stillEmpty = Run(emptied, again, detach);
        Assert.Empty(stillEmpty.Entities.Single(e => e.EntityId == legion.EntityId).Members);
        Assert.Empty(again.Entries.Where(e => e.Kind == TurnReportKinds.CommandDropped));
    }
}
