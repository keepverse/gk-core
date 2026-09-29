using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W10 (spec-world-movement.md §Zone of control, §Contact): position matters before any blow is
/// struck. Hostile forces halt a march and fight; guards do neither until they are attacked on
/// purpose.
/// </summary>
public class ContactAndClearTests
{
    /// <summary>The attacker always routs, the defender always wins, nobody dies — used only where a
    /// test needs a routed precondition. `actor-hub-and-combat-power-solid-fixing` T20 turned every
    /// non-district contact fight into an honest no-op (no engine resolves one, so no winner is
    /// invented), so a real fight in this file can no longer manufacture a rout on its own; the
    /// mechanic under test here is the turn-cost/fall-back bookkeeping, not combat, matching the same
    /// explicit-resolver pattern <see cref="RoutFallBackTests"/>/<see cref="RoutLifecycleTests"/>
    /// already use.</summary>
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

    /// <summary>Routs a fresh attacker, then wipes it on the very next call — a winner pressing its
    /// advantage on ground it already holds, the same shape <see cref="AttackerAlwaysRouts"/> cannot
    /// produce (it always routs, never finishes). Tracks its own call count rather than reading
    /// `attacker.Routed`: `TurnEngine.Step` clears a routed entity's flag at the *top* of the next
    /// turn as part of the "rout costs exactly one turn" bookkeeping, before any battle resolves, so
    /// the flag this resolver would see is always false regardless of what happened last turn.</summary>
    sealed class RoutsThenFinishes : IBattleResolver
    {
        bool _hasFoughtBefore;

        public BattleOutcome Resolve(BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed)
        {
            var attacker = combatants.Single(e => e.EntityId == request.AttackerEntityId);
            var defender = combatants.Single(e => e.EntityId == request.DefenderEntityId);

            var attackerSide = _hasFoughtBefore
                ? new BattleSideOutcome { EntityId = attacker.EntityId, Survivors = Array.Empty<WorldEntityMember>(), Destroyed = true }
                : new BattleSideOutcome { EntityId = attacker.EntityId, Survivors = attacker.Members, Routed = true };
            _hasFoughtBefore = true;

            return new BattleOutcome
            {
                BattleId = request.BattleId,
                WinnerEntityId = defender.EntityId,
                Sides = new[] { new BattleSideOutcome { EntityId = defender.EntityId, Survivors = defender.Members }, attackerSide }
            };
        }
    }

    /// <summary>Guard-kind contact resolves as a clean win — T20 made `BattleKinds.Guard` one of
    /// `DistrictAssaultResolver`'s refused non-district kinds too, so clearing a slot no longer
    /// happens on its own; this stands in to prove `Clear` still targets only the named slot.</summary>
    sealed class GuardAlwaysClears : IBattleResolver
    {
        public BattleOutcome Resolve(BattleRequest request, IReadOnlyList<WorldEntity> combatants, ulong seed)
        {
            var attacker = combatants.Single(e => e.EntityId == request.AttackerEntityId);
            return new BattleOutcome
            {
                BattleId = request.BattleId,
                WinnerEntityId = attacker.EntityId,
                GuardCleared = true,
                Sides = new[] { new BattleSideOutcome { EntityId = attacker.EntityId, Survivors = attacker.Members } }
            };
        }
    }

    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand Move(string commander, string entityId, params string[] lanePath) => new()
    {
        CommanderId = commander,
        CommandId = "m-" + entityId,
        Kind = WorldCommandKinds.Move,
        EntityId = entityId,
        LanePath = lanePath
    };

    static WorldCommand Clear(string commander, string entityId, string sectorId, int slotIndex) => new()
    {
        CommanderId = commander,
        CommandId = "c-" + entityId,
        Kind = WorldCommandKinds.Clear,
        EntityId = entityId,
        SectorId = sectorId,
        SlotIndex = slotIndex
    };

    static WorldEntity? Find(WorldState w, string id) => w.Entities.FirstOrDefault(e => e.EntityId == id);
    static WorldEntity Legion(WorldState w) => w.Entities.Single(e => e.EntityId == "e-dave-legion-1");

    /// <summary>Puts an entity somewhere else, keeping the world valid.</summary>
    static WorldState Place(WorldState w, string entityId, string sectorId, int movement = 1000) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId
                ? e with
                {
                    AtSectorId = sectorId, OnLaneId = null, OnLaneTowardSectorId = null,
                    LaneProgressMilli = 0, MovementRemaining = movement, Stance = "march"
                }
                : e)
            .ToList()
    };

    // ---- zone of control ----------------------------------------------------------------

    [Fact]
    public void Marching_through_a_guarded_sector_is_free()
    {
        // ember-hollow holds two intact guards and no hostile entity: the legion should pass
        // straight through and end up out on the next lane.
        var result = TurnEngine.Step(World(),
            new[] { Move("dave", "e-dave-legion-1", "l-home-ember", "l-ember-ash") }, seed: 1);

        var legion = Legion(result.World);
        Assert.Equal("l-ember-ash", legion.OnLaneId);
        Assert.InRange(legion.LaneProgressMilli, 1, 999);
    }

    [Fact]
    public void Entering_a_hostile_held_sector_halts_the_march()
    {
        // Same order, but now a wild warband stands in ember-hollow. Zone-of-control halts the march
        // exactly at ember-hollow rather than letting it continue onto `l-ember-ash` — the "zoc"
        // report line is written from the halt itself, before any battle resolves. T20 turned the
        // ensuing Sector-kind contact fight into an honest no-op (no engine resolves one), so unlike
        // before this fix, nothing decides it: the legion is left standing in ember-hollow, neither
        // routed nor victorious, rather than a Hp×Level comparison inventing a winner.
        var world = Place(World(), "e-wild-pack-1", "ember-hollow", movement: 0);

        var result = TurnEngine.Step(world,
            new[] { Move("dave", "e-dave-legion-1", "l-home-ember", "l-ember-ash") }, seed: 1);

        Assert.Contains(result.Report.Entries, e => e.Detail.Contains("zoc") && e.Detail.Contains("ember-hollow"));

        var legion = Find(result.World, "e-dave-legion-1");
        Assert.NotNull(legion);
        Assert.False(legion!.Routed);
        Assert.Equal("ember-hollow", legion.AtSectorId);
        Assert.Null(legion.OnLaneId);
    }

    [Fact]
    public void A_legion_already_inside_a_hostile_sector_may_still_leave()
    {
        // Both forces start in ash-waste; the halt is on *entering*, so leaving is allowed.
        var world = Place(World(), "e-dave-legion-1", "ash-waste");

        var result = TurnEngine.Step(world,
            new[] { Move("dave", "e-dave-legion-1", "l-ash-black") }, seed: 1);

        var legion = Find(result.World, "e-dave-legion-1");
        Assert.NotNull(legion);
        Assert.NotEqual("ash-waste", legion!.AtSectorId);
    }

    // ---- contact ------------------------------------------------------------------------

    [Fact]
    public void Two_hostile_forces_in_one_sector_fight_once()
    {
        var world = Place(World(), "e-wild-pack-1", "ember-hollow", movement: 0);

        var result = TurnEngine.Step(world,
            new[] { Move("dave", "e-dave-legion-1", "l-home-ember") }, seed: 1);

        var battles = result.Report.Entries.Where(e => e.Kind == TurnReportKinds.Battle).ToList();
        Assert.Single(battles);
        Assert.Contains("ember-hollow", battles[0].Detail);
    }

    [Fact]
    public void Forces_of_the_same_faction_stack_without_a_battle()
    {
        var world = World();
        var friendly = world with
        {
            Entities = world.Entities
                .Append(world.Entities.Single(e => e.EntityId == "e-dave-legion-1") with
                {
                    EntityId = "e-dave-legion-2", AtSectorId = "ember-hollow"
                })
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList()
        };

        var result = TurnEngine.Step(WorldValidation.Validate(friendly),
            new[] { Move("dave", "e-dave-legion-1", "l-home-ember") }, seed: 1);

        Assert.DoesNotContain(result.Report.Entries, e => e.Kind == TurnReportKinds.Battle);
        Assert.NotNull(Find(result.World, "e-dave-legion-2"));
    }

    [Fact]
    public void Two_hostile_legions_crossing_one_lane_meet_on_the_lane()
    {
        var world = Place(World(), "e-wild-pack-1", "ember-hollow");

        var result = TurnEngine.Step(world, new[]
        {
            Move("dave", "e-dave-legion-1", "l-home-ember"),
            Move("wild", "e-wild-pack-1", "l-home-ember")
        }, seed: 1);

        var battle = Assert.Single(result.Report.Entries.Where(e => e.Kind == TurnReportKinds.Battle));
        Assert.Contains("l-home-ember", battle.Detail);
    }

    [Fact]
    public void Contact_does_not_depend_on_the_order_the_commands_arrive_in()
    {
        var world = Place(World(), "e-wild-pack-1", "ember-hollow");
        var a = Move("dave", "e-dave-legion-1", "l-home-ember");
        var b = Move("wild", "e-wild-pack-1", "l-home-ember");

        var forward = TurnEngine.Step(world, new[] { a, b }, seed: 1);
        var reversed = TurnEngine.Step(world, new[] { b, a }, seed: 1);

        Assert.Equal(forward.StateHash, reversed.StateHash);
    }

    [Fact]
    public void A_routed_force_loses_the_next_turns_orders_and_then_recovers()
    {
        // T20: Sector-kind contact is now a no-op, so an explicit resolver manufactures the routed
        // precondition — the mechanic under test is the turn-cost bookkeeping, not combat.
        var resolver = new AttackerAlwaysRouts();
        var world = Place(World(), "e-wild-pack-1", "ember-hollow", movement: 0);
        var first = TurnEngine.Step(world,
            new[] { Move("dave", "e-dave-legion-1", "l-home-ember") }, seed: 1, resolver);

        var beaten = Find(first.World, "e-dave-legion-1");
        Assert.NotNull(beaten);
        Assert.True(beaten!.Routed, "the losing side should be routed, not merely wounded");
        Assert.Equal("homeworld", beaten.AtSectorId);

        // The victor moves on, so the next turn is about the rout and nothing else.
        var after = Place(first.World, "e-wild-pack-1", "ash-waste", movement: 0);

        var second = TurnEngine.Step(after,
            new[] { Move("dave", "e-dave-legion-1", "l-home-ember") }, seed: 1);
        Assert.Contains(second.Report.Dropped, e => e.Detail == "entity.routed");
        Assert.Equal("homeworld", Find(second.World, "e-dave-legion-1")!.AtSectorId);
        Assert.False(Find(second.World, "e-dave-legion-1")!.Routed, "rout costs exactly one turn");

        // ...and the turn after that, it marches again — unopposed this time (the wild pack left for
        // ash-waste above), so it actually arrives.
        var third = TurnEngine.Step(second.World,
            new[] { Move("dave", "e-dave-legion-1", "l-home-ember") }, seed: 1);
        Assert.Equal("ember-hollow", Find(third.World, "e-dave-legion-1")!.AtSectorId);
    }

    /// <summary>
    /// A routed force with nowhere to fall back to — it never moved this turn, so it has no lane to
    /// retreat down — keeps the ground it lost on and cannot order itself away, and the winner
    /// finishes it next turn. That is a consequence of the rout rule for a genuine standing defender,
    /// not an accident, and it is pinned here so changing it is a decision rather than a surprise.
    /// A legion that marched in and lost instead falls back (see the fixtures above) — this test used
    /// to reuse that same shape, which the fix now makes escape rather than getting finished off, so
    /// it moved to the one shape that genuinely never had anywhere to go (world-map, 2026-09-05).
    /// </summary>
    [Fact]
    public void A_routed_force_the_winner_stands_over_is_finished_off()
    {
        // T20: an explicit resolver stands in for combat (see AttackerAlwaysRouts's own remark) —
        // both forces already stand in ash-waste before the turn starts and neither files an order,
        // so `BattleApplication.FallBack` finds nothing to reverse either way.
        var resolver = new RoutsThenFinishes();
        var world = Place(World(), "e-dave-legion-1", "ash-waste");
        var first = TurnEngine.Step(world, Array.Empty<WorldCommand>(), seed: 1, resolver);
        var routed = Find(first.World, "e-dave-legion-1");
        Assert.NotNull(routed);
        Assert.True(routed!.Routed);
        Assert.Equal("ash-waste", routed.AtSectorId);

        var second = TurnEngine.Step(first.World, Array.Empty<WorldCommand>(), seed: 1, resolver);
        Assert.Null(Find(second.World, "e-dave-legion-1"));
    }

    // ---- clear --------------------------------------------------------------------------

    [Fact]
    public void Clear_flips_only_the_targeted_slot()
    {
        var world = Place(World(), "e-dave-legion-1", "ember-hollow");

        var result = TurnEngine.Step(world, new[] { Clear("dave", "e-dave-legion-1", "ember-hollow", 2) },
            seed: 1, new GuardAlwaysClears());

        var ember = result.World.Sectors.Single(s => s.SectorId == "ember-hollow");
        Assert.Equal(GuardState.Cleared, ember.Slots[2].GuardState);
        Assert.Equal(GuardState.Intact, ember.Slots[3].GuardState);
        Assert.Contains(result.Report.Entries, e => e.Kind == TurnReportKinds.Battle && e.Detail.Contains("guard"));
    }

    [Fact]
    public void Clearing_a_slot_in_a_sector_you_are_not_standing_in_is_dropped()
    {
        var result = TurnEngine.Step(World(), new[] { Clear("dave", "e-dave-legion-1", "ember-hollow", 2) }, seed: 1);

        Assert.Contains(result.Report.Dropped, e => e.Detail == "slot.elsewhere");
        var ember = result.World.Sectors.Single(s => s.SectorId == "ember-hollow");
        Assert.Equal(GuardState.Intact, ember.Slots[2].GuardState);
    }

    [Fact]
    public void Clearing_an_already_cleared_guard_is_dropped_with_a_reason()
    {
        var world = Place(World(), "e-dave-legion-1", "ember-hollow");

        // Slot 1 is plain wildland — nothing there was ever guarded.
        var result = TurnEngine.Step(world, new[] { Clear("dave", "e-dave-legion-1", "ember-hollow", 1) }, seed: 1);

        Assert.Contains(result.Report.Dropped, e => e.Detail == "guard.already-cleared");
    }

    [Fact]
    public void Clearing_a_slot_that_does_not_exist_is_refused_at_admission()
    {
        var world = Place(World(), "e-dave-legion-1", "ember-hollow");
        var (ok, reason) = WorldCommandAdmission.Admit(world, Clear("dave", "e-dave-legion-1", "ember-hollow", 99));

        Assert.False(ok);
        Assert.Equal("slot.unknown", reason);
    }
}
