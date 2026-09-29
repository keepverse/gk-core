using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// strain-splice-host SSH1.2 (host-gate F5) — <c>ItemSurfaceEndpoints.cs</c>'s <c>/combinations</c>
/// route previously built its <see cref="SocketHost"/> with a hard-coded
/// <see cref="ItemRole.ArmamentPrimary"/>, an empty frame, and never a set piece, regardless of what
/// the item actually was. A helm previewed a weapon's own Strains/Splices, and D21's set-piece
/// exclusion never applied to any real item. Both tests below drive a fully-satisfied recipe through
/// the real HTTP route — a shape that renders as <c>Active</c> on any ordinary host, bypassing the
/// compendium's reveal rule entirely (<c>CompendiumReveal.Render</c> never gates an Active row on held
/// stock), so the only thing that can make it vanish is the host itself.
/// </summary>
public class SocketHostForTests : IAsyncLifetime
{
    // A real row in the shipped gem corpus: family `atom.elemental-power`, element `fire`, tier 3.
    const string FireGem = "gem.g1-001";

    const string HelmContainerId = "item.host-gate-helm";
    const string HelmBaseTypeId = "item.host-gate-helm-base-a-001";
    const string SetPieceContainerId = "item.host-gate-set-piece";
    const string SetPieceBaseTypeId = "item.host-gate-set-piece-base-a-001";
    const string Rung = "cultivated";
    const string Frame = "humanoid";
    const int ItemLevel = 24;
    const string SpliceComboId = "combo.host-gate-splice";
    const string StrainComboId = "combo.host-gate-strain";
    const string IngredientFamily = "atom.elemental-power";

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    string _playerKey = "";
    string _helmInstanceId = "";
    string _setPieceInstanceId = "";

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerKey = _store.GetCurrentPlayerId().ToString(System.Globalization.CultureInfo.InvariantCulture);

        var surfaces = ItemSurfaceTuning.Parse(File.ReadAllText(Tuning("item-surfaces.v1.json")));
        var sockets = SocketTuning.Parse(File.ReadAllText(Tuning(SocketTuningFiles.Current)));
        var rarity = ItemRarityTuning.Parse(File.ReadAllText(Tuning("item-rarity.v1.json")));
        var gems = GemInsertCorpus.Load(Path.Combine(RepoRoot(), "data", "seed", "items", "gems"));

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);
        _store.SeedRarityLadder(rarity);

        // A Splice pinned to `core-guard`/`humanoid` -- unreachable through the OLD hard-coded
        // ArmamentPrimary/empty-frame host no matter what the fill was, since HostAdmits would refuse
        // by role before the multiset is even checked. One real fire-gem insert fully satisfies it.
        var splice = new ComboRecipe(
            SpliceComboId, ComboShape.Splice, "", 0, ItemRoles.Id(ItemRole.CoreGuard), Frame, 1, 1,
            new[] { new ComboIngredient(IngredientFamily, 1) }, BaseFloors: new[] { 1 });

        // A Strain with NO role/frame pin -- the SAME one-insert fill fully satisfies it too. On any
        // ordinary host it would be Active (proven by CombinationEvaluatorTests' own single-ingredient
        // Strain fixtures); D21 forbids it outright on a set piece.
        var strain = new ComboRecipe(
            StrainComboId, ComboShape.Strain, "", 0, "", "", 1, 1,
            new[] { new ComboIngredient(IngredientFamily, 1) }, BaseFloors: new[] { 1 });

        _store.SeedComboRecipes(new[] { splice, strain });

        SeedItem(HelmContainerId, HelmBaseTypeId, ItemRoles.Id(ItemRole.CoreGuard), out _helmInstanceId);
        SeedItem(SetPieceContainerId, SetPieceBaseTypeId, ItemRoles.Id(ItemRole.CoreGuard), out _setPieceInstanceId);

        _store.ImportSetCorpus(new[]
        {
            new SetDef(
                "set.host-gate-test", "Host Gate Test Set",
                new[] { new SetMemberDef(SetPieceContainerId, ItemRole.CoreGuard, ItemFrame.Humanoid) },
                Array.Empty<SetTierDef>()),
        });

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapItemSurfaces(surfaces, sockets, gems);
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Both hosts wear one real fire gem -- the same fully-satisfying fill on both, so the ONLY
        // variable between the two tests below is the host's own role/frame/set-piece facts.
        _store.SetSockets(_helmInstanceId, new[] { new SocketSlot(0, "", false, FireGem, null) });
        _store.SetSockets(_setPieceInstanceId, new[] { new SocketSlot(0, "", false, FireGem, null) });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task A_core_guard_pinned_splice_fires_on_a_real_core_guard_host()
    {
        var rows = await Combinations(_helmInstanceId);

        var splice = rows.SingleOrDefault(r => Id(r) == SpliceComboId);
        Assert.False(splice.ValueKind == JsonValueKind.Undefined,
            $"{SpliceComboId} did not render at all -- the OLD hard-coded ArmamentPrimary/empty-frame " +
            "host would have refused a core-guard-pinned recipe before the fill was even checked");
        Assert.Equal("Active", splice.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_set_piece_never_previews_a_strain_even_fully_satisfied()
    {
        var rows = await Combinations(_setPieceInstanceId);

        // A fully-satisfying fill is Active on ANY ordinary host (proven by the sibling helm test
        // above, and by CombinationEvaluatorTests' own Strain fixtures) -- Active bypasses the
        // compendium's reveal rule entirely, so the ONLY way this row is absent is D21's set-piece
        // exclusion actually reaching the evaluator, which the OLD hard-coded `IsSetPiece: false`
        // host could never do for any real item.
        var strain = rows.SingleOrDefault(r => Id(r) == StrainComboId);
        Assert.True(strain.ValueKind == JsonValueKind.Undefined,
            $"{StrainComboId} rendered on a real set piece -- D21's exclusion did not reach the evaluator");
    }

    [Fact]
    public void SocketHostFor_returns_null_for_an_instance_with_no_generation_row()
    {
        Assert.Null(_store.SocketHostFor("instance.does-not-exist", _ => null));
    }

    // ---- plumbing ------------------------------------------------------------------------------------

    static string Id(JsonElement row) => row.GetProperty("comboId").GetString() ?? "";

    async Task<IReadOnlyList<JsonElement>> Combinations(string instanceId)
    {
        var rows = await _http.GetFromJsonAsync<List<JsonElement>>($"/api/items/{instanceId}/combinations");
        return rows ?? new List<JsonElement>();
    }

    void SeedItem(string containerId, string baseTypeId, string role, out string instanceId)
    {
        var atomId = AtomRow.DeriveId("atom.host-gate-vitality", "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom.host-gate-vitality",
            Variant = "",
            Tier = 1,
            Name = "host gate vitality",
            ParamsJson = """{"channel":"maxHp","op":"flat","amount":10}""",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Slot = role,
            Rarity = Rung,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = containerId,
            RollSeed = 909,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, atomId, """{"amount":10}""") },
        });

        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId,
            PlayerId = _playerKey,
            AcquiredUtc = "2026-09-06T00:00:00Z",
            OriginKind = "drop",
        }).IsOk);

        _store.PersistLoot(
            _playerKey,
            new LootManifest("host-gate-drop-" + containerId, "table.host-gate", 11UL, ItemLevel,
                Array.Empty<LootGrant>(), Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty,
                null, false, null),
            "test", "host-gate", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(instanceId, 0, baseTypeId, RungOrdinal(), ItemLevel, Frame, role, "drop"),
            });
    }

    int RungOrdinal() => _store.ListRarities().First(r => r.RarityId == Rung).Ordinal;

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
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
