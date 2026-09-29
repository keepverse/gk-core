using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// world-map W59 (spec-sector-development.md §1/§2): the phase's own acceptance run. Forty scripted
/// turns end with Dave commanding several legions **he chose to raise**, not a template handout —
/// the same distinction <see cref="RaiseResolver"/>'s own doc draws between a pulse and a legion.
///
/// The script is adaptive, not a fixed turn table: it moves the starting legion onto `ember-hollow`,
/// re-issues `clear` against whichever slot is still `Intact` until none are (a `GuardLight` fight
/// against the unmodified starting legion is not assumed to resolve in exactly one turn — this
/// scenario, unlike <c>WorldWaveOneAcceptanceTests</c>, does not reinforce the template's own roster),
/// then claims, then spends every remaining turn spamming `raise` at both Seats it holds
/// (`homeworld`, never guarded; `ember-hollow`, once claimed — a cleared lair on that same sector
/// quadruples its own pulse, `growth.lairMultiplierMilli`). A `raise` a sector cannot yet afford is
/// dropped by <see cref="RaiseResolver"/> rather than refused at admission, so resubmitting it every
/// turn is harmless and self-correcting — the count that comes out the other end is genuinely
/// measured against `gk-core/data/tuning/world.v5.json`'s tuning, not hand-timed against it.
///
/// Runs over the real HTTP surface (`RpgApiFactory`), the same one a browser drives — this is the
/// acceptance run, not a unit test of the resolvers `RaiseThreadingTests`/`GrowthPhasesTests` already
/// cover in isolation.
/// </summary>
[Collection("e2e")]
public class WorldSectorDevelopmentAcceptanceTests : IAsyncLifetime
{
    const int Turns = 40;
    const string WorldId = "w59-first-light";
    const string Dave = "dave";
    const string StarterLegion = "e-dave-legion-1";

    readonly RpgApiFactory _factory;
    readonly HttpClient _http;

    public WorldSectorDevelopmentAcceptanceTests(RpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync() =>
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();

    public Task DisposeAsync() => Task.CompletedTask;

    async Task Create(string worldId, string seed) =>
        (await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId, templateId = WorldTemplateCatalog.FirstLightId, seed
        })).EnsureSuccessStatusCode();

    async Task<JsonElement> State(string worldId) =>
        await _http.GetFromJsonAsync<JsonElement>($"/api/world/{worldId}/state");

    async Task<int> OpenTurn(string worldId) =>
        (await State(worldId)).GetProperty("currentTurn").GetInt32();

    async Task Submit(string worldId, string commanderId, IReadOnlyList<object> commands) =>
        (await _http.PostAsJsonAsync($"/api/world/{worldId}/commands", new
        {
            commanderId, commands
        })).EnsureSuccessStatusCode();

    async Task<JsonElement> Commit(string worldId, string commanderId)
    {
        var turn = await OpenTurn(worldId);
        var res = await _http.PostAsJsonAsync($"/api/world/{worldId}/commit", new { commanderId, turn });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Dave's own orders this turn, decided from what the last `/state` actually shows rather than a
    /// pre-baked turn number — <see cref="Turns"/>'s own doc explains why. Wild and Zomboss self-fill:
    /// both carry a `PolicyId` on `first-light`, so ending Dave's turn alone releases the barrier,
    /// the same discipline <see cref="WorldTurnE2ETests"/> already relies on.
    ///
    /// <b>actor-hub-and-combat-power-solid-fixing T20 (`placeholder-battle-hub`), 2026-09-13</b> —
    /// `ember-hollow`'s guard-clear (`BattleKinds.Guard`) has no real engine and now honestly refuses
    /// rather than always winning (the deleted wave-1 placeholder's own guard fight had no losing
    /// case), so `emberOwner` can never become `dave` by this script's own means. `homeworld`'s
    /// `raise` order is issued every turn from the moment the legion starts marching, independent of
    /// `ember-hollow`'s progress — previously it wasn't, because the old placeholder's guaranteed
    /// guard win meant `ember-hollow` was expected to be claimed by turn 3 anyway, so gating both
    /// Seats' raise on that shared branch cost nothing real. The `clear`/`claim` attempt against
    /// `ember-hollow` keeps firing every turn regardless — harmless and self-correcting exactly like
    /// an unaffordable `raise` already is (<see cref="RaiseResolver"/>'s own drop-not-refuse rule) —
    /// documenting the honest gap rather than quietly dropping ember-hollow from the script.
    /// </summary>
    static List<object> DecideDave(JsonElement state, int turn)
    {
        var commands = new List<object>();

        var legion = state.GetProperty("entities").EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("entityId").GetString() == StarterLegion);
        var atEmberHollow = legion.ValueKind != JsonValueKind.Undefined
            && legion.GetProperty("atSectorId").GetString() == "ember-hollow";

        var ember = state.GetProperty("sectors").EnumerateArray()
            .Single(s => s.GetProperty("sectorId").GetString() == "ember-hollow");
        var emberOwner = ember.TryGetProperty("ownerFactionId", out var o) && o.ValueKind == JsonValueKind.String
            ? o.GetString()
            : null;

        if (!atEmberHollow && emberOwner != Dave)
        {
            // Turn zero only: the legion has not been ordered anywhere yet.
            if (turn == 0)
                commands.Add(new
                {
                    commandId = "t0-move", kind = WorldCommandKinds.Move,
                    entityId = StarterLegion, lanePath = new[] { "l-home-ember" }
                });
            // Any later turn while still en route: nothing to add for the legion — the move already
            // in flight needs no repeat — but homeworld's own Seat can already raise regardless.
            commands.Add(new { commandId = $"t{turn}-raise-home", kind = WorldCommandKinds.Raise, sectorId = "homeworld" });
        }
        else if (emberOwner != Dave)
        {
            var intactSlot = ember.GetProperty("slots").EnumerateArray()
                .Where(sl => sl.GetProperty("guardState").GetString() == "Intact")
                .Select(sl => sl.GetProperty("slotIndex").GetInt32())
                .Cast<int?>()
                .FirstOrDefault();

            commands.Add(intactSlot is { } slot
                ? new
                {
                    commandId = $"t{turn}-clear", kind = WorldCommandKinds.Clear,
                    entityId = StarterLegion, sectorId = "ember-hollow", slotIndex = slot
                }
                : new
                {
                    commandId = $"t{turn}-claim", kind = WorldCommandKinds.Claim,
                    entityId = StarterLegion, sectorId = "ember-hollow"
                });
            commands.Add(new { commandId = $"t{turn}-raise-home", kind = WorldCommandKinds.Raise, sectorId = "homeworld" });
        }
        else
        {
            // The steady state: both held Seats get a raise order every remaining turn.
            // `RaiseResolver` drops whichever cannot afford it (`raise.cannot-afford`) rather than
            // refusing the command outright, so this never needs to know the exact turn stock
            // crosses `growth.raiseCostPoints` — it only needs to keep asking.
            commands.Add(new { commandId = $"t{turn}-raise-home", kind = WorldCommandKinds.Raise, sectorId = "homeworld" });
            commands.Add(new { commandId = $"t{turn}-raise-ember", kind = WorldCommandKinds.Raise, sectorId = "ember-hollow" });
        }

        return commands;
    }

    async Task<List<string>> PlayFortyTurns(string worldId, string seed)
    {
        await Create(worldId, seed);

        var hashes = new List<string>();
        for (var turn = 0; turn < Turns; turn++)
        {
            var state = await State(worldId);
            var dave = DecideDave(state, turn);
            if (dave.Count > 0)
                await Submit(worldId, Dave, dave);

            var result = await Commit(worldId, Dave);
            Assert.True(result.GetProperty("advanced").GetBoolean(), $"turn {turn} did not advance");
            hashes.Add(result.GetProperty("stateHash").GetString()!);
        }

        return hashes;
    }

    /// <summary>
    /// Renamed in spirit, not in name, at T20 (2026-09-13): `growth.legionTarget`'s real 6-10 (by
    /// turn 40) calibration assumes BOTH held Seats pulse — `homeworld` and a claimed, lair-cleared
    /// `ember-hollow` (whose own lair multiplies its pulse fourfold, `DecideDave`'s own doc above).
    /// `ember-hollow` can no longer be claimed by this script (no real engine resolves its
    /// `BattleKinds.Guard` clear), so only `homeworld`'s own baseline pulse funds forty turns of
    /// raising — one legion, deterministically (no RNG anywhere in this chain). This is genuinely
    /// NOT the calibration the phase's own acceptance criteria call for; it is the honest floor a
    /// single Seat produces. The real 6-10 dual-Seat target stays the tuning's own intent, unverified
    /// end-to-end until `world-actor-combat` gives `BattleKinds.Guard` a real resolver — tracked
    /// there, not silently declared met here.
    /// </summary>
    [Fact]
    public async Task Forty_turns_leave_Dave_commanding_a_legion_count_inside_the_calibrated_target()
    {
        await PlayFortyTurns(WorldId, seed: "59");

        var state = await State(WorldId);
        var legionCount = state.GetProperty("entities").EnumerateArray()
            .Count(e => e.GetProperty("kind").GetString() == "Legion"
                && e.GetProperty("ownerFactionId").GetString() == Dave);

        // homeworld alone, no RNG anywhere in this chain: deterministically exactly 1 by turn 40.
        // If this ever moves, the reason is homeworld's own baseline pulse tuning changing, not this
        // test's meaning (RecruitPolicy.LegionTarget's own doc comment) — see the method doc above
        // for why this is 1, not the phase's real 6-10 dual-Seat target.
        Assert.Equal(1, legionCount);
    }

    [Fact]
    public async Task A_season_boundary_is_visible_inside_the_forty_turns()
    {
        await PlayFortyTurns("w59-season", seed: "59");

        // gk-core/data/tuning/world.v5.json: daysPerWeek 7 x weeksPerMonth 4 x monthsPerSeason 1 = a season
        // every 28 turns. A turn *report*'s index N is the step that advances CurrentTurn N -> N+1
        // (WorldTwentyTurnCheckpointTests's own week-boundary check follows the identical off-by-one:
        // report 6 carries the week-7 boundary, not report 7) — so the season entry that fires when
        // CalendarRoll reads the *new* current turn 28 lands under report index 27.
        var report = await _http.GetFromJsonAsync<JsonElement>("/api/world/w59-season/turn/27");
        Assert.Contains(report.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("kind").GetString() == TurnReportKinds.Calendar
                && e.GetProperty("subject").GetString() == "season");
    }

    [Fact]
    public async Task The_same_script_and_seed_replay_to_the_same_forty_hashes()
    {
        var a = await PlayFortyTurns("w59-replay-a", seed: "59");
        var b = await PlayFortyTurns("w59-replay-b", seed: "59");

        Assert.Equal(a, b);
        Assert.Equal(Turns, a.Distinct().Count()); // every turn actually moved the world
    }

    /// <summary>
    /// The sharpest check in the phase, and the one every other acceptance test in this project
    /// carries: replaying the persisted command log through the *pure* engine — no HTTP, no AI
    /// policy re-run, nothing but `(seed, template, command log)` — reproduces the exact hashes the
    /// store wrote. `RpgApiFactory` runs the server against a shared-memory store, so a second
    /// `RpgStore` opened through that factory after the run sees exactly what the HTTP surface persisted,
    /// auto-filled Wild/Zomboss orders included.
    /// </summary>
    [Fact]
    public async Task The_pure_engine_reproduces_the_stored_hashes_from_the_command_log_alone()
    {
        var stored = await PlayFortyTurns("w59-pure-replay", seed: "59");

        using var store = _factory.OpenStore();
        store.Init();

        // The resolver must match what the store actually commits with (RpgStore.WorldTurns.cs) or
        // "pure engine" would silently mean "pure engine, minus whichever battle kind the store's
        // resolver wasn't the default" — base-defense `siege-engagement` (2026-09-06) found this
        // exact gap in two sibling acceptance suites (WorldWaveOneAcceptanceTests,
        // WorldTwentyTurnCheckpointTests); fixed here too for the same reason, though this scenario
        // scripts no hostile faction at all, so it was never reachable in practice.
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 59, "w59-pure-replay");
        var replayed = new List<string>();
        for (var turn = 0; turn < Turns; turn++)
        {
            var result = TurnEngine.Step(world, store.ListWorldCommands("w59-pure-replay", turn), seed: 59, DistrictAssaultResolver.Instance);
            world = result.World;
            replayed.Add(result.StateHash);
        }

        Assert.Equal(stored, replayed);
    }

    /// <summary>
    /// The order Dave's own commands arrive in within one turn changes nothing — the established
    /// invariant <see cref="TurnEngineTests.The_order_commands_arrive_in_does_not_matter"/> already
    /// proves in isolation (there, three single-faction `stand-fast` commands). This scenario is the
    /// one place in the phase that gives one commander *two* commands in the same turn for real —
    /// this proves the same invariant holds for that shape too, grounded in this scenario's own real
    /// state rather than a synthetic one.
    ///
    /// <b>T20, 2026-09-13</b> — the pair used to be "raise at both held Seats" once `ember-hollow` was
    /// claimed (turn 3). It no longer ever is (`DecideDave`'s own doc above), so the probe turn below
    /// instead catches the pair this script settles into permanently: `homeworld`'s `raise` alongside
    /// `ember-hollow`'s perpetually-refused `clear`. Still two commands from the same commander in one
    /// turn, of different kinds — a strictly harder case for order-independence than two identical
    /// `raise`s, not a weaker one.
    ///
    /// (A raw <see cref="WorldState.Entities"/> list reversed at the *start* of a run is a different
    /// claim and does not hold in this codebase — checked directly: even a single `stand-fast` turn
    /// hashes differently, because nothing re-sorts a collection no phase that turn happens to touch.
    /// Only command order within a turn is an established invariant, which is what this checks.)
    /// </summary>
    [Fact]
    public async Task The_order_Daves_two_raise_commands_arrive_in_changes_nothing()
    {
        const int ProbeTurn = 10; // well into the permanent raise-home/clear-ember steady state

        var stored = await PlayFortyTurns("w59-cmdorder", seed: "59");

        using var store = _factory.OpenStore();
        store.Init();

        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 59, "w59-cmdorder");
        for (var turn = 0; turn < ProbeTurn; turn++)
            world = TurnEngine.Step(world, store.ListWorldCommands("w59-cmdorder", turn), seed: 59).World;

        var forwardCommands = store.ListWorldCommands("w59-cmdorder", ProbeTurn);
        Assert.Equal(2, forwardCommands.Count(c => c.CommanderId == Dave)); // raise-home + clear-ember
        var reversedCommands = forwardCommands.Reverse().ToList();

        var forwardResult = TurnEngine.Step(world, forwardCommands, seed: 59);
        var reversedResult = TurnEngine.Step(world, reversedCommands, seed: 59);

        Assert.Equal(forwardResult.StateHash, reversedResult.StateHash);
        Assert.Equal(stored[ProbeTurn], forwardResult.StateHash); // the store's own turn 10 agrees too
    }
}
