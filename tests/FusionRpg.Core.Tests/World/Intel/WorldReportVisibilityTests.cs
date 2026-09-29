using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Intel;

/// <summary>
/// W-F1's report-line rule, now in Core (notification-ssot NS5.2, cross-program ask A2 accepted by
/// the owner 2026-09-21). One rule, two consumers: the turn-report projection and the world-turn
/// notification source must show a player the same lines, so the rule is asserted here directly
/// while `WorldTurnReportFogTests` keeps proving it through the real endpoint (unmodified).
/// </summary>
public class WorldReportVisibilityTests
{
    static WorldState World(int turn = 0) =>
        WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1) with { CurrentTurn = turn };

    static BelievedWorldView View(WorldState world, string faction) => new(world, faction);

    static TurnReportEntry Entry(string kind, string detail, string? sectorId, string? audience = null) =>
        new("Movement", kind, "e-1", detail, sectorId, audience);

    [Fact]
    public void No_viewer_is_the_simulation_and_sees_everything()
    {
        var entry = Entry(TurnReportKinds.Battle, "battle.won", "ash-waste");

        Assert.True(WorldReportVisibility.VisibleTo(entry, viewer: null, believed: null));
        Assert.True(WorldReportVisibility.VisibleTo(entry, viewer: "dave", believed: null)); // an un-Fogged caller
    }

    [Fact]
    public void Rule_1_an_audience_line_reaches_only_its_owner()
    {
        var entry = Entry(TurnReportKinds.Event, "legion.resupplied", sectorId: null, audience: "dave");
        var dave = View(World(), "dave");

        Assert.True(WorldReportVisibility.VisibleTo(entry, "dave", dave));
        Assert.False(WorldReportVisibility.VisibleTo(entry, "zomboss", dave));
    }

    [Fact]
    public void Rule_2_a_dynamic_fact_needs_live_sight()
    {
        var world = World();
        var dave = View(world, "dave");
        var watched = world.Sectors.First(s => dave.StateOf(s.SectorId) == IntelState.Watched).SectorId;
        // Believed but no longer watched: a rumour or a stale glance is not "live sight".
        var remembered = world.Sectors.First(s =>
            dave.Believed(s.SectorId) is not null && dave.StateOf(s.SectorId) != IntelState.Watched).SectorId;

        Assert.True(WorldReportVisibility.VisibleTo(Entry(TurnReportKinds.Battle, "battle.won", watched), "dave", dave));
        Assert.False(WorldReportVisibility.VisibleTo(Entry(TurnReportKinds.Battle, "battle.won", remembered), "dave", dave));
    }

    [Fact]
    public void Rule_3_a_static_fact_needs_only_memory_of_the_ground()
    {
        var world = World();
        var dave = View(world, "dave");
        var remembered = world.Sectors.First(s =>
            dave.Believed(s.SectorId) is not null && dave.StateOf(s.SectorId) != IntelState.Watched).SectorId;
        var unseen = world.Sectors.First(s => dave.Believed(s.SectorId) is null).SectorId;

        foreach (var prefix in WorldReportVisibility.StaticFactDetailPrefixes)
        {
            Assert.True(WorldReportVisibility.VisibleTo(
                Entry(TurnReportKinds.Event, prefix + "s-1", remembered), "dave", dave));
            // Memory is not clairvoyance: a sector never seen reveals nothing, static fact or not.
            Assert.False(WorldReportVisibility.VisibleTo(
                Entry(TurnReportKinds.Event, prefix + "s-1", unseen), "dave", dave));
        }
    }

    [Fact]
    public void Rule_4_the_calendar_is_shown_to_everyone_and_nothing_else_sectorless_is()
    {
        var dave = View(World(), "dave");

        Assert.True(WorldReportVisibility.VisibleTo(
            Entry(TurnReportKinds.Calendar, "week.2", sectorId: null), "zomboss", dave));
        Assert.False(WorldReportVisibility.VisibleTo(
            Entry(TurnReportKinds.CommandAccepted, "command.accepted", sectorId: null), "zomboss", dave));
        Assert.False(WorldReportVisibility.VisibleTo(
            Entry(TurnReportKinds.Event, "somewhere.unplaced", sectorId: null), "zomboss", dave));
    }

    [Fact]
    public void IsStaticFact_reads_the_closed_prefix_list_and_only_for_an_event()
    {
        Assert.True(WorldReportVisibility.IsStaticFact(TurnReportKinds.Event, "claim.s-1"));
        Assert.True(WorldReportVisibility.IsStaticFact(TurnReportKinds.Event, "loam.lost:s-1"));
        // A prefix that merely starts the same way is not on the list.
        Assert.False(WorldReportVisibility.IsStaticFact(TurnReportKinds.Event, "claimant.s-1"));
        Assert.False(WorldReportVisibility.IsStaticFact(TurnReportKinds.Event, "loam.lost"));
        // The list is closed to Event-kind lines: a battle detail that happened to read "claim." is dynamic.
        Assert.False(WorldReportVisibility.IsStaticFact(TurnReportKinds.Battle, "claim.s-1"));
    }

    /// <summary>
    /// A CLOSED vocabulary the code owns and a human changes (validation-ssot: pin the enum, never a
    /// population). Pinned so that widening it is a reviewed edit: a new prefix makes facts visible
    /// from memory, which is exactly the direction that leaks stale information if it is wrong.
    /// </summary>
    [Fact]
    public void StaticFactDetailPrefixes_is_a_two_member_closed_vocabulary()
    {
        Assert.Equal(new[] { "claim.", "loam.lost:" }, WorldReportVisibility.StaticFactDetailPrefixes);
    }
}
