using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// A survey is supposed to equal the truth. Belief deliberately holds *less* than the world — that
/// is what stops fog being cosmetic — but every field it does hold has to be right, and every field
/// the wire exposes has to come from somewhere.
///
/// The failure mode this guards against is the quiet one: a DTO field nothing populates, reading as
/// zero forever, and looking exactly like a sector that genuinely has none.
/// </summary>
public class SurveyFidelityTests
{
    /// <summary>A world where the homeworld is developed and one of its slots has been claimed.</summary>
    static WorldState Developed()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

        return WorldValidation.Validate(world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId == "homeworld"
                    ? s with
                    {
                        DevelopmentLevel = 3,
                        RecruitStock = 340,
                        ProjectId = "placeholder-project",
                        ProjectTurnsRemaining = 2,
                        Slots = s.Slots
                            .Select(sl => sl.SlotIndex == 1 ? sl with { State = SlotState.Depleted } : sl)
                            .ToList()
                    }
                    : s)
                .ToList()
        });
    }

    static IntelSnapshot Believed(WorldState w, string faction, string sector) =>
        IntelRecorder.Observe(w, w, turn: 1)
            .Single(f => f.FactionId == faction)
            .Of(sector)!;

    [Fact]
    public void Standing_on_developed_ground_you_can_see_how_developed_it_is()
    {
        var world = Developed();
        var home = Believed(world, "dave", "homeworld");

        Assert.Equal(SectorSight.Full, home.Detail);
        Assert.Equal(3, home.DevelopmentLevel);
    }

    /// <summary>world-map W45 acceptance: a faction that owns/surveys the sector remembers all three new fields.</summary>
    [Fact]
    public void Standing_on_your_own_ground_you_remember_its_recruit_stock_and_project()
    {
        var world = Developed();
        var home = Believed(world, "dave", "homeworld");

        Assert.Equal(SectorSight.Full, home.Detail);
        Assert.Equal(340, home.RecruitStock);
        Assert.Equal("placeholder-project", home.ProjectId);
        Assert.Equal(2, home.ProjectTurnsRemaining);
    }

    [Fact]
    public void A_survey_records_what_state_each_slot_is_in()
    {
        var home = Believed(Developed(), "dave", "homeworld");

        Assert.Equal(SlotState.Depleted, home.Slots.Single(s => s.SlotIndex == 1).State);
        Assert.Equal(SlotState.Claimed, home.Slots.Single(s => s.SlotIndex == 0).State);
    }

    [Fact]
    public void A_glimpse_still_reports_no_slots_and_no_development()
    {
        // Development is something you read off the ground, not from one sector away.
        //
        // Re-anchored 2026-08-22 from ember-hollow to black-gate. The template authors ember-hollow
        // as `Scouted`, so Dave *starts* knowing its insides — this test only passed because the
        // recorder was destroying that authored survey on the first turn, which is the bug
        // `SurveyMemoryTests` now pins. black-gate is authored `Unknown` and is a true glimpse.
        var world = Developed() with
        {
            Entities = Developed().Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { AtSectorId = "ash-waste" } : e)
                .ToList()
        };

        var glimpsed = Believed(world, "dave", "black-gate");

        Assert.Equal(SectorSight.Glimpse, glimpsed.Detail);
        Assert.Empty(glimpsed.Slots);
        Assert.Equal(0, glimpsed.DevelopmentLevel);
    }

    /// <summary>world-map W45 acceptance: a faction that only glimpsed a sector remembers no recruit stock.</summary>
    [Fact]
    public void A_glimpse_reports_no_recruit_stock_and_no_project()
    {
        var world = Developed() with
        {
            Entities = Developed().Entities
                .Select(e => e.EntityId == "e-dave-legion-1" ? e with { AtSectorId = "ash-waste" } : e)
                .ToList()
        };

        var glimpsed = Believed(world, "dave", "black-gate");

        Assert.Equal(SectorSight.Glimpse, glimpsed.Detail);
        Assert.Equal(0, glimpsed.RecruitStock);
        Assert.Null(glimpsed.ProjectId);
        Assert.Null(glimpsed.ProjectTurnsRemaining);
    }

    [Fact]
    public void A_survey_carries_every_field_the_snapshot_claims_to_hold()
    {
        var world = Developed();
        var truth = world.Sectors.Single(s => s.SectorId == "homeworld");
        var home = Believed(world, "dave", "homeworld");

        Assert.Equal(truth.OwnerFactionId, home.OwnerFactionId);
        Assert.Equal(truth.Phase, home.Phase);
        Assert.Equal(truth.Climate, home.Climate);
        Assert.Equal(truth.DangerBand, home.DangerBand);
        Assert.Equal(truth.DevelopmentLevel, home.DevelopmentLevel);
        Assert.Equal(truth.RecruitStock, home.RecruitStock);
        Assert.Equal(truth.ProjectId, home.ProjectId);
        Assert.Equal(truth.ProjectTurnsRemaining, home.ProjectTurnsRemaining);
        Assert.Equal(
            truth.Slots.Select(s => (s.SlotIndex, s.SlotTypeId, s.Element, s.GuardWaveId, s.GuardState, s.State)),
            home.Slots.Select(s => (s.SlotIndex, s.SlotTypeId, s.Element, s.GuardWaveId, s.GuardState, s.State)));
    }

    [Fact]
    public void The_opening_belief_a_template_seeds_is_just_as_faithful()
    {
        var world = Developed();
        var seeded = IntelSeed.ForTemplate(world).Single(f => f.FactionId == "dave").Of("homeworld")!;

        Assert.Equal(3, seeded.DevelopmentLevel);
        Assert.Equal(340, seeded.RecruitStock);
        Assert.Equal("placeholder-project", seeded.ProjectId);
        Assert.Equal(SlotState.Depleted, seeded.Slots.Single(s => s.SlotIndex == 1).State);
    }
}
