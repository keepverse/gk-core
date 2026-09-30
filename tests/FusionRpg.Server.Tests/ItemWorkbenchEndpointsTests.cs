using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Gems;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using FusionRpg.Data.Tests;
using FusionRpg.Server;

namespace FusionRpg.Server.Tests;

/// <summary>
/// ⭐ item modules 14/15/16 — <b>the workbench executor</b>, against a real in-process host, a real
/// store, the real shipped tuning and the real shipped recipe corpus.
///
/// <para>All three modules shipped their Core half green and all three recorded the same blocker:
/// <c>TrySpendRecipe</c>, <c>AppendMutationOp</c> and <c>SetSockets</c> had <b>zero production
/// callers</b>, so no path anywhere ran debit → act → persist on one stored item. These tests drive
/// that cycle through the HTTP surface for each of the three modules' own verbs and then <b>read the
/// state back</b> — a computed result that is never stored is exactly the thing this task exists to
/// stop reporting as done.</para>
///
/// <para>Every assertion about a price is made against <see cref="MaterialRecipeCatalog.Resolve"/>'s
/// own answer rather than against a number copied out of the tuning: the claim under test is "the
/// debit equals the resolved price", which a hardcoded quantity would turn into "the tuning still
/// says 4".</para>
/// </summary>
[Trait("VerificationId", "server.item-workbench")]
public class ItemWorkbenchEndpointsTests : IAsyncLifetime
{
    const string Rung = "cultivated";
    const string BaseTypeId = "item.workbench-base-a-001";
    const string ContainerId = "item.workbench-blade";
    const string Frame = "humanoid";
    const int ItemLevel = 24;
    const int SocketMax = 4;

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;
    string _playerKey = "";
    string _instanceId = "";
    string _wordSpecimen = "";

    MaterialTuning _materials = null!;
    MaterialRecipeCatalog _recipes = null!;
    EnhancementTuning _enhancement = null!;
    SocketTuning _sockets = null!;
    PowerTuning _power = null!;
    // species-gear-chain T24: the host's craft-wear inputs, built in InitializeAsync exactly as
    // Program.cs builds them (shipped corpus + shipped tuning + the ladder).
    DeploymentHierarchyTuning _deployment = null!;
    CraftWearSource _craftWear = null!;
    ItemWorkbench _bench = null!;
    readonly ITestOutputHelper _out;

    public ItemWorkbenchEndpointsTests(ITestOutputHelper output) => _out = output;

    // ---- fixture -----------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _playerKey = _playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _materials = MaterialTuning.Parse(File.ReadAllText(Tuning(SocketTuningFiles.Materials)));
        _enhancement = EnhancementTuning.Parse(File.ReadAllText(Tuning("enhancement.v1.json")));
        _sockets = SocketTuning.Parse(File.ReadAllText(Tuning(SocketTuningFiles.Current)));
        // species-gear-chain T22: socket-insert mints at the ladder pin through the REAL shipped
        // power tuning — the same file production loads — so the frozen scale is the tuned one.
        _power = PowerTuningLoader.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));
        var rarity = ItemRarityTuning.Parse(File.ReadAllText(Tuning("item-rarity.v1.json")));
        _recipes = MaterialRecipeCatalog.Load(
            Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "seed", "items", "recipes"), "*.json")
                .OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText),
            _materials);

        // The ladder's ROWS have to exist before a container may name a rung (ContainerValidator's
        // own check); `SeedRarityLadder` seeds the rarity_budget keys beside them, and the workbench
        // reads none of those directly — the card does.
        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);
        _store.SeedRarityLadder(rarity);

        SeedItem();

        // A tiny in-memory corpus rather than the shipped `gems/*.json`, because the
        // fixture's own gems are not shipped ids. Every entry carries its mint Seed beside its
        // evaluator Def (species-gear-chain T22) — same band behind both, so the derived tier and
        // the minted atom can never disagree the way two separate loads could.
        var gemCorpus = GemInsertCorpus.From(new Dictionary<string, CardInsertLookup>(StringComparer.Ordinal)
        {
            ["gem.workbench-ember-t1"] = new CardInsertLookup(
                new InsertDef("gem.workbench-ember-t1", "atom.elemental-power", "fire", 1),
                "gem.ember-shard", "Ember Shard",
                new GemSeed("gem.workbench-ember-t1", "gem.ember-shard", "Ember Shard",
                    "atom.elemental-power", "fire", "trivial", null, Array.Empty<string>())),
            // species-gear-chain T9: the forge-gem ladder under test — tier 2 in, tier 3 out,
            // same family; plus a cross-family tier-2 decoy the verb must refuse.
            ["gem.workbench-t2"] = new CardInsertLookup(
                new InsertDef("gem.workbench-t2", "atom.elemental-power", "fire", 2),
                "gem.workbench-t2", "Workbench tier-2 power",
                new GemSeed("gem.workbench-t2", "gem.workbench-t2", "Workbench tier-2 power",
                    "atom.elemental-power", "fire", "low", null, Array.Empty<string>())),
            ["gem.g1-001"] = new CardInsertLookup(
                new InsertDef("gem.g1-001", "atom.elemental-power", "fire", 3),
                "gem.g1-001", "Workbench tier-3 power",
                new GemSeed("gem.g1-001", "gem.g1-001", "Workbench tier-3 power",
                    "atom.elemental-power", "fire", "medium", null, Array.Empty<string>())),
            // species-gear-chain T22: corpus-known but UNBUILDABLE — family "atom.other" has no
            // atom row in SeedItem, so the mint refuses it by name (the content-gap arm).
            ["gem.workbench-other-t2"] = new CardInsertLookup(
                new InsertDef("gem.workbench-other-t2", "atom.other", "fire", 2),
                "gem.workbench-other-t2", "Workbench other-family tier-2",
                new GemSeed("gem.workbench-other-t2", "gem.workbench-other-t2", "Workbench other-family tier-2",
                    "atom.other", "fire", "low", null, Array.Empty<string>())),
            // The whole-loop test's gem: same atom as ember (one catalog row, two container ids —
            // stacking's own shape), so the loop proves a second id mints off one row.
            ["gem.workbench-loop-t1"] = new CardInsertLookup(
                new InsertDef("gem.workbench-loop-t1", "atom.elemental-power", "fire", 1),
                "gem.workbench-loop-t1", "Workbench loop ember",
                new GemSeed("gem.workbench-loop-t1", "gem.workbench-loop-t1", "Workbench loop ember",
                    "atom.elemental-power", "fire", "trivial", null, Array.Empty<string>())),
        });

        // species-gear-chain T24: the host's craft-wear wiring, verbatim from Program.cs — the class
        // off the shipped base-type corpus, the rung off the ladder, the rate off the shipped key.
        _deployment = DeploymentHierarchyTuningLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "deployment-hierarchy.v5.json")));
        // species-gear-chain T23: the repair verb reads D1's destroy chance off the Hub, exactly as
        // Program.cs leaves it configured in production. Individual tests may re-configure it to make
        // the roll deterministic and restore the shipped value afterwards.
        DeploymentHierarchyTuningHub.Configure(_deployment);
        var classForBaseType = BaseTypeSocketMaxCorpus.LoadClassById(
            Path.Combine(RepoRoot(), "data", "seed", "items", "base-types"));
        _craftWear = new CraftWearSource(
            (baseTypeId, rungIndex) =>
                classForBaseType(baseTypeId) is { Length: > 0 } cls &&
                rungIndex >= 0 && rungIndex < RarityLadder.RungIds.Count
                    ? DurabilityTable.DeriveMax(
                        new HeadDerivationEntry(baseTypeId, cls, Array.Empty<string>()),
                        RarityLadder.RungIds[rungIndex], _deployment)
                    : null,
            _deployment.CraftWearPerAttemptMilli,
            // species-gear-chain T61: the fixture mirrors Program.cs, so it wires the potential max the
            // same way. Without it `CraftWearFor` returns null and no craft could ever wear.
            PotentialMaxFor: (baseTypeId, rungIndex) =>
                classForBaseType(baseTypeId) is { Length: > 0 } cls2 &&
                rungIndex >= 0 && rungIndex < RarityLadder.RungIds.Count
                    ? PotentialTable.DeriveMax(
                        new HeadDerivationEntry(baseTypeId, cls2, Array.Empty<string>()),
                        RarityLadder.RungIds[rungIndex], _deployment)
                    : null);

        var bench = new ItemWorkbench(
            _store, _materials, _recipes, _enhancement, _sockets,
            BaseTypeSocketMaxCorpus.From(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [BaseTypeId] = SocketMax,
            }),
            // item-content `item-naming` T4: the gem corpus the bench resolves an insert's element AND
            // name through, exactly as `Program.cs` hands it in.
            gemCorpus,
            // species-gear-chain T22: socket-insert's real mint, wired exactly as `Program.cs`
            // wires it — catalog rows off the store, seeds off the single corpus above, the real
            // shipped tuning. A bench without these refuses inserts as mint-unavailable (own test).
            lookupAtom: id => _store.GetAtom(id),
            lookupAffix: id => _store.GetAffix(id),
            lookupGemSeed: id => gemCorpus(id)?.Seed,
            gemPowerTuning: _power,
            // species-gear-chain T24: the host's own craft-wear wiring — the class off the shipped
            // base-type corpus, the rung off the instance's ladder index, the rate off the shipped
            // deployment-hierarchy key. Exactly what Program.cs builds.
            craftWear: _craftWear);
        _bench = bench;

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapWorkbench(bench);
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    static string Tuning(string file) => Path.Combine(RepoRoot(), "data", "tuning", file);

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A real container, a real frozen instance, a real ownership row and a real generation
    /// stamp — the four things a workbench verb reads off "a stored item".</summary>
    void SeedItem()
    {
        var coreAtomId = AtomRow.DeriveId("atom.workbench-vitality", "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = coreAtomId,
            KindId = "stat.modify",
            FamilyId = "atom.workbench-vitality",
            Variant = "",
            Tier = 1,
            Name = "workbench vitality",
            ParamsJson = """{"channel":"maxHp","op":"flat","amount":10}""",
        }).IsOk);

        var drawnAtomId = AtomRow.DeriveId("atom.workbench-ember", "fire", 2);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = drawnAtomId,
            KindId = "stat.modify",
            FamilyId = "atom.workbench-ember",
            Variant = "fire",
            Tier = 2,
            Name = "workbench ember",
            ParamsJson = """{"channel":"atk","op":"flat","amount":5}""",
        }).IsOk);

        // species-gear-chain T22: the fixture gems' own atoms, so socket-insert's real mint
        // resolves them out of the store exactly as production resolves the shipped catalog —
        // stat.modify/flat, the shape the host's own atoms already prove instantiable. Tier t
        // carries amount 5·t; "atom.other" is DELIBERATELY absent (the unbuildable-gem refusal
        // below needs a corpus-known family the catalog never authored).
        foreach (var (family, tier, amount) in new[]
        {
            ("atom.elemental-power", 1, 5L),
            ("atom.elemental-power", 2, 10L),
            ("atom.elemental-power", 3, 15L),
        })
        {
            Assert.True(_store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId(family, "fire", tier),
                KindId = "stat.modify",
                FamilyId = family,
                Variant = "fire",
                Tier = tier,
                Name = $"workbench gem power t{tier}",
                ParamsJson = "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":" + amount + "}",
            }).IsOk);
        }

        var upsert = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = ContainerId,
            Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(ItemRole.ArmamentPrimary),
            Rarity = Rung,
            Atoms = new[] { new ContainerAtomRow(0, coreAtomId) },
        });
        Assert.True(upsert.IsOk, upsert.ToString());

        _instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = ContainerId,
            RollSeed = 4242,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[]
            {
                new InstanceAtomRow(0, coreAtomId, """{"amount":10}"""),
                new InstanceAtomRow(1, drawnAtomId, """{"amount":5}"""),
            },
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
            new LootManifest("wb-drop", "table.workbench", 7UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "workbench", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(_instanceId, 0, BaseTypeId, RungOrdinal(), ItemLevel, Frame,
                    ItemRoles.Id(ItemRole.ArmamentPrimary), "drop"),
            });
    }

    int RungOrdinal() => _store.ListRarities().First(r => r.RarityId == Rung).Ordinal;

    int RungIndex() => RarityLadder.RungIds.ToList().IndexOf(Rung);

    RecipeContext ItemContext(int enhanceLevel = 0) =>
        new(RungIndex(), IlvlTierLadder.MaxTierAt(ItemLevel), ItemLevel, Frame, enhanceLevel);

    /// <summary>Fund exactly the resolved price and not a unit more, so "sufficient" and "insufficient"
    /// are one material apart rather than separated by a comfortable float.</summary>
    void Fund(IReadOnlyList<MaterialCostLine> lines, long extraSouls = 0)
    {
        var souls = lines.Where(l => l.Class == MaterialClass.Souls).Sum(l => l.Qty) + extraSouls;
        if (souls > 0) _store.AwardSouls(_playerId, souls, "test.fund", Guid.NewGuid().ToString("N"));

        var materials = lines
            .Where(l => l.Class != MaterialClass.Souls)
            .Select(l => (l.MaterialId, l.Qty))
            .ToList();
        if (materials.Count > 0) _store.GrantMaterials(_playerId, materials);
    }

    long Balance(string materialId) => _store.GetMaterialQty(_playerId, materialId);

    async Task<(HttpStatusCode Status, JsonElement Body)> Post(string verb, object body)
    {
        var resp = await _http.PostAsJsonAsync($"/api/items/workbench/{verb}", body);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    // ---- module 15 `enhance-reroll` — the enhance verb ---------------------------------------------

    [Fact]
    public async Task Enhance_withoutTheMaterials_isRefusedAndSpendsNothing()
    {
        var price = _recipes.Resolve("recipe.012", ItemContext());
        var substrate = price.First(l => l.Class == MaterialClass.Substrate);

        // One short of the price on exactly one leg. Every other leg is fully funded, so a refusal
        // here can only be the leg that is short.
        Fund(price);
        _store.GrantMaterials(_playerId, new[] { (substrate.MaterialId, -1L) });
        var before = Balance(substrate.MaterialId);
        var soulsBefore = _store.GetSoulBalance(_playerId).Balance;

        var (status, body) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
            correlationId = "wb-enhance-short",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("materials.insufficient", body.GetProperty("reason").GetString());

        // A refusal writes NOTHING — not the souls leg that came first in the fixed spend order,
        // not the op row, not the head.
        Assert.Equal(before, Balance(substrate.MaterialId));
        Assert.Equal(soulsBefore, _store.GetSoulBalance(_playerId).Balance);
        Assert.Empty(_store.ReadMutationOps(_instanceId));
        Assert.Equal(0, _store.GetInstanceMutationHead(_instanceId)!.EnhanceLevel);
    }

    [Fact]
    public async Task Enhance_withTheMaterials_debitsPersistsAndReadsBack()
    {
        var price = _recipes.Resolve("recipe.012", ItemContext());
        Fund(price);

        var soulsBefore = _store.GetSoulBalance(_playerId).Balance;
        var materialBefore = price
            .Where(l => l.Class != MaterialClass.Souls)
            .ToDictionary(l => l.MaterialId, l => Balance(l.MaterialId), StringComparer.Ordinal);

        var (status, body) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
            correlationId = "wb-enhance-1",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        // +1 sits in the Safe band at 1000‰, so the outcome is deterministic and this asserts the
        // real decision rather than tolerating either answer.
        Assert.Equal("success", body.GetProperty("outcome").GetString());
        Assert.Equal(1, body.GetProperty("enhanceLevel").GetInt32());
        Assert.Equal(1, body.GetProperty("opSeq").GetInt32());

        // 1. the debit is real, and it is exactly the resolved price
        foreach (var line in price.Where(l => l.Class != MaterialClass.Souls))
            Assert.Equal(materialBefore[line.MaterialId] - line.Qty, Balance(line.MaterialId));
        Assert.Equal(
            soulsBefore - price.Where(l => l.Class == MaterialClass.Souls).Sum(l => l.Qty),
            _store.GetSoulBalance(_playerId).Balance);

        // 2. the operation persisted — SECOND READ, from the store rather than the response
        var head = _store.GetInstanceMutationHead(_instanceId)!;
        Assert.Equal(1, head.EnhanceLevel);
        Assert.Equal(1, head.MutationSeq);
        Assert.NotNull(head.StateHash);
        Assert.NotNull(head.OriginValuesJson);   // D2 rung 1', written lazily at the first mutation

        // 3. the op ledger carries the spend beside the result (D2 clause 11)
        var op = Assert.Single(_store.ReadMutationOps(_instanceId));
        Assert.Equal(MutationOpKind.Enhance, op.Kind);
        Assert.Equal("wb-enhance-1", op.CorrelationId);
        Assert.Equal("success", op.Result.Outcome);
        Assert.Contains("\"qty\"", op.CostJson);
        foreach (var line in price)
            Assert.Contains($"\"qty\":{line.Qty}", op.CostJson);

        // 4. the spend log has exactly one row for the whole operation
        Assert.Equal(1, _store.CountMaterialSpendLog(_playerId));
    }

    [Fact]
    public async Task Enhance_retriedOnTheSameCorrelation_spendsNothingASecondTime()
    {
        var price = _recipes.Resolve("recipe.012", ItemContext());
        Fund(price);

        var first = await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
            correlationId = "wb-enhance-retry",
        });
        Assert.True(first.Status == HttpStatusCode.OK, first.Body.ToString());

        var soulsAfterFirst = _store.GetSoulBalance(_playerId).Balance;
        var materialsAfterFirst = price
            .Where(l => l.Class != MaterialClass.Souls)
            .ToDictionary(l => l.MaterialId, l => Balance(l.MaterialId), StringComparer.Ordinal);

        var second = await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
            correlationId = "wb-enhance-retry",
        });

        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.True(second.Body.GetProperty("replayed").GetBoolean());
        Assert.Equal(soulsAfterFirst, _store.GetSoulBalance(_playerId).Balance);
        foreach (var (id, qty) in materialsAfterFirst) Assert.Equal(qty, Balance(id));

        // The level moved once, not twice, and the ledger has one row.
        Assert.Equal(1, _store.GetInstanceMutationHead(_instanceId)!.EnhanceLevel);
        Assert.Single(_store.ReadMutationOps(_instanceId));
        Assert.Equal(1, _store.CountMaterialSpendLog(_playerId));
    }

    [Fact]
    public async Task Enhance_withARecipeForAnotherVerb_isRefusedByName()
    {
        Fund(_recipes.Resolve("recipe.012", ItemContext()));

        var (status, body) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019",  // a `bore`
            correlationId = "wb-enhance-wrong-verb",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("material.operation-mismatch", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ReadMutationOps(_instanceId));
    }

    [Fact]
    public async Task Enhance_withoutACorrelationId_is400()
    {
        var resp = await _http.PostAsJsonAsync("/api/items/workbench/enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// ⛔ <b>species-gear-chain T39 — the free ward is retired.</b> A stale client that still sends
    /// <c>wardLoaded: true</c> gets the named rule and nothing else: no level change, no debit and no op
    /// row. Fully funded beforehand, so a path that honoured the flag would show up as a spend.
    /// </summary>
    [Fact]
    public async Task Enhance_withWardLoadedTrue_isRefusedByNameAndWritesNothing()
    {
        var price = _recipes.Resolve("recipe.012", ItemContext());
        Fund(price);
        var soulsBefore = _store.GetSoulBalance(_playerId).Balance;
        var materialsBefore = price
            .Where(l => l.Class != MaterialClass.Souls)
            .ToDictionary(l => l.MaterialId, l => Balance(l.MaterialId), StringComparer.Ordinal);

        var (status, body) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
            correlationId = "wb-ward-retired", wardLoaded = true,
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("enhance.ward-flag-retired", body.GetProperty("reason").GetString());

        // Refused before anything ran: no souls spent, no material spent, no op row, no spend-log row.
        Assert.Equal(soulsBefore, _store.GetSoulBalance(_playerId).Balance);
        foreach (var (id, qty) in materialsBefore) Assert.Equal(qty, Balance(id));
        Assert.Empty(_store.ReadMutationOps(_instanceId));
        Assert.Equal(0, _store.GetInstanceMutationHead(_instanceId)!.EnhanceLevel);
        Assert.Equal(0, _store.CountMaterialSpendLog(_playerId));
    }

    /// <summary>
    /// The other half of T39: <c>wardLoaded: false</c> (what the shipped web client sends on EVERY
    /// enhance) and an absent key are both accepted and ignored — the flag never reaches
    /// <c>EnhanceContext.WardLoaded</c>.
    /// </summary>
    [Fact]
    public async Task Enhance_withWardLoadedFalseOrAbsent_isAcceptedAndIgnored()
    {
        // An explicit `false` — the shipped client's usual value.
        Fund(_recipes.Resolve("recipe.012", ItemContext()));
        var (falseStatus, falseBody) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
            correlationId = "wb-ward-false", wardLoaded = false,
        });
        Assert.Equal(HttpStatusCode.OK, falseStatus);
        Assert.Equal(1, falseBody.GetProperty("enhanceLevel").GetInt32());

        // No key at all.
        Fund(_recipes.Resolve("recipe.012", ItemContext(enhanceLevel: 1)));
        var (absentStatus, absentBody) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
            correlationId = "wb-ward-absent",
        });
        Assert.Equal(HttpStatusCode.OK, absentStatus);
        Assert.Equal(2, absentBody.GetProperty("enhanceLevel").GetInt32());
        Assert.Equal(2, _store.ReadMutationOps(_instanceId).Count);
    }

    // ---- species-gear-chain T24 (wire): craft wear through the real endpoint ------------------------

    /// <summary>The first shipped base type carrying BOTH a class (durability's derivation input) and a
    /// socketMax (proof it is a real equipment base type). Picked by scanning the corpus, so neither an
    /// id nor a count is pinned.</summary>
    static (string Id, string Class) FirstRealBaseType()
    {
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "data", "seed", "items", "base-types"), "*.json",
                     SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;
            foreach (var e in entries.EnumerateArray())
            {
                if (e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                    e.TryGetProperty("class", out var cls) && cls.ValueKind == JsonValueKind.String &&
                    e.TryGetProperty("socketMax", out var mx) && mx.ValueKind == JsonValueKind.Number)
                    return (id.GetString()!, cls.GetString()!);
            }
        }

        throw new InvalidOperationException("no shipped base type carries class + socketMax");
    }

    /// <summary>A second instance on a REAL base type, owned by the player and stamped as rolled
    /// equipment — the shape the wear derivation reads.</summary>
    string SeedRealBaseTypeInstance(string baseTypeId)
    {
        var id = _store.SaveInstance(new InstanceRow
        {
            ContainerId = ContainerId,
            RollSeed = 777,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[]
            {
                new InstanceAtomRow(0, AtomRow.DeriveId("atom.workbench-vitality", "", 1), """{"amount":10}"""),
            },
        });
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = id, PlayerId = _playerKey, AcquiredUtc = "2026-09-19T00:00:00Z", OriginKind = "drop",
        }).IsOk);
        _store.PersistLoot(_playerKey,
            new LootManifest("t24-wire-" + id, "table.workbench", 9UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "workbench", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(id, 0, baseTypeId, RungOrdinal(), ItemLevel, Frame,
                    ItemRoles.Id(ItemRole.ArmamentPrimary), "drop"),
            });
        return id;
    }

    void FundTemper() => Fund(_recipes.Resolve("recipe.012",
        new RecipeContext(RungIndex(), IlvlTierLadder.MaxTierAt(ItemLevel), ItemLevel, Frame, 0)));

    /// <summary>
    /// ⭐ species-gear-chain T24 (wire) — past potential exhaustion the REAL endpoint decays durability,
    /// read back through the normal store read rather than from the response.
    /// </summary>
    [Fact]
    public async Task Enhance_pastPotentialExhaustion_decaysDurabilityThroughTheRealEndpoint()
    {
        var (baseTypeId, _) = FirstRealBaseType();
        var id = SeedRealBaseTypeInstance(baseTypeId);
        var max = _craftWear.MaxFor(baseTypeId, RungIndex())!.Value;
        _store.SetDurability(id, max, max);
        _store.SetPotential(id, 10, 0);   // exhausted ⇒ Stage 2
        FundTemper();

        var (status, _) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = id, recipeId = "recipe.012", correlationId = "t24-wire-1",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var wear = CraftRiskPolicy.WearFor(max, _deployment.CraftWearPerAttemptMilli);
        Assert.True(wear > 0, "the shipped craft-wear key must actually wear");
        Assert.Equal(max, _store.GetDurability(id)!.Max);            // max never moves on a craft
        Assert.Equal(max - wear, _store.GetDurability(id)!.Current);
    }

    /// <summary>T10's stage is unchanged: while potential remains, the craft leaves durability alone.</summary>
    [Fact]
    public async Task Enhance_withPotentialRemaining_leavesDurabilityUntouched()
    {
        var (baseTypeId, _) = FirstRealBaseType();
        var id = SeedRealBaseTypeInstance(baseTypeId);
        var max = _craftWear.MaxFor(baseTypeId, RungIndex())!.Value;
        _store.SetDurability(id, max, max);
        _store.SetPotential(id, 10, 10);   // Assured
        FundTemper();

        var (status, _) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = id, recipeId = "recipe.012", correlationId = "t24-wire-2",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(max, _store.GetDurability(id)!.Current);
    }

    // ---- species-gear-chain T23: the workbench repair -----------------------------------------------

    /// <summary>The shipped repair row's own id, read off the corpus rather than pinned.</summary>
    static string RepairRecipeId() =>
        Catalog().Recipes.Values.First(r => r.Operation == CraftOperation.Repair).RecipeId;

    static MaterialRecipeCatalog Catalog() => MaterialRecipeCatalog.Load(
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "seed", "items", "recipes"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText),
        MaterialTuning.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Materials))));

    void FundRepair()
    {
        var recipeId = RepairRecipeId();
        Fund(_recipes.Resolve(recipeId,
            new RecipeContext(RungIndex(), IlvlTierLadder.MaxTierAt(ItemLevel), ItemLevel, Frame, 0)));
    }

    /// <summary>An item half-worn on a real base type, already owning a derived durability pair.</summary>
    string WornRealBaseTypeInstance(out long max)
    {
        var (baseTypeId, _) = FirstRealBaseType();
        var id = SeedRealBaseTypeInstance(baseTypeId);
        max = _craftWear.MaxFor(baseTypeId, RungIndex())!.Value;
        _store.SetDurability(id, max, max / 2);
        return id;
    }

    /// <summary>
    /// ⭐ species-gear-chain T23 — a worn item is restored through the REAL endpoint, and the state is
    /// read back through the normal store read. The destroy chance is pinned to 0 (then restored) so the
    /// assertion tests the restore rather than tolerating either outcome.
    /// </summary>
    [Fact]
    public async Task Repair_restoresDurabilityThroughTheRealEndpoint()
    {
        var id = WornRealBaseTypeInstance(out var max);
        DeploymentHierarchyTuningHub.Configure(_deployment with { RepairDestroyChanceMilli = 0 });
        try
        {
            FundRepair();
            var (status, body) = await Post("repair", new
            {
                playerId = _playerId, instanceId = id, recipeId = RepairRecipeId(), correlationId = "t23-repair-1",
            });

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("repaired", body.GetProperty("outcome").GetString());
            Assert.Equal(max, _store.GetDurability(id)!.Current);    // full coverage, full cap
            Assert.Equal("owned", _store.GetItem(id)!.Disposition);
            Assert.Single(_store.ReadMutationOps(id));
        }
        finally { DeploymentHierarchyTuningHub.Configure(_deployment); }
    }

    /// <summary>
    /// ⭐ D1 through the endpoint: a certain destroy chance takes the item. The op still records the
    /// attempt, and the disposition moves through the salvage path's own value.
    /// </summary>
    [Fact]
    public async Task Repair_destroysTheItemWhenTheAttemptRollsItsChance()
    {
        var id = WornRealBaseTypeInstance(out _);
        var before = _store.GetDurability(id)!.Current;
        DeploymentHierarchyTuningHub.Configure(_deployment with { RepairDestroyChanceMilli = 1000 });
        try
        {
            FundRepair();
            var (status, body) = await Post("repair", new
            {
                playerId = _playerId, instanceId = id, recipeId = RepairRecipeId(),
                correlationId = "t23-repair-destroy",
            });

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("destroyed", body.GetProperty("outcome").GetString());
            Assert.Equal("destroyed", _store.GetItem(id)!.Disposition);
            // The attempt is recorded, and it cost the item rather than the pair.
            var op = Assert.Single(_store.ReadMutationOps(id));
            Assert.Equal(MutationOpKind.Repair, op.Kind);
            Assert.Equal("destroyed", op.Result.Outcome);
            Assert.Equal(before, _store.GetDurability(id)!.Current);
        }
        finally { DeploymentHierarchyTuningHub.Configure(_deployment); }
    }

    [Fact]
    public async Task Repair_retriedOnTheSameCorrelation_spendsAndDestroysOnce()
    {
        var id = WornRealBaseTypeInstance(out var max);
        DeploymentHierarchyTuningHub.Configure(_deployment with { RepairDestroyChanceMilli = 0 });
        try
        {
            FundRepair();
            var first = await Post("repair", new
            {
                playerId = _playerId, instanceId = id, recipeId = RepairRecipeId(), correlationId = "t23-repair-retry",
            });
            Assert.True(first.Status == HttpStatusCode.OK, first.Body.ToString());

            var soulsAfter = _store.GetSoulBalance(_playerId).Balance;
            var second = await Post("repair", new
            {
                playerId = _playerId, instanceId = id, recipeId = RepairRecipeId(), correlationId = "t23-repair-retry",
            });

            Assert.Equal(HttpStatusCode.OK, second.Status);
            Assert.True(second.Body.GetProperty("replayed").GetBoolean());
            Assert.Equal(soulsAfter, _store.GetSoulBalance(_playerId).Balance);
            Assert.Single(_store.ReadMutationOps(id));
            Assert.Equal(max, _store.GetDurability(id)!.Current);
        }
        finally { DeploymentHierarchyTuningHub.Configure(_deployment); }
    }

    // ---- species-gear-chain T26: rarity promotion ----------------------------------------------------

    /// <summary>The shipped promote row's own id, read off the corpus rather than pinned.</summary>
    static string PromoteRecipeId() =>
        Catalog().Recipes.Values.First(r => r.Operation == CraftOperation.Elevate).RecipeId;

    /// <summary>The instance's CURRENT rung index — off `item_generation`, which promotion rewrites,
    /// never off the container, which is where every item off it started.</summary>
    int InstanceRungIndex(string id) =>
        RarityLadder.RungIndexOf(_store.ListRarities()
            .First(r => r.Ordinal == _store.GetItemGeneration(id)!.RarityOrdinal).RarityId);

    /// <summary>Fund exactly the price the item's CURRENT rung resolves, so each climb is paid for.</summary>
    void FundPromoteAt(string id) => Fund(_recipes.Resolve(PromoteRecipeId(),
        new RecipeContext(InstanceRungIndex(id), IlvlTierLadder.MaxTierAt(ItemLevel), ItemLevel, Frame, 0)));

    /// <summary>
    /// ⭐ T26 — promotion through the REAL endpoint: the rung moves, every affix survives identically,
    /// and the mark records where the item started.
    /// </summary>
    [Fact]
    public async Task Promote_movesTheRungAndRecordsTheSourceThroughTheRealEndpoint()
    {
        var (baseTypeId, _) = FirstRealBaseType();
        var id = SeedRealBaseTypeInstance(baseTypeId);
        var atomsBefore = _store.GetInstance(id)!.Atoms.Select(a => (a.Seq, a.AtomId, a.ValuesJson)).ToList();
        var fromOrdinal = _store.GetItemGeneration(id)!.RarityOrdinal;
        FundPromoteAt(id);

        var (status, body) = await Post("promote", new
        {
            playerId = _playerId, instanceId = id, recipeId = PromoteRecipeId(), correlationId = "t26-promote-1",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("promoted", body.GetProperty("outcome").GetString());

        // Additive: every affix is byte-identical, and no suppression or append was recorded.
        Assert.Equal(atomsBefore, _store.GetInstance(id)!.Atoms.Select(a => (a.Seq, a.AtomId, a.ValuesJson)).ToList());
        var op = Assert.Single(_store.ReadMutationOps(id));
        Assert.Equal(MutationOpKind.Promotion, op.Kind);
        Assert.Empty(op.Result.Suppressed);
        Assert.Empty(op.Result.Appended);

        // The rung moved and the mark is where it started.
        Assert.NotEqual(fromOrdinal, _store.GetItemGeneration(id)!.RarityOrdinal);
        Assert.Equal(fromOrdinal, _store.GetItemGeneration(id)!.PromotedFromOrdinal);
    }

    /// <summary>
    /// ⭐ T26 — promotion climbs exactly ONE rung per operation all the way to the ladder's top, and
    /// the top then refuses cleanly by name. Driving the whole climb through the real endpoint is the
    /// strongest available proof that a second promotion does not repeat the first (the rung is read
    /// off `item_generation`, which the op rewrites) and that `OneRungAbove`'s throw is unreachable.
    /// </summary>
    [Fact]
    public async Task Promote_climbsOneRungPerOperationAndRefusesCleanlyAtTheTop()
    {
        var (baseTypeId, _) = FirstRealBaseType();
        var id = SeedRealBaseTypeInstance(baseTypeId);
        var start = InstanceRungIndex(id);
        var climbs = 0;

        while (!RarityLadder.IsTopRung(RarityLadder.RungIds[InstanceRungIndex(id)]))
        {
            var before = InstanceRungIndex(id);
            FundPromoteAt(id);
            var (status, body) = await Post("promote", new
            {
                playerId = _playerId, instanceId = id, recipeId = PromoteRecipeId(),
                correlationId = $"t26-climb-{climbs}",
            });

            Assert.True(status == HttpStatusCode.OK, body.ToString());
            Assert.Equal(before + 1, InstanceRungIndex(id));   // exactly one rung, never two
            climbs++;
        }

        Assert.Equal(RarityLadder.RungCount - 1 - start, climbs);

        // The top refuses cleanly, by name, and writes no further op — the ladder's throw is never reached.
        var opsBefore = _store.ReadMutationOps(id).Count;
        FundPromoteAt(id);
        var (topStatus, topBody) = await Post("promote", new
        {
            playerId = _playerId, instanceId = id, recipeId = PromoteRecipeId(), correlationId = "t26-climb-top",
        });

        Assert.Equal(HttpStatusCode.Conflict, topStatus);
        Assert.Contains("promote.top-rung", topBody.GetProperty("reason").GetString());
        Assert.Equal(opsBefore, _store.ReadMutationOps(id).Count);
    }

    /// <summary>
    /// ⭐ T26 — every `elevate` recipe the corpus authors executes end to end, each on a FRESH
    /// instance so one climb cannot borrow another's rung. The recipe COUNT is never pinned (it is a
    /// content population that grows when content ships): the loop reads the corpus and executes
    /// whatever `elevate` rows it carries.
    /// </summary>
    [Fact]
    public async Task Every_authored_elevate_recipe_executesEndToEnd()
    {
        var elevate = Catalog().Recipes.Values
            .Where(r => r.Operation == CraftOperation.Elevate)
            .OrderBy(r => r.RecipeId, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(elevate);

        var (baseTypeId, _) = FirstRealBaseType();
        var n = 0;
        foreach (var recipe in elevate)
        {
            var id = SeedRealBaseTypeInstance(baseTypeId);
            var rung = InstanceRungIndex(id);
            Fund(_recipes.Resolve(recipe.RecipeId,
                new RecipeContext(rung, IlvlTierLadder.MaxTierAt(ItemLevel), ItemLevel, Frame, 0)));

            var (status, body) = await Post("promote", new
            {
                playerId = _playerId, instanceId = id, recipeId = recipe.RecipeId,
                correlationId = $"t26-all-{n++}",
            });

            Assert.True(status == HttpStatusCode.OK, $"{recipe.RecipeId}: {body}");
            Assert.Equal("promoted", body.GetProperty("outcome").GetString());
            Assert.Equal(rung + 1, InstanceRungIndex(id));
        }
    }

    /// <summary>A replay costs once and moves the rung once.</summary>    [Fact]
    public async Task Promote_retriedOnTheSameCorrelation_movesTheRungOnce()
    {
        var (baseTypeId, _) = FirstRealBaseType();
        var id = SeedRealBaseTypeInstance(baseTypeId);
        FundPromoteAt(id);

        var first = await Post("promote", new
        {
            playerId = _playerId, instanceId = id, recipeId = PromoteRecipeId(), correlationId = "t26-promote-retry",
        });
        Assert.True(first.Status == HttpStatusCode.OK, first.Body.ToString());
        var rung = _store.GetItemGeneration(id)!.RarityOrdinal;
        var soulsAfter = _store.GetSoulBalance(_playerId).Balance;

        var second = await Post("promote", new
        {
            playerId = _playerId, instanceId = id, recipeId = PromoteRecipeId(), correlationId = "t26-promote-retry",
        });

        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.True(second.Body.GetProperty("replayed").GetBoolean());
        Assert.Equal(rung, _store.GetItemGeneration(id)!.RarityOrdinal);
        Assert.Equal(soulsAfter, _store.GetSoulBalance(_playerId).Balance);
        Assert.Single(_store.ReadMutationOps(id));
    }

    // ---- module 16 `sockets` — the socket write ----------------------------------------------------

    [Fact]
    public async Task SocketAdd_withoutTheMaterials_isRefusedAndWritesNoSocketRow()
    {
        var (status, body) = await Post("socket-add", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019",
            correlationId = "wb-bore-short",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("insufficient", body.GetProperty("reason").GetString());
        Assert.Empty(_store.GetSockets(_instanceId));
        Assert.Empty(_store.ReadMutationOps(_instanceId));
    }

    [Fact]
    public async Task SocketAdd_withTheMaterials_debitsAndPersistsARealSocketRow()
    {
        var price = _recipes.Resolve("recipe.019", ItemContext());
        Fund(price);
        var soulsBefore = _store.GetSoulBalance(_playerId).Balance;
        var materialBefore = price
            .Where(l => l.Class != MaterialClass.Souls)
            .ToDictionary(l => l.MaterialId, l => Balance(l.MaterialId), StringComparer.Ordinal);

        var (status, _) = await Post("socket-add", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019",
            correlationId = "wb-bore-1",
        });

        Assert.Equal(HttpStatusCode.OK, status);

        foreach (var line in price.Where(l => l.Class != MaterialClass.Souls))
            Assert.Equal(materialBefore[line.MaterialId] - line.Qty, Balance(line.MaterialId));
        Assert.Equal(
            soulsBefore - price.Where(l => l.Class == MaterialClass.Souls).Sum(l => l.Qty),
            _store.GetSoulBalance(_playerId).Balance);

        // SECOND READ — item_socket is the SSOT (D2 §6), so this is the state, not a projection.
        var slots = Assert.Single(_store.GetSockets(_instanceId));
        Assert.Equal(0, slots.Index);
        Assert.True(slots.Crafted);          // D24: only a crafted socket may later be imbued
        Assert.Equal("", slots.Affinity);
        Assert.True(slots.IsEmpty);

        // The op is the audit receipt beside it (D2 clause 13), never the state.
        var op = Assert.Single(_store.ReadMutationOps(_instanceId));
        Assert.Equal(MutationOpKind.SocketAdd, op.Kind);
        Assert.Equal(0, op.Result.EnhanceLevelDelta);
    }

    [Fact]
    public async Task SocketAdd_withNoBaseTypeSocketMax_refusesByNameRatherThanGuessing()
    {
        // The same executor with no base-type lookup at all — module 6's missing table, reproduced.
        var blind = new ItemWorkbench(_store, _materials, _recipes, _enhancement, _sockets);
        Fund(_recipes.Resolve("recipe.019", ItemContext()));

        var outcome = blind.SocketAdd(_playerId, _instanceId, "recipe.019", "wb-bore-blind");

        Assert.False(outcome.Ok);
        Assert.Contains("socket.base-type-socket-max-unavailable", outcome.Reason);
        Assert.Empty(_store.GetSockets(_instanceId));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SocketInsert_consumesTheInsertFromStockAndFillsTheSocket()
    {
        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        Assert.Equal(HttpStatusCode.OK, (await Post("socket-add", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019",
            correlationId = "wb-bore-for-insert",
        })).Status);

        const string gem = "gem.workbench-ember-t1";
        _store.AdjustStock(_playerKey, gem, 1);
        var price = _recipes.Resolve("recipe.022", ItemContext());
        Fund(price);
        var soulsBefore = _store.GetSoulBalance(_playerId).Balance;

        var (status, _) = await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.022",
            insertContainerId = gem, correlationId = "wb-insert-1",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            soulsBefore - price.Where(l => l.Class == MaterialClass.Souls).Sum(l => l.Qty),
            _store.GetSoulBalance(_playerId).Balance);

        var slot = Assert.Single(_store.GetSockets(_instanceId));
        Assert.Equal(gem, slot.InsertContainerId);
        Assert.False(slot.IsEmpty);

        // The insert left stock in the SAME transaction — otherwise one gem fills every socket.
        Assert.DoesNotContain(_store.ListStock(_playerKey), s => s.ContainerId == gem && s.Qty > 0);
    }

    /// <summary>
    /// strain-splice-host SSH1.4, host-gate ruling 1 (§4 row 1) enforced through the REAL workbench:
    /// socketing composes at the binding layer (RpgStore.Sockets.cs's own doc comment already says so
    /// for every socket-* op), so filling a Strain must leave the host's own
    /// <see cref="InstanceRow.ContentFingerprint"/> and <c>item_generation.rarity_ordinal</c> byte-
    /// identical. Proven against a Strain that actually FIRES (read back through the real evaluator on
    /// the real post-insert socket state) -- otherwise "unchanged" would be trivially true of a no-op.
    /// </summary>
    [Fact]
    public async Task Filling_a_strain_leaves_the_host_fingerprint_and_rarity_unchanged()
    {
        _store.SeedComboRecipes(new[]
        {
            new ComboRecipe("combo.ssh14-strain", ComboShape.Strain, "", 0, "", "", 1, 1,
                new[] { new ComboIngredient("atom.elemental-power", 1) }, BaseFloors: new[] { 1 }),
        });

        var fingerprintBefore = _store.GetInstance(_instanceId)!.ContentFingerprint();
        var rarityBefore = _store.GetItemGeneration(_instanceId)!.RarityOrdinal;

        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        Assert.Equal(HttpStatusCode.OK, (await Post("socket-add", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019",
            correlationId = "wb-ssh14-bore",
        })).Status);

        const string gem = "gem.workbench-ember-t1";
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));

        var (status, _) = await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.022",
            insertContainerId = gem, correlationId = "wb-ssh14-insert",
        });
        Assert.Equal(HttpStatusCode.OK, status);

        // Prove the Strain actually fired against the REAL post-insert socket state.
        var host = new SocketHost(ContainerId, ItemRole.ArmamentPrimary, Frame, 1);
        var fill = _store.GetSockets(_instanceId)
            .Where(s => !s.IsEmpty)
            .Select(s => new SocketFill(s.Index, s.Affinity, new InsertDef(gem, "atom.elemental-power", "fire", 1)))
            .ToList();
        var active = CombinationEvaluator.Evaluate(host, fill, _store.GetComboRecipes(), _sockets);
        Assert.Contains(active, r => r.ComboId == "combo.ssh14-strain");

        Assert.Equal(fingerprintBefore, _store.GetInstance(_instanceId)!.ContentFingerprint());
        Assert.Equal(rarityBefore, _store.GetItemGeneration(_instanceId)!.RarityOrdinal);
    }

    [Fact]
    public async Task SocketInsert_withoutHoldingTheInsert_isRefused()
    {
        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        await Post("socket-add", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019",
            correlationId = "wb-bore-for-missing-insert",
        });
        Fund(_recipes.Resolve("recipe.022", ItemContext()));

        var (status, body) = await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.022",
            insertContainerId = "gem.not-held.t1", correlationId = "wb-insert-missing",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("socket.insert-not-held", body.GetProperty("reason").GetString());
        Assert.True(_store.GetSockets(_instanceId).Single().IsEmpty);
    }

    // ---- module 14 `salvage-craft` — the upcycle and salvage verbs ---------------------------------

    [Fact]
    public async Task Upcycle_withoutTheMaterials_isRefusedAndMintsNothing()
    {
        var recipe = _recipes.Recipes["recipe.005"];
        var output = recipe.OutputRef!;
        var before = Balance(output);

        var (status, body) = await Post("upcycle", new
        {
            playerId = _playerId, recipeId = "recipe.005", correlationId = "wb-upcycle-short",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("materials.insufficient", body.GetProperty("reason").GetString());
        Assert.Equal(before, Balance(output));
        Assert.Equal(0, _store.CountMaterialSpendLog(_playerId));
    }

    [Fact]
    public async Task Upcycle_withTheMaterials_debitsTheInputAndMintsTheOutput()
    {
        var recipe = _recipes.Recipes["recipe.005"];
        var price = _recipes.Resolve("recipe.005", UpcycleContext(recipe.Frame));
        Fund(price);

        var input = price.First(l => l.Class == MaterialClass.Substrate);
        var inputBefore = Balance(input.MaterialId);
        var outputBefore = Balance(recipe.OutputRef!);

        var (status, body) = await Post("upcycle", new
        {
            playerId = _playerId, recipeId = "recipe.005", correlationId = "wb-upcycle-1",
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("upcycled", body.GetProperty("outcome").GetString());

        // SECOND READ: the grinder took the input and produced the output, both persisted.
        Assert.Equal(inputBefore - input.Qty, Balance(input.MaterialId));
        Assert.Equal(outputBefore + recipe.OutputQty, Balance(recipe.OutputRef!));
        Assert.Equal(1, _store.CountMaterialSpendLog(_playerId));

        // ⭐ R2's direction, observed end to end: the upcycle spends strictly more units than it mints.
        Assert.True(input.Qty > recipe.OutputQty,
            $"upcycle minted {recipe.OutputQty} for {input.Qty} — a conversion that is not a loss is a faucet");
    }

    /// <summary>
    /// ⭐ Proves the executor's <c>MaterialHasNoRung</c> constant is not a silent assumption:
    /// <c>upcycle</c> has no rung leg in <c>materials.v1.json</c>, so the rung index it is handed
    /// cannot change the price. If a balance pass ever adds one, this goes red on the day it happens
    /// rather than the day a player notices.
    /// </summary>
    [Fact]
    public void Upcycle_cost_is_invariant_across_every_rung_index()
    {
        foreach (var recipeId in _recipes.Recipes.Values
                     .Where(r => r.Operation == CraftOperation.Upcycle)
                     .Select(r => r.RecipeId))
        {
            var frame = _recipes.Recipes[recipeId].Frame;
            var baseline = _recipes.Resolve(recipeId, UpcycleContext(frame));
            for (var rung = 0; rung < RarityLadder.RungIds.Count; rung++)
            {
                var priced = _recipes.Resolve(recipeId, UpcycleContext(frame) with { TargetRungIndex = rung });
                Assert.Equal(baseline, priced);
            }
        }
    }

    static RecipeContext UpcycleContext(string frame) => new(0, IlvlTierLadder.MinTier, 0, frame, 0);

    [Fact]
    public async Task Salvage_returnsMaterialsAndRetiresTheItem()
    {
        var expected = SalvagePolicy.Yield(
            new SalvageInput(RungIndex(), ItemLevel, Frame, 1,
                new Dictionary<string, int>(StringComparer.Ordinal) { ["fire"] = 1 }, 0),
            _materials);
        Assert.NotEmpty(expected);

        var before = expected.ToDictionary(l => l.MaterialId, l => Balance(l.MaterialId), StringComparer.Ordinal);

        var (status, body) = await Post("salvage", new { playerId = _playerId, instanceId = _instanceId });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("salvaged", body.GetProperty("outcome").GetString());

        // SECOND READ: the yield landed and the item is gone from the armoury as an owned thing.
        foreach (var line in expected)
            Assert.Equal(before[line.MaterialId] + line.Qty, Balance(line.MaterialId));
        Assert.Equal("salvaged", _store.GetItem(_instanceId)!.Disposition);
        Assert.Contains(_store.ListItemEvents(_instanceId), e => e.Kind == "salvaged");

        // Salvage never mints currency.
        Assert.Equal(0, _store.GetSoulBalance(_playerId).Balance);
    }

    [Fact]
    public async Task Salvage_twice_isRefusedAndGrantsNothingTheSecondTime()
    {
        Assert.Equal(HttpStatusCode.OK,
            (await Post("salvage", new { playerId = _playerId, instanceId = _instanceId })).Status);

        var after = _store.GetSockets(_instanceId);   // untouched either way
        var substrate = MaterialCatalog.SubstrateId(Frame, _materials.GradeForItemLevel(ItemLevel));
        var before = Balance(substrate);

        var (status, body) = await Post("salvage", new { playerId = _playerId, instanceId = _instanceId });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("item.not-owned: '" + _instanceId + "' is 'salvaged'", body.GetProperty("reason").GetString());
        Assert.Equal(before, Balance(substrate));
        Assert.Equal(after.Count, _store.GetSockets(_instanceId).Count);
    }

    [Fact]
    public async Task Salvage_aLockedItem_isRefused()
    {
        var item = _store.GetItem(_instanceId)!;
        _store.SaveItem(item with { Locked = true });
        var substrate = MaterialCatalog.SubstrateId(Frame, _materials.GradeForItemLevel(ItemLevel));
        var before = Balance(substrate);

        var (status, body) = await Post("salvage", new { playerId = _playerId, instanceId = _instanceId });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("item.locked", body.GetProperty("reason").GetString());
        Assert.Equal(before, Balance(substrate));
        Assert.Equal("owned", _store.GetItem(_instanceId)!.Disposition);
    }

    // ---- the gate every verb shares ----------------------------------------------------------------

    [Fact]
    public async Task AnItemAnotherPlayerOwns_isRefusedForEveryVerb()
    {
        var other = _store.CreatePlayer("other").Id;
        Fund(_recipes.Resolve("recipe.012", ItemContext()));

        var (enhanceStatus, enhanceBody) = await Post("enhance", new
        {
            playerId = other, instanceId = _instanceId, recipeId = "recipe.012",
            correlationId = "wb-not-mine",
        });
        Assert.Equal(HttpStatusCode.Conflict, enhanceStatus);
        Assert.Contains("item.not-owned", enhanceBody.GetProperty("reason").GetString());

        var (salvageStatus, salvageBody) = await Post("salvage", new { playerId = other, instanceId = _instanceId });
        Assert.Equal(HttpStatusCode.Conflict, salvageStatus);
        Assert.Contains("item.not-owned", salvageBody.GetProperty("reason").GetString());

        Assert.Equal("owned", _store.GetItem(_instanceId)!.Disposition);
        Assert.Empty(_store.ReadMutationOps(_instanceId));
    }

    [Fact]
    public async Task AnUnknownInstance_isRefusedRatherThanCrashing()
    {
        var (status, body) = await Post("salvage", new { playerId = _playerId, instanceId = "no-such-instance" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("item.unknown", body.GetProperty("reason").GetString());
    }

    /// <summary>
    /// ⛔ <b>Checkpoint 4's own criterion, on one item:</b> craft (a socket bored) → enhance → socket
    /// (an insert set) → salvage, in that order, each through the real endpoint, each debiting real
    /// balances, and every step's state read back from the store afterwards.
    /// </summary>
    [Fact]
    public async Task TheWholeLoopRunsOnOneItem_craftEnhanceSocketSalvage()
    {
        // 1 — craft: bore a socket.
        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        Assert.Equal(HttpStatusCode.OK, (await Post("socket-add", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019", correlationId = "loop-bore",
        })).Status);

        // 2 — enhance: +0 → +1.
        Fund(_recipes.Resolve("recipe.012", ItemContext()));
        Assert.Equal(HttpStatusCode.OK, (await Post("enhance", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012", correlationId = "loop-enhance",
        })).Status);

        // 3 — socket: set an insert the player holds.
        const string gem = "gem.workbench-loop-t1";
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext(enhanceLevel: 1)));
        Assert.Equal(HttpStatusCode.OK, (await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.022", insertContainerId = gem,
            correlationId = "loop-insert",
        })).Status);

        // The state after three operations, read back from the store.
        Assert.Equal(1, _store.GetInstanceMutationHead(_instanceId)!.EnhanceLevel);
        Assert.Equal(gem, _store.GetSockets(_instanceId).Single().InsertContainerId);
        Assert.Equal(3, _store.ReadMutationOps(_instanceId).Count);
        Assert.Equal(new[] { 1, 2, 3 }, _store.ReadMutationOps(_instanceId).Select(o => o.Seq).ToArray());
        Assert.Equal(3, _store.CountMaterialSpendLog(_playerId));

        // 4 — salvage: the same item back into materials, priced off the state the loop produced.
        var yieldBefore = SalvagePolicy.Yield(
            new SalvageInput(RungIndex(), ItemLevel, Frame, 1,
                new Dictionary<string, int>(StringComparer.Ordinal) { ["fire"] = 1 }, EnhanceLevel: 1),
            _materials);
        var before = yieldBefore.ToDictionary(l => l.MaterialId, l => Balance(l.MaterialId), StringComparer.Ordinal);

        Assert.Equal(HttpStatusCode.OK,
            (await Post("salvage", new { playerId = _playerId, instanceId = _instanceId })).Status);

        foreach (var line in yieldBefore)
            Assert.Equal(before[line.MaterialId] + line.Qty, Balance(line.MaterialId));
        Assert.Equal("salvaged", _store.GetItem(_instanceId)!.Disposition);
    }

    // ---- item-content `item-naming` T4 — the two READ routes a picker needs -------------------------

    /// <summary>
    /// ⛔ <b>Until 2026-09-06 NO route served the recipe corpus.</b> Thirty rows shipped in
    /// <c>material_recipe</c> and both benches asked the player to TYPE <c>recipe.014</c> —
    /// <c>GET /api/recipes</c> is the PvZ fusion table and a different thing entirely.
    ///
    /// <para>Two claims, and the second is the one that makes the picker safe: every row carries the
    /// corpus's own AUTHORED name (which <c>MaterialRecipeCatalog.Load</c> also dropped until today),
    /// and the list is <see cref="ItemWorkbench.Recipes"/> itself — so a row a picker offers can never
    /// be one the very next POST refuses with <c>material.recipe-unknown</c>.</para>
    /// </summary>
    [Fact]
    public async Task Recipes_areServedWithTheirAuthoredNamesAndAreExactlyWhatTheBenchPricesAgainst()
    {
        var resp = await _http.GetAsync("/api/items/workbench/recipes");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();

        // The route's list IS the executor's corpus — not a copy, not a subset.
        Assert.Equal(_recipes.Recipes.Count, rows.Count);
        Assert.Equal(
            _recipes.Recipes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            rows.Select(r => r.GetProperty("recipeId").GetString()!).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // Every shipped row authors a name, and no name is its own id wearing a different hat.
        Assert.All(rows, r =>
        {
            var name = r.GetProperty("name").GetString()!;
            Assert.NotEqual("", name);
            Assert.DoesNotContain("recipe.", name, StringComparison.Ordinal);
        });

        var temper = rows.Single(r => r.GetProperty("recipeId").GetString() == "recipe.014");
        Assert.Equal("Temper: Ultimate Enhancement", temper.GetProperty("name").GetString());
        Assert.Equal("temper", temper.GetProperty("operation").GetString());
    }

    /// <summary>The verb filter, so a temper picker never offers a bore recipe the executor would
    /// refuse for the control the player is actually looking at.</summary>
    [Fact]
    public async Task Recipes_narrowToOneVerbSoAPickerCannotOfferTheWrongOne()
    {
        var resp = await _http.GetAsync("/api/items/workbench/recipes?operation=bore");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("bore", r.GetProperty("operation").GetString()));
        Assert.Equal(
            _recipes.Recipes.Values.Count(r => CraftOperations.Id(r.Operation) == "bore"),
            rows.Count);
    }

    /// <summary>
    /// P8.5 — the CONTEXT filter. A craft bench asks "what can I run on THIS piece", which is a frame
    /// question, and the route answers it without the client re-filtering and without a second route.
    /// The verb filter still composes with it, and the typed-`recipeId` refusal path is untouched —
    /// `Recipes_refuseATypedIdTheCorpusDoesNotCarry` and the POST-level refusals still hold.
    /// </summary>
    [Fact]
    public async Task Recipes_narrowToAFrameSoABenchOnlyOffersWhatItCanRunOnThisPiece()
    {
        static bool Fits(MaterialRecipe r, string frame) =>
            string.Equals(r.Frame, frame, StringComparison.Ordinal) ||
            string.Equals(r.Frame, "any", StringComparison.Ordinal);

        var expected = _recipes.Recipes.Values.Where(r => Fits(r, "humanoid")).ToList();
        Assert.NotEmpty(expected);
        // The frame-agnostic verbs survive, or a humanoid bench loses every temper it can run.
        Assert.Contains(expected, r => CraftOperations.Id(r.Operation) == "temper");
        Assert.Contains(expected, r => string.Equals(r.Frame, "humanoid", StringComparison.Ordinal));

        var resp = await _http.GetAsync("/api/items/workbench/recipes?frame=humanoid");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(expected.Count, rows.Count);
        Assert.All(rows, r => Assert.True(Fits(
            _recipes.Recipes[r.GetProperty("recipeId").GetString()!], "humanoid")));

        // An unknown frame still sees every frame-agnostic verb (a bench on an exotic frame can still
        // temper) and NOTHING frame-specific — never the whole corpus.
        var other = await _http.GetAsync("/api/items/workbench/recipes?frame=nonexistent-frame");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using var otherDoc = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
        var otherRows = otherDoc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(otherRows);
        Assert.All(otherRows, r => Assert.Equal("any", r.GetProperty("frame").GetString()));
        Assert.Equal(
            _recipes.Recipes.Values.Count(r => string.Equals(r.Frame, "any", StringComparison.Ordinal)),
            otherRows.Count);
        Assert.True(otherRows.Count < _recipes.Recipes.Count);

        // And it composes with the verb filter instead of replacing it.
        var composed = await _http.GetAsync("/api/items/workbench/recipes?operation=temper&frame=humanoid");
        using var composedDoc = JsonDocument.Parse(await composed.Content.ReadAsStringAsync());
        var composedRows = composedDoc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(composedRows);
        Assert.All(composedRows, r =>
        {
            Assert.Equal("temper", r.GetProperty("operation").GetString());
            Assert.True(Fits(_recipes.Recipes[r.GetProperty("recipeId").GetString()!], "humanoid"));
        });
        Assert.Equal(
            _recipes.Recipes.Values.Count(r =>
                CraftOperations.Id(r.Operation) == "temper" && Fits(r, "humanoid")),
            composedRows.Count);
    }

    /// <summary>
    /// The other half of T4: the socket bench's insert field was a free-text box asking for a
    /// container id, so a player had to know <c>gem.workbench-ember-t1</c> existed before they could
    /// socket it. This serves what they actually hold, named through the gem corpus.
    /// </summary>
    [Fact]
    public async Task HeldInserts_areServedWithTheirAuthoredNamesAndOnlyWhatThePlayerHolds()
    {
        var empty = await _http.GetAsync($"/api/items/workbench/inserts/{_playerKey}");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using (var none = JsonDocument.Parse(await empty.Content.ReadAsStringAsync()))
            Assert.Empty(none.RootElement.EnumerateArray());

        _store.AdjustStock(_playerKey, "gem.workbench-ember-t1", 3);
        // Not an insert: a material in the same stock table must not reach an insert picker.
        _store.AdjustStock(_playerKey, "substrate.humanoid.crude", 9);

        var resp = await _http.GetAsync($"/api/items/workbench/inserts/{_playerKey}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = Assert.Single(doc.RootElement.EnumerateArray().ToList());

        Assert.Equal("gem.workbench-ember-t1", row.GetProperty("containerId").GetString());
        Assert.Equal("Ember Shard", row.GetProperty("name").GetString());
        Assert.Equal("fire", row.GetProperty("element").GetString());
        Assert.Equal(3, row.GetProperty("qty").GetInt64());
    }

    /// <summary>
    /// The socket cell in an operation's own reply carries the insert's authored name too, so the
    /// bench never prints <c>gem.workbench-ember-t1</c> where a name belongs. Driven through the real
    /// <c>socket-insert</c> verb rather than asserted on a hand-built DTO.
    /// </summary>
    [Fact]
    public async Task SocketInsert_replyNamesTheInsertItSet()
    {
        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        Assert.Equal(HttpStatusCode.OK, (await Post("socket-add", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019",
            correlationId = "wb-bore-for-name",
        })).Status);

        const string gem = "gem.workbench-ember-t1";
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));

        var (status, reply) = await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.022",
            insertContainerId = gem, correlationId = "wb-insert-named",
        });
        Assert.Equal(HttpStatusCode.OK, status);

        var filled = reply.GetProperty("sockets").EnumerateArray()
            .Single(sck => sck.GetProperty("insert").GetString() == gem);
        Assert.Equal("Ember Shard", filled.GetProperty("insertName").GetString());
    }

    // ---- species-gear-chain T9 — the forge-gem verb ---------------------------------------------

    /// <summary>
    /// The whole point: three tier-2 of a family become the tier-3 the recipe names, priced by the
    /// row's own forge-gem legs at the OUTPUT tier, in one transaction — over the REAL recipe.068
    /// the deterministic emitter shipped, not a hand-built row.
    /// </summary>
    [Fact]
    public async Task ForgeGem_upcyclesThreeOfAFamilyIntoTheNamedNextTier()
    {
        const string input = "gem.workbench-t2";
        const string output = "gem.g1-001";
        _store.AdjustStock(_playerKey, input, 3);
        Fund(_recipes.Resolve("recipe.068", new RecipeContext(3, 3, 0, "any", 0)));

        var (status, _) = await Post("forge-gem", new
        {
            playerId = _playerId, recipeId = "recipe.068", insertContainerId = input,
            correlationId = "wb-forge-gem",
        });
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal(1, StockOf(output));
        Assert.Equal(0, StockOf(input));
    }

    [Fact]
    public async Task ForgeGem_refusesCrossFamilyInputAndSkippedRungs()
    {
        _store.AdjustStock(_playerKey, "gem.workbench-other-t2", 3);
        Fund(_recipes.Resolve("recipe.068", new RecipeContext(3, 3, 0, "any", 0)));
        // Refusals are 409 with the named rule (Render's own contract), never a bare 400.
        Assert.Equal(HttpStatusCode.Conflict, (await Post("forge-gem", new
        {
            playerId = _playerId, recipeId = "recipe.068",
            insertContainerId = "gem.workbench-other-t2", correlationId = "wb-forge-cross",
        })).Status);

        // Same family, wrong step: tier 1 cannot jump to tier 3.
        _store.AdjustStock(_playerKey, "gem.workbench-ember-t1", 3);
        Assert.Equal(HttpStatusCode.Conflict, (await Post("forge-gem", new
        {
            playerId = _playerId, recipeId = "recipe.068",
            insertContainerId = "gem.workbench-ember-t1", correlationId = "wb-forge-skip",
        })).Status);
    }

    [Fact]
    public async Task ForgeGem_refusesShortStockAndReplaysIdempotently()
    {
        const string input = "gem.workbench-t2";
        _store.AdjustStock(_playerKey, input, 2);
        Fund(_recipes.Resolve("recipe.068", new RecipeContext(3, 3, 0, "any", 0)));
        Assert.Equal(HttpStatusCode.Conflict, (await Post("forge-gem", new
        {
            playerId = _playerId, recipeId = "recipe.068", insertContainerId = input,
            correlationId = "wb-forge-short",
        })).Status);

        _store.AdjustStock(_playerKey, input, 1);
        var first = await Post("forge-gem", new
        {
            playerId = _playerId, recipeId = "recipe.068", insertContainerId = input,
            correlationId = "wb-forge-replay",
        });
        var second = await Post("forge-gem", new
        {
            playerId = _playerId, recipeId = "recipe.068", insertContainerId = input,
            correlationId = "wb-forge-replay",
        });
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(1, StockOf("gem.g1-001"));
    }

    long StockOf(string containerId) => _store.ListStock(_playerKey)
        .Where(s => s.ContainerId == containerId).Sum(s => s.Qty);

    // ---- species-gear-chain T13 — the milestone append ------------------------------------------

    static readonly IReadOnlyDictionary<(string Family, int Tier), (string AtomId, long Min, long Max)> MilestoneRows =
        LoadMilestoneRows();

    static readonly IReadOnlySet<string> MilestoneFamilies = LoadMilestoneFamilies();

    static Dictionary<(string Family, int Tier), (string AtomId, long Min, long Max)> LoadMilestoneRows()
    {
        var rows = new Dictionary<(string, int), (string, long, long)>();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "atoms", "generated", "family-expand.milestones.json")));
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var family = e.GetProperty("family").GetString()!;
            var tier = e.GetProperty("tier").GetInt32();
            var amount = e.GetProperty("params").GetProperty("amount");
            rows[(family, tier)] = (
                FusionRpg.Core.Effects.Atoms.AtomRow.DeriveId(family, "", tier),
                amount.GetProperty("min").GetInt64(),
                amount.GetProperty("max").GetInt64());
        }
        return rows;
    }

    static HashSet<string> LoadMilestoneFamilies()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "items", "enhancement-milestones", "milestones.json")));
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            set.Add(e.GetProperty("runtimeFamily").GetString()!);
        return set;
    }

    /// <summary>The lookup contract, over the REAL generated rows: unknown families throw (content
    /// gap, loud), known-but-unexpanded families return null (generator limitation, skip).</summary>
    static MilestoneAtom? MilestoneLookup(string family, int tier) =>
        MilestoneRows.TryGetValue((family, tier), out var row)
            ? new MilestoneAtom(row.AtomId, row.Min, row.Max)
            : MilestoneFamilies.Contains(family)
                ? null
                : throw new MilestoneUnknownFamily(
                    $"milestone family '{family}' is not one of the milestone corpus's runtime families");

    ItemWorkbench MilestoneBench(IReadOnlyList<MilestoneTrackEntry> track) =>
        new(_store, _materials, _recipes, _enhancement, _sockets,
            baseTypeEnhanceTrack: _ => track,
            milestoneAtomFor: MilestoneLookup);

    WorkbenchOutcomeDto EnhanceTo(ItemWorkbench bench, int target)
    {
        WorkbenchOutcomeDto outcome = null!;
        for (var level = _store.GetInstanceMutationHead(_instanceId)!.EnhanceLevel; level < target; level++)
        {
            Fund(_recipes.Resolve("recipe.012", ItemContext(level)));
            outcome = bench.Enhance(_playerId, _instanceId, "recipe.012", $"wb-milestone-{target}-{level}");
            Assert.True(outcome.Ok, outcome.Reason);
        }
        return outcome;
    }

    /// <summary>The whole point: reaching +4 (safe band, always succeeds) appends the track's
    /// family atom at the ladder tier — recorded in the op row AND present as a row.</summary>
    [Fact]
    public void Enhance_toAMilestoneLevel_appendsTheFamilyAtom()
    {
        var bench = MilestoneBench(new[] { new MilestoneTrackEntry(4, "atom.enhance-vigor") });
        EnhanceTo(bench, 4);

        var ops = _store.ReadMutationOps(_instanceId);
        Assert.Equal(4, ops.Count);
        var appended = Assert.Single(ops[^1].Result.Appended);
        var expected = MilestoneRows[("atom.enhance-vigor", 1)];
        Assert.Equal(expected.AtomId, appended.AtomId);
        Assert.InRange(appended.Values["amount"], expected.Min, expected.Max);

        var head = _store.GetInstanceMutationHead(_instanceId)!;
        Assert.Equal(4, head.EnhanceLevel);
        var rows = _store.ListInstanceAtoms(_instanceId);
        var row = Assert.Single(rows, r => r.AtomId == expected.AtomId);
        using var valuesDoc = JsonDocument.Parse(row.ValuesJson);
        Assert.InRange(valuesDoc.RootElement.GetProperty("amount").GetInt64(), expected.Min, expected.Max);
    }

    [Fact]
    public void Enhance_toANonMilestoneLevel_appendsNothing()
    {
        var bench = MilestoneBench(new[] { new MilestoneTrackEntry(4, "atom.enhance-vigor") });
        EnhanceTo(bench, 1);
        Assert.Empty(_store.ReadMutationOps(_instanceId)[^1].Result.Appended);
    }

    /// <summary>An unwired corpus never fails an enhance: the shared fixture bench (no delegates)
    /// reaches +4 with no append and no refusal.</summary>
    [Fact]
    public async Task Enhance_withAnUnwiredMilestoneCorpus_succeedsWithoutAppend()
    {
        for (var i = 0; i < 4; i++)
        {
            Fund(_recipes.Resolve("recipe.012", ItemContext(i)));
            var (status, _) = await Post("enhance", new
            {
                playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.012",
                correlationId = $"wb-milestone-unwired-{i}",
            });
            Assert.Equal(HttpStatusCode.OK, status);
        }
        Assert.All(_store.ReadMutationOps(_instanceId), op => Assert.Empty(op.Result.Appended));
    }

    [Fact]
    public void Enhance_withAnUnknownTrackFamily_throwsRatherThanFabricatingARow()
    {
        var bench = MilestoneBench(new[] { new MilestoneTrackEntry(4, "atom.not-a-family") });
        var ex = Assert.Throws<MilestoneUnknownFamily>(() => EnhanceTo(bench, 4));
        Assert.Contains("atom.not-a-family", ex.Message);
    }

    /// <summary>aegis is a REAL milestone family the generator never expanded (no reference base
    /// for its channel): the attempt succeeds with no append — documented, and the generator's own
    /// refusal list says why. Failing here would punish the player for an authoring gap.</summary>
    [Fact]
    public void Enhance_withAnUnexpandedKnownFamily_succeedsWithoutAppend()
    {
        var bench = MilestoneBench(new[] { new MilestoneTrackEntry(4, "atom.enhance-aegis") });
        var outcome = EnhanceTo(bench, 4);
        Assert.True(outcome.Ok);
        Assert.All(_store.ReadMutationOps(_instanceId), op => Assert.Empty(op.Result.Appended));
    }

    /// <summary>⭐ <b>The PRODUCTION builders, not this harness's hand-rolled equivalents.</b> The two
    /// lookups <c>Program.cs</c> now supplies are built here exactly as the boot builds them — the track
    /// off the real base-type corpus through <c>BaseTypeEnhanceTrackFile</c>, and the atom off the
    /// store's own rows through <c>MilestoneAtomLookup</c> — and both must agree with the corpus facts
    /// the four milestone tests above already drive the workbench with.</summary>
    [Fact]
    public void The_production_milestone_lookups_read_the_same_corpus_facts_as_the_tested_ones()
    {
        var baseTypesDir = Path.Combine(RepoRoot(), "data", "seed", "items", "base-types");

        // 1) The track reader is a CONTRACT over the drop reader's own ids, not a population: every base
        // type the drop path can draw must carry a track, and a track must ascend by atLevel (the
        // reader orders it, so a re-ordered file is not a different track).
        var tracks = FusionRpg.Core.Items.Mutation.BaseTypeEnhanceTrackFile.LoadAll(baseTypesDir);
        var dropped = FusionRpg.Core.Items.Drops.BaseTypeSeedFile.LoadAll(baseTypesDir);
        Assert.NotEmpty(tracks);
        Assert.All(dropped, row => Assert.True(
            tracks.ContainsKey(row.Id), $"base type '{row.Id}' is drawable but carries no enhanceTrack"));
        foreach (var (id, track) in tracks)
            for (var i = 1; i < track.Count; i++)
                Assert.True(track[i].AtLevel >= track[i - 1].AtLevel,
                    $"'{id}'s track is not ascending by atLevel");

        // 2) The atom lookup, seeded from a REAL generated milestone row, must return exactly the tuple
        // this harness's own map holds — so the production lookup is the tested one, corpus-driven.
        var (family, tier) = MilestoneRows.Keys.First();
        var expected = MilestoneRows[(family, tier)];
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = expected.AtomId,
            KindId = "stat.modify",
            FamilyId = family,
            Variant = "",
            Tier = tier,
            Name = $"{family} t{tier}",
            ParamsJson = "{\"channel\":\"defense\",\"op\":\"flat\",\"amount\":{\"min\":"
                         + expected.Min + ",\"max\":" + expected.Max + ",\"roll\":\"onApply\"}}",
        }).IsOk);

        var production = FusionRpg.Server.MilestoneAtomLookup.Load(_store.GetAtom, MilestoneFamilies);
        var got = production(family, tier);
        Assert.NotNull(got);
        Assert.Equal(expected.AtomId, got!.AtomId);
        Assert.Equal(expected.Min, got.MinAmount);
        Assert.Equal(expected.Max, got.MaxAmount);

        // 3) And the two absences stay OPPOSITE, which is the contract's whole point: a family the corpus
        // names but the generator could not expand is QUIET (no append), an unnamed family is LOUD.
        var unexpanded = MilestoneFamilies.FirstOrDefault(
            f => !MilestoneRows.Keys.Any(k => string.Equals(k.Family, f, StringComparison.Ordinal)));
        Assert.NotNull(unexpanded);
        Assert.Null(production(unexpanded!, tier));
        Assert.Throws<MilestoneUnknownFamily>(() => production("atom.not-a-family", tier));

        // 4) End to end through the workbench with those PRODUCTION lookups and a REAL track: reaching
        // +4 appends the corpus's own atom at the ladder tier, with an amount inside its authored range.
        var realId = dropped.Select(r => r.Id)
            .First(id => tracks.TryGetValue(id, out var t)
                         && t.Count > 0 && t[0].AtLevel <= 4
                         && MilestoneRows.ContainsKey((t[0].Family, 1)));
        var realTrack = tracks[realId];
        var realAtom = MilestoneRows[(realTrack[0].Family, 1)];
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = realAtom.AtomId,
            KindId = "stat.modify",
            FamilyId = realTrack[0].Family,
            Variant = "",
            Tier = 1,
            Name = $"{realTrack[0].Family} t1",
            ParamsJson = "{\"channel\":\"defense\",\"op\":\"flat\",\"amount\":{\"min\":"
                         + realAtom.Min + ",\"max\":" + realAtom.Max + ",\"roll\":\"onApply\"}}",
        }).IsOk);

        var bench = new ItemWorkbench(_store, _materials, _recipes, _enhancement, _sockets,
            // The harness's own base-type id stands in for the item's; the TRACK is the real corpus's,
            // which is the half that matters here (the boot maps real ids to these same tracks).
            baseTypeEnhanceTrack: id => id == BaseTypeId ? realTrack : null,
            milestoneAtomFor: production);
        var outcome = EnhanceTo(bench, 4);
        Assert.True(outcome.Ok, outcome.Reason);
        var appended = Assert.Single(_store.ReadMutationOps(_instanceId)[^1].Result.Appended);
        Assert.Equal(realAtom.AtomId, appended.AtomId);
        Assert.InRange(appended.Values["amount"], realAtom.Min, realAtom.Max);
    }

    // ---- species-gear-chain T14 — the forge executor --------------------------------------------

    static IReadOnlyList<RoleFamilyCell> ForgeCells() => RoleFamilyTable.Derive(
        LoadForgeFamilies(), LoadForgeOverrides(), LoadForgeRelocation());

    static List<AffixFamilySource> LoadForgeFamilies()
    {
        var result = new List<AffixFamilySource>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "data", "seed", "items", "affix-families"), "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
                result.Add(new AffixFamilySource(
                    e.GetProperty("id").GetString()!,
                    e.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToList(),
                    e.GetProperty("frames").EnumerateArray().Select(r => r.GetString()!).ToList(),
                    e.GetProperty("side").GetString()!,
                    e.GetProperty("kindId").GetString()!));
        }
        return result;
    }

    static FamilyOverrides LoadForgeOverrides() => FamilyOverrides.Parse(File.ReadAllText(Path.Combine(
        RepoRoot(), "data", "seed", "items", "_registry", "family-overrides.v1.json")));

    static RoleRelocationTable LoadForgeRelocation() => RoleRelocationTable.Parse(File.ReadAllText(Path.Combine(
        RepoRoot(), "data", "seed", "items", "_registry", "role-relocation.v1.json")));

    static PowerTuning ForgePower() => PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    ItemWorkbench ForgeBench() =>
        new(_store, _materials, _recipes, _enhancement, _sockets,
            forgeMintCells: ForgeCells(), forgePowerTuning: ForgePower());

    static IReadOnlyList<(string RecipeId, string OutputRef, int Grade)> ForgeRows()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "items", "recipes", "recipes.json")));
        return doc.RootElement.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("operation").GetString() == "forge")
            .Select(e => (
                e.GetProperty("id").GetString()!,
                e.GetProperty("outputRef").GetString()!,
                MaterialCatalog.GradeOf(e.GetProperty("costLines").EnumerateArray()
                    .Select(c => c.GetProperty("material").GetString()!)
                    .Single(m => m.StartsWith("substrate.", StringComparison.Ordinal)))))
            .ToList();
    }

    /// <summary>SC1, computed from the corpus: EVERY authored forge row executes end to end and mints
    /// a real saved instance with Origin Craft — no pinned row count, no pinned row ids.</summary>
    [Fact]
    public void Forge_executesEveryAuthoredForgeRecipe()
    {
        var bench = ForgeBench();
        _store.ImportBaseTypes(BaseTypeSeedFile.LoadAll(
            Path.Combine(RepoRoot(), "data", "seed", "items", "base-types")));
        var minted = 0;
        foreach (var (recipeId, outputRef, grade) in ForgeRows())
        {
            var itemLevel = grade * _materials.ItemLevelPerGrade - 1;
            Fund(_recipes.Resolve(recipeId, new RecipeContext(0, 0, itemLevel, "humanoid", 0)));
            var outcome = bench.Forge(_playerId, recipeId, $"wb-forge-{recipeId}");
            Assert.True(outcome.Ok, outcome.Reason);
            var instance = _store.GetInstance(outcome.InstanceId!);
            Assert.NotNull(instance);
            Assert.Equal(InstanceOrigin.Craft, instance!.Origin);
            // The minted container is ephemeral by design (never persisted as its own
            // effect_container row) — but its id embeds the output base type's slug, which proves
            // the grant carried the recipe's output and not another base type.
            var slug = outputRef.StartsWith("item.", StringComparison.Ordinal) ? outputRef["item.".Length..] : outputRef;
            Assert.StartsWith($"item.drop-{slug}-", instance.ContainerId, StringComparison.Ordinal);
            minted++;
        }
        _out.WriteLine($"forge recipes executed: {minted}");
        Assert.True(minted > 0);
    }

    /// <summary>Seeded retry identity + idempotent replay: the same correlation mints the identical
    /// item once, and the debit happens once.</summary>
    [Fact]
    public void Forge_replaysIdempotentlyWithAnIdenticalItem()
    {
        var bench = ForgeBench();
        _store.ImportBaseTypes(BaseTypeSeedFile.LoadAll(
            Path.Combine(RepoRoot(), "data", "seed", "items", "base-types")));
        var (recipeId, _, grade) = ForgeRows()[0];
        var itemLevel = grade * _materials.ItemLevelPerGrade - 1;
        Fund(_recipes.Resolve(recipeId, new RecipeContext(0, 0, itemLevel, "humanoid", 0)));
        var soulsBefore = _store.GetSoulBalance(_playerId).Balance;

        var first = bench.Forge(_playerId, recipeId, "wb-forge-replay");
        var second = bench.Forge(_playerId, recipeId, "wb-forge-replay");
        Assert.True(first.Ok, first.Reason);
        Assert.True(second.Ok, second.Reason);
        Assert.Equal(first.InstanceId, second.InstanceId);
        Assert.True(second.Replayed);
        Assert.Equal(soulsBefore, _store.GetSoulBalance(_playerId).Balance + CostSouls(recipeId, itemLevel));
    }

    long CostSouls(string recipeId, int itemLevel) =>
        _recipes.Resolve(recipeId, new RecipeContext(0, 0, itemLevel, "humanoid", 0))
            .Where(l => l.Class == MaterialClass.Souls).Sum(l => l.Qty);

    /// <summary>The debit and the product commit together: an unfunded forge refuses with no instance
    /// and no spend (souls balance untouched).</summary>
    [Fact]
    public void Forge_withoutFunds_spendsNothingAndMintsNothing()
    {
        var bench = ForgeBench();
        _store.ImportBaseTypes(BaseTypeSeedFile.LoadAll(
            Path.Combine(RepoRoot(), "data", "seed", "items", "base-types")));
        var (recipeId, _, _) = ForgeRows()[0];
        var soulsBefore = _store.GetSoulBalance(_playerId).Balance;
        var outcome = bench.Forge(_playerId, recipeId, "wb-forge-broke");
        Assert.False(outcome.Ok);
        Assert.Equal("", outcome.InstanceId);
        Assert.Equal(soulsBefore, _store.GetSoulBalance(_playerId).Balance);
    }

    [Fact]
    public void Forge_refusesARecipeOfAnotherOperationByName()
    {
        var bench = ForgeBench();
        var outcome = bench.Forge(_playerId, "recipe.012", "wb-forge-wrong-op");
        Assert.False(outcome.Ok);
        Assert.Contains("recipe.012", outcome.Reason);
    }

    [Fact]
    public void Forge_refusesWhenTheMintInputsAreUnwired()
    {
        var bench = new ItemWorkbench(_store, _materials, _recipes, _enhancement, _sockets);
        var (recipeId, _, _) = ForgeRows()[0];
        var outcome = bench.Forge(_playerId, recipeId, "wb-forge-unwired");
        Assert.False(outcome.Ok);
        Assert.Contains("forge.mint-unavailable", outcome.Reason);
    }

    /// <summary>
    /// P8.1 — the mint inputs <c>Program.cs</c> now supplies are the SHIPPED power tuning and the
    /// role-family cells derived from the shipped affix-family corpus, not the hand-built
    /// <see cref="ForgePower"/> the sibling tests use. This test pins that pair the way
    /// <c>Program.cs</c> builds it (the same <see cref="ForgeCells"/> derivation, the same
    /// <c>gk-core/data/tuning/power-scale.v2.json</c> it configures the Hub from at <c>Program.cs:306</c>),
    /// on the row the P8.1 acceptance names by id, and asserts the minted container's id embeds the
    /// recipe's OWN <c>outputRef</c> slug — so the forged item is that recipe's product and not
    /// another base type.
    ///
    /// <para>The shipped tuning is parsed locally rather than read through
    /// <c>PowerTuningHub.Tuning</c> so this test does not mutate a process-wide static that other
    /// tests in the same collection share.</para>
    /// </summary>
    [Fact]
    public void Forge_mintsRecipe001ThroughTheShippedPowerTuningAndDerivedCells()
    {
        var shippedPower = PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "tuning", "power-scale.v2.json")));
        var bench = new ItemWorkbench(_store, _materials, _recipes, _enhancement, _sockets,
            forgeMintCells: ForgeCells(), forgePowerTuning: shippedPower);
        _store.ImportBaseTypes(BaseTypeSeedFile.LoadAll(
            Path.Combine(RepoRoot(), "data", "seed", "items", "base-types")));

        var row = ForgeRows().Single(r => r.RecipeId == "recipe.001");
        var itemLevel = row.Grade * _materials.ItemLevelPerGrade - 1;
        Fund(_recipes.Resolve(row.RecipeId, new RecipeContext(0, 0, itemLevel, "humanoid", 0)));
        var outcome = bench.Forge(_playerId, row.RecipeId, "wb-forge-p81");

        Assert.True(outcome.Ok, outcome.Reason);
        var instance = _store.GetInstance(outcome.InstanceId!);
        Assert.NotNull(instance);
        Assert.Equal(InstanceOrigin.Craft, instance!.Origin);
        var slug = row.OutputRef.StartsWith("item.", StringComparison.Ordinal)
            ? row.OutputRef["item.".Length..]
            : row.OutputRef;
        Assert.StartsWith($"item.drop-{slug}-", instance.ContainerId, StringComparison.Ordinal);
    }

    /// <summary>Closed vocabularies, pinned with reason: this module adds zero members to either enum
    /// (SC4). The reroll op kinds below are the two T15 inherits, asserted present, not added.</summary>
    [Fact]
    public void ClosedVocabularies_gainZeroMembers()
    {
        // species-gear-chain T23 took `forge-gem`'s predecessor count ten to eleven: `repair` is the
        // eleventh priced verb, added deliberately under the filed ask, not by accident.
        // species-gear-chain T37 took it to twelve: the owner ruled the upgrade tree's executor (a
        // consume-and-replace verb) with its own priced row `operations.upgrade` and its own
        // `potentialCostPerVerb` entry, so craft vocabulary and `MutationOpKind` (thirteen members,
        // `upgrade` the newest) grow together and deliberately — never by accident.
        Assert.Equal(12, Enum.GetValues<CraftOperation>().Length);
        Assert.Equal(13, Enum.GetValues<MutationOpKind>().Length);
        Assert.Contains(MutationOpKind.Upgrade, Enum.GetValues<MutationOpKind>());
        Assert.Contains(MutationOpKind.RerollValue, Enum.GetValues<MutationOpKind>());
        Assert.Contains(MutationOpKind.RerollAffix, Enum.GetValues<MutationOpKind>());
    }

    // ---- species-gear-chain T15 — the reroll executors -------------------------------------------

    ItemWorkbench RerollBench() =>
        new(_store, _materials, _recipes, _enhancement, _sockets,
            lookupAtom: id => _store.GetAtom(id),
            lookupAffix: id => _store.GetAffix(id));

    static RecipeContext RerollItemContext() =>
        new(RarityLadder.RungIds.ToList().IndexOf("fused"),
            IlvlTierLadder.MaxTierAt(ItemLevel), ItemLevel, Frame, 0);

    static IReadOnlyList<string> RerollRows(string operation)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "items", "recipes", "recipes.json")));
        return doc.RootElement.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("operation").GetString() == operation)
            .Select(e => e.GetProperty("id").GetString()!)
            .ToList();
    }

    string MintForReroll(string correlationSuffix)
    {
        // Dots would break the atom-id grammar, so the recipe id flattens to dashes.
        var tag = correlationSuffix.Replace(".", "-", StringComparison.Ordinal);
        // Two real store rows with OnInstantiate ranges (rerollable values) and their single-atom
        // affixes, drawn into a two-roll prefix pool — a real instance with two rerollable affixes.
        // Synthetic ids, real storage: the pool, the affixes and the atoms all resolve in the store.
        var atoms = new[]
        {
            new AtomRow
            {
                AtomId = $"atom.reroll-a-{tag}.t1", KindId = "stat.modify",
                FamilyId = $"atom.reroll-a-{tag}", Variant = "", Tier = 1,
                Name = "reroll probe a",
                ParamsJson = "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onInstantiate\"}}",
            },
            new AtomRow
            {
                AtomId = $"atom.reroll-b-{tag}.t1", KindId = "stat.modify",
                FamilyId = $"atom.reroll-b-{tag}", Variant = "", Tier = 1,
                Name = "reroll probe b",
                ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
            },
        };
        foreach (var atom in atoms)
        {
            var atomUpsert = _store.UpsertAtom(atom);
            Assert.True(atomUpsert.IsOk, atomUpsert.ToString());
            var affix = FusionRpg.Core.Effects.Atoms.AffixLibraryGenerator.SingleAtomAffix(atom);
            var affixUpsert = _store.UpsertAffix(affix, id => atoms.FirstOrDefault(a => a.AtomId == id));
            Assert.True(affixUpsert.IsOk, affixUpsert.ToString());
        }
        var containerId = $"item.reroll-probe-{tag}";
        var affixIds = atoms
            .Select(a => FusionRpg.Core.Effects.Atoms.AffixLibraryGenerator.SingleAtomAffix(a).AffixId)
            .ToList();
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Rarity = "fused",
            PrefixRolls = 2,
            Pool = affixIds.Select(id => new ContainerPoolRow(id, 100)).ToList(),
        }).IsOk);
        var container = _store.GetContainer(containerId)!;
        var instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = container.ContainerId,
            RollSeed = 4242,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[]
            {
                new InstanceAtomRow(0, atoms[0].AtomId, """{"amount":15}"""),
                new InstanceAtomRow(1, atoms[1].AtomId, """{"amount":45}"""),
            },
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
            new LootManifest($"wb-reroll-{tag}", "table.workbench", 7UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "workbench", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(instanceId, 0, BaseTypeId, RungOrdinal(), ItemLevel, Frame,
                    ItemRoles.Id(ItemRole.ArmamentPrimary), "drop"),
            });
        return instanceId;
    }

    /// <summary>SC1 for reroll-one, computed from the corpus: EVERY authored reroll-one row executes
    /// end to end — the op records `RerollValue`, suppresses exactly the target seq, and appends the
    /// SAME affix id with a redrawn value.</summary>
    [Fact]
    public void RerollOne_executesEveryAuthoredRow()
    {
        var bench = RerollBench();
        var executed = 0;
        foreach (var recipeId in RerollRows("reroll-one"))
        {
            var instanceId = MintForReroll(recipeId);
            var atoms = _store.GetInstance(instanceId)!.Atoms;
            var target = atoms[0].Seq;
            Fund(_recipes.Resolve(recipeId, RerollItemContext()));

            var outcome = bench.RerollOne(_playerId, instanceId, recipeId, target, $"wb-reroll-one-{recipeId}");
            Assert.True(outcome.Ok, outcome.Reason);
            var op = _store.ReadMutationOps(instanceId)[^1];
            Assert.Equal(MutationOpKind.RerollValue, op.Kind);
            Assert.Equal(new[] { target }, op.Result.Suppressed);
            var appended = Assert.Single(op.Result.Appended);
            var targetAtom = atoms.Single(a => a.Seq == target).AtomId;
            Assert.Equal(targetAtom, appended.AtomId);
            executed++;
        }
        _out.WriteLine($"reroll-one rows executed: {executed}");
        Assert.True(executed > 0);
    }

    /// <summary>SC1 for reroll-all: EVERY authored reroll-all row executes — the op records
    /// `RerollAffix`, suppresses exactly the named seqs, and the result still validates as
    /// generatable (proven inside the renderer; the op row carries it).</summary>
    [Fact]
    public void RerollAll_executesEveryAuthoredRow()
    {
        var bench = RerollBench();
        var executed = 0;
        foreach (var recipeId in RerollRows("reroll-all"))
        {
            var instanceId = MintForReroll(recipeId);
            var seqs = _store.GetInstance(instanceId)!.Atoms.Select(a => a.Seq).ToList();
            Fund(_recipes.Resolve(recipeId, RerollItemContext()));

            var outcome = bench.RerollAll(_playerId, instanceId, recipeId, seqs, $"wb-reroll-all-{recipeId}");
            Assert.True(outcome.Ok, outcome.Reason);
            var op = _store.ReadMutationOps(instanceId)[^1];
            Assert.Equal(MutationOpKind.RerollAffix, op.Kind);
            Assert.Equal(seqs.OrderBy(s => s), op.Result.Suppressed.OrderBy(s => s));
            Assert.Equal(seqs.Count, op.Result.Appended.Count);
            executed++;
        }
        _out.WriteLine($"reroll-all rows executed: {executed}");
        Assert.True(executed > 0);
    }

    [Fact]
    public void Reroll_replaysIdempotentlyWithoutRedrawing()
    {
        var bench = RerollBench();
        var recipeId = RerollRows("reroll-one")[0];
        var instanceId = MintForReroll(recipeId);
        var target = _store.GetInstance(instanceId)!.Atoms[0].Seq;
        Fund(_recipes.Resolve(recipeId, RerollItemContext()));

        var first = bench.RerollOne(_playerId, instanceId, recipeId, target, "wb-reroll-replay");
        var second = bench.RerollOne(_playerId, instanceId, recipeId, target, "wb-reroll-replay");
        Assert.True(first.Ok, first.Reason);
        Assert.True(second.Ok, second.Reason);
        Assert.True(second.Replayed);
        Assert.Single(_store.ReadMutationOps(instanceId));
    }

    [Fact]
    public void Reroll_refusesUnknownTargetsAndUnwiredCatalogs()
    {
        var bench = RerollBench();
        var recipeId = RerollRows("reroll-one")[0];
        var instanceId = MintForReroll(recipeId);
        Fund(_recipes.Resolve(recipeId, RerollItemContext()));

        var unknown = bench.RerollOne(_playerId, instanceId, recipeId, 424242, "wb-reroll-unknown");
        Assert.False(unknown.Ok);
        Assert.Contains("424242", unknown.Reason);

        var allRecipeId = RerollRows("reroll-all")[0];
        var empty = bench.RerollAll(_playerId, instanceId, allRecipeId, Array.Empty<int>(), "wb-reroll-empty");
        Assert.False(empty.Ok);
        Assert.Contains("no-target", empty.Reason);

        var unwired = new ItemWorkbench(_store, _materials, _recipes, _enhancement, _sockets);
        var refused = unwired.RerollOne(_playerId, instanceId, recipeId, 0, "wb-reroll-unwired");
        Assert.False(refused.Ok);
        Assert.Contains("reroll.unwired-catalogs", refused.Reason);
    }

    /// <summary>SC2, the loud-failure join: every recipe operation with rows has an executor, and the
    /// stranded set is EXACTLY {elevate} — rarity-promotion's own module, named. A future stranded
    /// verb (or a quiet elevate executor appearing without its module) fails here first.</summary>
    [Fact]
    public void EveryRecipeOperationWithRowsHasAnExecutorExceptElevate()
    {
        var withExecutor = new HashSet<string>(StringComparer.Ordinal)
        {
            // strain-splice-host SSH8.3: `imbue` HAS rows and an executor now — the corpus authors one
            // per (bore frame, concrete element) and `ItemWorkbench.SocketImbue` spends them, refusing
            // a mismatched essence by name. It joins this set in the same commit as those rows, which
            // is exactly what this guard is for.
            "forge", "temper", "upcycle", "bore", "socket", "forge-gem", "imbue",
            "reroll-one", "reroll-all",
            // species-gear-chain T23: `repair` has rows AND an executor (ItemWorkbench.Repair), so it
            // is no longer stranded — `elevate` stays the one verb awaiting its module.
            "repair",
        };
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "data", "seed", "items", "recipes", "recipes.json")));
        var stranded = doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("operation").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .Where(op => !withExecutor.Contains(op))
            .OrderBy(op => op, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "elevate" }, stranded);
    }

    // ---- species-gear-chain T22 — socket-insert mints a real instance ----------------------------

    async Task BoreOneSocket(string correlationId)
    {
        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        Assert.Equal(HttpStatusCode.OK, (await Post("socket-add", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.019",
            correlationId,
        })).Status);
    }

    /// <summary>Bore, stock, fund and insert through the direct verb; returns the minted id off the
    /// socket row. Every assertion about the spend lives with the caller.</summary>
    string InsertMintedGem(string gem, string boreCorrelation, string insertCorrelation)
    {
        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        Assert.True(_bench.SocketAdd(_playerId, _instanceId, "recipe.019", boreCorrelation).Ok);
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));
        var outcome = _bench.SocketInsert(_playerId, _instanceId, "recipe.022", gem, null, insertCorrelation);
        Assert.True(outcome.Ok, outcome.Reason);
        return _store.GetSockets(_instanceId).Single().InsertInstanceId!;
    }

    // ── SSH4.8 (spec-combo-bind §2.1): every socket trigger refreshes the binding set ───────────────

    const string WordComboId = "combo.strain-workbench-word";

    /// <summary>The combo fixture: the store's combination inputs, the container for each granted tier,
    /// and ONE Strain any single ember (family <c>atom.elemental-power</c>) satisfies. The container's
    /// atom is the variant-"" row the build derives — the fixture's own ember atom is the `fire`
    /// VARIANT, so it cannot stand in for a grant.</summary>
    void SeedCombinationWord(params int[] tiers)
    {
        foreach (var tier in tiers)
            Assert.True(_store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId("atom.elemental-power", "", tier), KindId = "stat.derived",
                FamilyId = "atom.elemental-power", Variant = "", Tier = tier, Name = "word power",
                ParamsJson = "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":30}",
            }).IsOk);

        // The boot's own wiring, in miniature: the shipped tuning plus the gem lookup that resolves an
        // insert container's family. Without it no combination evaluates, which is the pre-SSH4.6 shape.
        _store.UseEquipCombinationEvaluation(new RpgStore.EquipCombinationInputs(
            _sockets,
            id => string.Equals(id, "gem.workbench-ember-t1", StringComparison.Ordinal)
                ? new CardInsertLookup(new InsertDef(id, "atom.elemental-power", "fire", 1), "gem.ember-shard")
                : null));

        var lookups = new ComboContainerBuild.ComboContainerLookups(_store.GetAtom);
        foreach (var tier in tiers)
        {
            var container = ComboContainerBuild.TryBuild(
                WordComboId, new[] { "atom.elemental-power" }, tier, lookups, out var refusal);
            Assert.NotNull(container);
            Assert.True(_store.UpsertContainer(container!).IsOk, refusal);
        }
        _store.SeedComboRecipes(new[]
        {
            new ComboRecipe(WordComboId, ComboShape.Strain, "", 0, "", "", 1, 1,
                new[] { new ComboIngredient("atom.elemental-power", 1) }, BaseFloors: new[] { 1 }),
        });
    }

    OwnerScope WordScope => new(OwnerKind.UniqueActor, _wordSpecimen);

    /// <summary>A REAL unique actor row: the refresh looks the wearer up through GetUniqueActor, so a
    /// synthetic id would be skipped (correctly — a wearer with no actor row is not a projection target).</summary>
    void EnsureWordSpecimen()
    {
        if (_wordSpecimen.Length == 0)
            _wordSpecimen = _store.CreateUniqueActor(_playerId, "plant", 50).InstanceId;
    }

    string? ComboBindingOf(string host) => _store.ListBindings(WordScope)
        .Select(b => b.InstanceId)
        .FirstOrDefault(id => id.StartsWith("cmb:" + host + "#", StringComparison.Ordinal));

    void EquipHost(string host)
    {
        _store.SaveAssignment(_wordSpecimen, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, host);
        _store.MaterializeRolledEquipRuntime(_wordSpecimen, level: 50);
    }

    void FillWord(string host, string boreCorrelation, string insertCorrelation)
    {
        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        Assert.True(_bench.SocketAdd(_playerId, host, "recipe.019", boreCorrelation).Ok);
        _store.AdjustStock(_playerKey, "gem.workbench-ember-t1", 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));
        var insert = _bench.SocketInsert(
            _playerId, host, "recipe.022", "gem.workbench-ember-t1", null, insertCorrelation);
        Assert.True(insert.Ok, insert.Reason);
    }

    [Fact]
    public void Completing_a_word_on_an_equipped_host_binds_it_without_a_re_equip()
    {
        // The socket WRITE is the trigger. The host is equipped and projected FIRST — no word fires —
        // then the fill arrives through the real socket-insert verb, and the binding appears with no
        // further equip call: the read side self-corrects removals but not additions, so this edge is
        // exactly what the refresh exists for.
        SeedCombinationWord(1);
        EnsureWordSpecimen();
        EquipHost(_instanceId);
        Assert.Null(ComboBindingOf(_instanceId));

        FillWord(_instanceId, "wb-word-bore", "wb-word-insert");

        var combo = ComboBindingOf(_instanceId);
        Assert.NotNull(combo);
        Assert.EndsWith(WordComboId + "-t1", combo, StringComparison.Ordinal);
        Assert.Contains("#c0:", combo, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_then_equip_and_equip_then_fill_reach_the_same_bindings()
    {
        // Order-independent: filling an UNEQUIPPED host and equipping it later lands on the same word
        // (same container, same circuit) as equipping first and filling after — the latter through the
        // socket write's own refresh. Compared by the word's container suffix, because the binding id
        // carries the host it hangs off.
        SeedCombinationWord(1);
        EnsureWordSpecimen();

        FillWord(_instanceId, "wb-order-a-bore", "wb-order-a-insert");
        EquipHost(_instanceId);
        var fillFirst = ComboBindingOf(_instanceId);
        Assert.NotNull(fillFirst);
        Assert.EndsWith(WordComboId + "-t1", fillFirst, StringComparison.Ordinal);

        var second = SeedHostAt("chaff");
        EquipHost(second);
        FillWord(second, "wb-order-b-bore", "wb-order-b-insert");
        var equipFirst = ComboBindingOf(second);
        Assert.NotNull(equipFirst);
        Assert.EndsWith(WordComboId + "-t1", equipFirst, StringComparison.Ordinal);
    }

    [Fact]
    public void Imbuing_an_equipped_host_rebinds_at_the_attuned_tier()
    {
        // A crafted socket is imbued fire on an already-equipped host, then filled: the insert's
        // element now matches the affinity, so the word fires ATTUNED — the granted tier moves from 1
        // to 2, and the binding moves with it to the tier-2 container.
        SeedCombinationWord(1, 2);
        EnsureWordSpecimen();
        EquipHost(_instanceId);

        Fund(_recipes.Resolve(ImbueBoreRecipe, ItemContext()));
        Assert.True(_bench.SocketAdd(_playerId, _instanceId, ImbueBoreRecipe, "wb-word-imbue-bore").Ok);
        Fund(_recipes.Resolve(ImbueFireRecipe, ItemContext()));
        var imbue = _bench.SocketImbue(_playerId, _instanceId, ImbueFireRecipe, 0, "fire", "wb-word-imbue-fire");
        Assert.True(imbue.Ok, imbue.Reason);
        Assert.Null(ComboBindingOf(_instanceId));   // imbued but still empty: nothing fires yet

        _store.AdjustStock(_playerKey, "gem.workbench-ember-t1", 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));
        var insert = _bench.SocketInsert(
            _playerId, _instanceId, "recipe.022", "gem.workbench-ember-t1", null, "wb-word-imbue-insert");
        Assert.True(insert.Ok, insert.Reason);

        var combo = ComboBindingOf(_instanceId);
        Assert.NotNull(combo);
        Assert.EndsWith(WordComboId + "-t2", combo, StringComparison.Ordinal);
    }

    static long FrozenAmount(string valuesJson)
    {
        using var doc = JsonDocument.Parse(valuesJson);
        return doc.RootElement.GetProperty("amount").GetInt64();
    }

    [Fact]
    public async Task SocketInsert_mintsARealInstanceThatReachesTheBindings()
    {
        await BoreOneSocket("wb-bore-mint");
        const string gem = "gem.workbench-ember-t1";
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));

        var (status, _) = await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.022",
            insertContainerId = gem, correlationId = "wb-insert-mint",
        });
        Assert.Equal(HttpStatusCode.OK, status);

        // The column holds a real instance id — never "" — and the row exists beside the host.
        var slot = Assert.Single(_store.GetSockets(_instanceId));
        Assert.Equal(gem, slot.InsertContainerId);
        Assert.False(string.IsNullOrWhiteSpace(slot.InsertInstanceId));
        var minted = _store.GetInstance(slot.InsertInstanceId!);
        Assert.NotNull(minted);
        Assert.Equal(gem, minted!.ContainerId);
        Assert.Equal(InstanceOrigin.Craft, minted.Origin);
        // The pin (Θc=20): depthless content mints where depth changes nothing, so the authored
        // amount freezes verbatim — unit scale, not a second magnitude axis.
        Assert.Equal(20, minted.ThetaContent);
        Assert.Equal(1000, minted.ContentScaleMilli);
        Assert.Equal(5, FrozenAmount(Assert.Single(minted.Atoms).ValuesJson));

        // And the minted row flows into combat through the T21 projection — no parallel bind.
        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, _instanceId);
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);
        var bindings = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1"));
        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, b => b.InstanceId == slot.InsertInstanceId);
    }

    [Fact]
    public async Task SocketInsert_withAnInsertOutsideTheCorpus_refusesByNameAndWritesNothing()
    {
        await BoreOneSocket("wb-bore-outside-corpus");
        // Held (so the stock gate passes) but never authored — the corpus-miss arm.
        const string gem = "gem.not-in-corpus.t1";
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));
        var spendsBefore = _store.CountMaterialSpendLog(_playerId);

        var (status, body) = await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.022",
            insertContainerId = gem, correlationId = "wb-insert-outside-corpus",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        var reason = body.GetProperty("reason").GetString()!;
        Assert.Contains("socket.insert-unknown", reason);
        Assert.Contains(gem, reason);
        // No partial state: no spend row, the socket row untouched, and the save lives inside the
        // spend transaction so no orphan instance row can exist either (by construction).
        Assert.Equal(spendsBefore, _store.CountMaterialSpendLog(_playerId));
        Assert.True(_store.GetSockets(_instanceId).Single().IsEmpty);
    }

    [Fact]
    public async Task SocketInsert_withAnUnbuildableKnownGem_refusesByNameAndWritesNothing()
    {
        await BoreOneSocket("wb-bore-unbuildable");
        // Corpus-known (Def resolves) but its family has no atom row — the content-gap arm.
        const string gem = "gem.workbench-other-t2";
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));
        var spendsBefore = _store.CountMaterialSpendLog(_playerId);

        var (status, body) = await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = _instanceId, recipeId = "recipe.022",
            insertContainerId = gem, correlationId = "wb-insert-unbuildable",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        var reason = body.GetProperty("reason").GetString()!;
        Assert.Contains("socket.insert-unresolvable", reason);
        Assert.Contains(gem, reason);
        Assert.Equal(spendsBefore, _store.CountMaterialSpendLog(_playerId));
        Assert.True(_store.GetSockets(_instanceId).Single().IsEmpty);
    }

    [Fact]
    public void SocketInsert_withoutMintWiring_refusesAsUnavailable()
    {
        // The forge-blind precedent, for the socket mint: bore on the wired bench (socket rules
        // need no mint), then insert on one with no seed/atom/tuning delegates at all.
        Fund(_recipes.Resolve("recipe.019", ItemContext()));
        Assert.True(_bench.SocketAdd(_playerId, _instanceId, "recipe.019", "wb-bore-unwired-mint").Ok);
        const string gem = "gem.workbench-ember-t1";
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));

        var blind = new ItemWorkbench(_store, _materials, _recipes, _enhancement, _sockets);
        var outcome = blind.SocketInsert(_playerId, _instanceId, "recipe.022", gem, null, "wb-insert-unwired-mint");

        Assert.False(outcome.Ok);
        Assert.Contains("socket.mint-unavailable", outcome.Reason);
        Assert.True(_store.GetSockets(_instanceId).Single().IsEmpty);
    }

    [Fact]
    public void SocketInsert_unequippingTheHostWithdrawsTheMintedBinding()
    {
        var mintedId = InsertMintedGem("gem.workbench-ember-t1", "wb-bore-withdraw", "wb-insert-withdraw");

        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, _instanceId);
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);
        Assert.Equal(2, _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")).Count);

        _store.RemoveAssignment("specimen-1", ItemRole.ArmamentPrimary);
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var bindings = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1"));
        Assert.Empty(bindings);
        // The socket keeps its insert — withdraw happens at the binding layer, never by deleting
        // socket rows. Re-equipping re-materializes the same minted row, no second mint.
        Assert.Equal(mintedId, _store.GetSockets(_instanceId).Single().InsertInstanceId);
    }

    [Fact]
    public void SocketInsert_emptyingTheSocketWithdrawsTheMintedBindingButKeepsTheHost()
    {
        InsertMintedGem("gem.workbench-ember-t1", "wb-bore-empty", "wb-insert-empty");

        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, _instanceId);
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);
        Assert.Equal(2, _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")).Count);

        // The removal a future socket-remove verb will call: same wholesale write, same reaper.
        var emptied = _store.GetSockets(_instanceId)
            .Select(s => s with { InsertContainerId = null, InsertInstanceId = null })
            .ToList();
        _store.SetSockets(_instanceId, emptied);
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var host = Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")));
        Assert.Equal(_instanceId, host.InstanceId);
    }

    // ---- strain-splice-host SSH8.3 — socket-imbue is payable, and the essence names the element ----

    /// <summary>The two rows the imbue story uses: the humanoid bore template and its FIRE essence row
    /// (`recipegen/imbue.py` derives one per (bore frame, concrete element), SSH8.1/SSH8.2).</summary>
    const string ImbueBoreRecipe = "recipe.019";
    const string ImbueFireRecipe = "recipe.070";

    /// <summary>
    /// A second host at <paramref name="rung"/>, by re-running this fixture's own seeding tail with that
    /// rung: the containers and atoms already exist, so the ONLY difference from <see cref="_instanceId"/>
    /// is the rarity the workbench prices against.
    /// </summary>
    string SeedHostAt(string rung)
    {
        var ordinal = _store.ListRarities().First(r => r.RarityId == rung).Ordinal;
        var id = _store.SaveInstance(new InstanceRow
        {
            ContainerId = ContainerId,
            RollSeed = 7,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, AtomRow.DeriveId("atom.workbench-vitality", "", 1), """{"amount":10}""") },
        });
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = id, PlayerId = _playerKey, AcquiredUtc = "2026-09-06T00:00:00Z", OriginKind = "drop",
        }).IsOk);
        // ⛔ The manifest's correlation id keys the drop log, and `PersistLoot` returns early for one it
        // has already seen — without inserting the generation rows. One id per host, or the second host
        // silently gets no stamp and the workbench refuses it with `item.generation-missing`.
        _store.PersistLoot(
            _playerKey,
            new LootManifest($"wb-drop-{rung}", "table.workbench", 7UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "workbench", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(id, 0, BaseTypeId, ordinal, ItemLevel, Frame,
                    ItemRoles.Id(ItemRole.ArmamentPrimary), "drop"),
            });
        Assert.NotNull(_store.GetItemGeneration(id));
        return id;
    }

    /// <summary>The souls leg of one recipe at one rung — what the workbench resolves, asked directly.</summary>
    long SoulsAt(string recipeId, string rung) => _recipes
        .Resolve(recipeId, new RecipeContext(RarityLadder.RungIds.ToList().IndexOf(rung),
            IlvlTierLadder.MaxTierAt(ItemLevel), ItemLevel, Frame, 0))
        .Where(l => l.Class == MaterialClass.Souls).Sum(l => l.Qty);

    [Fact]
    public async Task Imbuing_with_a_mismatched_essence_is_refused_by_name()
    {
        var host = SeedHostAt("chaff");
        Fund(_recipes.Resolve(ImbueBoreRecipe, ItemContext()));
        Assert.True(_bench.SocketAdd(_playerId, host, ImbueBoreRecipe, "wb-mismatch-bore").Ok);

        // The FIRE row is funded and the socket asked for ICE: the essence a player pays for must name
        // the element it buys, so this refuses before a single soul moves.
        Fund(_recipes.Resolve(ImbueFireRecipe, ItemContext()));
        var spendsBefore = _store.CountMaterialSpendLog(_playerId);

        var (status, body) = await Post("socket-imbue", new
        {
            playerId = _playerId, instanceId = host, recipeId = ImbueFireRecipe,
            socketIndex = 0, element = "ice", correlationId = "wb-mismatch-imbue",
        });

        Assert.Equal(HttpStatusCode.Conflict, status);
        var reason = body.GetProperty("reason").GetString()!;
        Assert.Contains("socket.imbue-element-mismatch", reason);
        Assert.Contains("essence.fire", reason);
        Assert.Contains("ice", reason);
        Assert.Equal(spendsBefore, _store.CountMaterialSpendLog(_playerId));
        Assert.Equal("", _store.GetSockets(host).Single().Affinity);
    }

    [Fact]
    public async Task A_chaff_chassis_can_be_bored_imbued_and_filled_end_to_end()
    {
        var host = SeedHostAt("chaff");

        // 1. bore — the socket exists, crafted, with no affinity yet.
        Fund(_recipes.Resolve(ImbueBoreRecipe, ItemContext()));
        var bore = _bench.SocketAdd(_playerId, host, ImbueBoreRecipe, "wb-e2e-bore");
        Assert.True(bore.Ok, bore.Reason);
        Assert.True(_store.GetSockets(host).Single().Crafted);

        // 2. imbue — a REAL spend (the essence is paid for, not simulated), and the affinity lands on
        // the socket row.
        Fund(_recipes.Resolve(ImbueFireRecipe, ItemContext()));
        var spendsBefore = _store.CountMaterialSpendLog(_playerId);
        var imbue = _bench.SocketImbue(_playerId, host, ImbueFireRecipe, 0, "fire", "wb-e2e-imbue");
        Assert.True(imbue.Ok, imbue.Reason);
        Assert.True(_store.CountMaterialSpendLog(_playerId) > spendsBefore,
            "the essence a player pays for must be really spent");

        // 3. fill — a real gem goes in, through the same endpoint every other insert uses.
        const string gem = "gem.workbench-ember-t1";
        _store.AdjustStock(_playerKey, gem, 1);
        Fund(_recipes.Resolve("recipe.022", ItemContext()));
        var (insertStatus, _) = await Post("socket-insert", new
        {
            playerId = _playerId, instanceId = host, recipeId = "recipe.022",
            insertContainerId = gem, correlationId = "wb-e2e-insert",
        });
        Assert.Equal(HttpStatusCode.OK, insertStatus);

        // Read back through `GetSockets` — the stored row, not the response body.
        var slot = Assert.Single(_store.GetSockets(host));
        Assert.Equal("fire", slot.Affinity);
        Assert.Equal(gem, slot.InsertContainerId);
        Assert.False(string.IsNullOrWhiteSpace(slot.InsertInstanceId));
        Assert.False(slot.IsEmpty);
    }

    [Fact]
    public async Task Bore_and_imbue_cost_more_on_a_higher_rung_and_succeed_on_both()
    {
        // D23: rarity scales the PRICE, never the possibility. Same content, two hosts, one rung apart
        // at the extremes of the ladder.
        var chaff = SeedHostAt("chaff");
        var almanac = SeedHostAt(RarityLadder.RungIds[^1]);

        Assert.True(SoulsAt(ImbueBoreRecipe, RarityLadder.RungIds[^1]) > SoulsAt(ImbueBoreRecipe, "chaff"),
            $"bore souls chaff={SoulsAt(ImbueBoreRecipe, "chaff")} " +
            $"top={SoulsAt(ImbueBoreRecipe, RarityLadder.RungIds[^1])}");
        Assert.True(SoulsAt(ImbueFireRecipe, RarityLadder.RungIds[^1]) > SoulsAt(ImbueFireRecipe, "chaff"),
            $"imbue souls chaff={SoulsAt(ImbueFireRecipe, "chaff")} " +
            $"top={SoulsAt(ImbueFireRecipe, RarityLadder.RungIds[^1])}");

        foreach (var (host, rung) in new[] { (chaff, "chaff"), (almanac, RarityLadder.RungIds[^1]) })
        {
            var context = new RecipeContext(RarityLadder.RungIds.ToList().IndexOf(rung),
                IlvlTierLadder.MaxTierAt(ItemLevel), ItemLevel, Frame, 0);
            Fund(_recipes.Resolve(ImbueBoreRecipe, context));
            var bore = _bench.SocketAdd(_playerId, host, ImbueBoreRecipe, $"wb-rung-bore-{rung}");
            Assert.True(bore.Ok, $"bore on {rung}: {bore.Reason}");
            Fund(_recipes.Resolve(ImbueFireRecipe, context));
            var imbue = _bench.SocketImbue(_playerId, host, ImbueFireRecipe, 0, "fire", $"wb-rung-imbue-{rung}");
            Assert.True(imbue.Ok, imbue.Reason);
            Assert.Equal("fire", _store.GetSockets(host).Single().Affinity);
        }
    }

    /// <summary>
    /// species-gear-chain T61 — the potential pair must EXIST after a craft, and be the SHIPPED
    /// derivation's own value. Before this, nothing in production derived or wrote it: the pair stayed
    /// NULL for every item, `CanDecay(null)` is false by design, so craft wear could never engage at all.
    /// Uses a REAL base type off the shipped corpus, because the derivation reads the base-type registry.
    /// </summary>
    [Fact]
    public async Task A_craft_leaves_the_item_with_a_DERIVED_potential_pair()
    {
        var (baseTypeId, baseClass) = FirstRealBaseType();
        var id = SeedRealBaseTypeInstance(baseTypeId);
        FundTemper();

        Assert.Null(_store.GetPotential(id).Max);   // precondition: nothing has derived it yet

        var (status, body) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = id, recipeId = "recipe.012", correlationId = "t61-derive-1",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean(), body.GetProperty("reason").GetString());

        var pair = _store.GetPotential(id);
        Assert.NotNull(pair.Max);
        Assert.NotNull(pair.Current);

        // And the value is the shipped derivation's, honouring any authored override first.
        var entry = new HeadDerivationEntry(baseTypeId, baseClass, Array.Empty<string>());
        var expected = PotentialTable.DeriveMax(entry, RarityLadder.RungIds[RungIndex()], _deployment);
        Assert.Equal(expected, pair.Max);
        Assert.True(pair.Max > 0, "a derived potential ceiling must be positive");
    }

    /// <summary>Shipped deployment-hierarchy tuning with one edited cost, so a craft can exhaust a ceiling in one go.</summary>
    static string WithTemperPotentialCost(long cost)
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "deployment-hierarchy.v5.json"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["potentialCostPerVerb"]!["temper"] = cost;
        return node.ToJsonString();
    }

    /// <summary>
    /// ⭐ species-gear-chain T61 half B — a craft SPENDS its verb's potential cost, and the craft that finds
    /// the pair exhausted wears. The exhaustion is produced by the spend itself (one craft costs the whole
    /// ceiling), never by the test writing a pair: before this landed, nothing spent and nothing derived, so
    /// every craft was Assured forever.
    /// </summary>
    [Fact]
    public async Task A_craft_spends_potential_and_the_next_one_past_exhaustion_wears()
    {
        var (baseTypeId, baseClass) = FirstRealBaseType();
        var id = SeedRealBaseTypeInstance(baseTypeId);
        // `Fund` grants EXACTLY one craft's price, and this test crafts TWICE; the second craft's price can
        // differ from the first (the enhance level moved), so one spare grant covers the difference rather
        // than pinning a number here.
        FundTemper();
        FundTemper();
        FundTemper();

        var ceiling = PotentialTable.DeriveMax(
            new HeadDerivationEntry(baseTypeId, baseClass, Array.Empty<string>()),
            RarityLadder.RungIds[RungIndex()], _deployment);
        DeploymentHierarchyTuningHub.Configure(
            DeploymentHierarchyTuningLoader.Parse(WithTemperPotentialCost(ceiling)));

        var max = _craftWear.MaxFor(baseTypeId, RungIndex())!.Value;
        _store.SetDurability(id, max, max);

        var (first, firstBody) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = id, recipeId = "recipe.012", correlationId = "t61-spend-1",
        });
        Assert.True(first == HttpStatusCode.OK, firstBody.GetProperty("reason").GetString());
        Assert.Equal(0, _store.GetPotential(id).Current);            // the craft spent the whole ceiling
        Assert.Equal(max, _store.GetDurability(id).Current);         // potential remained ⇒ no wear

        var (second, secondBody) = await Post("enhance", new
        {
            playerId = _playerId, instanceId = id, recipeId = "recipe.012", correlationId = "t61-spend-2",
        });
        Assert.True(second == HttpStatusCode.OK, secondBody.ToString());
        Assert.Equal(
            CraftRiskPolicy.AfterWear(max, CraftRiskPolicy.WearFor(max, _deployment.CraftWearPerAttemptMilli)),
            _store.GetDurability(id).Current);                       // past exhaustion ⇒ the wear lands
    }
}
