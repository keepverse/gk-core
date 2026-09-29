using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// G8: the contract loop over the wire — bind, dispatch, release, refuse, ritual, and a time-travel
/// settle that charges tribute exactly.
/// </summary>
[Collection("e2e")]
public class ContractE2ETests : IAsyncLifetime
{
    readonly HttpClient _http;

    public ContractE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    static readonly string SpeciesId = CreatureSpeciesCatalog.All
        .First(s => s.Acquisition != CreatureAcquisition.CaptureOnly).SpeciesId;

    async Task<string> MintCreature()
    {
        var resp = await _http.PostAsJsonAsync($"/api/test/mint-creature?speciesId={SpeciesId}", new { });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("actor").GetProperty("instanceId").GetString()!;
    }

    async Task<JsonElement> State() => await _http.GetFromJsonAsync<JsonElement>("/api/contracts/1");

    async Task<long> Souls()
    {
        var souls = await _http.GetFromJsonAsync<JsonElement>("/api/souls/1");
        return souls.GetProperty("balance").GetInt64();
    }

    static JsonElement Row(JsonElement state, string instanceId) => state
        .GetProperty("contracts").EnumerateArray()
        .First(c => c.GetProperty("instanceId").GetString() == instanceId);

    [Fact]
    public async Task A_new_creature_arrives_contracted_and_shows_its_tribute()
    {
        var id = await MintCreature();
        var state = await State();

        Assert.Equal(1, state.GetProperty("capacity").GetProperty("used").GetInt32());
        Assert.Equal(ContractPolicy.BaseSlots, state.GetProperty("capacity").GetProperty("total").GetInt32());
        Assert.Equal(300, state.GetProperty("capacity").GetProperty("nextSlotPrice").GetInt64());
        Assert.True(state.GetProperty("dailyTribute").GetInt64() > 0);

        var row = Row(state, id);
        Assert.True(row.GetProperty("bound").GetBoolean());
        Assert.Equal("bound", row.GetProperty("rank").GetString());
        Assert.Equal(0, row.GetProperty("rankBonusMilli").GetInt32());   // the golden-safe band
        Assert.True(row.GetProperty("deployable").GetBoolean());
    }

    [Fact]
    public async Task Release_frees_the_slot_and_closes_the_battle_gate()
    {
        // Minting binds for free; RE-binding costs the pact fee, so this player needs a balance.
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=500", new { })).EnsureSuccessStatusCode();
        var id = await MintCreature();
        (await _http.PostAsJsonAsync("/api/contracts/release", new { instanceId = id }))
            .EnsureSuccessStatusCode();
        Assert.Equal(0, (await State()).GetProperty("capacity").GetProperty("used").GetInt32());

        // The web-battle path refuses a released creature by name.
        var battle = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-unbound", waveId = "rift-skirmish", squad = new[] { id } });
        Assert.Equal(HttpStatusCode.BadRequest, battle.StatusCode);
        Assert.Contains("unbound", await battle.Content.ReadAsStringAsync());

        // Re-binding costs one day of upkeep and reopens it.
        var before = await Souls();
        (await _http.PostAsJsonAsync("/api/contracts/bind", new { instanceId = id }))
            .EnsureSuccessStatusCode();
        var upkeep = Row(await State(), id).GetProperty("upkeepPerDay").GetInt32();
        Assert.Equal(before - upkeep, await Souls());
        Assert.True(Row(await State(), id).GetProperty("bound").GetBoolean());
    }

    [Fact]
    public async Task Slots_are_a_rising_soul_sink()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=2000", new { })).EnsureSuccessStatusCode();
        var before = await Souls();

        (await _http.PostAsJsonAsync("/api/contracts/slots/buy", new { correlationId = "slot-e2e-1" }))
            .EnsureSuccessStatusCode();
        var state = await State();
        Assert.Equal(ContractPolicy.BaseSlots + 1, state.GetProperty("capacity").GetProperty("total").GetInt32());
        Assert.Equal(600, state.GetProperty("capacity").GetProperty("nextSlotPrice").GetInt64());
        Assert.Equal(before - 300, await Souls());

        // Retry with the same correlation: replay, not a second purchase.
        (await _http.PostAsJsonAsync("/api/contracts/slots/buy", new { correlationId = "slot-e2e-1" }))
            .EnsureSuccessStatusCode();
        Assert.Equal(before - 300, await Souls());
    }

    [Fact]
    public async Task Time_travel_charges_tribute_once_per_day()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=2000", new { })).EnsureSuccessStatusCode();
        await MintCreature();
        await MintCreature();
        var tribute = (await State()).GetProperty("dailyTribute").GetInt64();
        var before = await Souls();

        var settle = await _http.PostAsJsonAsync("/api/test/contracts/settle", new { days = 3 });
        settle.EnsureSuccessStatusCode();
        var result = await settle.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, result.GetProperty("daysSettled").GetInt32());
        Assert.Equal(tribute * 3, result.GetProperty("soulsPaid").GetInt64());
        Assert.Equal(before - tribute * 3, await Souls());

        // Settling the same span again is free — the per-day dedupe key already holds those days.
        (await _http.PostAsJsonAsync("/api/test/contracts/settle", new { days = 3 })).EnsureSuccessStatusCode();
        Assert.Equal(before - tribute * 3, await Souls());
    }

    /// <summary>
    /// Auto-pick must SKIP what cannot serve, not refuse the request: the player asked for a
    /// battle, not a lecture about their roster.
    /// </summary>
    [Fact]
    public async Task A_squadless_match_skips_unbound_creatures_instead_of_failing()
    {
        var id = await MintCreature();
        (await _http.PostAsJsonAsync("/api/contracts/release", new { instanceId = id }))
            .EnsureSuccessStatusCode();

        var match = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-autopick", waveId = "rift-skirmish" });
        match.EnsureSuccessStatusCode();
        Assert.True((await match.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("runId").GetInt64() > 0);

        // The released creature sat it out, so nothing credited it.
        Assert.Equal(300, Row(await State(), id).GetProperty("loyalty").GetInt32());
    }

    /// <summary>An expedition credits loyalty for the trip as a whole (G6 wiring).</summary>
    [Fact]
    public async Task An_expedition_moves_the_loyalty_of_everyone_who_went()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=5000", new { })).EnsureSuccessStatusCode();
        var a = await MintCreature();
        var b = await MintCreature();
        var before = Row(await State(), a).GetProperty("loyalty").GetInt32();

        var dispatch = await _http.PostAsJsonAsync("/api/expeditions/dispatch",
            new { correlationId = "exp-contract-1", tierId = "scout-30m", squad = new[] { a, b } });
        dispatch.EnsureSuccessStatusCode();
        var expeditionId = (await dispatch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("expedition").GetProperty("id").GetInt64();

        // Make it due by MOVING THE CLOCK (RS3 increment 5b): the SIM timer rewind route is retired, so the
        // declaration the seam applies here is the same one a scenario's `clock.set` step makes.
        await RpgApiFactory.WithClockAheadAsync(1860, async () =>
        {
            (await _http.PostAsJsonAsync($"/api/expeditions/{expeditionId}/collect", new { }))
                .EnsureSuccessStatusCode();
        });

        var state = await State();
        var after = Row(state, a).GetProperty("loyalty").GetInt32();
        Assert.NotEqual(before, after);
        // Both members share the trip's verdict — one squad, one outcome.
        Assert.Equal(after, Row(state, b).GetProperty("loyalty").GetInt32());
    }

    [Fact]
    public async Task An_insubordinate_creature_is_refused_until_a_ritual_pays_for_it()
    {
        (await _http.PostAsJsonAsync("/api/test/seed-souls-demo?amount=5000", new { })).EnsureSuccessStatusCode();
        var id = await MintCreature();

        // Lose enough battles to fall under the floor: 10 defeats from a fresh 300.
        for (var i = 0; i < 11; i++)
        {
            (await _http.PostAsJsonAsync("/api/test/web-match",
                new { correlationId = $"wm-loss-{i}", waveId = "rift-tyrant", squad = new[] { id } }))
                .EnsureSuccessStatusCode();
        }

        var row = Row(await State(), id);
        Assert.False(row.GetProperty("deployable").GetBoolean(),
            "11 defeats against rift-tyrant must take a fresh contract under the floor");

        var refused = await _http.PostAsJsonAsync("/api/test/web-match",
            new { correlationId = "wm-sulk", waveId = "rift-skirmish", squad = new[] { id } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("insubordinate", await refused.Content.ReadAsStringAsync());

        var before = await Souls();
        (await _http.PostAsJsonAsync("/api/contracts/ritual",
            new { instanceId = id, correlationId = "ritual-e2e-1" })).EnsureSuccessStatusCode();
        Assert.True(Row(await State(), id).GetProperty("deployable").GetBoolean());
        Assert.True(await Souls() < before, "a ritual costs Souls");
    }
}
