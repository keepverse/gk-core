using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Rout costs exactly one turn of orders — and it has to cost one turn *per rout*, not one turn
/// however many times a force is broken.
///
/// The wave-1 placeholder happens to kill anything it routs twice, so this uses a resolver of its
/// own. That is the point: the engine's rout bookkeeping must be correct against the seam, not
/// against one implementation's wound arithmetic.
/// </summary>
public class RoutLifecycleTests
{
    /// <summary>The defender always wins and always routs the attacker, and nobody ever dies.</summary>
    sealed class AlwaysRouts : IBattleResolver
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

    static WorldState Standoff()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

        // Both forces in one sector, so contact fires every turn without anyone having to move.
        return world with
        {
            Entities = world.Entities
                .Select(e => e.EntityId == "e-dave-legion-1"
                    ? e with { AtSectorId = "ash-waste", MovementRemaining = 1000 }
                    : e)
                .ToList()
        };
    }

    static WorldEntity Legion(WorldState w) => w.Entities.Single(e => e.EntityId == "e-dave-legion-1");

    [Fact]
    public void A_force_broken_again_while_it_is_recovering_owes_another_turn()
    {
        var resolver = new AlwaysRouts();

        var first = TurnEngine.Step(Standoff(), Array.Empty<WorldCommand>(), seed: 1, resolver);
        Assert.True(Legion(first.World).Routed, "the first battle should rout it");

        // Second turn: its orders are dropped for the first rout — and it is broken all over again.
        var second = TurnEngine.Step(first.World, Array.Empty<WorldCommand>(), seed: 1, resolver);
        Assert.True(
            Legion(second.World).Routed,
            "being routed a second time must cost a second turn, not be cancelled by the first");

        // Third turn: still recovering, so an order filed now is still refused.
        var order = new WorldCommand
        {
            CommanderId = "dave",
            CommandId = "m1",
            Kind = WorldCommandKinds.Move,
            EntityId = "e-dave-legion-1",
            LanePath = new[] { "l-frost-ash" }
        };

        var third = TurnEngine.Step(second.World, new[] { order }, seed: 1, resolver);
        Assert.Contains(third.Report.Dropped, e => e.Detail == "entity.routed");
    }

    [Fact]
    public void A_single_rout_still_costs_exactly_one_turn()
    {
        var resolver = new AlwaysRouts();
        var first = TurnEngine.Step(Standoff(), Array.Empty<WorldCommand>(), seed: 1, resolver);

        // Break the standoff so nothing fights again, and the rout should be spent by the next turn.
        var apart = first.World with
        {
            Entities = first.World.Entities
                .Select(e => e.EntityId == "e-wild-pack-1" ? e with { AtSectorId = "verdant-shelf" } : e)
                .ToList()
        };

        var second = TurnEngine.Step(apart, Array.Empty<WorldCommand>(), seed: 1, resolver);
        Assert.False(Legion(second.World).Routed, "one rout, one turn");
    }
}
