using FusionRpg.Core.World;
using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-map W52: `develop` reaches production for the first time — proves the command reaches
/// <see cref="DevelopResolver"/> through a real <see cref="TurnEngine.Step"/> commit, not merely the
/// resolver called directly (<see cref="Growth.DevelopResolverTests"/> already covers every legality
/// branch at that level), and proves the acceptance's own stated consequence of the `Production`/
/// `Growth` split: a project completing this turn affects next turn's yield, never this turn's.
/// </summary>
public class DevelopThreadingTests
{
    static WorldState WorldWithHomeworldLoam(long stock)
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        return world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId == "homeworld" ? s with { LoamStock = stock } : s)
                .ToList()
        };
    }

    static WorldCommand Develop(string commander, string sectorId, string projectId) => new()
    {
        CommanderId = commander, CommandId = "c-develop", Kind = WorldCommandKinds.Develop,
        SectorId = sectorId, ProjectId = projectId
    };

    [Fact]
    public void A_committed_develop_order_starts_the_project_and_spends_loam_stock()
    {
        // Comfortably affordable even after this same turn's own Production yield and Pressure
        // upkeep both touch `LoamStock` before Snapshot resolves `develop` — this test proves the
        // command reaches DevelopResolver end to end, not the exact post-upkeep arithmetic, which
        // `DevelopResolverTests` already proves precisely at the resolver level directly.
        var project = ProjectCatalog.Get("raise-development-placeholder");
        var opening = WorldWithHomeworldLoam(project.CostMilli * 100);
        var stockBefore = opening.Sectors.Single(s => s.SectorId == "homeworld").LoamStock;

        var result = TurnEngine.Step(opening, new[] { Develop("dave", "homeworld", project.ProjectId) }, seed: 1);

        var sector = result.World.Sectors.Single(s => s.SectorId == "homeworld");
        Assert.Equal(project.ProjectId, sector.ProjectId);
        Assert.Equal(project.ProjectTurns, sector.ProjectTurnsRemaining);
        Assert.True(sector.LoamStock < stockBefore, "the project's own cost must have been spent");
        Assert.Contains(result.Report.Entries,
            e => e.Kind == TurnReportKinds.CommandAccepted && e.Subject == "c-develop");
        Assert.Contains(result.Report.Entries,
            e => e.Kind == TurnReportKinds.Event && e.Detail == "develop.started:" + project.ProjectId);
    }

    [Fact]
    public void A_project_that_completes_this_turn_is_reported_in_Growth_which_runs_after_Production()
    {
        // The acceptance's own stated consequence of the split (spec-sector-development.md §3):
        // `Production` advances a structure (`LoamPhases.DecrementConstruction`), `Growth` advances a
        // project — a project one turn from completion when this turn starts finishes *during*
        // `Growth`, provably after `Production` already ran, so anything `Production` reads this same
        // turn cannot yet see the completion.
        var project = ProjectCatalog.Get("raise-development-placeholder");
        var opening = WorldWithHomeworldLoam(0);
        var world = opening with
        {
            Sectors = opening.Sectors
                .Select(s => s.SectorId == "homeworld"
                    ? s with { ProjectId = project.ProjectId, ProjectTurnsRemaining = 1 }
                    : s)
                .ToList()
        };

        var result = TurnEngine.Step(world, Array.Empty<WorldCommand>(), seed: 1);

        var sector = result.World.Sectors.Single(s => s.SectorId == "homeworld");
        Assert.Null(sector.ProjectId);
        Assert.Null(sector.ProjectTurnsRemaining);

        var completed = Assert.Single(result.Report.Entries,
            e => e.Kind == TurnReportKinds.Event && e.Detail == "develop.completed:" + project.ProjectId);
        Assert.Equal(TurnEngine.Phases.Growth, completed.Phase);

        // The locked phase order itself is what proves "never this turn's": Production is recorded
        // before Growth in every turn, so a reader watching phases in order sees Production's own
        // pass happen strictly before the completion this task's report entry names.
        var productionIndex = result.Report.Phases.ToList().IndexOf(TurnEngine.Phases.Production);
        var growthIndex = result.Report.Phases.ToList().IndexOf(TurnEngine.Phases.Growth);
        Assert.True(productionIndex >= 0 && growthIndex > productionIndex);
    }

    [Fact]
    public void A_developer_who_no_longer_owns_the_sector_at_resolution_is_refused()
    {
        var project = ProjectCatalog.Get("raise-development-placeholder");
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[]
            {
                new WorldSector
                {
                    SectorId = "s", OwnerFactionId = null, Phase = SectorPhase.Lost, LoamStock = project.CostMilli
                }
            }
        };
        var report = new TurnReport();

        var result = DevelopResolver.Run(world, new[] { Develop("dave", "s", project.ProjectId) }, report, "snapshot");

        Assert.Null(result.Sectors.Single().ProjectId);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "develop.not-yours");
    }
}
