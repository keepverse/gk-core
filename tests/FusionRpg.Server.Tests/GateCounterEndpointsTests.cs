using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// passive-tree-todo.md G6 — GET/POST /api/gate-counters against a REAL, minimal in-process host,
/// same pattern as <c>PassiveTreeEndpointsTests</c>/<c>AptitudeEndpointsTests</c>. Proves the shipped
/// endpoint end to end: a credit batch actually reaches <c>RpgStore.FlushGateCounters</c> (never a
/// no-op), a round-tripped GET agrees with <see cref="MasteryIndex"/> computed independently in the
/// test, and the tier-0 reason (<c>hasProducer</c>) is a real field on the wire, not inferred from a
/// zero (spec-gate-counters.md §5.2).
/// </summary>
public class GateCounterEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();

        PassiveTreeTuningHub.Configure(Tuning());

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapGateCounters();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Get_unknownPlayer_returns404()
    {
        var resp = await _http.GetAsync("/api/gate-counters/999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_onAFreshPlayer_listsEveryRosterIdAtZero_bothFamiliesHaveAProducer()
    {
        var body = await GetState();

        Assert.True(body.Families.TryGetValue(StatusAppliedCounter.Quantity, out var status));
        Assert.True(status!.HasProducer);
        Assert.Equal(StatusCategoryRegistry.AllStatusIds.Count, status.Subjects.Count);
        Assert.All(status.Subjects.Values, s => Assert.Equal(0, s.Count));
        Assert.All(status.Subjects.Values, s => Assert.Equal(1, s.Index)); // §5.3: index 1 at day one, never 0 credits opening a tier
        Assert.All(status.Subjects.Values, s => Assert.Equal(0, s.Equivalents));

        Assert.True(body.Families.TryGetValue(ElementMasteryCounter.Quantity, out var element));
        Assert.True(element!.HasProducer);
        Assert.Equal(ElementRoster.Concrete.Count, element.Subjects.Count);
        Assert.All(element.Subjects.Values, s => Assert.Equal(0, s.Count));
    }

    [Fact]
    public async Task Credit_thenGet_roundTripsRawCountAndAgreesWithMasteryIndex()
    {
        var ownerKey = AptitudeEndpoints.ScopeKey(_playerId);
        var tuning = Tuning().GateCounters;
        const long witherDelta = 70L;
        const long fireDelta = 25L;

        await Post("/api/gate-counters/credit", new
        {
            credits = new[]
            {
                new { ownerKind = "player", ownerKey, quantity = StatusAppliedCounter.Quantity, subjectId = "wither", delta = witherDelta },
                new { ownerKind = "player", ownerKey, quantity = ElementMasteryCounter.Quantity, subjectId = "fire", delta = fireDelta }
            }
        });

        // Self-referential (population-pin SE3.4, 2026-09-20): this test posted witherDelta/fireDelta
        // credits itself, so it proves the round-trip, not a fact about shipped content.
        var body = await GetState();
        var wither = body.Families[StatusAppliedCounter.Quantity].Subjects["wither"];
        Assert.Equal(witherDelta, wither.Count);
        Assert.Equal(MasteryIndex.Index(witherDelta, tuning), wither.Index);
        Assert.Equal(MasteryIndex.Equivalents(witherDelta, tuning.StatusMasteryRatePoints, tuning), wither.Equivalents);

        var fire = body.Families[ElementMasteryCounter.Quantity].Subjects["fire"];
        Assert.Equal(fireDelta, fire.Count);
        Assert.Equal(MasteryIndex.Index(fireDelta, tuning), fire.Index);
        Assert.Equal(MasteryIndex.Equivalents(fireDelta, tuning.ElementMasteryRatePoints, tuning), fire.Equivalents);

        // Sparse elsewhere -- crediting one subject never touches another's row (§4.1).
        var untouched = body.Families[StatusAppliedCounter.Quantity].Subjects["poison"];
        Assert.Equal(0, untouched.Count);
    }

    [Fact]
    public async Task Credit_twoEntriesForTheSameKey_mergeAdditively()
    {
        var ownerKey = AptitudeEndpoints.ScopeKey(_playerId);
        await Post("/api/gate-counters/credit", new
        {
            credits = new[]
            {
                new { ownerKind = "player", ownerKey, quantity = StatusAppliedCounter.Quantity, subjectId = "poison", delta = 3L },
                new { ownerKind = "player", ownerKey, quantity = StatusAppliedCounter.Quantity, subjectId = "poison", delta = 4L }
            }
        });

        var body = await GetState();
        Assert.Equal(7, body.Families[StatusAppliedCounter.Quantity].Subjects["poison"].Count);
    }

    [Fact]
    public async Task Credit_emptyBatch_returns200WithZeroCredited()
    {
        var resp = await _http.PostAsJsonAsync("/api/gate-counters/credit", new { credits = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CreditResultDto>();
        Assert.Equal(0, body!.Credited);
    }

    [Fact]
    public async Task Credit_unknownQuantity_returns400()
    {
        var resp = await _http.PostAsJsonAsync("/api/gate-counters/credit", new
        {
            credits = new[] { new { ownerKind = "player", ownerKey = "player:1", quantity = "not_a_real_family", subjectId = "wither", delta = 1L } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(null, "player:1", "status_applied", "wither", 1L)]
    [InlineData("player", null, "status_applied", "wither", 1L)]
    [InlineData("player", "player:1", "status_applied", null, 1L)]
    public async Task Credit_missingIdentityField_returns400(string? ownerKind, string? ownerKey, string quantity, string? subjectId, long delta)
    {
        var resp = await _http.PostAsJsonAsync("/api/gate-counters/credit", new
        {
            credits = new[] { new { ownerKind, ownerKey, quantity, subjectId, delta } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Credit_nonPositiveDelta_returns400(long delta)
    {
        var resp = await _http.PostAsJsonAsync("/api/gate-counters/credit", new
        {
            credits = new[] { new { ownerKind = "player", ownerKey = "player:1", quantity = StatusAppliedCounter.Quantity, subjectId = "wither", delta } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    async Task<StateDto> GetState()
    {
        var resp = await _http.GetAsync($"/api/gate-counters/{_playerId}");
        if (!resp.IsSuccessStatusCode) throw new Exception(await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<StateDto>();
        Assert.NotNull(body);
        return body!;
    }

    async Task Post(string url, object payload)
    {
        var resp = await _http.PostAsJsonAsync(url, payload);
        if (!resp.IsSuccessStatusCode) throw new Exception($"{url} -> {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    static PassiveTreeTuning Tuning() => new(
        SchemaVersion: 1, Version: 1,
        TierLadder: new TierLadderTuning(ReqScalePoints: 5),
        Budget: new BudgetTuning(1000, 500),
        TreeShareMilli: 1000, TreeBudgetMilli: 1000,
        Potency: new PotencyTuning(182, 1, new long[] { 46, 91, 137, 182 }),
        Mechanism: new MechanismTuning(0, 1000),
        Archetype: new ArchetypeTuning(6000),
        Exclusion: new ExclusionTuning(20),
        ArchetypeAssignment: "ordinal-round-robin",
        DesignTarget: new DesignTargetTuning(92),
        Concentration: new ConcentrationTuning(FmaxMilli: 1200, WMilli: 500),
        SoulTrack: new SoulTrackTuning(1000),
        UnlockCost: new UnlockCostTuning(5, 2),
        Respec: new RespecTuning(50, 500),
        GateCounters: new GateCountersTuning(23, 23, 4, 4, 5000, null));

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    sealed class StateDto
    {
        public long PlayerId { get; set; }
        public Dictionary<string, FamilyDto> Families { get; set; } = new();
    }

    sealed class FamilyDto
    {
        public bool HasProducer { get; set; }
        public Dictionary<string, SubjectDto> Subjects { get; set; } = new();
    }

    sealed class SubjectDto
    {
        public long Count { get; set; }
        public long Index { get; set; }
        public long Equivalents { get; set; }
    }

    sealed class CreditResultDto
    {
        public int Credited { get; set; }
    }
}
