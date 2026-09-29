using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Movement;

/// <summary>
/// The march-loam gate's soft half (spec-loam-ai.md §"the march loam gate"): a `loam-legions`
/// follow-up the spec found un-tracked and fell through between two sealed specs. Pure reporting
/// over already-carried state, alongside every admitted march order — not an AI decision, not a
/// supply-connectivity simulation.
/// </summary>
public class MarchLoamGateTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand Move(string entityId, params string[] lanePath) => new()
    {
        CommanderId = "dave", CommandId = "m1", Kind = WorldCommandKinds.Move,
        EntityId = entityId, LanePath = lanePath
    };

    [Fact]
    public void An_admitted_march_reports_the_absolute_turn_its_carried_loam_runs_dry()
    {
        var world = World();
        var legion = world.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        var burn = LegionSupply.Burn(legion);

        var withCarry = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { CarriedLoam = burn * 3 } : e)
                .ToList()
        };

        var result = TurnEngine.Step(withCarry, new[] { Move("e-dave-legion-1", "l-home-ember") }, seed: 1);

        var expectedTurn = withCarry.CurrentTurn + 1 + 3;
        Assert.Contains(result.Report.Entries, e =>
            e.Subject == "e-dave-legion-1" && e.Detail == "legion.runway:" + expectedTurn);
    }

    [Fact]
    public void The_figure_reflects_what_is_carried_right_now_not_a_full_tank_assumption()
    {
        // Same legion, same march order, only the carried amount differs — the reported turn must
        // move with it, proving this reads WorldEntity.CarriedLoam and not LegionSupply.Capacity.
        var world = World();
        var legion = world.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        var burn = LegionSupply.Burn(legion);

        WorldState WithCarry(long carried) => world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { CarriedLoam = carried } : e)
                .ToList()
        };

        var low = TurnEngine.Step(WithCarry(burn), new[] { Move("e-dave-legion-1", "l-home-ember") }, seed: 1);
        var high = TurnEngine.Step(WithCarry(burn * 5), new[] { Move("e-dave-legion-1", "l-home-ember") }, seed: 1);

        string? RunwayDetail(TurnResult r) => r.Report.Entries
            .SingleOrDefault(e => e.Subject == "e-dave-legion-1" && e.Detail.StartsWith("legion.runway:")).Detail;

        Assert.NotEqual(RunwayDetail(low), RunwayDetail(high));
    }

    [Fact]
    public void A_command_the_reveal_phase_drops_never_gets_a_runway_line()
    {
        // A garrison order refused at Reveal (entity.held) never reaches MarchResolver.March at all —
        // no admitted order, no report line, matching the spec's own "alongside an admitted march order."
        var world = World();
        var held = world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { Stance = "hold" } : e)
                .ToList()
        };

        var result = TurnEngine.Step(held, new[] { Move("e-dave-legion-1", "l-home-ember") }, seed: 1);

        Assert.DoesNotContain(result.Report.Entries, e =>
            e.Subject == "e-dave-legion-1" && e.Detail.StartsWith("legion.runway:"));
    }
}
