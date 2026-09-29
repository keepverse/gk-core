using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// SSH4.9-F2 re-measurement + regression. The lane that found F2 read a card's <c>insertKey</c> (a
/// DISPLAY key, `spec-sockets` §2.4) as the socket row's stored container id and concluded the mint
/// and the read path disagree. They do not: both key on the shipped gem corpus's own <c>id</c>. These
/// two tests drive the live probe's own chain through the real routes on the in-process host and pin
/// both halves of that answer:
///
/// <list type="number">
/// <item>the word FIRES once the chassis admits the recipe (the probe's chassis could not: it is a
/// shipped SET PIECE, which D21 forbids a Strain/Splice on, and the recipe the probe chose is
/// frame-pinned to `plant` while the chassis is `humanoid`) — the second test measures the set-piece
/// half on the probe's own chassis id;</item>
/// <item>the socket row carries the corpus id it was inserted from, so the read path's lookup HITS and
/// the card renders rather than refusing (<c>ItemCardEndpoints.cs</c> answers a filled socket the gem
/// catalog cannot carry with a 409 <c>item.card-unrenderable</c>, never a rendered cell).</item>
/// </list>
/// </summary>
[Collection("e2e")]
public class SocketedGemCombinationE2ETests : IAsyncLifetime
{
    /// <summary>`combo.splice-vigor-bulwark`, a SHIPPED recipe: shape `splice`, host `core-guard` /
    /// `humanoid`, `minSockets` 4, ingredients `atom.arm-hardening`, `atom.arm-plate`, `atom.bulwark`,
    /// `atom.fortitude`, tier floors `[1,1,2,2]`.</summary>
    const string SpliceComboId = "combo.splice-vigor-bulwark";

    /// <summary>A shipped `core-guard` / `humanoid` chassis with `socketMax` 4 that is NOT a member of
    /// any shipped set (`item.humanoid-torso-a-005`, the live probe's own chassis, is in 8 of them).</summary>
    const string FiringChassis = "item.humanoid-torso-b-001";

    /// <summary>The live probe's own chassis — a shipped SET PIECE.</summary>
    const string SetPieceChassis = "item.humanoid-torso-a-005";

    const string BoreRecipe = "recipe.019";     // Bore: Open Metal (humanoid)
    const string InsertRecipe = "recipe.022";   // Socket: Gem Setting (any)
    const string Rung = "cultivated";

    /// <summary>One shipped gem per ingredient family of <see cref="SpliceComboId"/>, all four real
    /// entries of `gk-data/packs/fusion/data/seed/items/gems/**`: tiers 3/2/3/2, which meet the `[1,1,2,2]` floors.</summary>
    static readonly string[] FillGems =
    {
        "gem.g2-002", // atom.arm-hardening, powerBand high
        "gem.g1-023", // atom.arm-plate,     powerBand medium
        "gem.g3-009", // atom.bulwark,       powerBand high
        "gem.g1-019", // atom.fortitude,     powerBand medium
    };

    readonly HttpClient _http;
    readonly RpgStore _store;

    /// <summary>Per-test correlation namespace: `/api/test/reset` does not clear
    /// `rpg_material_spend_log`, so a deterministic correlation id would make the second test in this
    /// class replay the first one's op (and hand back THAT chassis's sockets).</summary>
    readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public SocketedGemCombinationE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
        _store = factory.Services.GetRequiredService<RpgStore>();
    }

    public async Task InitializeAsync()
    {
        var reset = await _http.PostAsJsonAsync("/api/test/reset", new { });
        reset.EnsureSuccessStatusCode();
        SeedRarityLadder();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_socketed_gem_fires_the_word_and_binds_it_on_a_chassis_that_admits_the_recipe()
    {
        var chassis = await GrantBoredAndFilled(FiringChassis);

        // 1. The mint/read agreement this lane was sent to check: the socket row holds the CORPUS id
        //    it was inserted from — the same key `ItemSurfaceEndpoints.InsertOf` resolves through.
        var rows = _store.GetSockets(chassis);
        Assert.Equal(FillGems, rows.Select(r => r.InsertContainerId).ToArray());
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.InsertInstanceId)));

        var host = _store.SocketHostFor(chassis, _ => 4);
        Assert.NotNull(host);
        Assert.False(host.Value.IsSetPiece);
        Assert.Equal("humanoid", host.Value.Frame);
        Assert.Equal(ItemRole.CoreGuard, host.Value.Role);

        // 2. The word FIRES, read back through the real route (`CompendiumReveal` shows an Active row
        //    regardless of held stock, so an empty or non-Active answer here is a real miss).
        var row = (await Combinations(chassis)).SingleOrDefault(r => ComboId(r) == SpliceComboId);
        Assert.False(row.ValueKind == JsonValueKind.Undefined,
            $"{SpliceComboId} did not render at all on a {host.Value.Frame}/{host.Value.Role} host whose " +
            $"four sockets hold exactly its four ingredient families");
        Assert.Equal("Active", row.GetProperty("state").GetString());

        // 3. The store half of the BIND — the same call `EquippedBoundAtoms` makes per equipped host,
        //    so the fired word reaches the actor's atoms rather than only the preview.
        var targets = _store.ComboTargetsFor(chassis, rows);
        var target = Assert.Single(targets);
        Assert.Equal(0, target.Circuit);
        var containerId = ComboContainerBuild.ContainerId(SpliceComboId, row.GetProperty("grantedTier").GetInt32());
        Assert.Equal($"cmb:{chassis}#c0:{containerId}", target.ComboInstanceId);

        // 4. Equip through the real route and read the binding back off the specimen: the word is bound,
        //    not merely previewed.
        var specimenId = await GrantSpecimen();
        var equip = await _http.PostAsJsonAsync("/api/items/equip", new
        {
            playerId = 1, specimenId, instanceId = chassis, role = "core-guard",
        });
        Assert.True(equip.StatusCode == HttpStatusCode.OK, await equip.Content.ReadAsStringAsync());

        var resolution = _store.ResolveBindings(
            new OwnerScope(OwnerKind.UniqueActor, specimenId), new BindContext(RuntimeId.Battle));
        Assert.Contains(resolution.Bindings, b => b.InstanceId == target.ComboInstanceId);
    }

    /// <summary>
    /// The measurement that explains the live probe's own reading, on the live probe's own chassis: a
    /// set piece carries no Strain/Splice (D21), so the same four gems in the same four sockets fire
    /// nothing — and <c>/combinations</c> answers <c>[]</c>, because an Active row is the only row
    /// `CompendiumReveal` shows once the ingredients have been spent out of stock.
    /// </summary>
    [Fact]
    public async Task The_probes_own_set_piece_chassis_fires_no_splice_by_design()
    {
        var chassis = await GrantBoredAndFilled(SetPieceChassis);

        var host = _store.SocketHostFor(chassis, _ => 4);
        Assert.NotNull(host);
        Assert.True(host.Value.IsSetPiece, $"{SetPieceChassis} is expected to be a shipped set piece");

        // The rows still hold the four corpus ids — the inserts are legal, the BONUS is what D21 withholds.
        Assert.Equal(FillGems, _store.GetSockets(chassis).Select(r => r.InsertContainerId).ToArray());

        var combos = await Combinations(chassis);
        Assert.DoesNotContain(combos, r => ComboId(r) == SpliceComboId);
        Assert.Empty(_store.ComboTargetsFor(chassis, _store.GetSockets(chassis)));
    }

    /// <summary>
    /// SSH4.9-F5: `/api/test/reset` must clear the material spend ledger, or a later test reusing a
    /// correlation id is answered with the EARLIER test's instance — `replayed: true` and the wrong id
    /// (measured while landing the socketed-gem test: `bore 0 answered 200 with 0 rows ... "replayed":true`
    /// naming another chassis). Two real chassis, one correlation id, one reset between them.
    /// </summary>
    [Fact]
    public async Task A_reset_clears_the_spend_ledger_so_a_correlation_id_is_not_replayed()
    {
        var correlationId = $"ssh49f2-{_runId}-replay";

        var first = await GrantChassisOnly(FiringChassis);
        var before = await Bore(first, correlationId);
        Assert.False(before.GetProperty("replayed").GetBoolean(), $"the first attempt is not a replay: {before}");
        Assert.Equal(first, before.GetProperty("instanceId").GetString());

        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();

        var second = await GrantChassisOnly(FiringChassis);
        Assert.NotEqual(first, second);
        var after = await Bore(second, correlationId);

        // The defect: without the spend-ledger delete this reads `replayed: true` and names `first`, so a
        // second test sees the first test's item and its own 200 is a lie about what ran.
        Assert.False(after.GetProperty("replayed").GetBoolean(),
            $"correlation '{correlationId}' was replayed after a reset: {after}");
        Assert.Equal(second, after.GetProperty("instanceId").GetString());
        Assert.Single(_store.GetSockets(second));
    }

    // ---- the chain ---------------------------------------------------------------------------------

    /// <summary>One real `socket-add` through the route, returning the parsed outcome.</summary>
    async Task<JsonElement> Bore(string instanceId, string correlationId)
    {
        var bore = await _http.PostAsJsonAsync("/api/items/workbench/socket-add", new
        {
            playerId = 1, instanceId, recipeId = BoreRecipe, correlationId,
        });
        var body = await bore.Content.ReadAsStringAsync();
        Assert.True(bore.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// A real chassis and the materials a bore costs — the shared first half of every chain here.
    /// Separate from <see cref="GrantBoredAndFilled"/> so the reset-replay test can grant a SECOND chassis
    /// without also boring the first one out.
    /// </summary>
    async Task<string> GrantChassisOnly(string baseTypeId)
    {
        var grant = await _http.PostAsJsonAsync("/api/debug/grant-item",
            new { playerId = 1, baseTypeId, rungId = Rung });
        var body = await grant.Content.ReadAsStringAsync();
        Assert.True(grant.StatusCode == HttpStatusCode.OK, body);
        var chassis = JsonDocument.Parse(body).RootElement.GetProperty("instanceId").GetString()!;

        var materials = await _http.PostAsJsonAsync("/api/debug/grant-materials", new
        {
            playerId = 1,
            souls = 500_000,
            materials = new object[]
            {
                new { materialId = "substrate.humanoid.sound", qty = 200 },
                new { materialId = "catalyst.forge", qty = 200 },
            },
        });
        Assert.True(materials.StatusCode == HttpStatusCode.OK, await materials.Content.ReadAsStringAsync());
        return chassis;
    }

    async Task<string> GrantBoredAndFilled(string baseTypeId)
    {
        var chassis = await GrantChassisOnly(baseTypeId);

        foreach (var gem in FillGems)
        {
            var granted = await _http.PostAsJsonAsync("/api/debug/grant-gem",
                new { playerId = 1, containerId = gem, qty = 1 });
            Assert.True(granted.StatusCode == HttpStatusCode.OK, await granted.Content.ReadAsStringAsync());
        }

        for (var i = 0; i < 4; i++)
        {
            var bore = await _http.PostAsJsonAsync("/api/items/workbench/socket-add", new
            {
                playerId = 1, instanceId = chassis, recipeId = BoreRecipe, correlationId = $"ssh49f2-{_runId}-bore-{i}",
            });
            var boreBody = await bore.Content.ReadAsStringAsync();
            Assert.True(bore.StatusCode == HttpStatusCode.OK, boreBody);
            Assert.True(_store.GetSockets(chassis).Count == i + 1,
                $"bore {i} answered 200 with {_store.GetSockets(chassis).Count} rows for '{chassis}': {boreBody}");
        }

        for (var i = 0; i < FillGems.Length; i++)
        {
            var insert = await _http.PostAsJsonAsync("/api/items/workbench/socket-insert", new
            {
                playerId = 1, instanceId = chassis, recipeId = InsertRecipe,
                insertContainerId = FillGems[i], socketIndex = i, correlationId = $"ssh49f2-{_runId}-insert-{i}",
            });
            Assert.True(insert.StatusCode == HttpStatusCode.OK, await insert.Content.ReadAsStringAsync());
        }

        return chassis;
    }

    async Task<string> GrantSpecimen()
    {
        var grant = await _http.PostAsJsonAsync("/api/creatures/debug/grant",
            new { playerId = 1, speciesId = "potatomine" });
        var body = await grant.Content.ReadAsStringAsync();
        Assert.True(grant.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement
            .GetProperty("specimen").GetProperty("actor").GetProperty("instanceId").GetString()!;
    }

    async Task<IReadOnlyList<JsonElement>> Combinations(string instanceId)
    {
        var rows = await _http.GetFromJsonAsync<List<JsonElement>>(
            $"/api/items/{instanceId}/combinations?playerId=1");
        return rows ?? new List<JsonElement>();
    }

    static string ComboId(JsonElement row) => row.GetProperty("comboId").GetString() ?? "";

    /// <summary>
    /// The rarity ladder `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json` seeds in a real install (the deploy runs
    /// `AtomImporter` over the whole `gk-data/packs/fusion/data/seed` tree). This host's own boot sweep cannot see that
    /// folder — `FusionRpg.Server.csproj` carries no copy rule for `gk-data/packs/fusion/data/seed/rarity/**`, so
    /// `SeedImportRunner.FindUp` stops at the exe's partial `gk-data/packs/fusion/data/seed` — so the ladder is seeded here
    /// the same way `SocketHostForTests` seeds it, or `grant-item`'s rung lookup has nothing to pick.
    /// </summary>
    void SeedRarityLadder()
    {
        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);

        var tuning = Path.Combine(RepoRoot(), "data", "tuning", "item-rarity.v1.json");
        _store.SeedRarityLadder(ItemRarityTuning.Parse(File.ReadAllText(tuning)));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find repo root");
    }
}
