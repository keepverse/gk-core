using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>base-defense siege-seam 7.3 (spec-siege-seam.md §5). `DistrictAssaultPhase` is modelled
/// on `SiegePhase` deliberately, but fights a different thing — the ground itself, not a slot's
/// guard — and `SiegePhase.cs` must stay untouched by its existence.</summary>
public class DistrictAssaultPhaseTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand Assault(string commander, string entityId, string sectorId) => new()
    {
        CommanderId = commander,
        CommandId = "a-" + entityId,
        Kind = WorldCommandKinds.Assault,
        EntityId = entityId,
        SectorId = sectorId
    };

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

    static WorldEntity? Find(WorldState w, string id) => w.Entities.FirstOrDefault(e => e.EntityId == id);

    [Fact]
    public void An_assault_on_a_hostile_sector_fights_the_force_holding_it()
    {
        // The wild pack sits at ash-waste by default (WorldTemplateCatalog). March Dave's legion
        // there directly (bypassing the lane, the same "Place" convention ContactAndClearTests
        // itself uses) and order an assault.
        var world = Place(World(), "e-dave-legion-1", "ash-waste");

        var result = TurnEngine.Step(world, new[] { Assault("dave", "e-dave-legion-1", "ash-waste") }, seed: 1);

        Assert.Contains(result.Report.Entries, e =>
            e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District + ":ash-waste:"));
    }

    [Fact]
    public void District_kind_puts_a_sector_id_in_the_sector_slot()
    {
        // The W13 bug class BattleReporting.Fight's own comment names: a Lane-kind battle must not
        // put a lane id in the sector slot. Proven here for the NEW kind specifically, not assumed
        // from reading the ternary: District != Lane, so it must fall on the sectorId side.
        var world = Place(World(), "e-dave-legion-1", "ash-waste");
        var result = TurnEngine.Step(world, new[] { Assault("dave", "e-dave-legion-1", "ash-waste") }, seed: 1);

        var battleEntry = result.Report.Entries.Single(e => e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District));
        Assert.Equal("ash-waste", battleEntry.SectorId);
    }

    [Fact]
    public void Battle_id_format_is_shared()
    {
        // BattleKinds.IdFor is colocated with the kinds specifically so movement and sieges (and now
        // assaults) cannot drift into two different id formats.
        var world = Place(World(), "e-dave-legion-1", "ash-waste");
        var result = TurnEngine.Step(world, new[] { Assault("dave", "e-dave-legion-1", "ash-waste") }, seed: 1);

        var battleEntry = result.Report.Entries.Single(e => e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District));
        var expectedPrefix = BattleKinds.IdFor(world.CurrentTurn + 1, BattleKinds.District, "ash-waste", "e-dave-legion-1", "e-wild-pack-1");
        Assert.Equal(expectedPrefix, battleEntry.Subject);
    }

    [Fact]
    public void Assaulting_your_own_sector_is_dropped_not_fought()
    {
        var world = Place(World(), "e-dave-legion-1", "homeworld"); // dave's own Seat
        var result = TurnEngine.Step(world, new[] { Assault("dave", "e-dave-legion-1", "homeworld") }, seed: 1);

        Assert.Contains(result.Report.Dropped, e => e.Detail == "sector.already-yours");
        Assert.DoesNotContain(result.Report.Entries, e => e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District));
    }

    [Fact]
    public void Assaulting_from_elsewhere_is_dropped()
    {
        // The legion never actually marched to ash-waste -- still at homeworld.
        var result = TurnEngine.Step(World(), new[] { Assault("dave", "e-dave-legion-1", "ash-waste") }, seed: 1);

        Assert.Contains(result.Report.Dropped, e => e.Detail == "sector.elsewhere");
    }

    [Fact]
    public void Unopposed_assault_still_resolves()
    {
        // ember-hollow: no hostile entity stands there by default in first-light, and Dave does not
        // own it -- an unopposed assault must still fire and resolve rather than silently no-op.
        var world = Place(World(), "e-dave-legion-1", "ember-hollow");
        var result = TurnEngine.Step(world, new[] { Assault("dave", "e-dave-legion-1", "ember-hollow") }, seed: 1);

        Assert.Contains(result.Report.Entries, e =>
            e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District + ":ember-hollow:"));
        Assert.NotNull(Find(result.World, "e-dave-legion-1")); // the sole combatant survives its own fight
    }

    [Fact]
    public void Guard_clearing_still_works_unchanged()
    {
        // SiegePhase's own `clear` order, run alongside the new Assaults phase, must behave
        // identically -- proving the new phase's addition to TurnEngine.Step is inert for it.
        var world = Place(World(), "e-dave-legion-1", "ember-hollow");
        var clear = new WorldCommand
        {
            CommanderId = "dave", CommandId = "c1", Kind = WorldCommandKinds.Clear,
            EntityId = "e-dave-legion-1", SectorId = "ember-hollow", SlotIndex = 2
        };

        var result = TurnEngine.Step(world, new[] { clear }, seed: 1);

        Assert.Contains(result.Report.Entries, e => e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.Guard + ":"));
    }
}
