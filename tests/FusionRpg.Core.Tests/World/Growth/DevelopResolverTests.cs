using FusionRpg.Core.World;
using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Growth;

/// <summary>
/// world-map W52 acceptance: the catalog validates at static init and rejects unknown ids at the
/// write gate; `develop` is rejected with its own reason for each illegal case; no `switch (id)` over
/// project ids anywhere. Direct-call, resolver-level tests — <see cref="DevelopThreadingTests"/>
/// proves the same mechanism reached through a real <see cref="TurnEngine.Step"/> commit.
/// </summary>
public class DevelopResolverTests
{
    const string Phase = "Test";
    const string ProjectId = "raise-development-placeholder";

    static WorldSector Sector(string id, string? owner, long loamStock, string? projectId = null, int? turnsRemaining = null) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = owner, LoamStock = loamStock,
            ProjectId = projectId, ProjectTurnsRemaining = turnsRemaining
        };

    static WorldState World(params WorldSector[] sectors) => new()
    {
        WorldId = "w", TemplateId = "test", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
        Sectors = sectors.OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList()
    };

    static WorldCommand Develop(string commander, string commandId, string? sectorId, string? projectId = ProjectId) => new()
    {
        CommanderId = commander, CommandId = commandId, Kind = WorldCommandKinds.Develop,
        SectorId = sectorId, ProjectId = projectId
    };

    [Fact]
    public void ProjectCatalog_validates_at_static_init_and_the_placeholder_row_is_known()
    {
        Assert.NotEmpty(ProjectCatalog.All);
        Assert.True(ProjectCatalog.IsKnown(ProjectId));
        Assert.False(ProjectCatalog.IsKnown("no-such-project"));
        Assert.Throws<ArgumentException>(() => ProjectCatalog.Get("no-such-project"));
    }

    [Fact]
    public void An_affordable_owned_sector_starts_the_project_and_spends_loam_stock()
    {
        var project = ProjectCatalog.Get(ProjectId);
        var world = World(Sector("s1", "f1", loamStock: project.CostMilli));
        var report = new TurnReport();

        var result = DevelopResolver.Run(world, new[] { Develop("f1", "c1", "s1") }, report, Phase);

        var sector = result.Sectors.Single();
        Assert.Equal(ProjectId, sector.ProjectId);
        Assert.Equal(project.ProjectTurns, sector.ProjectTurnsRemaining);
        Assert.Equal(0, sector.LoamStock);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.Event && e.Detail == "develop.started:" + ProjectId);
        Assert.Empty(report.Dropped);
    }

    [Fact]
    public void A_sector_the_commander_no_longer_owns_at_resolution_is_refused_not_yours()
    {
        var world = World(Sector("s1", "someone-else", loamStock: 1000));
        var report = new TurnReport();

        var result = DevelopResolver.Run(world, new[] { Develop("f1", "c1", "s1") }, report, Phase);

        Assert.Null(result.Sectors.Single().ProjectId);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "develop.not-yours");
    }

    [Fact]
    public void A_sector_already_developing_refuses_a_second_project_already_developing()
    {
        var world = World(Sector("s1", "f1", loamStock: 1000, projectId: ProjectId, turnsRemaining: 1));
        var report = new TurnReport();

        var result = DevelopResolver.Run(world, new[] { Develop("f1", "c1", "s1") }, report, Phase);

        Assert.Equal(1, result.Sectors.Single().ProjectTurnsRemaining); // unchanged
        Assert.Equal(1000, result.Sectors.Single().LoamStock); // no second spend
        Assert.Contains(report.Entries,
            e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "develop.already-developing:" + ProjectId);
    }

    [Fact]
    public void An_unknown_project_id_is_refused_project_unknown()
    {
        var world = World(Sector("s1", "f1", loamStock: 1000));
        var report = new TurnReport();

        var result = DevelopResolver.Run(world, new[] { Develop("f1", "c1", "s1", projectId: "no-such-project") }, report, Phase);

        Assert.Null(result.Sectors.Single().ProjectId);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "project.unknown");
    }

    [Fact]
    public void Insufficient_loam_stock_is_refused_cannot_afford()
    {
        var project = ProjectCatalog.Get(ProjectId);
        var world = World(Sector("s1", "f1", loamStock: project.CostMilli - 1));
        var report = new TurnReport();

        var result = DevelopResolver.Run(world, new[] { Develop("f1", "c1", "s1") }, report, Phase);

        Assert.Null(result.Sectors.Single().ProjectId);
        Assert.Equal(project.CostMilli - 1, result.Sectors.Single().LoamStock); // unspent
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "develop.cannot-afford");
    }

    [Fact]
    public void An_unnamed_or_unknown_sector_is_refused_sector_missing()
    {
        var world = World(Sector("s1", "f1", loamStock: 1000));
        var report = new TurnReport();

        var noSector = new WorldCommand { CommanderId = "f1", CommandId = "c1", Kind = WorldCommandKinds.Develop, SectorId = null, ProjectId = ProjectId };
        var unknownSector = Develop("f1", "c2", "nowhere");

        var result = DevelopResolver.Run(world, new[] { noSector, unknownSector }, report, Phase);

        Assert.Equal(2, report.Dropped.Count(e => e.Detail == "sector.missing"));
    }

    [Fact]
    public void Two_develop_orders_against_the_same_sector_in_one_turn_the_second_is_refused()
    {
        var project = ProjectCatalog.Get(ProjectId);
        var world = World(Sector("s1", "f1", loamStock: project.CostMilli * 2));
        var report = new TurnReport();

        var result = DevelopResolver.Run(
            world, new[] { Develop("f1", "c1", "s1"), Develop("f1", "c2", "s1") }, report, Phase);

        var sector = result.Sectors.Single();
        Assert.Equal(ProjectId, sector.ProjectId);
        Assert.Equal(project.CostMilli, sector.LoamStock); // spent exactly once
        Assert.Contains(report.Entries,
            e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "develop.already-developing:" + ProjectId);
    }

    [Fact]
    public void Developing_several_different_sectors_in_one_turn_succeeds_independently()
    {
        var project = ProjectCatalog.Get(ProjectId);
        var world = World(
            Sector("s1", "f1", loamStock: project.CostMilli),
            Sector("s2", "f1", loamStock: project.CostMilli),
            Sector("s3", "f1", loamStock: project.CostMilli));
        var report = new TurnReport();

        var result = DevelopResolver.Run(
            world,
            new[] { Develop("f1", "c1", "s1"), Develop("f1", "c2", "s2"), Develop("f1", "c3", "s3") },
            report, Phase);

        Assert.All(result.Sectors, s => Assert.Equal(ProjectId, s.ProjectId));
        Assert.Empty(report.Dropped);
    }
}
