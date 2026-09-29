using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-map: a routed force falls back rather than standing where the fight left it
/// (spec-world-movement.md's own "`routed` — routed legions fall back and skip a turn's orders",
/// specced but never wired — the owner confirmed directly, 2026-09-05, that it should be).
///
/// <see cref="RoutLifecycleTests"/> already proves the turn-cost bookkeeping using a stationary
/// Contact standoff (both forces `AtSectorId` since before the turn started, neither mid-lane) — this
/// file covers the two shapes that actually have somewhere to fall back *to*: a lane-crossing rout,
/// and (amended 2026-09-05, closing a real completeness gap the owner confirmed rather than an
/// intended scope line) an attacker that fully marches into a sector this same turn and then loses
/// the Sector-kind contact fight that follows.
/// </summary>
public class RoutFallBackTests
{
    /// <summary>The attacker always routs, the defender always wins, nobody dies either way.</summary>
    sealed class AttackerAlwaysRouts : IBattleResolver
    {
        public BattleOutcome Resolve(BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed)
        {
            var attacker = combatants.Single(e => e.EntityId == request.AttackerEntityId);
            var defender = combatants.Single(e => e.EntityId == request.DefenderEntityId);

            return new BattleOutcome
            {
                BattleId = request.BattleId,
                WinnerEntityId = defender.EntityId,
                Sides = new[]
                {
                    new BattleSideOutcome { EntityId = defender.EntityId, Survivors = defender.Members },
                    new BattleSideOutcome { EntityId = attacker.EntityId, Survivors = attacker.Members, Routed = true }
                }
            };
        }
    }

    /// <summary>Dave's legion, mid-crossing `l-home-ember` toward `ember-hollow`; the wild pack meets
    /// it head-on from the other end, matching <see cref="CrossingSymmetryTests"/>'s own fixture
    /// shape. `MovementRemaining` is generous enough that neither side's own budget, only the fight
    /// itself, decides what happens this turn.</summary>
    static WorldState MidCrossing() =>
        WorldValidation.Validate(WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1) is var world
            ? world with
            {
                Entities = world.Entities
                    .Select(e => e.EntityId switch
                    {
                        "e-dave-legion-1" => e with
                        {
                            AtSectorId = null,
                            OnLaneId = "l-home-ember",
                            OnLaneTowardSectorId = "ember-hollow",
                            LaneProgressMilli = 400,
                            MovementRemaining = 1000
                        },
                        "e-wild-pack-1" => e with
                        {
                            AtSectorId = null,
                            OnLaneId = "l-home-ember",
                            OnLaneTowardSectorId = "homeworld",
                            LaneProgressMilli = 400,
                            MovementRemaining = 1000,
                            Stance = "march"
                        },
                        _ => e
                    })
                    .ToList()
            }
            : throw new InvalidOperationException());

    /// <summary>Dave's legion starts at `homeworld` with a generous budget and marches the whole of
    /// `l-home-ember` to `ember-hollow` in one turn — the wild pack sits there already, `hold`ing
    /// since before the turn started (the template's own default position and stance), so it is the
    /// stationary defender and Dave is the one arriving fresh, not the one mid-crossing.</summary>
    static WorldState ArrivingAttacker() =>
        WorldValidation.Validate(WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1) is var world
            ? world with
            {
                Entities = world.Entities
                    .Select(e => e.EntityId switch
                    {
                        "e-dave-legion-1" => e with { MovementRemaining = 5000 },
                        "e-wild-pack-1" => e with { AtSectorId = "ember-hollow" },
                        _ => e
                    })
                    .ToList()
            }
            : throw new InvalidOperationException());

    static WorldEntity Legion(WorldState w) => w.Entities.Single(e => e.EntityId == "e-dave-legion-1");

    static WorldCommand March(string commander, string entityId) => new()
    {
        CommanderId = commander,
        CommandId = "m-" + entityId,
        Kind = WorldCommandKinds.Move,
        EntityId = entityId,
        LanePath = new[] { "l-home-ember" }
    };

    [Fact]
    public void A_legion_routed_mid_lane_falls_back_to_the_sector_it_crossed_from()
    {
        // Both budgets and starting progress are generous enough that the 200-milli gap between
        // them closes this same turn (matching CrossingSymmetryTests' own "did they meet" check) —
        // orders are what actually close the gap, not merely standing on the lane.
        var result = TurnEngine.Step(
            MidCrossing(),
            new[] { March("dave", "e-dave-legion-1"), March("wild", "e-wild-pack-1") },
            seed: 1, new AttackerAlwaysRouts());

        Assert.Contains(result.Report.Entries, e => e.Kind == TurnReportKinds.Battle); // sanity: they met

        var legion = Legion(result.World);
        Assert.True(legion.Routed);
        // `l-home-ember`'s ends are homeworld/ember-hollow; heading toward ember-hollow, "the way it
        // came" is homeworld.
        Assert.Equal("homeworld", legion.AtSectorId);
        Assert.Null(legion.OnLaneId);
        Assert.Null(legion.OnLaneTowardSectorId);
        Assert.Equal(0, legion.LaneProgressMilli);
    }

    [Fact]
    public void Falling_back_does_not_touch_a_legion_that_was_not_newly_routed_this_fight()
    {
        // The defender in this fixture never routs — its own lane position must be untouched by
        // BattleApplication's fall-back logic, proving the reversal is scoped to the routed side only.
        var result = TurnEngine.Step(
            MidCrossing(),
            new[] { March("dave", "e-dave-legion-1"), March("wild", "e-wild-pack-1") },
            seed: 1, new AttackerAlwaysRouts());

        var wildPack = result.World.Entities.Single(e => e.EntityId == "e-wild-pack-1");
        Assert.False(wildPack.Routed);
        Assert.Equal("l-home-ember", wildPack.OnLaneId);
        Assert.Equal("homeworld", wildPack.OnLaneTowardSectorId);
    }

    [Fact]
    public void A_legion_that_fully_arrives_and_is_then_routed_falls_back_down_the_lane_it_just_used()
    {
        // Dave never stops mid-lane here — the march fully completes, `AtSectorId` is set and
        // `OnLaneId` is cleared, exactly like any ordinary arrival — before the Sector-kind contact
        // fight against the wild pack's standing garrison ever fires.
        var result = TurnEngine.Step(
            ArrivingAttacker(), new[] { March("dave", "e-dave-legion-1") }, seed: 1, new AttackerAlwaysRouts());

        Assert.Contains(result.Report.Entries, e => e.Kind == TurnReportKinds.Battle); // sanity: they fought

        var legion = Legion(result.World);
        Assert.True(legion.Routed);
        // `l-home-ember`'s ends are homeworld/ember-hollow; Dave arrived at ember-hollow, so "the way
        // it came" is homeworld — the same lane it just marched, reversed.
        Assert.Equal("homeworld", legion.AtSectorId);
        Assert.Null(legion.OnLaneId);
        Assert.Null(legion.OnLaneTowardSectorId);
        Assert.Equal(0, legion.LaneProgressMilli);
    }

    [Fact]
    public void An_entrenched_defender_that_never_moved_this_turn_has_nowhere_to_fall_back_to()
    {
        // The wild pack never moved and never had a lane to fall back to in the first place — this is
        // the genuine "already standing at a sector when it routed" case FallBack's own doc comment
        // describes, still real after the arrival-lane fix landed alongside it.
        var outcome = new BattleOutcome
        {
            BattleId = "b-garrison",
            Sides = new[]
            {
                new BattleSideOutcome { EntityId = "e-wild-pack-1", Survivors = Array.Empty<WorldEntityMember>(), Routed = true }
            }
        };

        var world = ArrivingAttacker();
        var next = FusionRpg.Core.World.Turn.BattleApplication.Apply(world, outcome);
        var wildPack = next.Entities.Single(e => e.EntityId == "e-wild-pack-1");

        Assert.True(wildPack.Routed);
        Assert.Equal("ember-hollow", wildPack.AtSectorId);
        Assert.Null(wildPack.OnLaneId);
    }

    [Fact]
    public void A_force_already_routed_this_turn_does_not_fall_back_a_second_time()
    {
        // Route it once (falls back to homeworld), then feed the same already-routed world straight
        // back into a second fight naming the same entity — BattleApplication must not reverse a
        // force that arrived at this fight already routed (Apply's own `newlyRouted` gate).
        var first = TurnEngine.Step(
            MidCrossing(),
            new[] { March("dave", "e-dave-legion-1"), March("wild", "e-wild-pack-1") },
            seed: 1, new AttackerAlwaysRouts());
        var routedAtHomeworld = Legion(first.World);
        Assert.Equal("homeworld", routedAtHomeworld.AtSectorId);

        var outcome = new BattleOutcome
        {
            BattleId = "b2",
            Sides = new[]
            {
                new BattleSideOutcome { EntityId = routedAtHomeworld.EntityId, Survivors = routedAtHomeworld.Members, Routed = true }
            }
        };

        var again = FusionRpg.Core.World.Turn.BattleApplication.Apply(first.World, outcome);
        var stillAtHomeworld = again.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        Assert.Equal("homeworld", stillAtHomeworld.AtSectorId);
        Assert.Null(stillAtHomeworld.OnLaneId);
    }
}
