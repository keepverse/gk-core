using System.Reflection;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// L5 acceptance (spec-loam-model.md §Fog): `FractureIntensityMilli` is terrain and survives once
/// scouted; `LoamStock` is live state and must never reach belief or the wire at all.
/// </summary>
public class LoamFogTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldState Remembering(WorldState w, int turn) => w with { Intel = IntelRecorder.Observe(w, w, turn) };

    static IntelSnapshot? Believes(WorldState w, string faction, string sector) =>
        w.Intel.FirstOrDefault(f => f.FactionId == faction)?.Of(sector);

    static WorldState Place(WorldState w, string entityId, string sectorId) => w with
    {
        Entities = w.Entities
            .Select(e => e.EntityId == entityId
                ? e with { AtSectorId = sectorId, OnLaneId = null, OnLaneTowardSectorId = null, LaneProgressMilli = 0 }
                : e)
            .ToList()
    };

    [Fact]
    public void Standing_on_ground_means_believing_its_fracture_intensity_exactly()
    {
        var withGradient = World() with
        {
            Sectors = World().Sectors.Select(s => s.SectorId == "homeworld"
                ? s with { FractureIntensityMilli = 1750 }
                : s).ToList()
        };
        var observed = Remembering(withGradient, turn: 1);
        var home = Believes(observed, "dave", "homeworld")!;

        Assert.Equal(1750, home.FractureIntensityMilli);
    }

    [Fact]
    public void An_unseen_sector_carries_no_intensity_belief_at_all_regardless_of_the_true_value()
    {
        // black-gate is authored Unknown to Dave. Give it a deliberately loud, non-baseline
        // intensity so a leak would be unmistakable, then prove there is no belief entry to read it
        // from in the first place — the DTO layer's "never seen" branch cannot leak a value it
        // never has access to.
        var withLoudIntensity = World() with
        {
            Sectors = World().Sectors.Select(s => s.SectorId == "black-gate"
                ? s with { FractureIntensityMilli = 2999 }
                : s).ToList()
        };

        var observed = Remembering(withLoudIntensity, turn: 1);
        Assert.Null(Believes(observed, "dave", "black-gate"));
    }

    [Fact]
    public void A_scouted_sectors_intensity_survives_after_sight_is_lost()
    {
        // Move Dave's legion onto ash-waste (a glimpse-only sector when unvisited) to survey it,
        // then walk away so it falls out of sight, and confirm the remembered intensity is not
        // reset or dropped — the same shape as W19's "belief ages, it does not vanish".
        var withGradient = World() with
        {
            Sectors = World().Sectors.Select(s => s.SectorId == "ash-waste"
                ? s with { FractureIntensityMilli = 2200 }
                : s).ToList()
        };

        var onAshWaste = Remembering(Place(withGradient, "e-dave-legion-1", "ash-waste"), turn: 1);
        var awayAgain = Remembering(Place(onAshWaste, "e-dave-legion-1", "homeworld"), turn: 2);

        var remembered = Believes(awayAgain, "dave", "ash-waste")!;
        Assert.Equal(2200, remembered.FractureIntensityMilli);
    }

    /// <summary>
    /// The catch-all, W22's shape: reflection over every belief type, so a future field named after
    /// the live stat fails here even if nobody wrote a test for that particular field. The wire-side
    /// half of this property (the DTO and the actual JSON payload) is
    /// <c>WorldLoamFogE2ETests</c> — this project does not reference <c>FusionRpg.Contracts</c>.
    /// </summary>
    [Fact]
    public void No_belief_type_ever_carries_a_loam_stock_property()
    {
        var beliefTypes = new[] { typeof(IntelSnapshot), typeof(FactionIntel), typeof(RememberedSlot), typeof(RememberedForce) };

        foreach (var type in beliefTypes)
        {
            var leaky = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("LoamStock", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(leaky.Count == 0,
                $"{type.Name} exposes {string.Join(", ", leaky.Select(p => p.Name))} — live loam state must never leave the owner.");
        }
    }
}
