using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// The turn's HTTP surface (spec-turn-engine.md §Server): file orders, end your turn, read back what
/// happened. The barrier is the point — the world steps when the *last* commander commits, not when
/// the impatient one does.
/// </summary>
[Collection("e2e")]
public class WorldTurnE2ETests : IAsyncLifetime
{
    static readonly string[] Commanders = { "dave", "wild", "zomboss" };

    readonly HttpClient _http;

    public WorldTurnE2ETests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync() =>
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();

    public Task DisposeAsync() => Task.CompletedTask;

    async Task Create(string worldId) =>
        (await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId, templateId = "first-light", seed = "5"
        })).EnsureSuccessStatusCode();

    /// <summary>The turn currently open — the turn a commit has to name (W25).</summary>
    async Task<int> OpenTurn(string worldId) =>
        (await _http.GetFromJsonAsync<JsonElement>($"/api/world/{worldId}/state"))
        .GetProperty("currentTurn").GetInt32();

    async Task<JsonElement> Commit(string worldId, string commanderId)
    {
        var turn = await OpenTurn(worldId);
        var res = await _http.PostAsJsonAsync($"/api/world/{worldId}/commit", new { commanderId, turn });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_world_steps_only_when_the_last_commander_ends_its_turn()
    {
        await Create("e2e-turn");

        // The barrier still wants everyone. What W27 changed is that the factions with a policy
        // supply themselves, so the human is the last one outstanding.
        var wild = await Commit("e2e-turn", "wild");
        Assert.True(wild.GetProperty("ok").GetBoolean());
        Assert.False(wild.GetProperty("advanced").GetBoolean());
        Assert.Equal(0, wild.GetProperty("currentTurn").GetInt32());

        var last = await Commit("e2e-turn", "dave");

        Assert.True(last.GetProperty("advanced").GetBoolean());
        Assert.Equal(1, last.GetProperty("currentTurn").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(last.GetProperty("stateHash").GetString()));
    }

    [Fact]
    public async Task Ending_your_turn_is_enough_because_everyone_else_has_a_policy()
    {
        // What a player actually experiences: press End Turn, the world moves. Nothing in the
        // browser has ever been able to do this before — the barrier could not release over HTTP.
        await Create("e2e-endturn");

        var result = await Commit("e2e-endturn", "dave");

        Assert.True(result.GetProperty("advanced").GetBoolean());
        Assert.Equal(1, result.GetProperty("currentTurn").GetInt32());
    }

    [Fact]
    public async Task An_order_filed_before_the_barrier_shows_up_in_the_turn_it_belongs_to()
    {
        await Create("e2e-order");

        var submit = await _http.PostAsJsonAsync("/api/world/e2e-order/commands", new
        {
            commanderId = "dave",
            commands = new[]
            {
                new { commandId = "m1", kind = "move", entityId = "e-dave-legion-1", lanePath = new[] { "l-home-ember" } }
            }
        });
        submit.EnsureSuccessStatusCode();
        var accepted = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(accepted.GetProperty("results")[0].GetProperty("ok").GetBoolean());

        // Human last: a faction with a policy commits itself as soon as anyone ends the turn.
        foreach (var commander in Commanders.Where(c => c != "dave").Concat(new[] { "dave" }))
            await Commit("e2e-order", commander);

        var state = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-order/state");
        var legion = state.GetProperty("entities").EnumerateArray()
            .Single(e => e.GetProperty("entityId").GetString() == "e-dave-legion-1");
        Assert.Equal("ember-hollow", legion.GetProperty("atSectorId").GetString());

        var report = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-order/turn/0");
        Assert.Equal(0, report.GetProperty("turn").GetInt32());
        Assert.Contains(report.GetProperty("phases").EnumerateArray().Select(p => p.GetString()), p => p == "Movement");
        Assert.Contains(report.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("detail").GetString()!.Contains("ember-hollow"));
    }

    // ---- W28: the AI explains itself -------------------------------------------------

    [Fact]
    public async Task The_turn_report_says_what_each_commander_did_and_why()
    {
        // Under fog an AI's mistake and a bug look identical from outside. The reason is the only
        // thing that tells them apart, which is why it is a shipped field and not a log line.
        //
        // Read as Zomboss, because as Dave you are shown your own war: `stand-fast` names no ground,
        // so there is no sector the projection could decide you had seen it on. When `frontier-rules`
        // ships and Zomboss starts naming sectors, Dave will see the orders played out on ground he
        // has scouted — which is the version of this that is actually worth reading.
        await Create("e2e-reasons");
        await Commit("e2e-reasons", "dave");

        var report = await _http.GetFromJsonAsync<JsonElement>(
            "/api/world/e2e-reasons/turn/0?asFaction=zomboss");
        var commands = report.GetProperty("commands").EnumerateArray().ToList();

        var zomboss = commands.Single(c => c.GetProperty("commanderId").GetString() == "zomboss");
        Assert.False(string.IsNullOrWhiteSpace(zomboss.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task A_turn_report_does_not_name_ground_the_viewer_has_never_seen()
    {
        // The turn report is a second way onto the wire and it used to be unprojected: it handed
        // every viewer every faction's orders, map-wide, which is what /state spends its whole
        // existence preventing.
        await Create("e2e-fogreport");
        await Commit("e2e-fogreport", "dave");

        var report = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-fogreport/turn/0");
        var state = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-fogreport/state");

        var seen = state.GetProperty("sectors").EnumerateArray()
            .Where(s => s.GetProperty("intel").GetString() != "Unknown")
            .Select(s => s.GetProperty("sectorId").GetString())
            .ToHashSet();

        foreach (var command in report.GetProperty("commands").EnumerateArray())
        {
            if (command.GetProperty("commanderId").GetString() == "dave") continue;
            if (command.TryGetProperty("sectorId", out var sector) && sector.ValueKind != JsonValueKind.Null)
                Assert.Contains(sector.GetString(), seen);
        }
    }

    [Fact]
    public async Task The_turn_report_does_not_narrate_ground_the_viewer_has_never_seen()
    {
        // W39: the last hole in the fog. `entries` used to be handed to every viewer unprojected,
        // so a turn report quietly named every sector on the map — the thing the state projection
        // spends its whole existence preventing.
        await Create("e2e-fogentries");
        await Commit("e2e-fogentries", "dave");

        var report = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-fogentries/turn/0");
        var state = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-fogentries/state");

        var seen = state.GetProperty("sectors").EnumerateArray()
            .Where(s => s.GetProperty("intel").GetString() != "Unknown")
            .Select(s => s.GetProperty("sectorId").GetString())
            .ToHashSet();

        foreach (var entry in report.GetProperty("entries").EnumerateArray())
        {
            if (!entry.TryGetProperty("sectorId", out var sector)) continue;
            if (sector.ValueKind == JsonValueKind.Null) continue;

            Assert.Contains(sector.GetString(), seen);
        }
    }

    [Fact]
    public async Task A_line_about_nowhere_in_particular_is_shown_to_everyone()
    {
        // Calendar ticks and commands refused before they named ground reveal nothing about the map,
        // and dropping them would leave a viewer unable to tell "nothing happened" from "you are not
        // allowed to know". The turn always has *some* narrative.
        await Create("e2e-nowhere");
        await Commit("e2e-nowhere", "dave");

        var report = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-nowhere/turn/0");
        Assert.NotEmpty(report.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Auditing_another_commander_is_something_you_ask_for_on_purpose()
    {
        // Fog here is a rendering rule, not a secrecy boundary — /state has always let any caller
        // view as any faction. The turn report now has the same shape rather than a different one.
        await Create("e2e-audit-turn");
        await Commit("e2e-audit-turn", "dave");

        var asZomboss = await _http.GetFromJsonAsync<JsonElement>(
            "/api/world/e2e-audit-turn/turn/0?asFaction=zomboss");

        Assert.Contains(asZomboss.GetProperty("commands").EnumerateArray(),
            c => c.GetProperty("commanderId").GetString() == "zomboss");

        var stranger = await _http.GetAsync("/api/world/e2e-audit-turn/turn/0?asFaction=nobody");
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
    }

    [Fact]
    public async Task A_players_own_order_carries_no_reason_because_they_never_explain_themselves()
    {
        await Create("e2e-noreason");
        (await _http.PostAsJsonAsync("/api/world/e2e-noreason/commands", new
        {
            commanderId = "dave",
            commands = new[] { new { commandId = "mine", kind = "stand-fast" } }
        })).EnsureSuccessStatusCode();
        await Commit("e2e-noreason", "dave");

        var report = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-noreason/turn/0");
        var mine = report.GetProperty("commands").EnumerateArray()
            .Single(c => c.GetProperty("commandId").GetString() == "mine");

        Assert.Equal(JsonValueKind.Null, mine.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task A_turn_that_has_not_happened_yet_is_a_404_rather_than_an_empty_report()
    {
        await Create("e2e-future");

        var res = await _http.GetAsync("/api/world/e2e-future/turn/7");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- W25: a commit names the turn it means to end ---------------------------------

    [Fact]
    public async Task A_commit_that_does_not_name_a_turn_is_refused()
    {
        await Create("e2e-noturn");

        var res = await _http.PostAsJsonAsync("/api/world/e2e-noturn/commit", new { commanderId = "dave" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turn.missing", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Ending_a_turn_that_has_already_resolved_is_refused_rather_than_burning_another()
    {
        await Create("e2e-retry");
        foreach (var commander in Commanders) await Commit("e2e-retry", commander);
        Assert.Equal(1, await OpenTurn("e2e-retry"));

        // The client never saw its response and resends. Without the turn on the request this would
        // commit turn 1 instead, and once the AI auto-commits it would resolve a turn nobody played.
        var retry = await _http.PostAsJsonAsync("/api/world/e2e-retry/commit",
            new { commanderId = "dave", turn = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turn.stale", body.GetProperty("reason").GetString());
        Assert.Equal(1, await OpenTurn("e2e-retry"));
    }

    [Fact]
    public async Task Committing_for_a_faction_that_does_not_exist_is_refused()
    {
        await Create("e2e-ghost");

        var res = await _http.PostAsJsonAsync("/api/world/e2e-ghost/commit", new { commanderId = "nobody", turn = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Committing_against_a_world_that_does_not_exist_is_a_404()
    {
        var res = await _http.PostAsJsonAsync("/api/world/no-such-world/commit", new { commanderId = "dave", turn = 0 });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
