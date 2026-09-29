using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// `save-identity` SE4.31 — the REST-level half of "`POST /api/players` creates the save AND its empires":
/// the real server bootstrap (`RpgApiFactory`, `WebApplicationFactory&lt;Program&gt;`), the real route in
/// `Program.cs`, and the read-back through the new empires route. A minimal in-process host could not
/// reach this route (it is declared inline in `Program.cs`), which is exactly why this test boots the
/// real one.
///
/// <para>The one-transaction PROPERTY is the store's own (`CreatePlayer` seeds inside the same
/// transaction as the row — `RpgStore.cs`, SE4.12's acceptance), so what is asserted here is the
/// observable a REST consumer sees: one call creates a save that already has its empires, with no boot,
/// seed or second call in between.</para>
/// </summary>
[Collection("e2e")]
public class SaveEmpireE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    readonly HttpClient _http;

    public SaveEmpireE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creating_a_save_creates_its_empires_in_the_same_call()
    {
        var created = await (await _http.PostAsJsonAsync("/api/players", new { name = "EmpireFresh" }))
            .Content.ReadFromJsonAsync<PlayerDto>(Json);
        Assert.NotNull(created);

        var empires = await _http.GetFromJsonAsync<List<SaveEmpireDto>>($"/api/players/{created!.Id}/empires", Json);

        Assert.NotNull(empires);
        Assert.NotEmpty(empires!);
        Assert.Single(empires!, e => e.Controller == EmpireControllerTokens.Human);
        // The same registry a booted save gets: at least one non-human decider exists, and every row
        // names its empire. Ids are never asserted — which empires a save has is authored data.
        Assert.Contains(empires!, e => e.Controller == EmpireControllerTokens.Ai);
        Assert.All(empires!, e => Assert.False(string.IsNullOrWhiteSpace(e.EmpireId)));

        // The seeded save reads the same shape (its own empires were made at seed, not at this POST).
        var seeded = await _http.GetFromJsonAsync<List<SaveEmpireDto>>("/api/players/1/empires", Json);
        Assert.Single(seeded!, e => e.Controller == EmpireControllerTokens.Human);
    }

    [Fact]
    public async Task An_unknown_save_reads_404_from_the_empires_route()
    {
        var resp = await _http.GetAsync("/api/players/999999/empires");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }
}
