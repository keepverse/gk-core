using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// L3 acceptance (spec-loam-model.md, rules 9-13): each new validation rule has its own rejecting
/// case, and every rejection names the offending id.
/// </summary>
public class LoamValidationTests
{
    static WorldState FirstLight() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 42);

    static string Throws(WorldState w) =>
        Assert.Throws<InvalidOperationException>(() => WorldValidation.Validate(w)).Message;

    static IReadOnlyList<WorldSector> Replace(IReadOnlyList<WorldSector> source, int index, Func<WorldSector, WorldSector> edit) =>
        source.Select((item, i) => i == index ? edit(item) : item).ToList();

    static IReadOnlyList<WorldFaction> ReplaceFaction(IReadOnlyList<WorldFaction> source, int index, Func<WorldFaction, WorldFaction> edit) =>
        source.Select((item, i) => i == index ? edit(item) : item).ToList();

    [Theory]
    [InlineData(-1)]
    [InlineData(WorldValidation.MaxIntensityMilli + 1)]
    public void Rule9_fracture_intensity_out_of_bounds_rejects(int badIntensity)
    {
        var w = FirstLight();
        var broken = w with { Sectors = Replace(w.Sectors, 0, s => s with { FractureIntensityMilli = badIntensity }) };
        Assert.Contains(w.Sectors[0].SectorId, Throws(broken));
    }

    [Fact]
    public void Rule9_fracture_intensity_at_the_ceiling_is_legal()
    {
        var w = FirstLight();
        var atCeiling = w with { Sectors = Replace(w.Sectors, 0, s => s with { FractureIntensityMilli = WorldValidation.MaxIntensityMilli }) };
        WorldValidation.Validate(atCeiling); // does not throw
    }

    [Fact]
    public void Rule10_negative_loam_stock_rejects()
    {
        var w = FirstLight();
        var broken = w with { Sectors = Replace(w.Sectors, 0, s => s with { LoamStock = -1 }) };
        Assert.Contains(w.Sectors[0].SectorId, Throws(broken));
    }

    [Fact]
    public void Rule11_a_homeworld_with_no_rootbed_rejects()
    {
        var w = FirstLight();
        var homeIndex = w.Sectors.ToList().FindIndex(s => SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.Home));
        var homeless = w with
        {
            Sectors = Replace(w.Sectors, homeIndex, s => s with
            {
                Slots = s.Slots.Where(sl => sl.SlotTypeId != SlotTypeCatalog.RootbedSlotTypeId)
                    .Select((sl, i) => sl with { SlotIndex = i }).ToList()
            })
        };
        Assert.Contains("rootbed", Throws(homeless), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WorldValidation.MaxHandicapMilli + 1)]
    public void Rule12_handicap_out_of_bounds_rejects(int badHandicap)
    {
        var w = FirstLight();
        var broken = w with { Factions = ReplaceFaction(w.Factions, 0, f => f with { UpkeepHandicapMilli = badHandicap }) };
        Assert.Contains(w.Factions[0].FactionId, Throws(broken));
    }

    [Fact]
    public void The_existing_slot_shape_rule_already_rejects_a_rootbed_on_ground_that_forbids_it()
    {
        // spec-loam-model.md rule 3 ("a rootbed slot may only appear where AllowedSlotTypes
        // permits") is deliberately not new code — it is Rule6SlotShape, extended by L1's catalog
        // change. Barren ground never allows rootbed (L1: it is NoBase, never a base to settle).
        var w = FirstLight();
        var barrenIndex = w.Sectors.ToList().FindIndex(s =>
            SectorTypeCatalog.Get(s.TypeId).Flags.HasFlag(SectorTypeFlags.NoBase));
        var illegal = w with
        {
            Sectors = Replace(w.Sectors, barrenIndex, s => s with
            {
                Slots = s.Slots.Append(new WorldSlot
                {
                    SlotIndex = s.Slots.Count, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId
                }).ToList()
            })
        };
        Assert.Contains(SlotTypeCatalog.RootbedSlotTypeId, Throws(illegal));
    }
}
