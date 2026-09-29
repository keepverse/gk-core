using FusionRpg.Contracts;
using FusionRpg.Core.World.Notify;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Notify;

/// <summary>world-notify-source spec §2, test 1 — every entry built with the SAME prefix literal and
/// argument shape as its cited producer (verified against real call sites), so a real turn report's
/// entries always resolve exactly as this table promises.</summary>
public class WorldTurnNotificationClassifierTests
{
    static TurnReportEntry Entry(string kind, string subject, string detail, string? sectorId = null, string? audience = null) =>
        new("phase", kind, subject, detail, sectorId, audience);

    [Fact]
    public void Loam_shortfall_maps_to_important_subject_faction()
    {
        // LoamPhases.cs:186 — report.Add(phase, Event, faction.FactionId, "loam.shortfall:" + shortfall, weakest);
        var e = Entry(TurnReportKinds.Event, "f-dave", "loam.shortfall:340", sectorId: "s-weak");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("loam.shortfall", c!.Value.Category);
        Assert.Equal(NotifySeverity.Important, c.Value.Severity);
        Assert.Equal(WorldRecipientRule.SubjectFaction, c.Value.Rule);
        Assert.Equal("faction:f-dave", c.Value.SubjectKey);
    }

    [Fact]
    public void Loam_shortfall_unresolved_is_not_swallowed_by_the_plain_loam_shortfall_row()
    {
        // LoamPhases.cs:180 — report.Add(phase, Event, faction.FactionId, "loam.shortfall.unresolved:" + shortfall, audience: faction.FactionId);
        var e = Entry(TurnReportKinds.Event, "f-dave", "loam.shortfall.unresolved:200", audience: "f-dave");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("loam.shortfall", c!.Value.Category);
        Assert.Equal(NotifySeverity.Important, c.Value.Severity);
        Assert.Equal(WorldRecipientRule.SubjectFaction, c.Value.Rule);
    }

    [Fact]
    public void Legion_runway_maps_to_important_audience_keyed_by_the_legion()
    {
        // MovementPhase.cs:115-116
        var e = Entry(TurnReportKinds.Event, "e-dave-legion-1", "legion.runway:12", sectorId: "s-x", audience: "f-dave");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("legion.runway", c!.Value.Category);
        Assert.Equal(NotifySeverity.Important, c.Value.Severity);
        Assert.Equal(WorldRecipientRule.Audience, c.Value.Rule);
        Assert.Equal("legion:e-dave-legion-1", c.Value.SubjectKey);
    }

    [Fact]
    public void Legion_topup_maps_to_routine_supply_change_keyed_by_the_faction()
    {
        // LegionSupply.cs:105 — Subject is the faction id here (unlike supply.restored below).
        var e = Entry(TurnReportKinds.Event, "f-dave", "legion.topup:50", audience: "f-dave");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("supply.change", c!.Value.Category);
        Assert.Equal(NotifySeverity.Routine, c.Value.Severity);
        Assert.Equal(WorldRecipientRule.Audience, c.Value.Rule);
        Assert.Equal("faction:f-dave", c.Value.SubjectKey);
    }

    [Fact]
    public void Supply_restored_maps_to_routine_supply_change_keyed_by_the_legion()
    {
        // LegionSupply.cs:119-120 — Subject is entity.EntityId here, no Detail suffix.
        var e = Entry(TurnReportKinds.Event, "e-dave-legion-1", "supply.restored", sectorId: "s-x", audience: "f-dave");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("supply.change", c!.Value.Category);
        Assert.Equal(WorldRecipientRule.Audience, c.Value.Rule);
        Assert.Equal("legion:e-dave-legion-1", c.Value.SubjectKey);
    }

    [Fact]
    public void Supply_cut_maps_to_routine_subject_faction_keyed_by_the_sector()
    {
        // SupplyGraph.cs:119 — Subject is the owner faction id; SectorId is the cut sector.
        var e = Entry(TurnReportKinds.Event, "f-dave", "supply.cut:s-x", sectorId: "s-x");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("supply.change", c!.Value.Category);
        Assert.Equal(WorldRecipientRule.SubjectFaction, c.Value.Rule);
        Assert.Equal("sector:s-x", c.Value.SubjectKey);
    }

    [Fact]
    public void Supply_besieged_maps_the_same_way_as_supply_cut_ask_A6()
    {
        // SupplyGraph.cs:113-114
        var e = Entry(TurnReportKinds.Event, "f-dave", "supply.besieged:s-x", sectorId: "s-x", audience: "f-dave");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("supply.change", c!.Value.Category);
        Assert.Equal(WorldRecipientRule.SubjectFaction, c.Value.Rule);
        Assert.Equal("sector:s-x", c.Value.SubjectKey);
    }

    [Theory]
    [InlineData("growth.pulse:3")]
    [InlineData("develop.completed:silo")]
    [InlineData("development.raised:5")]
    public void Growth_lines_map_to_routine_sector_owner(string detail)
    {
        // GrowthPhases.cs:65,132,141 — Subject and SectorId are both the sector id.
        var e = Entry(TurnReportKinds.Event, "s-x", detail, sectorId: "s-x");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("growth", c!.Value.Category);
        Assert.Equal(NotifySeverity.Routine, c.Value.Severity);
        Assert.Equal(WorldRecipientRule.SectorOwner, c.Value.Rule);
        Assert.Equal("sector:s-x", c.Value.SubjectKey);
    }

    [Fact]
    public void Build_started_maps_to_growth_subject_command_keyed_by_the_command()
    {
        // BuildResolver.cs:169-170 — Subject is the command id, not an entity.
        var e = Entry(TurnReportKinds.Event, "c-1", "build.started:silo", sectorId: "s-x");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("growth", c!.Value.Category);
        Assert.Equal(WorldRecipientRule.SubjectCommand, c.Value.Rule);
        Assert.Equal("command:c-1", c.Value.SubjectKey);
    }

    [Fact]
    public void Intel_new_maps_to_routine_subject_faction()
    {
        // TurnEngine.cs:389
        var e = Entry(TurnReportKinds.Event, "f-dave", "intel.new:5");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("intel.new", c!.Value.Category);
        Assert.Equal(WorldRecipientRule.SubjectFaction, c.Value.Rule);
        Assert.Equal("faction:f-dave", c.Value.SubjectKey);
    }

    [Fact]
    public void Loam_lost_maps_to_important_territory_lost_keyed_by_the_sector()
    {
        // LoamPhases.cs:214 — Subject is the PREVIOUS owner's faction id.
        var e = Entry(TurnReportKinds.Event, "f-dave", "loam.lost:s-x", sectorId: "s-x");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("territory.lost", c!.Value.Category);
        Assert.Equal(NotifySeverity.Important, c.Value.Severity);
        Assert.Equal(WorldRecipientRule.SubjectFaction, c.Value.Rule);
        Assert.Equal("sector:s-x", c.Value.SubjectKey);
    }

    [Theory]
    [InlineData("entity.unknown")]
    [InlineData("wonder.cap-reached")]
    [InlineData("build.out-of-range:s-x")]
    public void Every_command_dropped_reason_maps_to_the_one_row_keyed_by_command(string reason)
    {
        var e = Entry(TurnReportKinds.CommandDropped, "c-1", reason);
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("command.dropped", c!.Value.Category);
        Assert.Equal(NotifySeverity.Routine, c.Value.Severity);
        Assert.Equal(WorldRecipientRule.SubjectCommand, c.Value.Rule);
        Assert.Equal("command:c-1", c.Value.SubjectKey);
    }

    [Fact]
    public void A_sector_battle_maps_to_routine_fog_visible_keyed_by_the_sector()
    {
        // BattleReporting.cs:76-80
        var e = Entry(TurnReportKinds.Battle, "battle-1", "sector:s-x:e-winner", sectorId: "s-x");
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("battle.result", c!.Value.Category);
        Assert.Equal(NotifySeverity.Routine, c.Value.Severity);
        Assert.Equal(WorldRecipientRule.FogVisible, c.Value.Rule);
        Assert.Equal("sector:s-x", c.Value.SubjectKey);
    }

    [Fact]
    public void A_lane_battle_has_no_sector_and_so_no_subject_key_never_throttled()
    {
        // BattleReporting.cs — SectorId is null when the battle kind is Lane.
        var e = Entry(TurnReportKinds.Battle, "battle-2", "lane:l-1:none", sectorId: null);
        var c = WorldTurnNotificationClassifier.Classify(e);
        Assert.NotNull(c);
        Assert.Equal("battle.result", c!.Value.Category);
        Assert.Null(c.Value.SubjectKey);
    }

    [Fact]
    public void Calendar_is_unmapped()
    {
        Assert.Null(WorldTurnNotificationClassifier.Classify(Entry(TurnReportKinds.Calendar, "week", "special")));
    }

    [Fact]
    public void Command_accepted_is_unmapped()
    {
        Assert.Null(WorldTurnNotificationClassifier.Classify(Entry(TurnReportKinds.CommandAccepted, "c-1", "")));
    }

    [Fact]
    public void Legion_starved_is_unmapped_until_ask_A4_gives_it_an_audience()
    {
        var e = Entry(TurnReportKinds.Event, "e-dave-legion-1", "legion.starved:s-x", sectorId: "s-x");
        Assert.Null(WorldTurnNotificationClassifier.Classify(e));
    }

    [Fact]
    public void Cache_retrieval_outcomes_are_unmapped()
    {
        // RpgStore.CacheRetrieval.cs — post-Step Event-kind entries with no row in this table.
        var e = Entry(TurnReportKinds.Event, "mission-1", "cache.retrieved:table-1");
        Assert.Null(WorldTurnNotificationClassifier.Classify(e));
    }

    [Fact]
    public void Claim_lost_is_unmapped_pending_ask_A3()
    {
        var e = Entry(TurnReportKinds.Event, "f-dave", "claim.lost:s-x", sectorId: "s-x");
        Assert.Null(WorldTurnNotificationClassifier.Classify(e));
    }
}
