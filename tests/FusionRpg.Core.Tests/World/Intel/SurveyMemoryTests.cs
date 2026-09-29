using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// Found by playing the game: a legion surveys a sector, walks one lane away, and forgets what was
/// in it. `IntelRecorder` wrote a fresh snapshot at whatever level it could currently see, so
/// stepping back to glimpse range **overwrote a survey with nothing**.
///
/// W22 fixed exactly this for the template's authored intel — "the better of the two wins" — and the
/// same defect survived in the live path, where it matters more: it makes standing on ground to
/// learn what is there nearly worthless.
/// </summary>
public class SurveyMemoryTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldState At(WorldState world, string sectorId) => world with
    {
        Entities = world.Entities
            .Select(e => e.EntityId == "e-dave-legion-1" ? e with { AtSectorId = sectorId } : e)
            .ToList()
    };

    [Fact]
    public void Walking_away_from_a_sector_does_not_forget_what_was_in_it()
    {
        // Stand in ember-hollow: a full survey, slots and all.
        var standing = At(World(), "ember-hollow");
        var surveyed = standing with { Intel = IntelRecorder.Observe(standing, standing, turn: 1) };

        var known = new BelievedWorldView(surveyed, "dave").Believed("ember-hollow")!;
        Assert.Equal(SectorSight.Full, known.Detail);
        Assert.NotEmpty(known.Slots);

        // Step home. ember-hollow is now one lane away — a glimpse, which sees no slots.
        var away = At(surveyed, "homeworld");
        var later = away with { Intel = IntelRecorder.Observe(away, away, turn: 2) };

        var remembered = new BelievedWorldView(later, "dave").Believed("ember-hollow")!;
        Assert.NotEmpty(remembered.Slots);
        Assert.Equal(SectorSight.Full, remembered.Detail);
    }

    [Fact]
    public void A_fresh_glimpse_still_updates_what_a_glimpse_can_actually_see()
    {
        // The other half: keeping the old slots must not freeze the sector in amber. Who holds it
        // and who is standing on it are things you can see from next door, and they refresh.
        var standing = At(World(), "ember-hollow");
        var surveyed = standing with { Intel = IntelRecorder.Observe(standing, standing, turn: 1) };

        var taken = At(surveyed, "homeworld") with
        {
            Sectors = surveyed.Sectors
                .Select(s => s.SectorId == "ember-hollow" ? s with { OwnerFactionId = "zomboss" } : s)
                .ToList()
        };
        var later = taken with { Intel = IntelRecorder.Observe(taken, taken, turn: 2) };

        var remembered = new BelievedWorldView(later, "dave").Believed("ember-hollow")!;
        Assert.Equal("zomboss", remembered.OwnerFactionId);   // seen from next door
        Assert.Equal(2, remembered.LastSeenTurn);             // you did just look at it
        Assert.NotEmpty(remembered.Slots);                    // and you still remember the inside
    }

    [Fact]
    public void Ground_you_never_surveyed_still_reports_no_slots()
    {
        // A glimpse that was only ever a glimpse has nothing to keep — the fix must not invent
        // knowledge, only refuse to throw it away.
        //
        // black-gate, not ember-hollow: the template authors ember-hollow as `Scouted`, so Dave
        // starts already knowing its insides and it can never be a pure glimpse. black-gate is
        // authored `Unknown`, and is one lane from ash-waste.
        var world = At(World(), "ash-waste");
        var seen = world with { Intel = IntelRecorder.Observe(world, world, turn: 1) };

        var glimpsed = new BelievedWorldView(seen, "dave").Believed("black-gate")!;
        Assert.Equal(SectorSight.Glimpse, glimpsed.Detail);
        Assert.Empty(glimpsed.Slots);
    }
}
