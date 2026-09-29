using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L24 acceptance (spec-loam-fe.md's abandonment surface): the forecast the server projects a turn
/// early to warn a player must agree with what <see cref="LoamPhases.Pressure"/> actually goes on to
/// do that same turn — proven here by running both against the same fixture and comparing, not by
/// trusting the arithmetic on its own.
/// </summary>
public class LoamForecastTests
{
    const string Phase = "Test";

    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSector Sector(string id, long stock = 0, int stability = 1000, int development = 0, int danger = 0, IReadOnlyList<WorldSlot>? slots = null) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = "f1", LoamStock = stock, StabilityMilli = stability,
            DevelopmentLevel = development, DangerBand = danger, Slots = slots ?? Array.Empty<WorldSlot>()
        };

    static WorldState World(IReadOnlyList<WorldSector> sectors, IReadOnlyList<WorldLane>? lanes = null) => new()
    {
        WorldId = "w", TemplateId = "t", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
        Sectors = sectors,
        Lanes = lanes ?? Array.Empty<WorldLane>()
    };

    /// <summary>An unconnected rootbed sector so G-C's "no source anywhere" exemption never swallows
    /// the upkeep of the component under test — the same pitfall <c>A_cut_sector_runs_down_and_is_lost</c>
    /// in <c>LoamPhasesTests</c> exists to name.</summary>
    static WorldSector Elsewhere() => Sector("elsewhere", slots: new[] { Rootbed(0) });

    static IReadOnlyList<string> ComponentOf(WorldState world, string sectorId) =>
        TerritoryComponents.For(world, "f1").Single(c => c.Contains(sectorId));

    [Fact]
    public void A_healthy_component_forecasts_no_release()
    {
        var world = World(new[] { Sector("s", slots: new[] { Rootbed(0) }) });
        Assert.Null(LoamForecast.WillRelease(world, ComponentOf(world, "s")));
    }

    [Fact]
    public void A_shortfall_that_would_not_zero_the_weakest_forecasts_no_release()
    {
        // Full stability plus LoamPolicy.MaxDecayMilli's single-turn cap mean one turn's shortfall
        // can dim a sector but never zero it outright starting from full health.
        var world = World(new[] { Sector("s", stock: 0, stability: 1000, development: 5, danger: 4), Elsewhere() });
        Assert.Null(LoamForecast.WillRelease(world, ComponentOf(world, "s")));
    }

    /// <summary>
    /// world-map W55 (empire-economy-ssot.md A8): once `LoamProduction.For` gains a real
    /// development-yield term, a sourceless sector's own `DevelopmentLevel` alone can no longer
    /// create a shortfall — A8 requires the yield rate to exceed the upkeep rate, so any positive
    /// `DevelopmentLevel` is now a net *contributor*, not a drag. Every fixture below that still
    /// wants "a sourceless, high-upkeep sector" adds a compensating `danger: 2 * development` on top
    /// of whatever danger it already carried — `2` because `DevelopmentYieldPerLevel(6) /
    /// DangerUpkeepPerBand(3) == 2` exactly, at the real configured tuning — which cancels the new
    /// yield term precisely and reproduces the exact pre-W55 upkeep-minus-production balance,
    /// verified against the real shipped tuning rather than assumed.
    /// </summary>
    [Fact]
    public void A_shortfall_against_a_sector_already_this_fragile_forecasts_its_release()
    {
        var world = World(new[] { Sector("s", stock: 0, stability: 50, development: 10, danger: 4 + 2 * 10), Elsewhere() });
        Assert.Equal("s", LoamForecast.WillRelease(world, ComponentOf(world, "s")));
    }

    [Fact]
    public void Picks_the_weakest_contributor_not_the_first_by_id()
    {
        var mild = Sector("mild", stock: 0, stability: 50, development: 1, danger: 0 + 2 * 1);
        var harsh = Sector("harsh", stock: 0, stability: 50, development: 5, danger: 4 + 2 * 5);
        var world = World(
            new[] { mild, harsh, Elsewhere() },
            new[] { new WorldLane { LaneId = "l", FromSectorId = "mild", ToSectorId = "harsh", TypeId = LaneTypeCatalog.RiftLaneTypeId } });

        Assert.Equal("harsh", LoamForecast.WillRelease(world, ComponentOf(world, "mild")));
    }

    [Fact]
    public void Weakest_returns_null_when_the_pool_can_cover_its_own_upkeep()
    {
        // Direct coverage of Weakest's own early-return guard: neither of its two call sites
        // (Pressure only calls it once a shortfall is already known; WillRelease's own downstream
        // FadePolicy check happens to swallow a broken guard here too) actually exercises this
        // branch, so mutation testing — not line coverage — is what found the gap.
        var world = World(new[] { Sector("s", slots: new[] { Rootbed(0) }) });
        var component = ComponentOf(world, "s");

        Assert.Null(LoamForecast.Weakest(world, component, available: 100, upkeep: 50));
    }

    [Fact]
    public void ProjectedStock_caps_new_accrual_at_capacity_per_sector()
    {
        // A rootbed sector sitting 10 short of its cap: the forecast must add only the room left
        // (10), not the sector's full nominal yield (SeepPerTurn=50) — the same throttle
        // LoamPhases.Production itself applies, replayed one turn ahead without mutating state.
        var world = World(new[] { Sector("s", stock: LoamPolicy.LoamCapacity - 10, slots: new[] { Rootbed(0) }) });
        var component = ComponentOf(world, "s");

        Assert.Equal(LoamPolicy.LoamCapacity, LoamForecast.ProjectedStock(component, world));
    }

    // ---- W25: the `cede` preference is an input to Weakest, never a second code path ----------

    [Fact]
    public void A_ceded_sector_in_this_component_and_unwarded_wins_over_the_default_ordering()
    {
        // Default ordering would pick "harsh" (worse balance) — filing a cede on "mild" instead must
        // override that, proving the preference is read, not merely accepted and ignored.
        var mild = Sector("mild", stock: 0, stability: 50, development: 1, danger: 0);
        var harsh = Sector("harsh", stock: 0, stability: 50, development: 5, danger: 4);
        var world = World(
            new[] { mild, harsh, Elsewhere() },
            new[] { new WorldLane { LaneId = "l", FromSectorId = "mild", ToSectorId = "harsh", TypeId = LaneTypeCatalog.RiftLaneTypeId } });
        var component = ComponentOf(world, "mild");

        Assert.Equal("harsh", LoamForecast.Weakest(world, component, available: 0, upkeep: 10));
        Assert.Equal("mild", LoamForecast.Weakest(world, component, available: 0, upkeep: 10, ceded: "mild"));
    }

    [Fact]
    public void Ceding_a_warded_sector_is_a_candidate_like_any_other()
    {
        // warden-freeze-fix (RulesetVersion 13): a binding no longer takes a sector out of the
        // candidates. Under 12 this cede was ignored and "harsh" answered.
        var mild = Sector("mild", stock: 0, stability: 50, development: 1, danger: 0) with { WardenBindingId = "w" };
        var harsh = Sector("harsh", stock: 0, stability: 50, development: 5, danger: 4);
        var world = World(
            new[] { mild, harsh, Elsewhere() },
            new[] { new WorldLane { LaneId = "l", FromSectorId = "mild", ToSectorId = "harsh", TypeId = LaneTypeCatalog.RiftLaneTypeId } });
        var component = ComponentOf(world, "mild");

        Assert.Equal("mild", LoamForecast.Weakest(world, component, available: 0, upkeep: 10, ceded: "mild"));
    }

    [Fact]
    public void A_warded_sector_with_the_worst_balance_is_the_weakest()
    {
        // The same pair with the binding on the worse-balanced member: under 12 the exclusion made
        // "mild" answer. Now the worst balance answers, binding or not.
        var mild = Sector("mild", stock: 0, stability: 50, development: 1, danger: 0);
        var harsh = Sector("harsh", stock: 0, stability: 50, development: 5, danger: 4) with { WardenBindingId = "w" };
        var world = World(
            new[] { mild, harsh, Elsewhere() },
            new[] { new WorldLane { LaneId = "l", FromSectorId = "mild", ToSectorId = "harsh", TypeId = LaneTypeCatalog.RiftLaneTypeId } });
        var component = ComponentOf(world, "mild");

        Assert.Equal("harsh", LoamForecast.Weakest(world, component, available: 0, upkeep: 10));
    }

    [Fact]
    public void A_warded_sector_is_forecast_to_release_and_pressure_releases_it()
    {
        // The forecast must match the phase with a binding present. Under 12 both sides froze the
        // sector: WillRelease said null and Pressure left it standing. Now both release it.
        var fragile = Sector("s", stock: 0, stability: 50, development: 10, danger: 4 + 2 * 10) with { WardenBindingId = "creature-1" };
        var world = World(new[] { fragile, Elsewhere() });

        var predicted = LoamForecast.WillRelease(world, ComponentOf(world, "s"));
        var actual = LoamPhases.Pressure(LoamPhases.Production(world, new TurnReport(), Phase), new TurnReport(), Phase);

        Assert.Equal("s", predicted);
        Assert.Equal(SectorPhase.Lost, actual.Sectors.Single(x => x.SectorId == "s").Phase);
    }

    [Fact]
    public void Ceding_a_sector_outside_this_component_is_not_a_candidate_and_the_default_ordering_answers()
    {
        // "elsewhere" is a real sector in the world, just not a member of the component under test —
        // a stale or foreign order must not reach across components.
        var world = World(new[] { Sector("s", stock: 0, stability: 50, development: 10, danger: 4), Elsewhere() });
        var component = ComponentOf(world, "s");

        Assert.Equal("s", LoamForecast.Weakest(world, component, available: 0, upkeep: 10, ceded: "elsewhere"));
    }

    [Fact]
    public void A_component_that_covers_its_own_upkeep_releases_nothing_no_matter_what_was_ceded()
    {
        var world = World(new[] { Sector("s", slots: new[] { Rootbed(0) }) });
        var component = ComponentOf(world, "s");

        Assert.Null(LoamForecast.Weakest(world, component, available: 100, upkeep: 50, ceded: "s"));
    }

    [Fact]
    public void The_forecast_agrees_with_what_pressure_actually_does_this_turn()
    {
        var cases = new (WorldState World, bool ShouldRelease)[]
        {
            // world-map W55: `danger: 4 + 2 * 10` compensates the new development-yield term the
            // same way the two tests above do — see their own shared doc comment.
            (World(new[] { Sector("s", stock: 0, stability: 50, development: 10, danger: 4 + 2 * 10), Elsewhere() }), true),
            (World(new[] { Sector("s", slots: new[] { Rootbed(0) }) }), false),
            // warden-freeze-fix (RulesetVersion 13): the same two cases with a binding left on the
            // sector — it is data only, so neither the forecast nor the phase may treat it apart.
            (World(new[] { Sector("s", stock: 0, stability: 50, development: 10, danger: 4 + 2 * 10) with { WardenBindingId = "w" }, Elsewhere() }), true),
            (World(new[] { Sector("s", slots: new[] { Rootbed(0) }) with { WardenBindingId = "w" } }), false)
        };

        foreach (var (world, shouldRelease) in cases)
        {
            var predicted = LoamForecast.WillRelease(world, ComponentOf(world, "s"));
            var actual = LoamPhases.Pressure(LoamPhases.Production(world, new TurnReport(), Phase), new TurnReport(), Phase);
            var lostForReal = actual.Sectors.Single(x => x.SectorId == "s").Phase == SectorPhase.Lost;

            Assert.Equal(shouldRelease, predicted is not null);
            Assert.Equal(shouldRelease, lostForReal);
        }
    }
}
