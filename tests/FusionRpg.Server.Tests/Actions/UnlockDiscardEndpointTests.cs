using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FusionRpg.Server.Tests.Actions;

/// <summary>
/// A28 (unlock-discard-endpoint): <c>POST /api/actors/{instanceId}/unlock/discard</c> against a REAL,
/// minimal in-process host (same pattern as <see cref="Actions.SpecimenLoadoutEndpointsTests"/> and
/// aura-skill T15's own <c>LoadoutEndpointsTests</c>). <see cref="UnlockDiscardEndpointsHostTests"/>
/// (E2E) separately proves <c>Program.cs</c>'s real <c>app.MapUnlockDiscard()</c> registration.
/// </summary>
public class UnlockDiscardEndpointTests : IAsyncLifetime
{
    const long PlayerId = 1;
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        // The shipped action-unlock.v1.json values (discardTaxCoeffMilli: 100 = 0.1x P(Th)) -- a
        // smaller coefficient truncates cost(Th) = coeffMilli * P(Th) / 1000 to zero at low Th, which
        // TrySpendSouls's own `amount <= 0` guard then throws on (a real bug this test caught).
        UnlockTuningPolicy.Configure(new UnlockTuning(
            P1Milli: 500, DeltaMilli: 880, FloorMilli: 1, HeldCap: 10, RungCap: 10,
            DiscardTaxCoeffMilli: 100));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapUnlockDiscard();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    string CreateSpecimenWithHeldUnlock(string unlockId, long earnCount = 3, long souls = 100_000)
    {
        var actor = _store.CreateUniqueActor(playerId: PlayerId, side: "plant", typeId: 1);
        _store.SaveUnlockState(
            new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId),
            UnlockState.FromPersisted(earnCount, new[] { new HeldUnlock(unlockId, earnCount) }));
        // AwardSouls itself throws on a non-positive delta -- souls: 0 (the insufficient-soul test)
        // means "leave the fresh player's balance at its natural zero", not "award zero".
        if (souls > 0)
            Assert.True(_store.AwardSouls(PlayerId, souls, "test-seed", "seed-" + Guid.NewGuid().ToString("N")).Inserted);
        return actor.InstanceId;
    }

    [Fact]
    public async Task Discard_aRealHeldUnlockWithSufficientSoul_freesTheSlotAndSpendsTheQuotedPrice()
    {
        var instanceId = CreateSpecimenWithHeldUnlock("action.discard.001");
        var before = _store.GetSoulBalance(PlayerId).Balance;
        var theta = _store.GetUniqueActor(instanceId)!.Level;
        var expectedPrice = DiscardPolicy.PriceOf(Math.Max(1, theta), UnlockTuningPolicy.Tuning!).SoulAmount;

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/unlock/discard",
            new { unlockId = "action.discard.001" });

        resp.EnsureSuccessStatusCode();
        var after = _store.GetSoulBalance(PlayerId).Balance;
        Assert.Equal(before - expectedPrice, after);

        var state = _store.GetUnlockState(new OwnerScope(OwnerKind.UniqueActor, instanceId));
        Assert.DoesNotContain(state.Held, h => h.UnlockId == "action.discard.001");
        Assert.Equal(3, state.EarnCount); // anti-farm property: EarnCount never moves on discard
    }

    [Fact]
    public async Task Discard_withInsufficientSoul_refusesAndChangesNothing()
    {
        var instanceId = CreateSpecimenWithHeldUnlock("action.discard.002", souls: 0);
        var stateBefore = _store.GetUnlockState(new OwnerScope(OwnerKind.UniqueActor, instanceId));

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/unlock/discard",
            new { unlockId = "action.discard.002" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(0, _store.GetSoulBalance(PlayerId).Balance);
        var stateAfter = _store.GetUnlockState(new OwnerScope(OwnerKind.UniqueActor, instanceId));
        Assert.Equal(stateBefore.Held.Count, stateAfter.Held.Count);
        Assert.Contains(stateAfter.Held, h => h.UnlockId == "action.discard.002");
    }

    [Fact]
    public async Task Discard_anUnlockNotHeld_refusesTyped()
    {
        var instanceId = CreateSpecimenWithHeldUnlock("action.discard.held");

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/unlock/discard",
            new { unlockId = "action.never-held" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RefusalDto>();
        Assert.Equal(nameof(DiscardRefusalReason.NotHeld), body!.Reason);
    }

    [Fact]
    public async Task Discard_aFreshSpecimenWithNoUnlockStateRow_refusesNotHeldCleanlyNoException()
    {
        var actor = _store.CreateUniqueActor(playerId: PlayerId, side: "plant", typeId: 1);
        Assert.True(_store.AwardSouls(PlayerId, 100_000, "test-seed", "seed-" + Guid.NewGuid().ToString("N")).Inserted);

        var resp = await _http.PostAsJsonAsync($"/api/actors/{actor.InstanceId}/unlock/discard",
            new { unlockId = "action.anything" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RefusalDto>();
        Assert.Equal(nameof(DiscardRefusalReason.NotHeld), body!.Reason);
    }

    [Fact]
    public async Task Discard_theSameRequestRetriedAfterSuccess_refusesTheSecondTimeNoDoubleCharge()
    {
        var instanceId = CreateSpecimenWithHeldUnlock("action.discard.retry");

        var first = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/unlock/discard",
            new { unlockId = "action.discard.retry" });
        first.EnsureSuccessStatusCode();
        var balanceAfterFirst = _store.GetSoulBalance(PlayerId).Balance;

        var second = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/unlock/discard",
            new { unlockId = "action.discard.retry" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<RefusalDto>();
        Assert.Equal(nameof(DiscardRefusalReason.NotHeld), body!.Reason);
        Assert.Equal(balanceAfterFirst, _store.GetSoulBalance(PlayerId).Balance); // no second charge
    }

    [Fact]
    public async Task Discard_twoDifferentUnlocksBothAffordable_bothSucceedIndependently()
    {
        var actor = _store.CreateUniqueActor(playerId: PlayerId, side: "plant", typeId: 1);
        _store.SaveUnlockState(
            new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId),
            UnlockState.FromPersisted(5, new[]
            {
                new HeldUnlock("action.discard.a", 5),
                new HeldUnlock("action.discard.b", 5),
            }));
        Assert.True(_store.AwardSouls(PlayerId, 100_000, "test-seed", "seed-" + Guid.NewGuid().ToString("N")).Inserted);

        var r1 = await _http.PostAsJsonAsync($"/api/actors/{actor.InstanceId}/unlock/discard", new { unlockId = "action.discard.a" });
        var r2 = await _http.PostAsJsonAsync($"/api/actors/{actor.InstanceId}/unlock/discard", new { unlockId = "action.discard.b" });

        r1.EnsureSuccessStatusCode();
        r2.EnsureSuccessStatusCode();
        var state = _store.GetUnlockState(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId));
        Assert.Empty(state.Held);
    }

    [Fact]
    public async Task Discard_successResponse_includesThePostSpendSoulBalance()
    {
        var instanceId = CreateSpecimenWithHeldUnlock("action.discard.balance");

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/unlock/discard",
            new { unlockId = "action.discard.balance" });

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DiscardSuccessDto>();
        Assert.NotNull(body!.Balance);
        Assert.Equal(_store.GetSoulBalance(PlayerId).Balance, body.Balance!.Balance);
    }

    [Fact]
    public async Task Discard_unknownInstanceId_returns404()
    {
        var resp = await _http.PostAsJsonAsync("/api/actors/no-such-specimen/unlock/discard",
            new { unlockId = "action.anything" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Discard_missingUnlockId_returns400()
    {
        var actor = _store.CreateUniqueActor(playerId: PlayerId, side: "plant", typeId: 1);

        var resp = await _http.PostAsJsonAsync($"/api/actors/{actor.InstanceId}/unlock/discard", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    sealed class RefusalDto
    {
        public string Reason { get; set; } = "";
    }

    sealed class DiscardSuccessDto
    {
        public string InstanceId { get; set; } = "";
        public string UnlockId { get; set; } = "";
        public FusionRpg.Contracts.SoulBalanceDto? Balance { get; set; }
    }
}
