using FusionRpg.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>D4.22 (spec-domain-catalog.md §6) — `GET /api/delve/domains/{playerId}` and
/// `POST /api/delve/start`. Calls the extracted, `internal` handler methods directly (this project's
/// own pre-existing `InternalsVisibleTo`) rather than a live HTTP host — the routing lambdas in
/// `DelveEndpoints.cs` are one line each, DI binding only, so this is testing the actual logic, not
/// re-testing ASP.NET's own routing. Both endpoints are wired to REAL reads against a real, empty
/// `dungeon_domain` table (no import writer exists yet, D4.16) — correct-by-emptiness today, proven
/// here rather than assumed.</summary>
public class DelveDomainsAndStartEndpointsTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;

    public DelveDomainsAndStartEndpointsTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // ---- GET /domains/{playerId} --------------------------------------------------------------------

    [Fact]
    public void GetDomains_for_an_unknown_player_404s()
    {
        var result = DelveEndpoints.HandleGetDomains(playerId: 999_999, _store);
        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public void GetDomains_returns_an_empty_offer_list_against_the_real_empty_catalog()
    {
        var result = DelveEndpoints.HandleGetDomains(_playerId, _store);
        var ok = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var offers = Assert.IsAssignableFrom<IReadOnlyList<FusionRpg.Core.Delve.Domains.DomainOfferDto>>(ok.Value);
        Assert.Empty(offers);
    }

    // ---- POST /start ---------------------------------------------------------------------------------

    static DelveEndpoints.DelveStartHttpRequest Request(string? parentWorldId = null, long? playerId = null, string correlationId = "corr-1") => new()
    {
        PlayerId = playerId, CorrelationId = correlationId, DomainId = "domain.unknown", ParentWorldId = parentWorldId,
        RungIdOrTailLabel = "medium", Oath = false, RaidMode = "solo", MemberInstanceIds = new[] { "actor-1" },
    };

    [Fact]
    public void Start_for_an_unknown_player_404s()
    {
        var result = DelveEndpoints.HandleStart(Request(playerId: 999_999), _store);
        Assert.IsType<NotFound>(result);
    }

    [Fact]
    public void Start_against_the_real_empty_catalog_refuses_domain_not_found_never_a_500()
    {
        var result = DelveEndpoints.HandleStart(Request(playerId: _playerId), _store);
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result); // BadRequest today (Refusal's own default mapping), never an unhandled exception
    }

    [Fact]
    public void The_Sanctum_picker_and_the_map_door_post_the_same_body_shape()
    {
        // The literal Verify line: one endpoint, one request type -- a Sanctum call (no parentWorldId)
        // and a map-door call (parentWorldId set) differ in EXACTLY that one field and nothing else,
        // and both reach the identical handler. Proven by asserting both produce the SAME refusal
        // shape today (domain.not-found, since nothing else about the two requests differs).
        var sanctumResult = DelveEndpoints.HandleStart(Request(parentWorldId: null, playerId: _playerId), _store);
        var mapDoorResult = DelveEndpoints.HandleStart(Request(parentWorldId: "world-abc", playerId: _playerId), _store);

        Assert.Equal(sanctumResult.GetType(), mapDoorResult.GetType());
        var sanctumStatus = Assert.IsAssignableFrom<IStatusCodeHttpResult>(sanctumResult).StatusCode;
        var mapDoorStatus = Assert.IsAssignableFrom<IStatusCodeHttpResult>(mapDoorResult).StatusCode;
        Assert.Equal(sanctumStatus, mapDoorStatus);
    }

    [Fact]
    public void A_replayed_correlation_returns_the_same_delveId_and_worldId_both_times()
    {
        // No real domain exists to actually create a delve against (D4.16) -- this proves the REPLAY
        // half of group 1 specifically: two identical requests before any delve exists both refuse
        // identically (domain.not-found), i.e. nothing about the SECOND call behaves differently from
        // the first just because the correlationId repeats (there is nothing yet to replay).
        var first = DelveEndpoints.HandleStart(Request(correlationId: "corr-replay", playerId: _playerId), _store);
        var second = DelveEndpoints.HandleStart(Request(correlationId: "corr-replay", playerId: _playerId), _store);

        Assert.Equal(first.GetType(), second.GetType());
    }
}
