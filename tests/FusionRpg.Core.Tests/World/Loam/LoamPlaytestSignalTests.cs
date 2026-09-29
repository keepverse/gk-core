using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// The Checkpoint 5 gate, by owner decision 2026-08-23 (see `tasks/loam-todo.md`): a manual ten-turn
/// playtest is replaced by this suite. Each of `spec-loam-maps.md`'s three questions gets a measurable
/// stand-in the engine can actually report — not the subjective feeling itself, but the mechanical
/// property that feeling would have to be resting on. If a property fails here, no human playtest was
/// ever going to save it; if it passes, that is the gate's verdict now, not a rehearsal for one.
/// </summary>
public class LoamPlaytestSignalTests
{
    const string Phase = "Test";
    readonly ITestOutputHelper _output;

    public LoamPlaytestSignalTests(ITestOutputHelper output) => _output = output;

    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSector Sector(string id, long stock = 0, int stability = 1000, int development = 0, int danger = 0, IReadOnlyList<WorldSlot>? slots = null) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = "f1", LoamStock = stock, StabilityMilli = stability,
            DevelopmentLevel = development, DangerBand = danger, Slots = slots ?? Array.Empty<WorldSlot>()
        };

    /// <summary>
    /// Proxy for Q2 ("does the fade read as tense, or as bookkeeping?") and, incidentally, Q3
    /// ("is a split economy frightening?"): how many turns of visible warning does the gauge actually
    /// give between "your supply can't cover its own keep" (componentNet turns negative — exactly what
    /// `LoamGauge` shows) and the sector actually being lost. A number near zero is a shock nobody
    /// could react to — bookkeeping dressed as a surprise. A number that never arrives (net stays
    /// positive forever) is the opposite failure: nothing to be tense about at all. This measures
    /// the real gap, rather than asserting it can't be measured.
    /// </summary>
    [Fact]
    public void Severing_a_supply_line_gives_several_turns_of_visible_warning_before_the_actual_loss()
    {
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[]
            {
                Sector("rich", slots: new[] { Rootbed(0) }),
                Sector("poor", development: 3, danger: 2)   // ordinary ground, no source of its own
            },
            Lanes = new[] { new WorldLane { LaneId = "l", FromSectorId = "rich", ToSectorId = "poor", TypeId = LaneTypeCatalog.RiftLaneTypeId } }
        };

        // Subsidised while connected, same as TwoHearthsStoryTests' own cut-off story — run it to a
        // real steady state first so "poor" starts the severed half of the story already anchored,
        // not fresh off the boat.
        for (var i = 0; i < 30; i++)
            world = LoamPhases.Pressure(LoamPhases.Production(world, new TurnReport(), Phase), new TurnReport(), Phase);

        world = world with { Lanes = world.Lanes.Select(l => l with { State = LaneState.Severed }).ToList() };

        int? firstNegativeNetTurn = null, firstReleaseFlaggedTurn = null, lostTurn = null;
        var log = new System.Text.StringBuilder();

        for (var turn = 0; turn < 60 && lostTurn is null; turn++)
        {
            var poor = world.Sectors.Single(s => s.SectorId == "poor");
            if (poor.OwnerFactionId is null) { lostTurn = turn; break; }

            var component = TerritoryComponents.For(world, "f1").Single(c => c.Contains("poor"));
            var componentNet = LoamBalance.PerComponent(world, component);
            var willRelease = LoamForecast.WillRelease(world, component) is not null;

            if (componentNet < 0 && firstNegativeNetTurn is null) firstNegativeNetTurn = turn;
            if (willRelease && firstReleaseFlaggedTurn is null) firstReleaseFlaggedTurn = turn;

            log.AppendLine($"turn {turn}: net={componentNet} stability={poor.StabilityMilli} willReleaseNextTurn={willRelease}");

            world = LoamPhases.Pressure(LoamPhases.Production(world, new TurnReport(), Phase), new TurnReport(), Phase);
        }

        _output.WriteLine(log.ToString());
        Assert.NotNull(firstNegativeNetTurn);
        Assert.NotNull(lostTurn);

        var warningWindow = lostTurn!.Value - firstNegativeNetTurn!.Value;
        _output.WriteLine($"warning window (net-goes-negative -> actually lost): {warningWindow} turns");
        _output.WriteLine($"forecast flag (willReleaseNextTurn) first true at turn {firstReleaseFlaggedTurn?.ToString() ?? "never — released before the forecast caught it"}");

        // The proxy bar this sets: at least one full turn between the gauge turning negative and the
        // sector being gone, so a player watching the always-visible reading has *something* to react
        // to before the loss — not a guarantee the reaction feels dramatic, only that it is possible.
        Assert.True(warningWindow >= 1, $"the gauge gave only {warningWindow} turns of warning before the loss — too little to react to");
    }

    /// <summary>
    /// The same measurement as the previous test, against the harshest deficit the map's own numbers
    /// can produce, to report a range rather than one point. A single moderate fixture giving 22
    /// turns could easily be the generous end of the scale rather than representative of it.
    /// </summary>
    [Fact]
    public void The_warning_window_shrinks_under_a_severe_deficit_but_never_to_an_instant_surprise()
    {
        // Isolated from turn one — no connected phase to simulate, since the question is purely "how
        // fast can the worst realistic deficit burn through full health," not "does severing matter."
        // An unconnected rootbed keeps G-C from exempting the faction (the same pitfall named in
        // LoamForecastTests' own Elsewhere() helper).
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[]
            {
                Sector("poor", development: 5, danger: 5),
                Sector("elsewhere", slots: new[] { Rootbed(0) })
            },
            Entities = Enumerable.Range(0, 20).Select(i => new WorldEntity
            {
                EntityId = $"e-{i}", Kind = WorldEntityKind.Legion, OwnerFactionId = "f1", AtSectorId = "poor", Stance = "hold",
                Members = new[] { new WorldEntityMember { SpeciesId = "peashooterzombie", Level = 1, Hp = 100 } }
            }).ToList()
        };

        int? firstNegativeNetTurn = null, lostTurn = null;
        for (var turn = 0; turn < 60 && lostTurn is null; turn++)
        {
            var poor = world.Sectors.Single(s => s.SectorId == "poor");
            if (poor.OwnerFactionId is null) { lostTurn = turn; break; }

            var component = TerritoryComponents.For(world, "f1").Single(c => c.Contains("poor"));
            if (LoamBalance.PerComponent(world, component) < 0 && firstNegativeNetTurn is null) firstNegativeNetTurn = turn;

            world = LoamPhases.Pressure(LoamPhases.Production(world, new TurnReport(), Phase), new TurnReport(), Phase);
        }

        Assert.NotNull(firstNegativeNetTurn);
        Assert.NotNull(lostTurn);
        var warningWindow = lostTurn!.Value - firstNegativeNetTurn!.Value;
        _output.WriteLine($"severe-deficit warning window: {warningWindow} turns (moderate-deficit case measured 22)");

        // MaxDecayMilli=300 per turn means even the worst deficit cannot zero a full-health sector in
        // one step — the floor this asserts is the mechanic's own designed guarantee, not a guess.
        Assert.True(warningWindow >= (1000 / LoamPolicy.MaxDecayMilli),
            $"the mechanic's own decay ceiling guarantees at least {1000 / LoamPolicy.MaxDecayMilli} turns; got {warningWindow}");
    }

    /// <summary>
    /// Proxy for Q1 ("is choosing what to let go a real decision?"). The engine, not the player,
    /// currently picks which sector fades (pinning is post-gate) — so the player's only lever is
    /// upstream, before a shortfall ever happens. `DevelopmentLevel` turned out not to be that lever
    /// yet: it is grep-confirmed authored-only in this wave (`loam-structures`, which would let a
    /// player raise it, is unspecced and post-gate) — an earlier draft of this test claimed otherwise
    /// and would have been reporting a lever that does not exist yet. Garrison size is the honest,
    /// currently-live one: `LoamUpkeep.For`'s truth overload sums `world.Entities` standing at a
    /// sector, and marching troops in or out of ground is an ordinary Move order today. This measures
    /// whether that lever actually moves the "weakest" designation — if it does not, there is no real
    /// decision hiding upstream either, just a fixed outcome dressed as a choice.
    /// </summary>
    [Fact]
    public void The_engines_weakest_pick_responds_to_where_a_player_garrisons_troops()
    {
        var rootbed = Sector("source", slots: new[] { Rootbed(0) });
        var a = Sector("a", development: 0, danger: 0);
        var b = Sector("b", development: 0, danger: 0); // identical to "a" at first — a genuine tie

        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { rootbed, a, b },
            Lanes = new[]
            {
                new WorldLane { LaneId = "l1", FromSectorId = "source", ToSectorId = "a", TypeId = LaneTypeCatalog.RiftLaneTypeId },
                new WorldLane { LaneId = "l2", FromSectorId = "source", ToSectorId = "b", TypeId = LaneTypeCatalog.RiftLaneTypeId }
            }
        };

        long ScoreOf(WorldState w, string id) => LoamBalance.PerSector(w, w.Sectors.Single(s => s.SectorId == id));

        _output.WriteLine($"tied, no garrison anywhere: a={ScoreOf(world, "a")}, b={ScoreOf(world, "b")}");
        Assert.Equal(ScoreOf(world, "a"), ScoreOf(world, "b"));

        // The player's own move, available today: park a legion at "b" — an ordinary Move order, not
        // a future feature. Garrisoning it should be enough, on its own, to make "b" the weaker half.
        var garrisoned = world with
        {
            Entities = new[]
            {
                new WorldEntity
                {
                    EntityId = "e-1", Kind = WorldEntityKind.Legion, OwnerFactionId = "f1", AtSectorId = "b",
                    Stance = "hold", Members = new[] { new WorldEntityMember { SpeciesId = "peashooterzombie", Level = 1, Hp = 100 } }
                }
            }
        };

        _output.WriteLine($"after garrisoning b: a={ScoreOf(garrisoned, "a")}, b={ScoreOf(garrisoned, "b")}");

        var component = TerritoryComponents.For(garrisoned, "f1").Single(c => c.Contains("a"));
        var weakest = component.OrderBy(id => ScoreOf(garrisoned, id)).ThenBy(id => id, StringComparer.Ordinal).First();

        Assert.True(ScoreOf(garrisoned, "a") > ScoreOf(garrisoned, "b"),
            "garrisoning b must cost more to hold, flipping which sector is weaker");
        Assert.Equal("b", weakest);
    }

    /// <summary>
    /// Proxy for Q3's other half ("is a split economy frightening?"): not just "does the fold
    /// eventually reflect a split" but "is there any lag before it does." `TerritoryComponents.For`
    /// and `LoamBalance.PerComponent` are pure reads over whatever `WorldState` is handed to them —
    /// there is no cached or turn-delayed copy anywhere in the pipeline — so the instant a lane is
    /// severed, the very next read already shows two components and the starving one's true balance.
    /// Proven here rather than assumed, because "the architecture has no cache" is exactly the kind
    /// of claim that should be checked once instead of carried as folklore.
    /// </summary>
    [Fact]
    public void A_severed_lane_shows_up_as_two_components_on_the_very_same_read_no_turn_of_lag()
    {
        var world = new WorldState
        {
            WorldId = "w", TemplateId = "t", Seed = 1,
            Factions = new[] { new WorldFaction { FactionId = "f1", Kind = WorldFactionKind.Player, Name = "F1" } },
            Sectors = new[] { Sector("rich", slots: new[] { Rootbed(0) }), Sector("poor", development: 3, danger: 2) },
            Lanes = new[] { new WorldLane { LaneId = "l", FromSectorId = "rich", ToSectorId = "poor", TypeId = LaneTypeCatalog.RiftLaneTypeId } }
        };

        Assert.Single(TerritoryComponents.For(world, "f1")); // one connected component before severing

        var severed = world with { Lanes = world.Lanes.Select(l => l with { State = LaneState.Severed }).ToList() };

        var components = TerritoryComponents.For(severed, "f1");
        Assert.Equal(2, components.Count);

        var poorComponent = components.Single(c => c.Contains("poor"));
        _output.WriteLine($"poor's component net, read the instant the lane is severed: {LoamBalance.PerComponent(severed, poorComponent)}");
        Assert.True(LoamBalance.PerComponent(severed, poorComponent) < 0,
            "the starving half's net must already read negative on the same state that shows the severed lane — nothing here waits a turn");
    }
}
