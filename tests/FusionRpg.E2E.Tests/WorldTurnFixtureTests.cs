using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// world-stage W20: `first-light-turn.json` — the turn-report counterpart to `first-light.json`
/// (W19). No turn-report fixture existed before this; `world.spec.ts` stubbed
/// `**/api/world/first-light/turn/**` as a flat 404, leaving `world-playback` with nothing to build
/// against.
///
/// The scripted turns below are a real, played six-turn opening on the `first-light` template,
/// chosen deliberately to produce at least one entry of each of W-F1's visibility classes
/// (spec-world-wire.md §2, world-stage W14) in Dave's own account of the turns — not synthetic
/// entries, a real narrative:
///
/// - Turn 0: Dave marches to `ember-hollow` — his own `legion.runway` line is the **audience**
///   example (Rule 1: shown to its own faction, no ground needed).
/// - Turns 1–2: Dave clears `ember-hollow`'s two light guards — each is a **live-sight battle**
///   (Rule 2: he is standing right there when it happens).
/// - Turn 3: Dave claims `ember-hollow` — `claim.held:ember-hollow` is the **remembered-sight**
///   example (Rule 3: a static fact, shown on any ground ever seen).
/// - Turn 4: Dave marches toward `ash-waste`, held by Wild's warband — a **halt** (`halt:zoc:`,
///   Rule 1 via `Audience = entity.OwnerFactionId`) and a live Contact battle land the same turn.
/// - Turn 5: Zomboss's own warband is ordered to march toward `verdant-shelf` — its own
///   `legion.runway` line (`Audience = "zomboss"`) is the **excluded** example: proven, not assumed,
///   because Dave's own account of turn 5 does not contain it (see the assertion below, checked
///   before the fixture is trusted).
/// </summary>
[Collection("e2e")]
public class WorldTurnFixtureTests : IAsyncLifetime
{
    const string FixturePath = "web/fusion-rpg-web/src/stages/world/fixtures/first-light-turn.json";
    const string WorldId = "first-light";
    const int FirstTurn = 0;
    const int LastTurn = 5;

    readonly HttpClient _http;

    public WorldTurnFixtureTests(RpgApiFactory factory) => _http = factory.CreateClient();

    public async Task InitializeAsync() =>
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();

    public Task DisposeAsync() => Task.CompletedTask;

    async Task<HttpResponseMessage> SubmitDave(string commandId, object command) =>
        await _http.PostAsJsonAsync($"/api/world/{WorldId}/commands", new
        {
            commanderId = "dave",
            commands = new[] { command }
        });

    async Task Commit(int turn, string commander = "dave")
    {
        var response = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commit", new { commanderId = commander, turn });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_checked_in_turn_fixture_still_matches_a_real_played_opening()
    {
        (await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = WorldId,
            templateId = "first-light",
            seed = "1"
        })).EnsureSuccessStatusCode();

        // Turn 0: march to ember-hollow.
        (await SubmitDave("c0", new { commandId = "c0", kind = "move", entityId = "e-dave-legion-1", lanePath = new[] { "l-home-ember" } }))
            .EnsureSuccessStatusCode();
        await Commit(0);

        // Turns 1-2: clear ember-hollow's two light guards, standing there the whole time.
        (await SubmitDave("c1", new { commandId = "c1", kind = "clear", entityId = "e-dave-legion-1", sectorId = "ember-hollow", slotIndex = 2 }))
            .EnsureSuccessStatusCode();
        await Commit(1);
        (await SubmitDave("c2", new { commandId = "c2", kind = "clear", entityId = "e-dave-legion-1", sectorId = "ember-hollow", slotIndex = 3 }))
            .EnsureSuccessStatusCode();
        await Commit(2);

        // Turn 3: claim ember-hollow, now that both guards are down.
        (await SubmitDave("c3", new { commandId = "c3", kind = "claim", entityId = "e-dave-legion-1", sectorId = "ember-hollow" }))
            .EnsureSuccessStatusCode();
        await Commit(3);

        // Turn 4: march toward ash-waste — Wild's warband there halts the march and a Contact
        // battle follows, both the same turn Dave is standing right there to see them.
        (await SubmitDave("c4", new { commandId = "c4", kind = "move", entityId = "e-dave-legion-1", lanePath = new[] { "l-ember-ash" } }))
            .EnsureSuccessStatusCode();
        await Commit(4);

        // Turn 5: order zomboss's own warband to march — a manual order in this SIM harness, not
        // an AI decision, so the excluded line is deterministic rather than left to `FrontierRulesPolicy`.
        (await _http.PostAsJsonAsync($"/api/world/{WorldId}/commands", new
        {
            commanderId = "zomboss",
            commands = new[] { new { commandId = "z5", kind = "move", entityId = "e-zomboss-band-1", lanePath = new[] { "l-black-verdant" } } }
        })).EnsureSuccessStatusCode();
        await Commit(5);
        await Commit(5, "zomboss");

        // Proven, not assumed: zomboss's own audience-scoped line from turn 5 must not reach dave.
        var turn5AsDave = await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/turn/5?asFaction=dave");
        Assert.DoesNotContain(
            turn5AsDave.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("subject").GetString() == "e-zomboss-band-1");

        var reports = new List<JsonElement>();
        for (var turn = FirstTurn; turn <= LastTurn; turn++)
            reports.Add(await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/turn/{turn}?asFaction=dave"));

        var json = JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }) + "\n";

        var path = Path.Combine(RepoRoot(), FixturePath);
        if (Environment.GetEnvironmentVariable("FUSIONRPG_BLESS_WORLD_FIXTURE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        Assert.True(File.Exists(path), $"missing fixture {FixturePath} — run with FUSIONRPG_BLESS_WORLD_FIXTURE=1");
        Assert.Equal(json.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
