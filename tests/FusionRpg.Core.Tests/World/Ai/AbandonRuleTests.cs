using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// L20 acceptance (spec-loam-ai-survival.md): the `Abandon` rule. The W35 trap applies here too —
/// each firing case must build a world where `Defend` and `Finish` both decline, stated in the test,
/// not assumed.
/// </summary>
public class AbandonRuleTests
{
    const string Zomboss = "zomboss";
    const ulong Seed = 1;

    static WorldSlot Slot(int index, string typeId, string? guard = null) => new()
    {
        SlotIndex = index, SlotTypeId = typeId, GuardWaveId = guard,
        GuardState = guard is null ? GuardState.Cleared : GuardState.Intact
    };

    static WorldEntity Legion(string at) => new()
    {
        EntityId = "e-zomboss-1", Kind = WorldEntityKind.Warband, OwnerFactionId = Zomboss,
        AtSectorId = at, Stance = "march", MovementRemaining = MovementPolicy.BudgetFor("march"),
        Members = new[] { new WorldEntityMember { SpeciesId = "normalzombie", Level = 1, Hp = 200 } }
    };

    /// <summary>
    /// a-b-c chain (no Seat anywhere, so `Defend` always declines by construction — it only ever
    /// looks at sectors with a Seat slot). `sectors` overrides slots/development/danger/stock per id;
    /// every id not named there is left as a bare, unguarded, sourceless barren sector.
    /// </summary>
    static IWorldView Chain(string legionAt, IReadOnlyDictionary<string, WorldSector> sectors)
    {
        var ids = new[] { "a", "b", "c" };
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = Zomboss, Kind = WorldFactionKind.Zomboss, Name = "Z" } },
            Sectors = ids.Select(id => sectors.TryGetValue(id, out var s) ? s : new WorldSector { SectorId = id, TypeId = "barren", OwnerFactionId = Zomboss })
                .OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),
            Lanes = new[]
            {
                new WorldLane { LaneId = "l-ab", FromSectorId = "a", ToSectorId = "b", TypeId = "rift" },
                new WorldLane { LaneId = "l-bc", FromSectorId = "b", ToSectorId = "c", TypeId = "rift" }
            },
            Entities = new[] { Legion(legionAt) }
        };

        return new BelievedWorldView(world with { Intel = IntelRecorder.Observe(world, world, 0) }, Zomboss);
    }

    static WorldSector Sector(string id, int development = 0, int danger = 0, long stock = 0, WorldSlot[]? slots = null) => new()
    {
        SectorId = id, TypeId = "barren", OwnerFactionId = Zomboss,
        DevelopmentLevel = development, DangerBand = danger, LoamStock = stock,
        Slots = slots ?? Array.Empty<WorldSlot>()
    };

    [Fact]
    public void It_fires_a_hopeless_component_releases_its_weakest_sector_with_a_reason()
    {
        // a: the component's one source (escapes G-C). b, c: no source, heavy upkeep, no stock.
        // Defend declines (no Seat anywhere in this fixture) and Finish declines (nothing guarded at
        // "c") — stated, not assumed.
        var view = Chain("c", new Dictionary<string, WorldSector>
        {
            ["a"] = Sector("a", slots: new[] { Slot(0, SlotTypeCatalog.RootbedSlotTypeId) }),
            ["b"] = Sector("b", development: 2, danger: 1),
            ["c"] = Sector("c", development: 4, danger: 3)   // the worse of the two — should be picked
        });

        var entity = view.OwnForces.Single();
        var orders = FrontierRulesPolicy.Instance.Decide(view, Seed);
        var order = Assert.Single(orders);

        Assert.Equal(WorldCommandKinds.Move, order.Command.Kind);
        Assert.Contains("abandon c", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_fire_when_insolvent_but_the_stock_still_has_plenty_of_runway()
    {
        // A negative balance alone is not enough — the horizon check is a genuinely separate guard
        // from the solvency check, and this is the only fixture that can tell them apart: deep
        // stock at "b" means the shortfall will not bite for many turns yet.
        var view = Chain("c", new Dictionary<string, WorldSector>
        {
            ["a"] = Sector("a", slots: new[] { Slot(0, SlotTypeCatalog.RootbedSlotTypeId) }),
            ["b"] = Sector("b", development: 2, danger: 1),
            ["c"] = Sector("c", development: 4, danger: 3, stock: 100_000)
        });

        var orders = FrontierRulesPolicy.Instance.Decide(view, Seed);
        var order = Assert.Single(orders);

        Assert.DoesNotContain("abandon", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_fire_a_surplus_component_keeps_everything_including_its_worst_sector()
    {
        // Two rootbeds against three sectors of modest upkeep is comfortably solvent.
        var view = Chain("c", new Dictionary<string, WorldSector>
        {
            ["a"] = Sector("a", slots: new[] { Slot(0, SlotTypeCatalog.RootbedSlotTypeId) }),
            ["b"] = Sector("b", slots: new[] { Slot(0, SlotTypeCatalog.RootbedSlotTypeId) }),
            ["c"] = Sector("c", development: 1, danger: 0)   // still the worst balance in the component
        });

        var orders = FrontierRulesPolicy.Instance.Decide(view, Seed);
        var order = Assert.Single(orders);

        Assert.DoesNotContain("abandon", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void It_picks_the_worst_contributor_not_the_first_by_id_and_only_its_own_occupant_evacuates()
    {
        // "b" is worse than "c" (higher development and danger). The legion stands at "c" — not the
        // weakest — so Abandon must decline for it even though the component as a whole is hopeless.
        var view = Chain("c", new Dictionary<string, WorldSector>
        {
            ["a"] = Sector("a", slots: new[] { Slot(0, SlotTypeCatalog.RootbedSlotTypeId) }),
            ["b"] = Sector("b", development: 5, danger: 4),   // the true weakest
            ["c"] = Sector("c", development: 1, danger: 0)
        });

        var orders = FrontierRulesPolicy.Instance.Decide(view, Seed);
        var order = Assert.Single(orders);

        Assert.DoesNotContain("abandon", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Defend_wins_a_threatened_seat_is_defended_rather_than_abandoned()
    {
        // The legion stands at "a" — the component's *weakest* sector (no source, heavy upkeep), so
        // Abandon wants to evacuate it. But "c" carries a Seat under more threat than its garrison
        // (zero), and `Defend` never fires for a unit already standing on the sector it would
        // defend — so the only way to prove precedence is a *different* threatened sector than the
        // one Abandon would release. Defend must still win: it sits higher in the chain regardless
        // of which sector either rule is about.
        var sectors = new Dictionary<string, WorldSector>
        {
            ["a"] = Sector("a", development: 4, danger: 3),
            ["b"] = Sector("b", slots: new[] { Slot(0, SlotTypeCatalog.RootbedSlotTypeId) }),
            ["c"] = Sector("c", slots: new[] { Slot(0, "seat") })
        };
        var ids = new[] { "a", "b", "c" };
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[]
            {
                new WorldFaction { FactionId = Zomboss, Kind = WorldFactionKind.Zomboss, Name = "Z" },
                new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" }
            },
            Sectors = ids.Select(id => sectors[id]).OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),
            Lanes = new[]
            {
                new WorldLane { LaneId = "l-ab", FromSectorId = "a", ToSectorId = "b", TypeId = "rift" },
                new WorldLane { LaneId = "l-bc", FromSectorId = "b", ToSectorId = "c", TypeId = "rift" }
            },
            Entities = new[]
            {
                Legion("a"),
                // A hostile force at "b", well beyond what an empty garrison at "c" could match —
                // Defend's threat map must see it as an incoming margin over zero at "c".
                new WorldEntity
                {
                    EntityId = "e-dave-1", Kind = WorldEntityKind.Legion, OwnerFactionId = "dave",
                    AtSectorId = "b", Stance = "march", MovementRemaining = MovementPolicy.BudgetFor("march"),
                    Members = new[]
                    {
                        new WorldEntityMember { SpeciesId = "normalzombie", Level = 5, Hp = 500 },
                        new WorldEntityMember { SpeciesId = "normalzombie", Level = 5, Hp = 500 }
                    }
                }
            }
        };

        var view = new BelievedWorldView(world with { Intel = IntelRecorder.Observe(world, world, 0) }, Zomboss);
        var orders = FrontierRulesPolicy.Instance.Decide(view, Seed);
        var order = Assert.Single(orders);

        Assert.Contains("defend", order.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A4_a_staged_multi_turn_evacuation_does_not_oscillate()
    {
        // The A4 hypothesis (map §7): a *stateless* policy re-decided fresh from belief every turn,
        // staging a decision across several turns, must not flip back and forth as the entity
        // marches and the world updates under it. Longer chain (a-b-c-d-e) so reaching safety from
        // the doomed sector takes several turns, giving oscillation room to actually show up if it
        // exists rather than resolving in one step.
        var sectors = new Dictionary<string, WorldSector>
        {
            ["a"] = Sector("a", slots: new[] { Slot(0, SlotTypeCatalog.RootbedSlotTypeId) }),
            ["b"] = Sector("b", development: 4, danger: 3),   // the weakest — the evacuation origin
            ["c"] = Sector("c"),
            ["d"] = Sector("d"),
            ["e"] = Sector("e")
        };
        var ids = new[] { "a", "b", "c", "d", "e" };
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = Zomboss, Kind = WorldFactionKind.Zomboss, Name = "Z" } },
            Sectors = ids.Select(id => sectors[id]).OrderBy(s => s.SectorId, StringComparer.Ordinal).ToList(),
            Lanes = new[]
            {
                new WorldLane { LaneId = "l-ab", FromSectorId = "a", ToSectorId = "b", TypeId = "rift", Length = 2000 },
                new WorldLane { LaneId = "l-bc", FromSectorId = "b", ToSectorId = "c", TypeId = "rift", Length = 2000 },
                new WorldLane { LaneId = "l-cd", FromSectorId = "c", ToSectorId = "d", TypeId = "rift", Length = 2000 },
                new WorldLane { LaneId = "l-de", FromSectorId = "d", ToSectorId = "e", TypeId = "rift", Length = 2000 }
            },
            Entities = new[] { Legion("b") }
        };

        // Belief across turns comes from `TurnEngine.Step`'s own `Observe` phase — re-running
        // `IntelRecorder.Observe` by hand here, out of step with the engine's own turn counter,
        // is exactly the kind of belief corruption the A4 hypothesis warns against confusing with
        // a real policy defect: it produced a phantom "b is not mine any more" reading that no
        // actual play could reach. Building the view straight from `world` is the correct call.
        var reasonsByTurn = new List<string>();
        for (var turn = 0; turn < 3; turn++)
        {
            var view = new BelievedWorldView(world, Zomboss);
            var orders = FrontierRulesPolicy.Instance.Decide(view, Seed);
            var order = Assert.Single(orders);
            reasonsByTurn.Add(order.Reason);

            world = TurnEngine.Step(world, new[] { order.Command }, Seed).World;
        }

        // Consistent: every turn's decision is the same abandonment (still evacuating along the
        // same march, or standing pat once safe) — never a flip to Expand marching the unit straight
        // back into the sector it just gave up.
        Assert.DoesNotContain(reasonsByTurn, r => r.StartsWith("expand to b", StringComparison.Ordinal));
    }

    [Fact]
    public void Zombosss_territory_survives_a_hundred_turns_on_two_hearths()
    {
        // The property this module exists for, asserted rather than eyeballed (map §7 finding S5).
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 7, worldId: "survival");
        const ulong seed = 4242;

        for (var turn = 0; turn < 100; turn++)
        {
            var view = new BelievedWorldView(world, Zomboss);
            var commands = FrontierRulesPolicy.Instance.Decide(view, seed).Select(o => o.Command).ToList();
            world = TurnEngine.Step(world, commands, seed).World;
        }

        var zombossSectors = world.Sectors.Count(s => s.OwnerFactionId == Zomboss);
        Assert.True(zombossSectors > 0, "Zomboss's territory reached zero across a hundred turns");
    }
}
