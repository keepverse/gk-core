using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// W4 (spec-world-model.md §Server): world reads in SIM — create from a template, read the header,
/// read the full graph. Reads only; the turn engine owns every write.
/// </summary>
[Collection("e2e")]
public class WorldE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public WorldE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    async Task<JsonElement> CreateWorld(string worldId, ulong seed = 7)
    {
        var res = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId,
            templateId = "first-light",
            seed = seed.ToString()
        });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Create_then_read_the_header_and_the_graph()
    {
        await CreateWorld("e2e-1");

        var header = await _http.GetFromJsonAsync<JsonElement>("/api/world/1");
        Assert.Equal("e2e-1", header.GetProperty("worldId").GetString());
        Assert.Equal("first-light", header.GetProperty("templateId").GetString());
        Assert.Equal(0, header.GetProperty("currentTurn").GetInt32());

        var state = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-1/state");
        Assert.Equal(6, state.GetProperty("sectors").GetArrayLength());
        Assert.Equal(6, state.GetProperty("lanes").GetArrayLength());
        Assert.Equal(3, state.GetProperty("factions").GetArrayLength());
        // `entities` is the viewer's own forces now — everything else is believed, per sector,
        // at whatever detail it was seen (world-intel).
        Assert.Equal(1, state.GetProperty("entities").GetArrayLength());
    }

    [Fact]
    public async Task The_graph_carries_what_a_map_view_needs_to_draw_itself()
    {
        await CreateWorld("e2e-draw");
        var state = await _http.GetFromJsonAsync<JsonElement>("/api/world/e2e-draw/state");

        var sectors = state.GetProperty("sectors").EnumerateArray().ToList();
        var home = sectors.Single(s => s.GetProperty("sectorId").GetString() == "homeworld");

        // Layout coordinates: a graph you cannot picture is unusable.
        Assert.Equal(0, home.GetProperty("layoutX").GetInt32());
        Assert.True(sectors.Any(s => s.GetProperty("layoutX").GetInt32() != 0));

        // Climate is nullable, and the homeworld is the null.
        Assert.Equal(JsonValueKind.Null, home.GetProperty("climate").ValueKind);
        Assert.Contains(sectors, s => s.GetProperty("climate").ValueKind == JsonValueKind.String);

        // Slots come with their guard state so the map can show what still has to be cleared.
        var guarded = sectors
            .SelectMany(s => s.GetProperty("slots").EnumerateArray())
            .Where(sl => sl.GetProperty("guardState").GetString() == "Intact")
            .ToList();
        Assert.NotEmpty(guarded);
        Assert.All(guarded, sl => Assert.False(string.IsNullOrEmpty(sl.GetProperty("guardWaveId").GetString())));
    }

    [Fact]
    public async Task The_wire_never_leaks_the_world_seed()
    {
        await CreateWorld("e2e-seed", seed: 123456789);

        var raw = await _http.GetStringAsync("/api/world/e2e-seed/state");
        Assert.DoesNotContain("123456789", raw);
        Assert.DoesNotContain("\"seed\"", raw);
    }

    [Fact]
    public async Task Unknown_worlds_and_players_are_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/world/no-such-world/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/world/4242")).StatusCode);
    }

    [Fact]
    public async Task Creating_the_same_world_twice_is_refused_by_the_test_hook()
    {
        await CreateWorld("e2e-dupe");

        var second = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = "e2e-dupe",
            templateId = "first-light",
            seed = "7"
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Orders_are_filed_per_command_and_a_replay_is_reported_as_one()
    {
        await CreateWorld("e2e-orders");

        var body = new
        {
            commands = new[]
            {
                new { commandId = "o1", kind = "stand-fast", entityId = (string?)null },
                new { commandId = "o2", kind = "stand-fast", entityId = "e-wild-pack-1" } // not Dave's
            }
        };

        var first = await _http.PostAsJsonAsync("/api/world/e2e-orders/commands", body);
        first.EnsureSuccessStatusCode();
        var results = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray().ToList();

        // Partial acceptance: one stale order must not throw away the rest of the turn.
        Assert.True(results[0].GetProperty("ok").GetBoolean());
        Assert.False(results[1].GetProperty("ok").GetBoolean());
        Assert.Equal("entity.not-yours", results[1].GetProperty("reason").GetString());

        var again = await _http.PostAsJsonAsync("/api/world/e2e-orders/commands", body);
        var replayed = (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("results").EnumerateArray().First();
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_template_is_a_bad_request()
    {
        var res = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = "e2e-bad-template",
            templateId = "no-such-template",
            seed = "1"
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
