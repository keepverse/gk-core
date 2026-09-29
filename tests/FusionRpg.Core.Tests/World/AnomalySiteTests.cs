using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// npc-story-events NR2.31 (spec-world-anomaly-sites.md §1, §3): the `anomaly` slot type is allowed on
/// exactly three sector types, and the list is VALIDATION ONLY — its one reader is
/// <see cref="WorldValidation"/>'s own slot-shape rule, which is why widening it moves no world hash and
/// why every existing template and golden stays byte-identical.
///
/// <para>The three are a declared, reviewed set with a reason each, not a derived one: `storm` (the
/// Fracture at its most active), `nexus` (where clusters join — the only type that can also host a Seat),
/// `barren` (ground the Fracture stripped, `NoBase`, so a site to raid and never to settle; the only
/// fitting type in BOTH shipped templates).</para>
/// </summary>
public class AnomalySiteTests
{
    const string Anomaly = "anomaly";

    static WorldState FirstLight() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 42);
    static WorldState TwoHearths() => WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, seed: 7);

    [Fact]
    public void Anomaly_is_the_catalogs_own_slot_kind()
    {
        // The id is not invented here: the slot type already exists, with its own kind and no
        // catalog row change (spec §3's own "no new slot type" clause).
        Assert.Equal(SlotKind.Anomaly, SlotTypeCatalog.Get(Anomaly).Kind);
    }

    [Fact]
    public void Anomaly_is_allowed_exactly_on_the_three_reviewed_types()
    {
        var allowing = SectorTypeCatalog.All
            .Where(s => s.AllowedSlotTypes.Contains(Anomaly, StringComparer.Ordinal))
            .Select(s => s.TypeId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "barren", "nexus", "storm" }, allowing);
    }

    [Fact]
    public void The_two_underworld_types_that_already_carry_a_study_site_do_not_allow_a_second_one()
    {
        // `stable` and `boss-lair` already allow `vault` (the other study site). Widening the anomaly
        // list there would put two study sites of different kinds on one sector; spec §3 leaves them out.
        Assert.Contains("vault", SectorTypeCatalog.Get("stable").AllowedSlotTypes);
        Assert.Contains("vault", SectorTypeCatalog.Get("boss-lair").AllowedSlotTypes);
        Assert.DoesNotContain(Anomaly, SectorTypeCatalog.Get("stable").AllowedSlotTypes);
        Assert.DoesNotContain(Anomaly, SectorTypeCatalog.Get("boss-lair").AllowedSlotTypes);
    }

    [Fact]
    public void A_stable_sector_with_an_anomaly_slot_is_refused_by_validation()
    {
        var world = FirstLight();
        var stableIndex = world.Sectors.ToList().FindIndex(s => s.TypeId == "stable");
        Assert.True(stableIndex >= 0, "the first-light template must carry a stable sector for this falsifier");

        var illegal = world with
        {
            Sectors = world.Sectors.Select((sector, i) => i == stableIndex
                ? sector with
                {
                    Slots = sector.Slots.Append(new WorldSlot
                    {
                        SlotIndex = sector.Slots.Count, SlotTypeId = Anomaly
                    }).ToList()
                }
                : sector).ToList()
        };

        var message = Assert.Throws<InvalidOperationException>(() => WorldValidation.Validate(illegal)).Message;

        Assert.Contains(world.Sectors[stableIndex].SectorId, message, StringComparison.Ordinal);
        Assert.Contains(Anomaly, message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_anomaly_slot_on_a_barren_sector_validates()
    {
        // The positive control for the falsifier above: the same edit on ground the list allows is
        // legal, so the refusal is the allow-list and not the added slot.
        var world = TwoHearths();
        var barrenIndex = world.Sectors.ToList().FindIndex(s => s.TypeId == "barren");
        Assert.True(barrenIndex >= 0, "the two-hearths template must carry a barren sector");

        var legal = world with
        {
            Sectors = world.Sectors.Select((sector, i) => i == barrenIndex
                ? sector with
                {
                    Slots = sector.Slots.Append(new WorldSlot
                    {
                        SlotIndex = sector.Slots.Count, SlotTypeId = Anomaly
                    }).ToList()
                }
                : sector).ToList()
        };

        WorldValidation.Validate(legal); // does not throw
    }

    [Fact]
    public void No_shipped_template_places_an_anomaly_slot_yet()
    {
        // The template half is NR2.32. Until it lands the list is inert, which is what makes this
        // change validation-only: measured, not assumed.
        foreach (var world in new[] { FirstLight(), TwoHearths() })
            foreach (var sector in world.Sectors)
                Assert.DoesNotContain(sector.Slots, slot => slot.SlotTypeId == Anomaly);
    }
}
