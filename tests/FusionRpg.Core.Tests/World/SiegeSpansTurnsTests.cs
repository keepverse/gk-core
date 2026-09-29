using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// base-defense `siege-engagement`'s own named, deferred gap, closed 2026-09-06: a CONTINUING siege
/// (turn 2+, no fresh `assault` order) used to fall to <c>ContactResolver.SectorContacts</c>, which
/// always built a <see cref="BattleKinds.Sector"/> request — <c>DistrictAssaultResolver</c>'s own
/// delegation guard then correctly routed that to the placeholder resolver instead of the real board,
/// so a siege silently stopped being a real fight the moment its attacker held still for one turn.
/// This suite proves the fix (<c>MovementPhase.BuildContactRequest</c>) fires ONLY for a contact in a
/// sector that is actually owned and besieged, and leaves every other sector contact — the entire
/// existing open-field game — completely untouched.
/// </summary>
public class SiegeSpansTurnsTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldState Place(WorldState w, string entityId, string sectorId) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId
                ? e with
                {
                    AtSectorId = sectorId, OnLaneId = null, OnLaneTowardSectorId = null,
                    LaneProgressMilli = 0, MovementRemaining = 1000, Stance = "hold"
                }
                : e)
            .ToList()
    };

    [Fact]
    public void A_continuing_siege_with_no_fresh_order_still_builds_a_district_battle_not_a_sector_one()
    {
        // Dave's own legion already sits at "homeworld" (his Seat) by default. Marching the hostile
        // zomboss band in and submitting a turn with NO commands at all simulates exactly the gap's
        // own scenario: an attacker already standing its ground from a previous turn, nobody filing a
        // fresh assault this turn.
        var world = Place(World(), "e-zomboss-band-1", "homeworld");

        var result = TurnEngine.Step(world, Array.Empty<WorldCommand>(), seed: 1);

        Assert.Contains(result.Report.Entries, e =>
            e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District + ":homeworld:"));
        Assert.DoesNotContain(result.Report.Entries, e =>
            e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.Sector + ":homeworld:"));
    }

    [Fact]
    public void The_defender_is_whoever_owns_the_sector_not_whoever_happened_not_to_move()
    {
        // Both sides are equally "stationary" here (ContactResolver.FirstHostilePair's own ordinal
        // tie-break would otherwise call either one the "attacker" arbitrarily) -- the fix must read
        // ownership, not movement, to get the roles right.
        var world = Place(World(), "e-zomboss-band-1", "homeworld");
        var result = TurnEngine.Step(world, Array.Empty<WorldCommand>(), seed: 1);

        var battleEntry = result.Report.Entries.Single(e =>
            e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District));
        var expectedId = BattleKinds.IdFor(world.CurrentTurn + 1, BattleKinds.District, "homeworld",
            "e-zomboss-band-1", "e-dave-legion-1");
        Assert.Equal(expectedId, battleEntry.Subject);
    }

    [Fact]
    public void An_ordinary_unowned_sector_contact_still_builds_a_sector_battle_exactly_as_before()
    {
        // ember-hollow has no owner in this fixture -- IsUnderSiege can never be true there, so this
        // is the entire existing open-field game, untouched by the fix.
        var world = Place(Place(World(), "e-dave-legion-1", "ember-hollow"), "e-wild-pack-1", "ember-hollow");

        var result = TurnEngine.Step(world, Array.Empty<WorldCommand>(), seed: 1);

        Assert.Contains(result.Report.Entries, e =>
            e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.Sector + ":ember-hollow:"));
        Assert.DoesNotContain(result.Report.Entries, e =>
            e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District));
    }

    [Fact]
    public void A_fresh_assault_command_still_works_exactly_as_the_district_assault_phase_specifies()
    {
        // Regression: the Assaults phase's own explicit-order path must be completely unaffected by
        // this fix -- it goes through DistrictAssaultPhase.Run directly, never through the
        // MovementPhase contact path this task changed.
        var world = Place(World(), "e-dave-legion-1", "ash-waste");
        var command = new WorldCommand
        {
            CommanderId = "dave", CommandId = "a-e-dave-legion-1", Kind = WorldCommandKinds.Assault,
            EntityId = "e-dave-legion-1", SectorId = "ash-waste"
        };

        var result = TurnEngine.Step(world, new[] { command }, seed: 1);

        Assert.Contains(result.Report.Entries, e =>
            e.Kind == TurnReportKinds.Battle && e.Detail.StartsWith(BattleKinds.District + ":ash-waste:"));
    }
}
