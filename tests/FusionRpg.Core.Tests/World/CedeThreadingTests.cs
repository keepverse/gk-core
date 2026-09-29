using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-stage W25, end to end: <c>TurnEngine.Pressure</c> builds its faction id → ceded sector id
/// map from this turn's committed `cede` orders the same way `Snapshot` already builds `postures`
/// from `stance` orders (`TurnEngine.cs:285-288`) — proven here through the whole `TurnEngine.Step`
/// pipeline, not just the direct `LoamPhases.Pressure` call <see cref="Loam.LoamPhasesTests"/> makes.
/// </summary>
public class CedeThreadingTests
{
    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSector Sector(string id, string? owner, long stock = 0, int stability = 1000, int development = 0, int danger = 0, IReadOnlyList<WorldSlot>? slots = null) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = owner, LoamStock = stock, StabilityMilli = stability,
            DevelopmentLevel = development, DangerBand = danger, Slots = slots ?? Array.Empty<WorldSlot>()
        };

    static WorldState BaseWorld() => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
        Sectors = new[]
        {
            Sector("mild", "f1", stock: 0, development: 1, danger: 0),
            Sector("harsh", "f1", stock: 0, development: 5, danger: 4),
            Sector("elsewhere", "f1", stock: 0, slots: new[] { Rootbed(0) })
        },
        Lanes = new[] { new WorldLane { LaneId = "l", FromSectorId = "mild", ToSectorId = "harsh", TypeId = LaneTypeCatalog.RiftLaneTypeId } }
    };

    static WorldCommand Cede(string commander, string sectorId) => new()
    {
        CommanderId = commander, CommandId = "c-cede", Kind = WorldCommandKinds.Cede, SectorId = sectorId
    };

    static WorldSector Find(WorldState world, string id) => world.Sectors.Single(s => s.SectorId == id);

    [Fact]
    public void A_committed_cede_order_changes_which_sector_the_turn_actually_fades()
    {
        var withoutCede = TurnEngine.Step(BaseWorld(), Array.Empty<WorldCommand>(), seed: 1);
        Assert.True(Find(withoutCede.World, "harsh").StabilityMilli < 1000, "default ordering fades the harsher sector");
        Assert.Equal(1000, Find(withoutCede.World, "mild").StabilityMilli);

        var withCede = TurnEngine.Step(BaseWorld(), new[] { Cede("f1", "mild") }, seed: 1);
        Assert.True(Find(withCede.World, "mild").StabilityMilli < 1000, "the ceded sector must absorb the shortfall instead");
        Assert.Equal(1000, Find(withCede.World, "harsh").StabilityMilli);
    }

    [Fact]
    public void A_cede_order_for_a_sector_the_faction_does_not_own_is_dropped_at_reveal_and_changes_nothing()
    {
        // Admission already refuses this at submit time (WorldCommandAdmissionTests) — this proves
        // the same refusal holds at Reveal, so a stale order (legal when filed, not by the time the
        // turn resolves) cannot reach the ceded map either.
        var world = BaseWorld() with
        {
            Sectors = BaseWorld().Sectors
                .Select(s => s.SectorId == "harsh" ? s with { OwnerFactionId = "zomboss" } : s)
                .ToList(),
            Factions = BaseWorld().Factions
                .Append(new WorldFaction { FactionId = "zomboss", Kind = WorldFactionKind.Zomboss, Name = "Z" })
                .ToList()
        };

        var result = TurnEngine.Step(world, new[] { Cede("f1", "harsh") }, seed: 1);

        Assert.Contains(result.Report.Dropped, e => e.Detail == "sector.not-yours");
        // f1's own singleton component ("harsh" is no longer f1's neighbour) still fades on its own.
        Assert.True(Find(result.World, "mild").StabilityMilli < 1000);
    }
}
