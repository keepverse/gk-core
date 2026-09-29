using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Notify;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using FusionRpg.Server.Notifications;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>
/// world-notify-source spec §1-§2, Testing 2, 4 and 5 — recipients, dedup keys and the release
/// forecast, over a real `rpg_worlds`/`rpg_world_commands` store and the real first-light template.
/// The forecast half builds its own starving world state (the shipped template has no release
/// candidate at turn 0), so it proves the RULE — which sector, at which key, only for the latest
/// resolved turn — against a fixture the real forecast agrees with.
/// </summary>
[Collection("NotificationHub")]
public class WorldNotifySourceTests : IDisposable
{
    const string WorldId = "wn-1";
    const string DAVE = "dave";
    const string WILD = "wild";
    const string ZOMBOSS = "zomboss";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _save;
    readonly WorldState _world;
    readonly WorldHeaderRow _header;
    readonly WorldReportNotificationSource _source;

    public WorldNotifySourceTests()
    {
        NotificationHubFixture.Configure();
        WorldTuningTestSupport.ConfigureOnce();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _save = _store.GetCurrentPlayerId();
        _store.CreateWorld(_save, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, (ulong)_save, WorldId));
        _world = _store.LoadWorldState(WorldId)!;
        _header = _store.GetWorldHeader(WorldId)!;
        _source = new WorldReportNotificationSource(_store, new WorldFactionSaves());
    }

    public void Dispose() => _testStore.Dispose();

    static TurnReport Report(params TurnReportEntry[] entries) =>
        TurnReport.FromStored(new[] { "Movement" }, entries);

    static TurnReportEntry Event(string subject, string detail, string? sectorId = null, string? audience = null) =>
        new("Movement", TurnReportKinds.Event, subject, detail, sectorId, audience);

    static TurnReportEntry Calendar() => new("Calendar", TurnReportKinds.Calendar, "", "week.2");

    WorldTurnNotificationContext Ctx(TurnReport? report, int turn = 1, bool isLatest = true) =>
        new(WorldId, turn, isLatest, _header, _world, report);

    IReadOnlyList<AddressedDraft> Collect(TurnReport? report, int turn = 1, bool isLatest = true) =>
        _source.Collect(Ctx(report, turn, isLatest)).ToList();

    [Fact]
    public void A_loam_shortfall_reaches_its_save_and_is_keyed_by_its_persisted_index()
    {
        // Index 0 is a calendar line, which is report-only — so the shortfall's key proves the index
        // is the entry's own position in the PERSISTED report, not a renumbering of mapped entries.
        var drafts = Collect(Report(Calendar(), Event(DAVE, "loam.shortfall:340", "homeworld")));

        var draft = Assert.Single(drafts);
        Assert.Equal(new SaveId(_save), draft.SaveId);
        Assert.Equal("loam.shortfall", draft.Draft.CategoryId);
        Assert.Equal(NotifySeverity.Important, draft.Draft.Severity);
        Assert.Equal("world.turn-entry", draft.Draft.MessageKey);
        Assert.Equal($"world:{WorldId}:t1:e1", draft.Draft.DedupKey);
        Assert.Equal($"faction:{DAVE}", draft.Draft.SubjectKey);
        Assert.Equal(WorldId, draft.Draft.WorldId);
        Assert.Equal(1, draft.Draft.WorldTurn);
    }

    [Fact]
    public void An_AI_factions_own_shortfall_yields_no_draft()
    {
        var zomboss = _world.Factions.Single(f => f.FactionId == ZOMBOSS);
        Assert.NotEqual(WorldFactionKind.Player, zomboss.Kind); // the fixture that makes this a test

        Assert.Empty(Collect(Report(Event(ZOMBOSS, "loam.shortfall:200"))));
    }

    /// <summary>Testing 2: an enemy watching my shortfall sector sees the line in the turn report
    /// (the moved fog rule) and is still not told about it — the notification is about ME.</summary>
    [Fact]
    public void An_enemy_watching_my_shortfall_sector_gets_no_notification_though_it_sees_the_line()
    {
        // ash-waste is the wild pack's own seat: it watches that ground.
        var entry = Event(DAVE, "loam.shortfall:340", "ash-waste");

        var wild = new BelievedWorldView(_world, WILD);
        Assert.True(WorldReportVisibility.VisibleTo(entry, WILD, wild), "the turn-report GET shows wild this line");

        var draft = Assert.Single(Collect(Report(entry)));
        Assert.Equal(new SaveId(_save), draft.SaveId); // the save whose faction the line is about
    }

    /// <summary>Testing 2: a battle line reaches exactly the factions the turn GET shows it to,
    /// through the same moved function.</summary>
    [Fact]
    public void A_battle_line_reaches_exactly_the_factions_the_turn_GET_shows_it_to()
    {
        var entry = new TurnReportEntry("Battle", TurnReportKinds.Battle, "battle-1", "sector:homeworld:e-winner", "homeworld");

        Assert.True(WorldReportVisibility.VisibleTo(entry, DAVE, new BelievedWorldView(_world, DAVE)));
        Assert.False(WorldReportVisibility.VisibleTo(entry, WILD, new BelievedWorldView(_world, WILD)));

        var draft = Assert.Single(Collect(Report(entry)));
        Assert.Equal("battle.result", draft.Draft.CategoryId);
        Assert.Equal($"sector:homeworld", draft.Draft.SubjectKey);
    }

    [Fact]
    public void A_dropped_order_reaches_only_its_commander()
    {
        FileOrder(DAVE, "c-dave-1");
        FileOrder(WILD, "c-wild-1");

        // Orders are filed against the world's OPEN turn, which is the turn they resolve in.
        var turn = _header.CurrentTurn;
        var drafts = Collect(Report(
            new TurnReportEntry("Admission", TurnReportKinds.CommandDropped, "c-dave-1", "entity.unknown"),
            new TurnReportEntry("Admission", TurnReportKinds.CommandDropped, "c-wild-1", "entity.unknown")), turn);

        // Dave's order reaches the save; the wild pack's commander is not a player, so its dropped
        // order produces no notification at all.
        var draft = Assert.Single(drafts);
        Assert.Equal($"world:{WorldId}:t{turn}:e0", draft.Draft.DedupKey);
        Assert.Equal("command.dropped", draft.Draft.CategoryId);
        Assert.Equal("command:c-dave-1", draft.Draft.SubjectKey);
    }

    [Fact]
    public void A_sector_owned_line_reaches_the_current_owner_only()
    {
        // homeworld is dave's; ash-waste belongs to the wild pack.
        var mine = Assert.Single(Collect(Report(Event("s-x", "growth.pulse:3", "homeworld"))));
        Assert.Equal("growth", mine.Draft.CategoryId);
        Assert.Equal($"sector:homeworld", mine.Draft.SubjectKey);
        Assert.Equal(new SaveId(_save), mine.SaveId);

        Assert.Empty(Collect(Report(Event("s-x", "growth.pulse:3", "ash-waste"))));
    }

    [Fact]
    public void An_audience_line_reaches_only_its_audience()
    {
        var mine = Event("e-dave-legion-1", "legion.runway:12", "homeworld", audience: DAVE);
        var theirs = mine with { Audience = WILD };

        Assert.Single(Collect(Report(mine)));
        Assert.Empty(Collect(Report(theirs)));
    }

    /// <summary>Testing 4: the same persisted report classified twice yields identical keys — the
    /// property R-N5 rests on (a re-run must store and push nothing).</summary>
    [Fact]
    public void The_same_report_classified_twice_yields_identical_keys()
    {
        var report = Report(
            Calendar(),
            Event(DAVE, "loam.shortfall:340", "homeworld"),
            new TurnReportEntry("Battle", TurnReportKinds.Battle, "battle-1", "sector:homeworld:e-winner", "homeworld"),
            Event(DAVE, "legion.topup:50", audience: DAVE));

        var first = Collect(report).Select(d => d.Draft.DedupKey).ToList();
        var second = Collect(report).Select(d => d.Draft.DedupKey).ToList();

        Assert.Equal(3, first.Count);
        Assert.Equal(first, second);
    }

    /// <summary>A trimmed turn carries no report, so the source reads nothing and builds nothing
    /// (the pump, not this source, owns the cursor advance).</summary>
    [Fact]
    public void A_trimmed_turn_yields_nothing()
    {
        Assert.Empty(Collect(null));
    }

    // ---- the release forecast (Testing 5) ----

    static WorldSlot Rootbed(int index) =>
        new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSector Sector(string id, long stock = 0, int stability = 1000, int development = 0, int danger = 0,
        IReadOnlyList<WorldSlot>? slots = null) => new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = DAVE, LoamStock = stock, StabilityMilli = stability,
            DevelopmentLevel = development, DangerBand = danger, Slots = slots ?? Array.Empty<WorldSlot>()
        };

    /// <summary>LoamForecastTests' own releasing fixture: `s` is fragile enough that this turn's
    /// upkeep would zero it, and `elsewhere` carries a rootbed so G-C's "no source anywhere"
    /// exemption never swallows the upkeep under test.</summary>
    static WorldState StarvingWorld() => new()
    {
        WorldId = "wn-forecast", TemplateId = "first-light", Seed = 1,
        Factions = new[] { new WorldFaction { FactionId = DAVE, Kind = WorldFactionKind.Player, Name = "Dave" } },
        Sectors = new[]
        {
            Sector("s", stock: 0, stability: 50, development: 10, danger: 4 + 2 * 10),
            Sector("elsewhere", slots: new[] { Rootbed(0) })
        },
        Lanes = Array.Empty<WorldLane>()
    };

    [Fact]
    public void The_release_forecast_is_published_only_for_the_latest_resolved_turn()
    {
        var world = StarvingWorld();
        var component = TerritoryComponents.For(world, DAVE).Single(c => c.Contains("s"));
        Assert.Equal("s", LoamForecast.WillRelease(world, component)); // the fixture forecasts a release

        var header = new WorldHeaderRow("wn-forecast", _save, "first-light", 1UL, 3, "Active", "2026-01-01T00:00:00Z", 1);

        var latest = _source.Collect(new WorldTurnNotificationContext("wn-forecast", 2, true, header, world, null)).ToList();
        var draft = Assert.Single(latest);
        Assert.Equal(new SaveId(_save), draft.SaveId);
        Assert.Equal("loam.release", draft.Draft.CategoryId);
        Assert.Equal("world.release-forecast", draft.Draft.MessageKey);
        Assert.Equal("world:wn-forecast:t2:release:s", draft.Draft.DedupKey);
        Assert.Equal("sector:s", draft.Draft.SubjectKey);
        var arg = Assert.Single(draft.Draft.Args);
        Assert.Equal(NotifyArgKind.Ref, arg.Kind);
        Assert.Equal("sector", arg.Name);

        // A lagging turn in a catch-up run carries no forecast: a forecast is about the NEXT turn.
        Assert.Empty(_source.Collect(new WorldTurnNotificationContext("wn-forecast", 1, false, header, world, null)));
    }

    void FileOrder(string commander, string commandId)
    {
        var (ok, reason, _) = _store.SubmitWorldCommand(WorldId, new WorldCommand
        {
            CommanderId = commander,
            CommandId = commandId,
            Kind = WorldCommandKinds.Stance,
            EntityId = commander == DAVE ? "e-dave-legion-1" : "e-wild-pack-1",
            Stance = "hold"
        });
        Assert.True(ok, reason);
    }
}
