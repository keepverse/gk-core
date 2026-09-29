using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// A27 (specimen-loadout-endpoints, action-map.md §17): the audit finding this task was built to close
/// — <c>SpecimenLoadoutEndpoints.MapSpecimenLoadout()</c> is a static extension method that does
/// nothing until <c>Program.cs</c> calls it. <see cref="SpecimenLoadoutEndpointsTests"/>
/// (`FusionRpg.Server.Tests`) proves the endpoint LOGIC against a hand-built minimal host (fast, full
/// behavioral matrix); this file proves the route is actually reachable through the REAL production
/// bootstrap (`RpgApiFactory : WebApplicationFactory&lt;Program&gt;`, same placement reasoning as
/// `UnlockTuningActivationTests`, T63) — the exact gap a compiled-but-never-`Map`ped endpoint would
/// leave invisible to a unit test that builds its own host.
/// </summary>
[Collection("e2e")]
public class SpecimenLoadoutEndpointsHostTests
{
    readonly RpgApiFactory _factory;

    public SpecimenLoadoutEndpointsHostTests(RpgApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_real_host_resolves_GET_and_POST_on_the_specimen_loadout_route()
    {
        using var http = _factory.CreateClient(); // boots Program via the real bootstrap

        using var store = _factory.OpenStore(); // the same memory databases Program.cs's own store opened
        store.Init();

        var actor = store.CreateUniqueActor(playerId: 1, side: "plant", typeId: 1);

        var containerId = "skill.t64-host-registration";
        Assert.True(store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
        }).IsOk);
        Assert.True(store.UpsertAction(new ActionRow
        {
            ActionId = "action.t64-host-registration",
            Name = "T64 host registration",
            Kind = ActionKind.Skill,
            Rung = 1,
            Enabled = true,
            Grantable = true,
            ContainerId = containerId,
        }).IsOk);
        Assert.True(store.UpsertGrant(new ActionGrantRow(
            OwnerKind.UniqueActor, actor.InstanceId, "action.t64-host-registration", "test")).IsOk);

        // GET: a compiled-but-unmapped endpoint would 404 here (Minimal API routing, not a 500) --
        // this is the proof `app.MapSpecimenLoadout()` is a real line in the booted Program.
        var getResp = await http.GetAsync($"/api/actors/{actor.InstanceId}/loadout");
        getResp.EnsureSuccessStatusCode();

        var postResp = await http.PostAsJsonAsync($"/api/actors/{actor.InstanceId}/loadout",
            new { actionIds = new[] { "action.t64-host-registration" } });
        postResp.EnsureSuccessStatusCode();

        var afterPost = await http.GetAsync($"/api/actors/{actor.InstanceId}/loadout");
        var body = await afterPost.Content.ReadFromJsonAsync<LoadoutDto>();
        Assert.Equal(new[] { "action.t64-host-registration" }, body!.ActionIds);
    }

    [Fact]
    public async Task Get_unknownInstanceId_returns404OnTheRealHost()
    {
        using var http = _factory.CreateClient();

        var resp = await http.GetAsync("/api/actors/no-such-specimen-t64/loadout");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    sealed class LoadoutDto
    {
        public string InstanceId { get; set; } = "";
        public List<string> ActionIds { get; set; } = new();
    }
}
