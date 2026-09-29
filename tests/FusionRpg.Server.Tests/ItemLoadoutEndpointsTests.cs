using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Data;
using FusionRpg.Server.Gates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// build-preset BP1.8 (spec-item-loadout-apply.md, contract owned by
/// <c>docs/architecture/item/spec-armoury.md:220</c>) — the five armoury loadout routes over a real
/// in-process host, a real bound specimen and real stored items. The armoury spec's own two named
/// tests, now driven over HTTP, plus <c>force_reports_what_it_stripped</c> for its second clause
/// (same house pattern as <c>ItemEquipEndpointsTests.cs</c>).
/// </summary>
public class ItemLoadoutEndpointsTests : IAsyncLifetime
{
    const string Rung = "cultivated";
    const string BaseTypeId = "item.loadout-ep-base-001";
    const string BladeContainer = "item.loadout-ep-blade";
    const int ItemLevel = 10;
    static readonly string ArmamentPrimary = ItemRoles.Id(ItemRole.ArmamentPrimary);

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;
    string _playerKey = "";
    string _specimenId = "";

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _playerKey = _playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);

        _specimenId = _store.CreateUniqueActor(_playerId, "plant", 1).InstanceId;

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<ItemEquipService>();
        builder.Services.AddSingleton<ItemLoadoutApplyService>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapItemEquip(new ItemEquipService(_store));
        _app.MapItemLoadouts();
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

    string SeedItem(string containerId, ItemRole role)
    {
        var atomId = AtomRow.DeriveId("atom.loadout-ep-vitality", "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.modify", FamilyId = "atom.loadout-ep-vitality",
            Variant = "", Tier = 1, Name = "vitality",
            ParamsJson = """{"channel":"maxHp","op":"flat","amount":10}""",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item, Slot = ItemRoles.Id(role),
            Rarity = Rung, Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        var instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = containerId, RollSeed = 4242, CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop, Atoms = new[] { new InstanceAtomRow(0, atomId, """{"amount":10}""") },
        });

        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = _playerKey, AcquiredUtc = "2026-09-06T00:00:00Z", OriginKind = "drop",
        }).IsOk);

        _store.PersistLoot(_playerKey,
            new LootManifest("le-drop", "table.loadout-ep", 7UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "loadout-ep", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(instanceId, 0, BaseTypeId, RungOrdinal(), ItemLevel, "humanoid",
                    ItemRoles.Id(role), "drop"),
            });

        return instanceId;
    }

    int RungOrdinal() => _store.ListRarities().First(r => r.RarityId == Rung).Ordinal;

    async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    async Task Save(string loadoutId, params (string Role, string RefKind, string RefId)[] entries)
    {
        var (status, _) = await Send(HttpMethod.Put, $"/api/items/loadouts/{loadoutId}", new
        {
            playerId = _playerKey,
            name = "Preset",
            entries = entries.Select(e => new { role = e.Role, refKind = e.RefKind, refId = e.RefId }),
        });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_loadout_entry_whose_item_was_salvaged_returns_missing()
    {
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        await Save("lo-1", (ArmamentPrimary, "item", instanceId));
        _store.DeleteInstance(instanceId);

        var (status, body) = await Send(HttpMethod.Post, "/api/items/loadouts/lo-1/apply",
            new { playerId = _playerId, targetId = _specimenId });

        Assert.Equal(HttpStatusCode.OK, status);
        var results = body.GetProperty("results").EnumerateArray().ToList();
        var entry = Assert.Single(results);
        Assert.False(entry.GetProperty("ok").GetBoolean());
        Assert.Equal("loadout.entry-missing", entry.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Applying_a_loadout_whose_item_is_held_elsewhere_refuses_with_LoadoutConflict()
    {
        var otherSpecimen = _store.CreateUniqueActor(_playerId, "plant", 1).InstanceId;
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        _store.SaveAssignment(otherSpecimen, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, instanceId, "2026-01-01T00:00:00Z");
        await Save("lo-1", (ArmamentPrimary, "item", instanceId));

        var (status, body) = await Send(HttpMethod.Post, "/api/items/loadouts/lo-1/apply",
            new { playerId = _playerId, targetId = _specimenId });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.True(body.GetProperty("plan").GetProperty("refused").GetBoolean());
        var conflicts = body.GetProperty("plan").GetProperty("conflicts").EnumerateArray().ToList();
        Assert.Single(conflicts);
        Assert.Empty(_store.ListAssignments(_specimenId));
    }

    [Fact]
    public async Task Force_reports_what_it_stripped()
    {
        var otherSpecimen = _store.CreateUniqueActor(_playerId, "plant", 1).InstanceId;
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        _store.SaveAssignment(otherSpecimen, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, instanceId, "2026-01-01T00:00:00Z");
        await Save("lo-1", (ArmamentPrimary, "item", instanceId));

        var (status, body) = await Send(HttpMethod.Post, "/api/items/loadouts/lo-1/apply",
            new { playerId = _playerId, targetId = _specimenId, force = true });

        Assert.Equal(HttpStatusCode.OK, status);
        var stripped = body.GetProperty("plan").GetProperty("stripped").EnumerateArray().ToList();
        var cell = Assert.Single(stripped);
        Assert.Equal(otherSpecimen, cell.GetProperty("specimenId").GetString());
        Assert.Empty(_store.ListAssignments(otherSpecimen));
    }

    [Fact]
    public async Task List_save_and_delete_round_trip_over_HTTP()
    {
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        await Save("lo-1", (ArmamentPrimary, "item", instanceId));

        var (listStatus, listBody) = await Send(HttpMethod.Get, $"/api/items/loadouts?playerId={_playerKey}");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var loadout = Assert.Single(listBody.GetProperty("loadouts").EnumerateArray());
        Assert.Equal("lo-1", loadout.GetProperty("loadoutId").GetString());

        var (previewStatus, previewBody) = await Send(HttpMethod.Post, "/api/items/loadouts/lo-1/preview",
            new { playerId = _playerId, targetId = _specimenId });
        Assert.Equal(HttpStatusCode.OK, previewStatus);
        Assert.False(previewBody.GetProperty("refused").GetBoolean());
        Assert.Empty(_store.ListAssignments(_specimenId)); // preview writes nothing

        var (deleteStatus, _) = await Send(HttpMethod.Delete, $"/api/items/loadouts/lo-1?playerId={_playerKey}");
        Assert.Equal(HttpStatusCode.OK, deleteStatus);
        var (afterStatus, afterBody) = await Send(HttpMethod.Get, $"/api/items/loadouts?playerId={_playerKey}");
        Assert.Equal(HttpStatusCode.OK, afterStatus);
        Assert.Empty(afterBody.GetProperty("loadouts").EnumerateArray());
    }
}
