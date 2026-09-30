using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
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
/// ⛔ <b>The socket routes read an insert's REAL element</b> — the defect these tests pin, found live
/// on 2026-09-06 and fixed the same day.
///
/// <para><c>ItemSurfaceEndpoints</c> built every <see cref="InsertDef"/> with a hardcoded
/// <c>Element: ""</c> while <c>CombinationEvaluator</c> matches ingredients on
/// <c>Insert.Element</c> — so every element-shaped resonance (Pure, Ring, Eclipse, and Diversity's
/// distinct-element count) was unreachable through the combinations route no matter what the player
/// had socketed. The element was never missing content: <c>gk-data/packs/fusion/data/seed/items/gems/*.json</c> authors it
/// per gem, and <see cref="GemInsertCorpus"/> already read it for the card route.</para>
///
/// <para>Everything below runs against the <b>real</b> shipped gem corpus, the real socket tuning,
/// the real generated resonance catalog and a real store — the point of the test is that a real fire
/// gem now fires a real fire resonance, which a stub element would not prove.</para>
/// </summary>
public class ItemInsertElementTests : IAsyncLifetime
{
    // A real row in the shipped corpus: `gem.g1-001` / `atom.elemental-power` / element `fire`.
    const string FireGemA = "gem.g1-001";

    // `gem.g1-014` is the second fire row in the same file; asserted below rather than assumed, so a
    // corpus edit fails the test instead of silently weakening it.
    const string SecondFireGem = "gem.g1-014";

    const string ContainerId = "item.insert-element-blade";
    const string BaseTypeId = "item.insert-element-base-a-001";
    const string Rung = "cultivated";
    const string Frame = "humanoid";
    const int ItemLevel = 24;

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    string _playerKey = "";
    string _instanceId = "";
    SocketTuning _sockets = null!;
    Func<string, CardInsertLookup?> _gems = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerKey = _store.GetCurrentPlayerId().ToString(System.Globalization.CultureInfo.InvariantCulture);

        _sockets = SocketTuning.Parse(File.ReadAllText(Tuning(SocketTuningFiles.Current)));
        var surfaces = ItemSurfaceTuning.Parse(File.ReadAllText(Tuning("item-surfaces.v1.json")));
        var rarity = ItemRarityTuning.Parse(File.ReadAllText(Tuning("item-rarity.v1.json")));

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);
        _store.SeedRarityLadder(rarity);

        // The 25 resonances, GENERATED from the element roster exactly as the server seeds them.
        _store.SeedComboRecipes(ResonanceGenerator.Generate(_sockets));

        _gems = GemInsertCorpus.Load(Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "gems"));

        SeedItem();

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapItemSurfaces(surfaces, _sockets, _gems);
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    // ---- the corpus itself -------------------------------------------------------------------------

    [Fact]
    public void The_shipped_gem_corpus_carries_a_real_element_for_both_test_gems()
    {
        // If this goes red the fixture below is testing nothing, so it is asserted rather than assumed.
        Assert.Equal("fire", _gems(FireGemA)?.Def.Element);
        Assert.Equal("fire", _gems(SecondFireGem)?.Def.Element);
        Assert.NotEqual(_gems(FireGemA)?.Def.ContainerId, _gems(SecondFireGem)?.Def.ContainerId);
    }

    // ---- ItemSurfaceEndpoints.cs:134 — the socket fill ---------------------------------------------

    [Fact]
    public async Task Two_fire_gems_in_sockets_fire_the_pure_fire_resonance()
    {
        _store.SetSockets(_instanceId, new[]
        {
            new SocketSlot(0, "", false, FireGemA, null),
            new SocketSlot(1, "", false, SecondFireGem, null),
            new SocketSlot(2, "", false),
        });

        var rows = await Combinations();

        // `combo.pure-fire-2` is the generated Pure row at threshold 2 — real content, not a fixture.
        var pure = rows.SingleOrDefault(r => Id(r) == "combo.pure-fire-2");
        Assert.False(pure.ValueKind == JsonValueKind.Undefined,
            "combo.pure-fire-2 did not render at all — with the element hardcoded to \"\" it can never "
            + "become Active, which is the defect this test pins");
        Assert.Equal("Active", pure.GetProperty("state").GetString());
        Assert.Empty(pure.GetProperty("missingElements").EnumerateArray());
    }

    [Fact]
    public async Task One_fire_gem_names_fire_as_the_missing_element_rather_than_nothing()
    {
        _store.SetSockets(_instanceId, new[]
        {
            new SocketSlot(0, "", false, FireGemA, null),
            new SocketSlot(1, "", false),
            new SocketSlot(2, "", false),
        });
        // Held so the compendium's reveal rule lets a non-active row through at all.
        _store.AdjustStock(_playerKey, FireGemA, 1);

        var rows = await Combinations(_playerKey);

        var pure = rows.SingleOrDefault(r => Id(r) == "combo.pure-fire-2");
        Assert.False(pure.ValueKind == JsonValueKind.Undefined, "combo.pure-fire-2 was not revealed");
        Assert.Equal("OneAway", pure.GetProperty("state").GetString());
        Assert.Equal(
            new[] { "fire" },
            pure.GetProperty("missingElements").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // ---- ItemSurfaceEndpoints.cs:145 — the held ledger ---------------------------------------------

    [Fact]
    public async Task Held_fire_gems_reveal_the_fire_resonance_even_with_an_empty_item()
    {
        _store.SetSockets(_instanceId, new[]
        {
            new SocketSlot(0, "", false),
            new SocketSlot(1, "", false),
            new SocketSlot(2, "", false),
        });
        _store.AdjustStock(_playerKey, FireGemA, 2);

        // The reveal rule for a Pure row is `held.Elements.Contains(recipe.Element)` — with the held
        // ledger's element hardcoded to "" no fire row could ever be revealed by holding fire gems.
        var revealed = await Combinations(_playerKey);
        Assert.Contains(revealed, r => Id(r) == "combo.pure-fire-2");

        // Same store, no playerId: the ledger is empty by construction, so the row is not revealed.
        // This is the control that proves the assertion above is about the ledger and not the fill.
        var anonymous = await Combinations();
        Assert.DoesNotContain(anonymous, r => Id(r) == "combo.pure-fire-2");
    }

    // ---- fixture -----------------------------------------------------------------------------------

    static string Id(JsonElement row) => row.GetProperty("comboId").GetString() ?? "";

    async Task<IReadOnlyList<JsonElement>> Combinations(string? playerId = null)
    {
        var url = $"/api/items/{_instanceId}/combinations"
                  + (playerId is { Length: > 0 } ? $"?playerId={playerId}" : "");
        var rows = await _http.GetFromJsonAsync<List<JsonElement>>(url);
        return rows ?? new List<JsonElement>();
    }

    void SeedItem()
    {
        var atomId = AtomRow.DeriveId("atom.insert-element-vitality", "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom.insert-element-vitality",
            Variant = "",
            Tier = 1,
            Name = "insert element vitality",
            ParamsJson = """{"channel":"maxHp","op":"flat","amount":10}""",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = ContainerId,
            Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(ItemRole.ArmamentPrimary),
            Rarity = Rung,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        _instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = ContainerId,
            RollSeed = 909,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, atomId, """{"amount":10}""") },
        });

        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = _instanceId,
            PlayerId = _playerKey,
            AcquiredUtc = "2026-09-06T00:00:00Z",
            OriginKind = "drop",
        }).IsOk);

        _store.PersistLoot(
            _playerKey,
            new LootManifest("ie-drop", "table.insert-element", 11UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "insert-element", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(_instanceId, 0, BaseTypeId, RungOrdinal(), ItemLevel, Frame,
                    ItemRoles.Id(ItemRole.ArmamentPrimary), "drop"),
            });
    }

    int RungOrdinal() => _store.ListRarities().First(r => r.RarityId == Rung).Ordinal;

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string Tuning(string file) => Path.Combine(RepoRoot(), "data", "tuning", file);

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
