using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-stage W28 made `bind-warden` the production writer of `WorldSector.WardenBindingId`.
/// warden-freeze-fix (RulesetVersion 13) retired the verb: the owner withdrew the warden freeze on
/// 2026-09-13 (warden-mortality-ideal.md). What this file now proves is the retirement through the
/// real engine path: an order is dropped at Reveal and binds nothing, and a binding written before
/// the retirement is data only. `WardenResolver` itself stays, so its own resolution-time check is
/// still covered here by calling it directly.
/// </summary>
public class BindWardenThreadingTests
{
    static WorldState World() => WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand BindWarden(string commander, string sectorId, string wardenId) => new()
    {
        CommanderId = commander, CommandId = "c-bind", Kind = WorldCommandKinds.BindWarden,
        SectorId = sectorId, WardenId = wardenId
    };

    [Fact]
    public void A_bind_warden_order_is_dropped_at_reveal_as_retired_and_binds_nothing()
    {
        // Reveal re-admits every order, so this is also what happens to a bind filed under
        // RulesetVersion 12 that was still open when 13 shipped.
        var result = TurnEngine.Step(World(), new[] { BindWarden("dave", "homeworld", "creature-1") }, seed: 1);

        Assert.Null(result.World.Sectors.Single(s => s.SectorId == "homeworld").WardenBindingId);
        var dropped = Assert.Single(result.Report.Dropped, e => e.Subject == "c-bind");
        Assert.Equal(WorldCommandAdmission.WardenRetired, dropped.Detail);
        Assert.DoesNotContain(result.Report.Entries,
            e => e.Kind == TurnReportKinds.CommandAccepted && e.Subject == "c-bind");
        Assert.DoesNotContain(result.Report.Entries, e => e.Detail.StartsWith("warden.bound:"));
    }

    [Fact]
    public void A_retired_bind_changes_no_state()
    {
        // The dropped order leaves the world exactly where a turn with no orders leaves it.
        var withBind = TurnEngine.Step(World(), new[] { BindWarden("dave", "homeworld", "creature-1") }, seed: 1);
        var withNothing = TurnEngine.Step(World(), Array.Empty<WorldCommand>(), seed: 1);

        Assert.Equal(withNothing.StateHash, withBind.StateHash);
    }

    [Fact]
    public void A_sector_bound_before_the_retirement_is_still_a_weakest_candidate()
    {
        // The binding is written by the resolver directly, the way a pre-13 turn wrote it, so the
        // world shape is the one a legacy save carries. A cede on that sector must win: it only wins
        // when the sector is a candidate, which under 12 a bound sector never was.
        var bound = WardenResolver.Run(
            World(), new[] { BindWarden("dave", "homeworld", "creature-1") }, new TurnReport(), "snapshot");
        Assert.Equal("creature-1", bound.Sectors.Single(s => s.SectorId == "homeworld").WardenBindingId);
        var component = TerritoryComponents.For(bound, "dave").Single(c => c.Contains("homeworld"));

        Assert.Equal("homeworld", LoamForecast.Weakest(bound, component, available: 0, upkeep: 999_999, ceded: "homeworld"));
    }

    [Fact]
    public void A_binder_who_no_longer_owns_the_sector_at_resolution_is_refused()
    {
        // Re-validated at resolution, not trusted from admission — the same discipline
        // ClaimResolver/BuildResolver already apply, and for the same reason: the ground may have
        // been lost to fade or conquest later the same turn the order was filed.
        var world = new WorldState
        {
            Factions = new[] { new WorldFaction { FactionId = "dave", Kind = WorldFactionKind.Player, Name = "Dave" } },
            Sectors = new[]
            {
                new WorldSector { SectorId = "s", OwnerFactionId = null, Phase = SectorPhase.Lost }
            }
        };
        var report = new TurnReport();

        var result = WardenResolver.Run(world, new[] { BindWarden("dave", "s", "creature-1") }, report, "snapshot");

        Assert.Null(result.Sectors.Single(s => s.SectorId == "s").WardenBindingId);
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Detail == "warden.not-yours");
    }
}
