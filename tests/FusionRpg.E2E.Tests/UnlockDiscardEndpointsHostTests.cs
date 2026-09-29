using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// A28 (unlock-discard-endpoint, action-map.md §17): the audit finding this task was built to close —
/// <c>UnlockDiscardEndpoints.MapUnlockDiscard()</c> is a static extension method that does nothing
/// until <c>Program.cs</c> calls it. <see cref="UnlockDiscardEndpointTests"/> (`FusionRpg.Server.Tests`)
/// proves the endpoint LOGIC; this file proves the route is reachable through the REAL production
/// bootstrap, same reasoning as <see cref="SpecimenLoadoutEndpointsHostTests"/> (T64).
/// </summary>
[Collection("e2e")]
public class UnlockDiscardEndpointsHostTests
{
    readonly RpgApiFactory _factory;

    public UnlockDiscardEndpointsHostTests(RpgApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_real_host_resolves_POST_on_the_unlock_discard_route()
    {
        using var http = _factory.CreateClient(); // boots Program via the real bootstrap (configures UnlockTuningPolicy)

        using var store = _factory.OpenStore(); // the same memory databases Program.cs's own store opened
        store.Init();

        var actor = store.CreateUniqueActor(playerId: 1, side: "plant", typeId: 1);
        store.SaveUnlockState(
            new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId),
            UnlockState.FromPersisted(3, new[] { new HeldUnlock("action.t68-host-registration", 3) }));
        Assert.True(store.AwardSouls(1, 100_000, "test-seed", "seed-" + Guid.NewGuid().ToString("N")).Inserted);

        // A compiled-but-unmapped endpoint would 404 here (Minimal API routing) -- this is the proof
        // `app.MapUnlockDiscard()` is a real line in the booted Program.
        var resp = await http.PostAsJsonAsync($"/api/actors/{actor.InstanceId}/unlock/discard",
            new { unlockId = "action.t68-host-registration" });
        resp.EnsureSuccessStatusCode();

        var state = store.GetUnlockState(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId));
        Assert.DoesNotContain(state.Held, h => h.UnlockId == "action.t68-host-registration");
    }

    [Fact]
    public async Task Discard_unknownInstanceId_returns404OnTheRealHost()
    {
        using var http = _factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/actors/no-such-specimen-t68/unlock/discard",
            new { unlockId = "action.anything" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
