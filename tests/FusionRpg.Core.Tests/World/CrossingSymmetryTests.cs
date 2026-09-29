using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Two forces that meet must meet in the *same place*. Their positions are stored from opposite ends
/// of the lane, so the invariant is that the pair sums to the lane's full length — anything else
/// means the map is drawing them apart while the engine thinks they are together.
///
/// Asserted across a spread of speeds and starting positions rather than one hand-picked case,
/// because the failure mode here is integer truncation, which only shows up at particular ratios.
/// </summary>
public class CrossingSymmetryTests
{
    /// <summary>Neither side dies, so both are still on the lane to be measured.</summary>
    sealed class NobodyDies : IBattleResolver
    {
        public BattleOutcome Resolve(BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed) =>
            new()
            {
                BattleId = request.BattleId,
                Sides = combatants
                    .Select(e => new BattleSideOutcome { EntityId = e.EntityId, Survivors = e.Members })
                    .ToList()
            };
    }

    static WorldState FacingOff(int daveBudget, int wildBudget, int daveProgress, int wildProgress)
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

        return WorldValidation.Validate(world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId switch
                {
                    // Both on l-home-ember, walking at each other from opposite ends.
                    "e-dave-legion-1" => e with
                    {
                        AtSectorId = null,
                        OnLaneId = "l-home-ember",
                        OnLaneTowardSectorId = "ember-hollow",
                        LaneProgressMilli = daveProgress,
                        MovementRemaining = daveBudget
                    },
                    // Stance matters since W23: the template digs this pack in, and a garrison's
                    // march is refused at reveal — which would leave every assertion below sitting
                    // behind an early return, quietly passing while testing nothing.
                    "e-wild-pack-1" => e with
                    {
                        AtSectorId = null,
                        OnLaneId = "l-home-ember",
                        OnLaneTowardSectorId = "homeworld",
                        LaneProgressMilli = wildProgress,
                        MovementRemaining = wildBudget,
                        Stance = "march"
                    },
                    _ => e
                })
                .ToList()
        });
    }

    static WorldCommand March(string commander, string entityId) => new()
    {
        CommanderId = commander,
        CommandId = "m-" + entityId,
        Kind = WorldCommandKinds.Move,
        EntityId = entityId,
        LanePath = new[] { "l-home-ember" }
    };

    public static TheoryData<int, int, int, int> Approaches()
    {
        var data = new TheoryData<int, int, int, int>();
        foreach (var daveBudget in new[] { 200, 350, 500, 1000 })
        foreach (var wildBudget in new[] { 150, 400, 900 })
        foreach (var start in new[] { 0, 130, 470 })
            data.Add(daveBudget, wildBudget, start, start / 2);
        return data;
    }

    [Theory]
    [MemberData(nameof(Approaches))]
    public void Two_forces_that_meet_are_in_the_same_place(int daveBudget, int wildBudget, int daveProgress, int wildProgress)
    {
        var world = FacingOff(daveBudget, wildBudget, daveProgress, wildProgress);

        var result = TurnEngine.Step(world,
            new[] { March("dave", "e-dave-legion-1"), March("wild", "e-wild-pack-1") },
            seed: 1, new NobodyDies());

        var dave = result.World.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        var wild = result.World.Entities.Single(e => e.EntityId == "e-wild-pack-1");

        // Neither of them met if the gap outlived the turn — that is a legal outcome, not a failure.
        var met = result.Report.Entries.Any(e => e.Kind == TurnReportKinds.Battle);
        if (!met) return;

        Assert.Equal("l-home-ember", dave.OnLaneId);
        Assert.Equal("l-home-ember", wild.OnLaneId);
        Assert.Equal(1000, dave.LaneProgressMilli + wild.LaneProgressMilli);
    }

    [Theory]
    [MemberData(nameof(Approaches))]
    public void Neither_force_is_pushed_backwards_by_the_meeting(int daveBudget, int wildBudget, int daveProgress, int wildProgress)
    {
        var world = FacingOff(daveBudget, wildBudget, daveProgress, wildProgress);

        var result = TurnEngine.Step(world,
            new[] { March("dave", "e-dave-legion-1"), March("wild", "e-wild-pack-1") },
            seed: 1, new NobodyDies());

        if (!result.Report.Entries.Any(e => e.Kind == TurnReportKinds.Battle)) return;

        var dave = result.World.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        var wild = result.World.Entities.Single(e => e.EntityId == "e-wild-pack-1");

        Assert.True(dave.LaneProgressMilli >= daveProgress, "a march must not lose ground");
        Assert.True(wild.LaneProgressMilli >= wildProgress, "a march must not lose ground");
    }

    [Fact]
    public void The_answer_does_not_depend_on_which_force_is_walked_first()
    {
        var world = FacingOff(700, 400, 100, 50);
        var a = March("dave", "e-dave-legion-1");
        var b = March("wild", "e-wild-pack-1");

        var forward = TurnEngine.Step(world, new[] { a, b }, seed: 1, new NobodyDies());
        var reversed = TurnEngine.Step(world, new[] { b, a }, seed: 1, new NobodyDies());

        Assert.Equal(forward.StateHash, reversed.StateHash);
    }
}
