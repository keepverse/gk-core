using FusionRpg.Core.World;
using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-map W51: `raise` actually founds a legion in production for the first time — proves the
/// command reaches <see cref="RaiseResolver"/> through a real <see cref="TurnEngine.Step"/> commit,
/// not merely the resolver called directly (<see cref="Growth.RaiseResolverTests"/> already covers
/// every legality branch at that level).
/// </summary>
public class RaiseThreadingTests
{
    static WorldState WorldWithHomeworldStock(long stock)
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);
        return world with
        {
            Sectors = world.Sectors
                .Select(s => s.SectorId == "homeworld" ? s with { RecruitStock = stock } : s)
                .ToList()
        };
    }

    static WorldCommand Raise(string commander, string sectorId) => new()
    {
        CommanderId = commander, CommandId = "c-raise", Kind = WorldCommandKinds.Raise, SectorId = sectorId
    };

    [Fact]
    public void A_committed_raise_order_founds_a_legion_and_spends_the_stock()
    {
        var world = WorldWithHomeworldStock(RecruitPolicy.RaiseCostPoints);
        var expectedTurn = world.CurrentTurn + 1;

        var result = TurnEngine.Step(world, new[] { Raise("dave", "homeworld") }, seed: 1);

        var expectedId = $"e-dave-legion-{expectedTurn}-homeworld";
        var legion = Assert.Single(result.World.Entities, e => e.EntityId == expectedId);
        Assert.Equal(WorldEntityKind.Legion, legion.Kind);
        Assert.Equal("dave", legion.OwnerFactionId);
        Assert.Equal("homeworld", legion.AtSectorId);
        Assert.Equal(0, result.World.Sectors.Single(s => s.SectorId == "homeworld").RecruitStock);

        Assert.Contains(result.Report.Entries,
            e => e.Kind == TurnReportKinds.CommandAccepted && e.Subject == "c-raise");
        Assert.Contains(result.Report.Entries,
            e => e.Kind == TurnReportKinds.Event && e.Detail == "raise.founded:" + expectedId);
        Assert.Empty(result.Report.Dropped);
    }

    [Fact]
    public void A_raiser_who_no_longer_owns_the_sector_at_resolution_is_refused()
    {
        // Re-validated at resolution, not trusted from admission — the same discipline
        // ClaimResolver/BuildResolver/WardenResolver already apply, and for the same reason: the
        // ground may have been lost to fade or conquest later the same turn the order was filed.
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[]
            {
                new WorldSector
                {
                    SectorId = "s", OwnerFactionId = null, Phase = SectorPhase.Lost,
                    RecruitStock = RecruitPolicy.RaiseCostPoints,
                    Slots = new[] { new WorldSlot { SlotIndex = 0, SlotTypeId = SlotTypeCatalog.SeatSlotTypeId } }
                }
            }
        };
        var report = new TurnReport();

        var result = RaiseResolver.Run(world, new[] { Raise("dave", "s") }, report, "snapshot", turn: 1);

        Assert.Empty(result.Entities);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "raise.not-yours");
    }
}
