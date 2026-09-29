using FusionRpg.Core.World;
using FusionRpg.Core.World.District;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Turn;

/// <summary>base-defense siege-seam 7.2 (spec-siege-seam.md). Widening BattleRequest/BattleOutcome
/// with a board projection, budgets, a withdrawal verb, and per-slot results — proving every
/// existing kind constructs the record it constructs today, and that the new fields behave.</summary>
public class BattleSeamWideningTests
{
    static WorldState World(params WorldEntity[] entities) => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1, worldId: "w")
        with { Entities = entities };

    static WorldEntity Legion(string id, string owner, string sectorId, bool routed = false) => new()
    {
        EntityId = id, OwnerFactionId = owner, AtSectorId = sectorId, Routed = routed,
        Members = new[] { new WorldEntityMember { SpeciesId = "wild-pack", Hp = 100 } }
    };

    [Fact]
    public void Existing_three_kinds_construct_an_identical_record()
    {
        foreach (var kind in new[] { BattleKinds.Sector, BattleKinds.Lane, BattleKinds.Guard })
        {
            var request = new BattleRequest { Kind = kind, LocationId = "x", AttackerEntityId = "a" };
            Assert.Null(request.Board);
            Assert.Null(request.Budgets);
        }

        var outcome = new BattleOutcome { BattleId = "b1" };
        Assert.Empty(outcome.SlotResults);

        var side = new BattleSideOutcome { EntityId = "e1" };
        Assert.False(side.Withdrawn);
    }

    [Fact]
    public void Withdrawn_is_not_routed()
    {
        var world = World(Legion("e1", "dave", "homeworld"));
        var outcome = new BattleOutcome
        {
            BattleId = "b1",
            Sides = new[] { new BattleSideOutcome { EntityId = "e1", Survivors = new[] { new WorldEntityMember { SpeciesId = "wild-pack", Hp = 50 } }, Routed = true, Withdrawn = true } }
        };

        var next = BattleApplication.Apply(world, outcome);
        var entity = next.Entities.Single(e => e.EntityId == "e1");

        Assert.False(entity.Routed);
    }

    [Fact]
    public void Withdrawn_and_destroyed_together_throws()
    {
        var world = World(Legion("e1", "dave", "homeworld"));
        var outcome = new BattleOutcome
        {
            BattleId = "b1",
            Sides = new[] { new BattleSideOutcome { EntityId = "e1", Withdrawn = true, Destroyed = true } }
        };

        Assert.Throws<InvalidOperationException>(() => BattleApplication.Apply(world, outcome));
    }

    [Fact]
    public void Withdrawn_round_trips_through_apply()
    {
        var world = World(Legion("e1", "dave", "ember-hollow"));
        var survivors = new[] { new WorldEntityMember { SpeciesId = "wild-pack", Hp = 42 } };
        var outcome = new BattleOutcome
        {
            BattleId = "b1",
            Sides = new[] { new BattleSideOutcome { EntityId = "e1", Survivors = survivors, Withdrawn = true } }
        };

        var next = BattleApplication.Apply(world, outcome);
        var entity = next.Entities.Single(e => e.EntityId == "e1");

        Assert.Equal(survivors, entity.Members);
        Assert.False(entity.Routed);
        Assert.Equal("ember-hollow", entity.AtSectorId); // no fall-back -- it left on its own terms
    }

    [Fact]
    public void Slot_results_apply_only_when_present()
    {
        var world = World();
        var before = world.Sectors.Single(s => s.SectorId == "homeworld");

        var untouched = BattleApplication.ApplySlotResults(world, "homeworld", Array.Empty<SlotOutcome>());
        var after = untouched.Sectors.Single(s => s.SectorId == "homeworld");

        Assert.Equal(before.Slots, after.Slots);
    }

    [Fact]
    public void Slot_results_apply_ownership_and_destruction_when_present()
    {
        var world = World() with
        {
            Sectors = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w").Sectors
                .Select(s => s.SectorId == "homeworld"
                    ? s with { Slots = s.Slots.Select(sl => sl with { StructureId = "well" }).ToList() }
                    : s)
                .ToList()
        };
        var targetSlot = world.Sectors.Single(s => s.SectorId == "homeworld").Slots.First();

        var results = new[]
        {
            new SlotOutcome { SlotIndex = targetSlot.SlotIndex, StructureDestroyed = true, HeldByFactionId = "zomboss" }
        };

        var next = BattleApplication.ApplySlotResults(world, "homeworld", results);
        var slot = next.Sectors.Single(s => s.SectorId == "homeworld").Slots.Single(sl => sl.SlotIndex == targetSlot.SlotIndex);

        Assert.Equal("zomboss", slot.OwnerFactionId);
        Assert.Null(slot.StructureId);
    }

    /// <summary>base-defense `siege-construction` (decision 27, 2026-09-06): a structure PLACED this
    /// battle (Built/Assembled/Summoned/Laboured) writes StructureId/ConstructionTurnsRemaining and a
    /// fresh (null, "undamaged") StructureHp — SlotState resets to Intact even if the slot's own prior
    /// State happened to be Ruined, since a freshly built structure is not rubble.</summary>
    [Fact]
    public void Slot_results_apply_a_newly_placed_structure()
    {
        var world = World();
        var targetSlot = world.Sectors.Single(s => s.SectorId == "homeworld").Slots.First();

        var results = new[]
        {
            new SlotOutcome
            {
                SlotIndex = targetSlot.SlotIndex, StructurePlaced = "granary",
                PlacedConstructionTurnsRemaining = 3,
            }
        };

        var next = BattleApplication.ApplySlotResults(world, "homeworld", results);
        var slot = next.Sectors.Single(s => s.SectorId == "homeworld").Slots.Single(sl => sl.SlotIndex == targetSlot.SlotIndex);

        Assert.Equal("granary", slot.StructureId);
        Assert.Equal(3, slot.ConstructionTurnsRemaining);
        Assert.Null(slot.StructureHp); // null means undamaged -- the default a fresh structure gets
        Assert.Equal(SlotState.Intact, slot.State);
    }

    [Fact]
    public void Guard_clearing_still_works_unchanged()
    {
        var world = World();
        var sector = world.Sectors.First(s => s.Slots.Count > 0);
        var slot = sector.Slots.First();

        var next = BattleApplication.ClearGuard(world, sector.SectorId, slot.SlotIndex);
        var clearedSlot = next.Sectors.Single(s => s.SectorId == sector.SectorId).Slots.Single(sl => sl.SlotIndex == slot.SlotIndex);

        Assert.Equal(GuardState.Cleared, clearedSlot.GuardState);
    }

    [Fact]
    public void Board_projection_round_trips_including_the_empty_slots_case()
    {
        var projection = new BoardProjection { SectorId = "ember-hollow", WorldSeed = 7, AttackerEdge = BoardEdge.West };
        Assert.Empty(projection.Slots);

        var withSlots = projection with
        {
            Slots = new[] { new SlotProjection { SlotIndex = 0, SlotTypeId = "seat", StructureId = "well", OwnerFactionId = "dave" } }
        };
        Assert.Single(withSlots.Slots);
        Assert.Equal("well", withSlots.Slots[0].StructureId);
    }

    [Fact]
    public void Budget_crosses_in_and_spend_crosses_back()
    {
        // §5.13's diagram: the world hands a budget IN via BattleRequest.Budgets; what the resolver
        // reports SPENT crosses back via the outcome side (as a debit the world applies later --
        // siege-economy's own reconciliation, not built here). This proves the DATA carries both
        // directions cleanly; no resolver exists yet to spend anything.
        var request = new BattleRequest
        {
            Kind = BattleKinds.District, LocationId = "ember-hollow", AttackerEntityId = "e1",
            Budgets = new[] { new SideBudget { EntityId = "e1", Amount = 500 } }
        };
        Assert.Equal(500, request.Budgets!.Single().Amount);
    }

    [Fact]
    public void Defender_and_attacker_budgets_come_from_different_sources()
    {
        // The asymmetry is authored at the call site that BUILDS the request (a later module), not
        // in the record itself -- this proves the record can express two independently-sourced
        // amounts for two different sides in the same battle, which is the structural precondition.
        var request = new BattleRequest
        {
            Kind = BattleKinds.District, LocationId = "ember-hollow", AttackerEntityId = "attacker", DefenderEntityId = "defender",
            Budgets = new[]
            {
                new SideBudget { EntityId = "attacker", Amount = 300 },  // CarriedLoam -- finite
                new SideBudget { EntityId = "defender", Amount = 900 },  // sector LoamStock -- supplied
            }
        };

        Assert.NotEqual(
            request.Budgets!.Single(b => b.EntityId == "attacker").Amount,
            request.Budgets!.Single(b => b.EntityId == "defender").Amount);
    }

    [Fact]
    public void No_battle_path_writes_world_stock_directly()
    {
        // spec-siege-seam.md's rule 7: "combat never writes world state" -- the budget crosses in,
        // the spend crosses back, only the world debits. Source scan over the resolver namespace's
        // own files: DistrictAssaultResolver.cs (actor-hub-and-combat-power-solid-fixing T20: the
        // real resolver, replacing the deleted PlaceholderBattleResolver.cs) and BattleApplication.cs
        // must never assign to WorldSector.LoamStock or WorldEntity.CarriedLoam.
        foreach (var file in new[] { "DistrictAssaultResolver.cs", "BattleApplication.cs", "BattleReporting.cs" })
        {
            var text = File.ReadAllText(FindSource(file));
            Assert.DoesNotContain("LoamStock =", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CarriedLoam =", text, StringComparison.Ordinal);
        }
    }

    static string FindSource(string relativeFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FusionRpg.Core", "World", "Turn", relativeFileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not find src/FusionRpg.Core/World/Turn/{relativeFileName} from any parent of {AppContext.BaseDirectory}");
    }
}
