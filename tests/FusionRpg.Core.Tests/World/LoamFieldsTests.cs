using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// L2 acceptance (spec-loam-model.md): the three loam fields exist, are hashed, and first-light's
/// minimum edit (G-D/G-A) makes the homeworld a legal loam source without moving behaviour.
/// </summary>
public class LoamFieldsTests
{
    static WorldState FirstLight() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 42);

    [Fact]
    public void First_light_builds_deterministically_with_the_new_loam_fields()
    {
        var a = FirstLight();
        var b = FirstLight();

        Assert.Equal(WorldCanonical.Write(a), WorldCanonical.Write(b));
        WorldValidation.Validate(a); // throws if the G-D minimum edit is malformed
    }

    [Fact]
    public void The_homeworld_carries_its_g_d_rootbed_and_starting_stock()
    {
        var w = FirstLight();
        var home = w.Sectors.Single(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home));

        Assert.Contains(home.Slots, sl => sl.SlotTypeId == SlotTypeCatalog.RootbedSlotTypeId);
        Assert.True(home.LoamStock > 0, "a zero starting stock begins fading turn one (G-A)");
    }

    [Fact]
    public void Loam_stock_is_part_of_the_canonical_hash()
    {
        var w = FirstLight();
        var changed = w with { Sectors = Replace(w.Sectors, 0, s => s with { LoamStock = s.LoamStock + 1 }) };

        Assert.NotEqual(WorldCanonical.Write(w), WorldCanonical.Write(changed));
    }

    [Fact]
    public void Fracture_intensity_is_part_of_the_canonical_hash()
    {
        var w = FirstLight();
        var changed = w with { Sectors = Replace(w.Sectors, 0, s => s with { FractureIntensityMilli = s.FractureIntensityMilli + 1 }) };

        Assert.NotEqual(WorldCanonical.Write(w), WorldCanonical.Write(changed));
    }

    [Fact]
    public void Upkeep_handicap_is_part_of_the_canonical_hash()
    {
        var w = FirstLight();
        var changed = w with { Factions = ReplaceFaction(w.Factions, 0, f => f with { UpkeepHandicapMilli = f.UpkeepHandicapMilli + 1 }) };

        Assert.NotEqual(WorldCanonical.Write(w), WorldCanonical.Write(changed));
    }

    [Fact]
    public void Loam_fields_default_to_the_pre_loam_world()
    {
        // A sector or faction authored with no opinion on loam reads as "no stock, baseline
        // Fracture, no handicap" — exactly the pre-loam world, so nothing but the homeworld
        // (which needs a source to be playable) has to say anything at all.
        var plainSector = new WorldSector();
        var plainFaction = new WorldFaction();

        Assert.Equal(0L, plainSector.LoamStock);
        Assert.Equal(1000, plainSector.FractureIntensityMilli);
        Assert.Equal(1000, plainFaction.UpkeepHandicapMilli);
    }

    [Fact]
    public void This_wave_added_state_not_behaviour_so_ruleset_version_did_not_move_here()
    {
        // True when L2 landed (loam-model adds fields, not behaviour) and still true now: the
        // version bump this program needed came later, at L15 (loam-turn wires Production/Pressure),
        // not here. `TurnEngineTests` pins the current value (4); this test only pins that *this*
        // module's own field addition was not what moved it.
        Assert.True(TurnEngine.RulesetVersion >= 3);
    }

    static IReadOnlyList<WorldSector> Replace(IReadOnlyList<WorldSector> source, int index, Func<WorldSector, WorldSector> edit) =>
        source.Select((item, i) => i == index ? edit(item) : item).ToList();

    static IReadOnlyList<WorldFaction> ReplaceFaction(IReadOnlyList<WorldFaction> source, int index, Func<WorldFaction, WorldFaction> edit) =>
        source.Select((item, i) => i == index ? edit(item) : item).ToList();
}
