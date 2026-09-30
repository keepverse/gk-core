using System.Net;
using FusionRpg.Core.Items.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// ⭐ item module 4 (<c>equip-assign</c>) — <b>the equip executor</b>, against a real in-process host,
/// a real store, a real bound specimen and a real stored item.
///
/// <para>Module 4 shipped its table, its two writes, its gate and its projector green in September
/// and recorded the same blocker the workbench modules did: <c>SaveAssignment</c> and
/// <c>RemoveAssignment</c> had <b>zero callers outside <c>tests/</c></b>. These tests drive both
/// through the HTTP surface and then <b>read the state back from the store</b>, which is the only
/// thing that separates "the route returned 200" from "the player is wearing it".</para>
///
/// <para>⛔ Every refusal case asserts <b>two</b> things: the named rule on the wire, and that the
/// table is unchanged. A gate that answers 409 and writes anyway is the failure mode a status-code
/// assertion alone cannot see.</para>
/// </summary>
public class ItemEquipEndpointsTests : IAsyncLifetime
{
    const string Rung = "cultivated";
    const string BaseTypeId = "item.equip-base-a-001";
    const string BladeContainer = "item.equip-blade";
    const string HelmContainer = "item.equip-helm";
    const string LordlyContainer = "item.equip-lordly-blade";
    const string Frame = "humanoid";
    const int ItemLevel = 24;

    /// <summary>Above the level a freshly bound specimen starts at (<c>rpg_unique_actors.level</c>
    /// defaults to 1), so the level arm refuses for a real reason rather than a contrived one.</summary>
    const int OutOfReachLevelReq = 50;

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;
    string _playerKey = "";
    string _specimenId = "";
    string _bladeId = "";
    string _helmId = "";
    string _lordlyId = "";

    static readonly string ArmamentPrimary = ItemRoles.Id(ItemRole.ArmamentPrimary);
    static readonly string HeadGuard = ItemRoles.Id(ItemRole.HeadGuard);

    // ---- fixture -----------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _playerKey = _playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);

        _specimenId = _store.CreateUniqueActor(_playerId, "plant", 1).InstanceId;

        _bladeId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        _helmId = SeedItem(HelmContainer, ItemRole.HeadGuard);
        _lordlyId = SeedItem(LordlyContainer, ItemRole.ArmamentPrimary, levelReq: OutOfReachLevelReq);

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        // The OTHER flow that writes `rpg_item_assignment`, mapped here on purpose: defect R1 was
        // that these two routes were asymmetric, and an asymmetry between two routes can only be
        // proven with both of them reachable from one host.
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapItemEquip(new ItemEquipService(_store));
        _app.MapUniqueActors();
        // Module 20's read-only armoury, on its REAL shipped tuning files (defect R4's surface).
        var tuningDir = Path.Combine(RepoRoot(), "data", "tuning");
        _app.MapItemSurfaces(
            FusionRpg.Core.Items.Surfaces.ItemSurfaceTuning.Parse(
                File.ReadAllText(Path.Combine(tuningDir, "item-surfaces.v1.json"))),
            FusionRpg.Core.Items.Sockets.SocketTuning.Parse(
                File.ReadAllText(Path.Combine(tuningDir, SocketTuningFiles.Current))),
            lookupInsert: null,
            // item-content `item-naming` T3: the SAME base-type delegate the card route reads, handed
            // in exactly as `Program.cs` hands it in. These three test containers are not in the
            // shipped 740-entry corpus, so a real authored name is supplied for one of them here and
            // the other two exercise the honest "" the route sends for an unknown container.
            lookupBaseType: FusionRpg.Server.ItemBaseTypeCorpus.From(
                new Dictionary<string, FusionRpg.Core.Items.Display.CardBaseType>(StringComparer.Ordinal)
                {
                    [BladeContainer] = new FusionRpg.Core.Items.Display.CardBaseType(
                        "base.equip-blade", "class.blade", "humanoid", "role.armament-primary", null,
                        "Honed Hatchet"),
                }));
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A real container, a real frozen instance, a real ownership row and a real generation
    /// stamp — the four things the equip gate reads off "a stored item".</summary>
    string SeedItem(string containerId, ItemRole role, int? levelReq = null, string? playerKey = null)
    {
        var atomId = AtomRow.DeriveId("atom.equip-vitality", "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom.equip-vitality",
            Variant = "",
            Tier = 1,
            Name = "equip vitality",
            ParamsJson = """{"channel":"maxHp","op":"flat","amount":10}""",
        }).IsOk);

        var upsert = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(role),
            Rarity = Rung,
            LevelReq = levelReq,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(upsert.IsOk, upsert.ToString());

        var instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = containerId,
            RollSeed = 4242,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, atomId, """{"amount":10}""") },
        });

        var owner = playerKey ?? _playerKey;
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId,
            PlayerId = owner,
            AcquiredUtc = "2026-09-06T00:00:00Z",
            OriginKind = "drop",
        }).IsOk);

        _store.PersistLoot(
            owner,
            new LootManifest("eq-drop", "table.equip", 7UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "equip", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(instanceId, 0, BaseTypeId, RungOrdinal(), ItemLevel, Frame,
                    ItemRoles.Id(role), "drop"),
            });

        return instanceId;
    }

    int RungOrdinal() => _store.ListRarities().First(r => r.RarityId == Rung).Ordinal;

    async Task<(HttpStatusCode Status, JsonElement Body)> Post(string verb, object body)
    {
        var resp = await _http.PostAsJsonAsync($"/api/items/{verb}", body);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    /// <summary>An INDEPENDENT read of what the specimen wears — over HTTP, not off the write's own
    /// reply. A response echoing back what it was handed proves nothing about what was stored.</summary>
    async Task<JsonElement> ReadAssignments()
    {
        var resp = await _http.GetAsync($"/api/items/assignments/{_specimenId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    // ---- equip: the happy path, proven from the store ---------------------------------------------

    /// <summary>
    /// ⭐ The whole point of the module: an item goes into a role over HTTP, and the row is there
    /// afterwards. Read back twice, both times from somewhere other than the reply — once through the
    /// store directly (which is what <c>SaveAssignment</c> actually wrote) and once through a second
    /// HTTP call on its own connection.
    /// </summary>
    [Fact]
    public async Task Equip_writesTheAssignment_andTwoIndependentReadsSeeIt()
    {
        Assert.Empty(_store.ListAssignments(_specimenId));

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("equip", body.GetProperty("verb").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("replaced").ValueKind);

        // 1 — SECOND READ, straight off the store. `rpg_item_assignment` is the SSOT.
        var row = Assert.Single(_store.ListAssignments(_specimenId));
        Assert.Equal(ItemRole.ArmamentPrimary, row.Role);
        Assert.Equal(ItemEquipService.RolledRefKind, row.RefKind);
        Assert.Equal(_bladeId, row.RefId);
        Assert.False(string.IsNullOrWhiteSpace(row.AssignedUtc));

        // 2 — THIRD READ, over a separate request.
        var listed = Assert.Single((await ReadAssignments()).EnumerateArray().ToList());
        Assert.Equal(ArmamentPrimary, listed.GetProperty("role").GetString());
        Assert.Equal(_bladeId, listed.GetProperty("refId").GetString());
    }

    /// <summary>
    /// ⭐ <c>ref_kind</c> is <c>"rolled"</c>, and that is load-bearing rather than cosmetic:
    /// <c>RpgStore.ApplyEquipProjection</c> only turns a <c>"rolled"</c> assignment into a runtime
    /// binding, so an equip that stored any other kind would persist a decision module 5 could never
    /// project. Pinned here so a future rename has to come through this test.
    /// </summary>
    [Fact]
    public async Task Equip_storesTheRolledRefKind_theOnlyKindTheProjectorBinds()
    {
        await Post("equip", new { playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary });

        Assert.Equal("rolled", Assert.Single(_store.ListAssignments(_specimenId)).RefKind);
    }

    /// <summary>Retrying an equip is safe without a correlation id, which is why the route asks for
    /// none: the upsert lands the same single row.</summary>
    [Fact]
    public async Task Equip_repeated_isIdempotentAndStillOneRow()
    {
        var first = await Post("equip", new { playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary });
        Assert.Equal(HttpStatusCode.OK, first.Status);

        var second = await Post("equip", new { playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary });
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal("equip.already-in-this-role", second.Body.GetProperty("reason").GetString());

        Assert.Single(_store.ListAssignments(_specimenId));
    }

    /// <summary>Two items for one role is a swap. It is allowed, and the reply names the piece that
    /// came off so the surface never has to work it out.</summary>
    [Fact]
    public async Task Equip_overAWornPiece_swapsAndNamesWhatItDisplaced()
    {
        var second = SeedItem("item.equip-blade-b", ItemRole.ArmamentPrimary);
        await Post("equip", new { playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary });

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = second, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(_bladeId, body.GetProperty("replaced").GetProperty("refId").GetString());

        var row = Assert.Single(_store.ListAssignments(_specimenId));
        Assert.Equal(second, row.RefId);

        // The displaced item is still owned — coming off a role never destroys a piece (module 1's R1).
        Assert.Equal("owned", _store.GetItem(_bladeId)!.Disposition);
    }

    // ---- unequip -----------------------------------------------------------------------------------

    /// <summary>⭐ <c>RemoveAssignment</c>'s first production caller: one row deleted, no second
    /// writer (§6.4's atomicity claim), and the item survives it.</summary>
    [Fact]
    public async Task Unequip_deletesTheRow_andLeavesTheItemOwned()
    {
        await Post("equip", new { playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary });
        Assert.Single(_store.ListAssignments(_specimenId));

        var (status, body) = await Post("unequip", new
        {
            playerId = _playerId, specimenId = _specimenId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(_bladeId, body.GetProperty("replaced").GetProperty("refId").GetString());

        // SECOND READ: gone from the table, still in the armoury.
        Assert.Empty(_store.ListAssignments(_specimenId));
        Assert.Empty((await ReadAssignments()).EnumerateArray());
        Assert.Equal("owned", _store.GetItem(_bladeId)!.Disposition);
    }

    [Fact]
    public async Task Unequip_anEmptyRole_isRefusedByName()
    {
        var (status, body) = await Post("unequip", new
        {
            playerId = _playerId, specimenId = _specimenId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.role-empty", body.GetProperty("reason").GetString());
    }

    // ---- refusals: each one asserts the table is untouched -----------------------------------------

    /// <summary>⭐ The gate refuses a piece aimed at the wrong role, the same way the relic flow's own
    /// <c>SlotMatchesItem</c> does for its three legacy slots.</summary>
    [Fact]
    public async Task Equip_intoTheWrongRole_isRefusedAndWritesNothing()
    {
        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _helmId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.role-mismatch", body.GetProperty("reason").GetString());
        Assert.Contains(HeadGuard, body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListAssignments(_specimenId));
    }

    /// <summary>⭐ A4, end to end rather than as a branch: <c>level_req</c> is compared against the
    /// SPECIMEN's level (`spec-equip-assign.md`'s recommendation) and actually refuses.</summary>
    [Fact]
    public async Task Equip_whenLevelReqIsAboveTheSpecimen_isRefusedAndWritesNothing()
    {
        Assert.Equal(1, _store.GetUniqueActor(_specimenId)!.Level);

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _lordlyId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.level-too-low", body.GetProperty("reason").GetString());
        Assert.Contains($"needs level {OutOfReachLevelReq}", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListAssignments(_specimenId));
    }

    [Fact]
    public async Task Equip_anItemAnotherPlayerOwns_isRefusedAndWritesNothing()
    {
        var other = _store.CreatePlayer("other").Id;
        var theirs = SeedItem("item.equip-theirs", ItemRole.ArmamentPrimary,
            playerKey: other.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = theirs, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("item.not-owned", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListAssignments(_specimenId));
    }

    [Fact]
    public async Task Equip_toASpecimenAnotherPlayerOwns_isRefused()
    {
        var other = _store.CreatePlayer("other-2").Id;
        var theirSpecimen = _store.CreateUniqueActor(other, "plant", 2).InstanceId;

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = theirSpecimen, instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.specimen-not-owned", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListAssignments(theirSpecimen));
    }

    /// <summary>save-identity SE4.25: a Zomboss specimen of THIS save (same `player_id` as `_playerId`,
    /// only its `empire_id` differs) is refused too — the exact gap `row.PlayerId != playerId` alone
    /// would have missed after SE4.22 shares the save's row between its empires.</summary>
    [Fact]
    public async Task Equip_toAZombossSpecimenOfTheSameSave_isRefused()
    {
        var zombossSpecimen = _store.CreateUniqueActor(_playerId, "plant", 3).InstanceId;
        using (var db = FusionRpg.Data.Sqlite.SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE rpg_unique_actors SET empire_id = 'zomboss' WHERE instance_id = $id;";
            cmd.Parameters.AddWithValue("$id", zombossSpecimen);
            cmd.ExecuteNonQuery();
        }

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = zombossSpecimen, instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.specimen-not-owned", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListAssignments(zombossSpecimen));
    }

    [Fact]
    public async Task Equip_toAnUnknownSpecimen_isRefusedRatherThanCrashing()
    {
        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = "no-such-specimen", instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.specimen-unknown", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Equip_aSalvagedItem_isRefused()
    {
        var item = _store.GetItem(_bladeId)!;
        _store.SaveItem(item with { Disposition = "salvaged" });

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("is 'salvaged'", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListAssignments(_specimenId));
    }

    /// <summary>One physical copy cannot be worn twice. The primary key stops two items sharing a
    /// role; this stops one item filling the same role on two specimens.</summary>
    [Fact]
    public async Task Equip_theSameCopyOnASecondSpecimen_isRefused()
    {
        var secondSpecimen = _store.CreateUniqueActor(_playerId, "plant", 3).InstanceId;
        Assert.Equal(HttpStatusCode.OK, (await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        })).Status);

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = secondSpecimen, instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.already-worn", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListAssignments(secondSpecimen));
        Assert.Single(_store.ListAssignments(_specimenId));
    }

    /// <summary>
    /// ⛔ <b>Two flows, one table, no shared writes.</b> Since D1 §10 M1 the four relics live in
    /// <c>rpg_item_assignment</c> as <c>ref_kind='stock'</c>, written by
    /// <c>PUT /api/unique/actors/{id}/equipment/{slot}</c> — which also rebuilds <c>mods_json</c> and
    /// the <c>unique-equip</c> atom bindings in the same call. Clobbering that cell from here would
    /// delete the row and leave both derived states standing, so it is refused by name and the
    /// player is pointed at the flow that owns it.
    /// </summary>
    [Fact]
    public async Task Equip_intoARoleARelicHolds_isRefusedRatherThanClobberingTheRelicFlow()
    {
        _store.SaveAssignment(_specimenId, ItemRole.ArmamentPrimary, "stock", "relic.ashen_reliquary");

        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.role-held-by-relic", body.GetProperty("reason").GetString());

        var row = Assert.Single(_store.ListAssignments(_specimenId));
        Assert.Equal("stock", row.RefKind);
        Assert.Equal("relic.ashen_reliquary", row.RefId);
    }

    /// <summary>
    /// ⭐ <b>The missing half, measured as defect R1 and closed 2026-09-06.</b> The test above proves
    /// this route refuses a relic's role. Until today the relic route refused <i>nothing</i>: with a
    /// real blade in <c>armament-primary</c>, <c>PUT /api/unique/actors/{id}/equipment/weapon</c>
    /// answered <b>200</b> and its upsert replaced the row, so the player's item came off with no
    /// refusal and no notice.
    ///
    /// <para>Driven end to end through <b>both real routes</b> on one host — the item goes in through
    /// <c>POST /api/items/equip</c>, not through a seeded row — because the defect was an asymmetry
    /// between the two, and only both of them together can show it is gone.</para>
    /// </summary>
    [Fact]
    public async Task Equip_thenTheRelicRouteOverTheSameRole_isRefusedAndTheItemSurvives()
    {
        var (equipStatus, _) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        });
        Assert.Equal(HttpStatusCode.OK, equipStatus);

        var resp = await _http.PutAsJsonAsync(
            $"/api/unique/actors/{_specimenId}/equipment/weapon", new { itemId = "relic.ashen_reliquary" });

        // 409, not 200 — a well-formed request the rules say no to, the same shape and the same
        // status this route's mirror refusal already answered with.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("slot.claimed_by_item", doc.RootElement.GetProperty("reason").GetString());

        // And the item's own row is untouched — the assertion a status-code check cannot make.
        var row = Assert.Single(_store.ListAssignments(_specimenId));
        Assert.Equal(ItemRole.ArmamentPrimary, row.Role);
        Assert.Equal(ItemEquipService.RolledRefKind, row.RefKind);
        Assert.Equal(_bladeId, row.RefId);
    }

    /// <summary>The <c>DELETE</c> half of R1, which was the worse one: clearing the legacy slot runs
    /// an unqualified delete on <c>(specimen_id, role)</c> and would have unequipped an item the relic
    /// flow never put there.</summary>
    [Fact]
    public async Task Equip_thenTheRelicRoutesDelete_isRefusedAndTheItemSurvives()
    {
        await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        });

        var resp = await _http.DeleteAsync($"/api/unique/actors/{_specimenId}/equipment/weapon");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("slot.claimed_by_item", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal(_bladeId, Assert.Single(_store.ListAssignments(_specimenId)).RefId);
    }

    /// <summary>⚠ The boundary: only a <c>rolled</c> occupant is refused. A relic replacing a relic is
    /// the relic wire's own job and must stay a 200 — a guard that protected the item flow by breaking
    /// the relic flow would be a worse defect than R1.</summary>
    [Fact]
    public async Task TheRelicRoute_stillReplacesItsOwnStockRow()
    {
        _store.SaveAssignment(_specimenId, ItemRole.ArmamentPrimary, "stock", "relic.ashen_reliquary");

        var resp = await _http.PutAsJsonAsync(
            $"/api/unique/actors/{_specimenId}/equipment/weapon", new { itemId = "relic.sunworn_charm" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var row = Assert.Single(_store.ListAssignments(_specimenId));
        Assert.Equal("stock", row.RefKind);
        Assert.Equal("relic.sunworn_charm", row.RefId);
    }

    // ---- R4: the armoury's `assigned` flag --------------------------------------------------------

    /// <summary>
    /// ⛔ <b>Defect R4, fixed 2026-09-06.</b> <c>ArmouryRowDto.Assigned</c> was hard-coded
    /// <c>false</c> — harmless while nothing could be assigned, and wrong from the day this equip
    /// route shipped. The web armoury's <c>hideAssigned</c> filter (<c>ArmouryList.tsx:60</c>) and the
    /// <c>assigned</c> sort key were both already built and reading it, so both were inert.
    ///
    /// <para>Driven through the real equip route, then read back off the real armoury route: one item
    /// equipped, two not, and the flag has to separate them.</para>
    /// </summary>
    [Fact]
    public async Task Armoury_reportsAssignedForAnEquippedItemAndNotForTheRest()
    {
        var before = await ArmouryRows();
        Assert.Equal(3, before.Count);
        Assert.All(before.Values, assigned => Assert.False(assigned));

        var (status, _) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        });
        Assert.Equal(HttpStatusCode.OK, status);

        var after = await ArmouryRows();
        Assert.True(after[_bladeId]);
        Assert.False(after[_helmId]);
        Assert.False(after[_lordlyId]);
    }

    /// <summary>Unequipping puts the flag back. The item is still owned (module 1's R1), so it must
    /// reappear as un-assigned rather than vanish from the armoury.</summary>
    [Fact]
    public async Task Armoury_dropsAssignedAgainWhenTheItemComesOff()
    {
        await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        });
        Assert.True((await ArmouryRows())[_bladeId]);

        await Post("unequip", new { playerId = _playerId, specimenId = _specimenId, role = ArmamentPrimary });

        var after = await ArmouryRows();
        Assert.Equal(3, after.Count);
        Assert.False(after[_bladeId]);
    }

    /// <summary>⚠ A <c>stock</c> row is the relic wire's catalog id and never pins one of the
    /// player's instances, so it must not light up an armoury row. Pinned because the naive join —
    /// "any assignment row whose ref_id matches" — would, the day a relic id ever collided with an
    /// instance id.</summary>
    [Fact]
    public async Task Armoury_ignoresAStockAssignmentWhenDecidingAssigned()
    {
        _store.SaveAssignment(_specimenId, ItemRole.ArmamentPrimary, "stock", _bladeId);

        Assert.False((await ArmouryRows())[_bladeId]);
    }

    /// <summary>
    /// ⭐ item-content <c>granted-action-text</c> (T15): <c>ssot-presentation.md</c> §9.14's own ask —
    /// <i>"the battle-only tag needs to be visible in the compact list line too, not only the card — a
    /// player scanning an armoury should not have to open each item to learn that half of them are
    /// inert on the lawn."</i>
    ///
    /// <para>It was NOT true before this task: <c>ArmouryRowDto</c> carried no such field, so the only
    /// way to learn an item's action was battle-only was to open its card.</para>
    /// </summary>
    [Fact]
    public async Task Armoury_carriesTheBattleOnlyTagOnTheCompactLine()
    {
        // Nothing grants anything yet, so no row claims to be battle-only.
        Assert.All((await ArmouryBattleOnly()).Values, Assert.False);

        // A DefaultAttack grant replaces the species' basic attack, which only exists in a battle.
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow(
            BladeContainer, 0, "action.general.0003", ItemGrantRole.DefaultAttack));
        // A plain `Granted` entry is an extra selectable and is NOT inert on the lawn — the negative
        // arm, so the tag is proven to discriminate rather than to light up for any grant at all.
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow(
            HelmContainer, 0, "action.general.0001", ItemGrantRole.Granted));

        var rows = await ArmouryBattleOnly();
        Assert.True(rows[_bladeId]);
        Assert.False(rows[_helmId]);
    }

    /// <summary>instanceId → `battleOnly`, off the same real module 20 route.</summary>
    async Task<Dictionary<string, bool>> ArmouryBattleOnly()
    {
        var resp = await _http.GetAsync($"/api/items/armoury/{_playerKey}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("instanceId").GetString()!,
                          r => r.GetProperty("battleOnly").GetBoolean(),
                          StringComparer.Ordinal);
    }

    /// <summary>instanceId → (`role`, `frame`), off the real module 20 route.</summary>
    async Task<Dictionary<string, (string Role, string Frame)>> ArmouryRoleAndFrame(string? role = null, string? frame = null)
    {
        var query = role is null && frame is null
            ? ""
            : "?" + (role is null ? "" : $"role={role}") + (frame is null ? "" : (role is null ? "" : "&") + $"frame={frame}");
        var resp = await _http.GetAsync($"/api/items/armoury/{_playerKey}{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("instanceId").GetString()!,
                          r => (r.GetProperty("role").GetString()!, r.GetProperty("frame").GetString()!),
                          StringComparer.Ordinal);
    }

    /// <summary>
    /// ⛔ <b>The two rows that recorded this gap both blamed a missing table, and both halves of that
    /// blame have since dissolved.</b> `P1.4`'s "the role is typed, not derived" and `P5.4`'s "an
    /// armoury row's role and frame come back empty" said the fields "live on the item's BASE TYPE,
    /// and module 6 shipped the corpus and the Core readers but not a table". The table exists
    /// (`item_base_type`, seeded at boot by `ImportBaseTypes`) <b>and</b> the fact is carried per
    /// instance by `rpg_item_generation`, which is what the mint actually stamped.
    ///
    /// <para>This fixture seeds instances directly rather than minting them, so its rows have no
    /// generation row and therefore empty role/frame — asserted, because "empty" must mean "this item
    /// has no generation row", not "the route forgot". The FILTER half is what discriminates here: a
    /// route that ignored the new parameters would return all three rows for a role none of them
    /// carries.</para>
    /// </summary>
    [Fact]
    public async Task Armoury_carriesRoleAndFrameOffTheGenerationRowAndNarrowsByThem()
    {
        var unfiltered = await ArmouryRoleAndFrame();
        Assert.Equal(3, unfiltered.Count);

        // The blade's own container authors `armament-primary` and the store stamps the frame beside
        // it, so the route reads a REAL pair here — wire shape and data path in one assertion.
        Assert.Equal(("armament-primary", "humanoid"), unfiltered[_bladeId]);

        // A row with no generation row of its own carries empty rather than a guess from the slot:
        // "empty" must mean "this item has none", never "the route forgot".
        Assert.Equal(("", ""), unfiltered[_helmId]);

        // The narrowing is the discriminating half — a route that ignored the new parameters would
        // return all three rows.
        var narrowed = await ArmouryRoleAndFrame(role: "armament-primary");
        Assert.Equal(new[] { _bladeId }, narrowed.Keys.ToArray());
        Assert.Empty(await ArmouryRoleAndFrame(frame: "plant"));
        Assert.Empty(await ArmouryRoleAndFrame(role: "armament-primary", frame: "plant"));
    }

    /// <summary>instanceId → `containerName`, off the real module 20 route (item-content T3).</summary>
    async Task<Dictionary<string, string>> ArmouryNames()
    {
        var resp = await _http.GetAsync($"/api/items/armoury/{_playerKey}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("instanceId").GetString()!,
                          r => r.GetProperty("containerName").GetString()!,
                          StringComparer.Ordinal);
    }

    /// <summary>
    /// live-probe Task 21 (found 2026-09-16 closing Task 14): <c>RpgItemRow.OriginKind</c> always
    /// existed on the store row — every fixture item here is seeded with it (`OriginKind = "drop"`
    /// above) — but the armoury route never put it on the wire, so "lists it with a real provenance"
    /// (Task 14's own Verify line) could only be checked by reading the mint path in code. Two
    /// different real origins asserted, not just the fixture's own default, so a route that always
    /// answered a hard-coded "drop" would still fail this.
    /// </summary>
    [Fact]
    public async Task Armoury_carriesTheItemsRealOriginKind()
    {
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = _lordlyId,
            PlayerId = _playerKey,
            AcquiredUtc = "2026-09-06T00:00:00Z",
            OriginKind = "quest-reward",
        }).IsOk);

        var resp = await _http.GetAsync($"/api/items/armoury/{_playerKey}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var byId = doc.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("instanceId").GetString()!,
                          r => r.GetProperty("originKind").GetString()!,
                          StringComparer.Ordinal);

        Assert.Equal("drop", byId[_bladeId]);
        Assert.Equal("quest-reward", byId[_lordlyId]);
    }

    /// <summary>
    /// ⛔ <b>item-content `item-naming` T3.</b> The armoury row carried no name at all, so
    /// `ArmouryList.tsx` fell back to `adapt.ts`'s `?? containerId` and printed
    /// <c>item.equip-blade</c> where a name belongs. T2 had already carried the base type's authored
    /// `name` through to the CARD; this puts the same string on the row.
    ///
    /// <para>⚠ And the absent case is asserted beside it: a container the corpus does not carry sends
    /// <c>""</c>, never the id. The client turns that into a sentence — a shortened or prettified id
    /// would still be an id.</para>
    /// </summary>
    [Fact]
    public async Task Armoury_carriesTheBaseTypesAuthoredNameAndNeverTheContainerId()
    {
        var names = await ArmouryNames();
        Assert.Equal(3, names.Count);
        Assert.Equal("Honed Hatchet", names[_bladeId]);
        Assert.Equal("", names[_helmId]);
        Assert.Equal("", names[_lordlyId]);
        Assert.DoesNotContain(names.Values, v => v.Contains('.', StringComparison.Ordinal));
    }

    /// <summary>instanceId → `assigned`, off the real module 20 route.</summary>
    async Task<Dictionary<string, bool>> ArmouryRows()
    {
        var resp = await _http.GetAsync($"/api/items/armoury/{_playerKey}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("instanceId").GetString()!,
                          r => r.GetProperty("assigned").GetBoolean(),
                          StringComparer.Ordinal);
    }

    [Fact]
    public async Task Unequip_aRelicsRole_isRefusedRatherThanTakingItOffBehindTheRelicFlowsBack()
    {
        _store.SaveAssignment(_specimenId, ItemRole.ArmamentPrimary, "stock", "relic.ashen_reliquary");

        var (status, body) = await Post("unequip", new
        {
            playerId = _playerId, specimenId = _specimenId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.role-held-by-relic", body.GetProperty("reason").GetString());
        Assert.Single(_store.ListAssignments(_specimenId));
    }

    [Fact]
    public async Task Equip_anUnknownRole_isRefusedByName()
    {
        var (status, body) = await Post("equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = "left-antenna",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("equip.role-unknown", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Equip_withoutARole_is400()
    {
        var resp = await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- the commander wears the same roles through the same gate (owner ruling 2026-09-16, item-ideal D1) ----

    [Fact]
    public async Task The_commander_equips_through_the_same_route_as_a_specimen()
    {
        var commander = "commander:dave";

        var resp = await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = _playerId, specimenId = commander, instanceId = _bladeId, role = ArmamentPrimary,
        });

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        // Read the state back through the store, not the response: the commander's own durable table.
        var worn = Assert.Single(_store.ListPlayerItemAssignments(_playerKey));
        Assert.Equal(ItemRole.ArmamentPrimary, worn.Role);
        Assert.Equal(_bladeId, worn.RefId);
        Assert.Empty(_store.ListAssignments(_specimenId));   // never the specimen's table
    }

    /// <summary>
    /// ⭐ Found live 2026-09-16, one call after the commander equip above succeeded:
    /// <c>GET /api/items/assignments/commander:dave</c> came back <c>[]</c>. The write gate resolved the
    /// commander onto <c>rpg_player_item_assignment</c> while the read still went only to
    /// <c>ListAssignments</c> (the specimen table), so the pouch was invisible to every reader — the FE
    /// included. One gate means one resolution on both verbs, not just the write.
    /// </summary>
    [Fact]
    public async Task The_commanders_own_pouch_is_what_the_assignments_read_returns()
    {
        var commander = "commander:dave";
        (await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = _playerId, specimenId = commander, instanceId = _bladeId, role = ArmamentPrimary,
        })).EnsureSuccessStatusCode();

        var read = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/items/assignments/{commander}?playerId={_playerId}");

        var row = Assert.Single(read.EnumerateArray());
        Assert.Equal(ArmamentPrimary, row.GetProperty("role").GetString());
        Assert.Equal(_bladeId, row.GetProperty("refId").GetString());

        // The specimen route is unaffected — the two scopes read their own tables, never each other's.
        var specimenRead = await _http.GetFromJsonAsync<JsonElement>($"/api/items/assignments/{_specimenId}");
        Assert.Empty(specimenRead.EnumerateArray());
    }

    [Fact]
    public async Task A_commander_item_binds_on_the_players_own_scope_so_the_push_carries_it()
    {
        // The write must not land with no effect: AtomPushService.OwnersForSave already pushes the Player scope to the
        // injector, so a commander binding reaches a live lawn by the same route a specimen's does.
        var commander = "commander:dave";
        (await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = _playerId, specimenId = commander, instanceId = _bladeId, role = ArmamentPrimary,
        })).EnsureSuccessStatusCode();

        var playerScope = new OwnerScope(OwnerKind.Player, _playerKey);
        var bound = Assert.Single(_store.ListBindings(playerScope));
        Assert.Equal(_bladeId, bound.InstanceId);
        Assert.Equal(ArmamentPrimary, bound.Slot);
        Assert.Contains(playerScope, AtomPushService.OwnersForSave(_store, _playerId));
    }

    [Fact]
    public async Task The_commander_unequips_through_the_same_route()
    {
        var commander = "commander:dave";
        (await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = _playerId, specimenId = commander, instanceId = _helmId, role = HeadGuard,
        })).EnsureSuccessStatusCode();

        var resp = await _http.PostAsJsonAsync("/api/items/unequip", new
        {
            playerId = _playerId, specimenId = commander, role = HeadGuard,
        });

        resp.EnsureSuccessStatusCode();
        Assert.Empty(_store.ListPlayerItemAssignments(_playerKey));
    }

    [Fact]
    public async Task One_copy_cannot_be_worn_by_the_commander_and_a_specimen_at_once()
    {
        var commander = "commander:dave";
        (await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = _playerId, specimenId = commander, instanceId = _bladeId, role = ArmamentPrimary,
        })).EnsureSuccessStatusCode();

        var resp = await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = _playerId, specimenId = _specimenId, instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("equip.already-worn", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListAssignments(_specimenId));
        Assert.Single(_store.ListPlayerItemAssignments(_playerKey));
    }

    [Fact]
    public async Task The_other_commander_is_not_this_players_to_equip()
    {
        var zomboss = "commander:zomboss";

        var resp = await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = _playerId, specimenId = zomboss, instanceId = _bladeId, role = ArmamentPrimary,
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("equip.commander-not-owned", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ListPlayerItemAssignments(_playerKey));
    }

    /// <summary>
    /// ⭐ <b>Module 3's unlock predicate is consulted on the real equip path</b>, not merely built —
    /// the wiring gap `spec-equip-assign.md` calls out by name. Driven in-process because closing a
    /// slot needs an <c>ISlotUnlockRule</c>, and nothing configures one at boot today (D2: every slot
    /// ships open, and the predicate exists so that stays reversible).
    /// </summary>
    [Fact]
    public void Equip_intoASlotTheUnlockPredicateCloses_isRefusedWithItsOwnReason()
    {
        var closed = new ItemEquipService(_store, new EquipGate(new SlotUnlock(new ClosesEverything())));

        var outcome = closed.Equip(_playerId, _specimenId, _bladeId, ArmamentPrimary);

        Assert.False(outcome.Ok);
        Assert.Contains("equip.role-locked", outcome.Reason);
        Assert.Empty(_store.ListAssignments(_specimenId));
    }

    [Fact]
    public void A_combo_binding_is_recognised_per_host_with_its_circuit_and_a_stale_one_is_not()
    {
        // SSH4.7 (spec-combo-bind §3): the read side recognises a combination binding only while its
        // deterministic instance id is a CURRENT target, re-evaluated with the ONE evaluator — never a
        // second fold of combat numbers (guard-actor-hub).
        const string gemFamily = "atom.combo-read-probe";
        const string gemContainer = "gem.combo-read-probe";
        const string comboId = "combo.strain-read-probe";

        var host = SeedComboHost("item.combo-read-host", ItemRole.ArmamentPrimary);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId(gemFamily, "", 1), KindId = "stat.derived",
            FamilyId = gemFamily, Variant = "", Tier = 1, Name = "Combo Read Probe",
            ParamsJson = "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":30}",
        }).IsOk);
        var gem = _store.SaveInstance(new InstanceRow
        {
            ContainerId = gemContainer, RollSeed = 7,
            CatalogRevision = _store.GetCatalogRevision(), Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, AtomRow.DeriveId(gemFamily, "", 1), "{\"amount\":30}") },
        });

        _store.UseEquipCombinationEvaluation(new RpgStore.EquipCombinationInputs(
            SocketTuning.Parse(File.ReadAllText(
                Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current))),
            id => string.Equals(id, gemContainer, StringComparison.Ordinal)
                ? new CardInsertLookup(new InsertDef(id, gemFamily, "", 1), "insert." + gemFamily)
                : null));
        var lookups = new ComboContainerBuild.ComboContainerLookups(_store.GetAtom);
        var built = ComboContainerBuild.TryBuild(comboId, new[] { gemFamily }, 1, lookups, out var refusal);
        Assert.NotNull(built);
        Assert.True(_store.UpsertContainer(built!).IsOk, refusal);
        _store.SeedComboRecipes(new[]
        {
            new ComboRecipe(comboId, ComboShape.Strain, "", 0, "", "", 1, 1,
                new[] { new ComboIngredient(gemFamily, 1) }, BaseFloors: new[] { 1 }),
        });

        _store.SaveAssignment(_specimenId, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, host);
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, gemContainer, gem) });
        _store.MaterializeRolledEquipRuntime(_specimenId, level: 50);

        var combo = Assert.Single(
            EquippedBoundAtoms.InputsFromStore(_store, _specimenId), i => i.ComboId is not null);
        Assert.Equal(comboId, combo.ComboId);
        Assert.Equal(0, combo.Circuit);
        Assert.Equal(ItemRoles.Id(ItemRole.ArmamentPrimary), combo.Role);
        Assert.Equal(host, combo.ItemRefId);

        // A cmb: binding with no current target contributes nothing: emptying the socket stops the word
        // firing, so the read side refuses to recognise its binding even before the reaper withdraws it.
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false) });
        Assert.DoesNotContain(
            EquippedBoundAtoms.InputsFromStore(_store, _specimenId), i => i.ComboId is not null);
    }

    /// <summary>A host whose <c>item_generation</c> row this test owns. `SeedItem` below reuses one
    /// manifest id (<c>"eq-drop"</c>) for every item it seeds, so only its FIRST call's generation row is
    /// written — which is why `GetItemGeneration(_helmId)` is null and `SocketHostFor` never resolves a
    /// host inside this class. A unique manifest id is the fix, and it is local to this test.</summary>
    string SeedComboHost(string containerId, ItemRole role)
    {
        var atomId = AtomRow.DeriveId("atom.combo-read-host-vitality", "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.modify", FamilyId = "atom.combo-read-host-vitality",
            Variant = "", Tier = 1, Name = "combo read host vitality",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        var upsert = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item, Slot = ItemRoles.Id(role),
            Rarity = Rung, Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(upsert.IsOk, upsert.ToString());
        var instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = containerId, RollSeed = 5150,
            CatalogRevision = _store.GetCatalogRevision(), Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, atomId, "{\"amount\":10}") },
        });
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = _playerKey,
            AcquiredUtc = "2026-09-22T00:00:00Z", OriginKind = "drop",
        }).IsOk);
        _store.PersistLoot(_playerKey,
            new LootManifest("combo-read-drop-" + instanceId, "table.combo-read", 11UL, ItemLevel,
                Array.Empty<LootGrant>(), Array.Empty<string>(), "{}", LootPityState.Empty,
                LootPityState.Empty, null, false, null),
            "test", "combo-read", _store.GetCatalogRevision(), 1,
            new[] { new ItemGenerationRow(instanceId, 0, BaseTypeId, RungOrdinal(), ItemLevel, Frame,
                ItemRoles.Id(role), "drop") });
        return instanceId;
    }

    sealed class ClosesEverything : ISlotUnlockRule
    {
        public bool Evaluate(ItemRole role, ActorContext actor) => false;
    }
}
