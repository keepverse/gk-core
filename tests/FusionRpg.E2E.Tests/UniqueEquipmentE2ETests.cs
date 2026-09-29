using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class UniqueEquipmentE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public UniqueEquipmentE2ETests(RpgApiFactory factory)
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
    public async Task Equip_in_Roster_rebuilds_mods_and_gates_non_Roster()
    {
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { side = "plant", typeId = 1 });
        create.EnsureSuccessStatusCode();
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>(Json);
        Assert.NotNull(actor);

        var put = await _http.PutAsJsonAsync(
            $"/api/unique/actors/{actor!.InstanceId}/equipment/weapon",
            new { itemId = "stub.atk_ring" });
        put.EnsureSuccessStatusCode();
        var eq = await put.Content.ReadFromJsonAsync<UniqueEquipmentListDto>(Json);
        Assert.Contains(eq!.Items, x => x.Slot == "weapon" && x.ItemId == "stub.atk_ring");
        // mods-absorption (spec-mods-absorption.md): stub.atk_ring is atom-backed, so its grant no
        // longer reaches mods_json — it grants exclusively through effect_binding now.
        Assert.DoesNotContain("fx.passive_atk_flat", eq.ModsJson, StringComparison.Ordinal);

        var get = await _http.GetFromJsonAsync<UniqueEquipmentListDto>(
            $"/api/unique/actors/{actor.InstanceId}/equipment", Json);
        Assert.Equal("stub.atk_ring", get!.Items.First(x => x.Slot == "weapon").ItemId);

        var corr = "e2e-equip-" + Guid.NewGuid().ToString("N")[..8];
        (await _http.PostAsJsonAsync($"/api/unique/actors/{actor.InstanceId}/deploy",
            new { correlationId = corr })).EnsureSuccessStatusCode();

        var blocked = await _http.PutAsJsonAsync(
            $"/api/unique/actors/{actor.InstanceId}/equipment/armor",
            new { itemId = "stub.hp_charm" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var err = await blocked.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("phase.not_roster", err.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Delete_equipment_clears_and_gates_non_Roster()
    {
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { side = "plant", typeId = 1 });
        create.EnsureSuccessStatusCode();
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>(Json);
        Assert.NotNull(actor);

        (await _http.PutAsJsonAsync(
            $"/api/unique/actors/{actor!.InstanceId}/equipment/weapon",
            new { itemId = "stub.atk_ring" })).EnsureSuccessStatusCode();

        var del = await _http.DeleteAsync($"/api/unique/actors/{actor.InstanceId}/equipment/weapon");
        del.EnsureSuccessStatusCode();
        var eq = await del.Content.ReadFromJsonAsync<UniqueEquipmentListDto>(Json);
        Assert.DoesNotContain(eq!.Items, x => x.Slot == "weapon");

        var corr = "e2e-del-" + Guid.NewGuid().ToString("N")[..8];
        (await _http.PostAsJsonAsync($"/api/unique/actors/{actor.InstanceId}/deploy",
            new { correlationId = corr })).EnsureSuccessStatusCode();

        var blocked = await _http.DeleteAsync($"/api/unique/actors/{actor.InstanceId}/equipment/armor");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var err = await blocked.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("phase.not_roster", err.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Put_unknown_item_and_empty_clear()
    {
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { side = "zombie", typeId = 2 });
        create.EnsureSuccessStatusCode();
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>(Json);
        Assert.NotNull(actor);

        var unknown = await _http.PutAsJsonAsync(
            $"/api/unique/actors/{actor!.InstanceId}/equipment/weapon",
            new { itemId = "stub.nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var err = await unknown.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("unknown_item", err.GetProperty("reason").GetString());

        (await _http.PutAsJsonAsync(
            $"/api/unique/actors/{actor.InstanceId}/equipment/trinket",
            new { itemId = "stub.butter_bead" })).EnsureSuccessStatusCode();

        var clear = await _http.PutAsJsonAsync(
            $"/api/unique/actors/{actor.InstanceId}/equipment/trinket",
            new { itemId = "" });
        clear.EnsureSuccessStatusCode();
        var eq = await clear.Content.ReadFromJsonAsync<UniqueEquipmentListDto>(Json);
        Assert.DoesNotContain(eq!.Items, x => x.Slot == "trinket");
    }

    [Fact]
    public async Task Award_xp_levels_and_refuses_retired()
    {
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { side = "zombie", typeId = 2 });
        create.EnsureSuccessStatusCode();
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>(Json);
        Assert.NotNull(actor);

        var xp = await _http.PostAsJsonAsync($"/api/unique/actors/{actor!.InstanceId}/xp",
            new { delta = 150.0, reason = "e2e" });
        xp.EnsureSuccessStatusCode();
        var after = await xp.Content.ReadFromJsonAsync<UniqueActorDto>(Json);
        Assert.Equal(2, after!.Level);
        Assert.Equal(50, after.Xp);

        (await _http.PostAsJsonAsync($"/api/unique/actors/{actor.InstanceId}/retire", new { }))
            .EnsureSuccessStatusCode();
        var refuse = await _http.PostAsJsonAsync($"/api/unique/actors/{actor.InstanceId}/xp",
            new { delta = 10.0 });
        Assert.Equal(HttpStatusCode.Conflict, refuse.StatusCode);
        var err = await refuse.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("phase.retired", err.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Award_xp_rejects_bad_delta()
    {
        var create = await _http.PostAsJsonAsync("/api/unique/actors", new { side = "plant", typeId = 1 });
        create.EnsureSuccessStatusCode();
        var actor = await create.Content.ReadFromJsonAsync<UniqueActorDto>(Json);
        Assert.NotNull(actor);

        var bad = await _http.PostAsJsonAsync($"/api/unique/actors/{actor!.InstanceId}/xp",
            new { delta = 0.0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var err = await bad.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("bad_delta", err.GetProperty("reason").GetString());
    }
}
