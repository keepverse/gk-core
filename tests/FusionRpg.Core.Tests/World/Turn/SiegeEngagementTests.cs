using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Turn;

/// <summary>
/// base-defense `siege-engagement` (module 20, spec-siege-engagement.md): the pure `EngagementExit`
/// mapping and the derived, never-stored `IsUnderSiege` predicate. See `SiegeEngagement.cs`'s own top
/// comment for the real, named, un-started gap this module does not fix — the Sector-vs-District
/// battle-kind dispatch that means a continuing siege does not actually repeat today.
/// </summary>
public class SiegeEngagementTests
{
    static BattleSideOutcome Side(string entityId, bool withdrawn = false) => new() { EntityId = entityId, Withdrawn = withdrawn };

    [Fact]
    public void ExitFor_maps_CoreTaken_and_Inconclusive_directly()
    {
        var sides = new[] { Side("attacker"), Side("defender") };
        Assert.Equal(EngagementExit.CoreTaken, SiegeEngagement.ExitFor(SiegeOutcomeKind.CoreTaken, sides, "attacker"));
        Assert.Equal(EngagementExit.Spent, SiegeEngagement.ExitFor(SiegeOutcomeKind.Inconclusive, sides, "attacker"));
    }

    [Fact]
    public void ExitFor_splits_AssaultBroken_into_broken_vs_withdrawn_by_the_attackers_own_flag()
    {
        var beaten = new[] { Side("attacker", withdrawn: false), Side("defender") };
        Assert.Equal(EngagementExit.AssaultBroken, SiegeEngagement.ExitFor(SiegeOutcomeKind.AssaultBroken, beaten, "attacker"));

        var withdrew = new[] { Side("attacker", withdrawn: true), Side("defender") };
        Assert.Equal(EngagementExit.Withdrawn, SiegeEngagement.ExitFor(SiegeOutcomeKind.AssaultBroken, withdrew, "attacker"));
    }

    [Fact]
    public void ExitFor_treats_a_missing_attacker_side_as_not_withdrawn()
    {
        var sides = new[] { Side("defender") };
        Assert.Equal(EngagementExit.AssaultBroken, SiegeEngagement.ExitFor(SiegeOutcomeKind.AssaultBroken, sides, "attacker"));
    }

    [Fact]
    public void IsUnderSiege_is_false_for_an_unowned_sector()
    {
        var world = new WorldState { TemplateId = "t", Sectors = new[] { new WorldSector { SectorId = "s1", OwnerFactionId = null } } };
        Assert.False(SiegeEngagement.IsUnderSiege(world, "s1"));
    }

    [Fact]
    public void IsUnderSiege_is_false_for_an_unknown_sector()
    {
        var world = new WorldState { TemplateId = "t", Sectors = Array.Empty<WorldSector>() };
        Assert.False(SiegeEngagement.IsUnderSiege(world, "nope"));
    }

    [Fact]
    public void Board_positions_do_not_persist()
    {
        // WorldEntity carries no cell/grid-position field at all -- a board is derived per-battle,
        // never stored on the world.
        var props = typeof(WorldEntity).GetProperties().Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain(props, n => n.Contains("cell") || n.Contains("gridpos") || n.Contains("boardposition"));
    }

    [Fact]
    public void Is_under_siege_is_never_stored()
    {
        var props = typeof(WorldSector).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(props, n => n.Contains("Besieged", StringComparison.OrdinalIgnoreCase)
            || n.Contains("UnderSiege", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Existing_battle_kinds_construct_an_outcome_with_no_exit()
    {
        var outcome = new FusionRpg.Core.World.Turn.BattleOutcome { BattleId = "b1" };
        Assert.Null(outcome.Exit);
    }
}
