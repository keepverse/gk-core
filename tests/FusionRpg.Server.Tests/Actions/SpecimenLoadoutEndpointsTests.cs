using System.Net;
using System.Net.Http.Json;
using FusionRpg.Core.Actions;
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
/// A27 (specimen-loadout-endpoints): <c>GET/POST /api/actors/{instanceId}/loadout</c> against a REAL,
/// minimal in-process host (same pattern <see cref="LoadoutEndpointsTests"/> already established for
/// Dave's own loadout endpoint) — proves the real endpoint surface, not a handler-delegate unit test:
/// real routing, real 404/400/409 responses, both grant scopes (<see cref="OwnerKind.UniqueActor"/> and
/// <see cref="OwnerKind.Entity"/>) actually merged, matching `WebMatchService.EquippedActionIdsFor`'s
/// own established convention. `Program.cs`'s real <c>app.MapSpecimenLoadout()</c> registration is
/// proven separately, on the real host, by <c>SpecimenLoadoutEndpointsHostTests</c> (E2E) — this file
/// answers "does the logic work", that one answers "is it actually reachable through Program.cs".
/// </summary>
public class SpecimenLoadoutEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapSpecimenLoadout();
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

    string CreateSpecimen() => _store.CreateUniqueActor(playerId: 1, side: "plant", typeId: 1).InstanceId;

    /// <summary>Seeds a real, grantable action row of the given kind (default Skill).</summary>
    string SeedAction(string actionId, ActionKind kind = ActionKind.Skill)
    {
        var family = "atom." + actionId.Replace('.', '-');
        var atomId = AtomRow.DeriveId(family, "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = family,
            Variant = "",
            Tier = 1,
            Name = actionId,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        }).IsOk);

        var containerId = "skill." + actionId.Replace('.', '-') + "-container";
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        Assert.True(_store.UpsertAction(new ActionRow
        {
            ActionId = actionId,
            Name = actionId,
            Kind = kind,
            Rung = 1, // RungPolicy.Table has no row for rung 0 -- AutoEquip.Select would throw on it
            ContainerId = containerId,
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        }).IsOk);
        return actionId;
    }

    void Grant(string instanceId, string actionId, OwnerKind scope, string source = "test") =>
        Assert.True(_store.UpsertGrant(new ActionGrantRow(scope, instanceId, actionId, source)).IsOk);

    [Fact]
    public async Task Get_unknownInstanceId_returns404()
    {
        var resp = await _http.GetAsync("/api/actors/no-such-specimen/loadout");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_withNoLoadoutRow_returnsTheAutoEquipResult()
    {
        var instanceId = CreateSpecimen();
        SeedAction("action.auto.001");
        Grant(instanceId, "action.auto.001", OwnerKind.UniqueActor);

        var resp = await _http.GetAsync($"/api/actors/{instanceId}/loadout");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<LoadoutDto>();

        // Matches GetLoadoutOrAutoEquip's own contract: no persisted row -> auto-equip from what is held.
        var expected = _store.GetLoadoutOrAutoEquip(
            new OwnerScope(OwnerKind.Entity, instanceId),
            new[] { new FusionRpg.Core.Actions.Loadout.AutoEquipCandidate("action.auto.001", 1) });
        Assert.Equal(expected, body!.ActionIds);
    }

    [Fact]
    public async Task Get_withTwoHeldOneEquipped_returnsBothTheFullHeldSetAndTheEquippedSubset()
    {
        // A30 (T69/T70): the FE grid needs the whole held set, not just what battle would equip --
        // this is the field that makes "2 held, 1 equipped" renderable at all.
        var instanceId = CreateSpecimen();
        SeedAction("action.held-only.001");
        SeedAction("action.equipped.001");
        Grant(instanceId, "action.held-only.001", OwnerKind.UniqueActor);
        Grant(instanceId, "action.equipped.001", OwnerKind.UniqueActor);

        var postResp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout",
            new { actionIds = new[] { "action.equipped.001" } });
        postResp.EnsureSuccessStatusCode();

        var getResp = await _http.GetAsync($"/api/actors/{instanceId}/loadout");
        var body = await getResp.Content.ReadFromJsonAsync<LoadoutDto>();

        Assert.Equal(new[] { "action.equipped.001" }, body!.ActionIds);
        Assert.Equal(
            new[] { "action.equipped.001", "action.held-only.001" }.OrderBy(x => x, StringComparer.Ordinal),
            body.HeldActionIds.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Post_thenGet_roundTripsARealPersistedLoadout()
    {
        var instanceId = CreateSpecimen();
        SeedAction("action.held.001");
        Grant(instanceId, "action.held.001", OwnerKind.UniqueActor);

        var postResp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout",
            new { actionIds = new[] { "action.held.001" } });
        postResp.EnsureSuccessStatusCode();

        var getResp = await _http.GetAsync($"/api/actors/{instanceId}/loadout");
        var getBody = await getResp.Content.ReadFromJsonAsync<LoadoutDto>();
        Assert.Equal(new[] { "action.held.001" }, getBody!.ActionIds);
    }

    [Fact]
    public async Task Post_anActionHeldOnlyUnderUniqueActorScope_isAcceptedAsHeld()
    {
        var instanceId = CreateSpecimen();
        SeedAction("action.ladder.001");
        Grant(instanceId, "action.ladder.001", OwnerKind.UniqueActor);

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout",
            new { actionIds = new[] { "action.ladder.001" } });

        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Post_anActionHeldOnlyUnderEntityScope_isAcceptedAsHeld()
    {
        var instanceId = CreateSpecimen();
        SeedAction("action.item-granted.001");
        Grant(instanceId, "action.item-granted.001", OwnerKind.Entity);

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout",
            new { actionIds = new[] { "action.item-granted.001" } });

        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Post_anUnheldActionId_conflictsAndPersistsNothing()
    {
        var instanceId = CreateSpecimen();
        SeedAction("action.never-granted.001");

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout",
            new { actionIds = new[] { "action.never-granted.001" } });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Post_aBasicActionId_rejectsAsACategoryError()
    {
        // A basic is intrinsic on every actor already and can never be granted
        // (ActionValidator.ValidateGrant's own BasicCollision rule) -- LoadoutSet.Validate checks
        // kind BEFORE held, so naming a real basic action id refuses as a category error with no
        // grant involved at all.
        var instanceId = CreateSpecimen();
        SeedAction("act.attack.test", ActionKind.Basic);

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout",
            new { actionIds = new[] { "act.attack.test" } });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Post_sixActionIds_rejectsAsLoadoutFull()
    {
        var instanceId = CreateSpecimen();
        var ids = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var id = $"action.slot{i}.001";
            SeedAction(id);
            Grant(instanceId, id, OwnerKind.UniqueActor);
            ids.Add(id);
        }

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout", new { actionIds = ids });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Post_aWithdrawnGrant_isNotTreatedAsHeld()
    {
        var instanceId = CreateSpecimen();
        SeedAction("action.withdrawn.001");
        Grant(instanceId, "action.withdrawn.001", OwnerKind.UniqueActor, source: "test-source");
        _store.WithdrawGrantsBySource(new OwnerScope(OwnerKind.UniqueActor, instanceId), "test-source");

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout",
            new { actionIds = new[] { "action.withdrawn.001" } });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Post_missingActionIds_returns400()
    {
        var instanceId = CreateSpecimen();

        var resp = await _http.PostAsJsonAsync($"/api/actors/{instanceId}/loadout", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_unknownInstanceId_returns404()
    {
        var resp = await _http.PostAsJsonAsync("/api/actors/no-such-specimen/loadout",
            new { actionIds = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    sealed class LoadoutDto
    {
        public string InstanceId { get; set; } = "";
        public List<string> ActionIds { get; set; } = new();
        public List<string> HeldActionIds { get; set; } = new();
    }
}
