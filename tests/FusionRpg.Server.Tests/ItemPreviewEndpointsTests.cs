using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Generation;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Surfaces;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// ⭐ item-content module <c>atom-preview</c> (T8) — <b>the preview route</b>, against a real
/// in-process host, a real SQLite store, the real shipped atom corpus and an <b>unsaved</b> container.
///
/// <para>The acceptance test is <see cref="Preview_isTheCoreRenderersOwnModelOverTheSameInput"/>: the
/// posted body must render to exactly what <c>ItemCardRenderer.Render</c> produces when called
/// directly, in-process, on the same input. That is what separates "a preview route exists" from
/// ssot-presentation.md §8.6's named failure — <i>a second renderer appears and the two drift</i>.</para>
///
/// <para>⛔ <b>Nothing here is a fixture atom.</b> The catalog is the REAL
/// <c>gk-data/packs/fusion/data/seed/items/affix-families/*.json</c> corpus through the REAL <c>FamilyExpansion</c> and
/// <c>AffixLibraryGenerator</c>, and the display templates are the REAL shipped rows.</para>
/// </summary>
public class ItemPreviewEndpointsTests : IAsyncLifetime
{
    const string Rung = "heirloom";
    const string PreviewContainer = "item.preview-proof-blade";
    const string Frame = "humanoid";
    const int ItemLevel = 24;
    const int LevelReq = 20;
    const long RollSeed = 0xC0FFEE;
    const int PrefixRolls = 4;

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    ItemCardCorpus _corpus = null!;
    ContainerRow _container = null!;
    CardBaseType _baseType;

    // ---- the real corpus -------------------------------------------------------------------------

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

    static string Seed(params string[] parts) =>
        Path.Combine(new[] { RepoRoot(), "data", "seed" }.Concat(parts).ToArray());

    static readonly PowerTuning Power = PowerAndAptitudeTuningTestBootstrap.DefaultPower;

    /// <summary>The ladder's own pin index — the same default the route falls back to, so the test and
    /// the route agree about depth without either of them naming a literal.</summary>
    static readonly int PinTheta = Power.Curve.PinIndex;

    static long? FlatReferenceBase(string channel) => channel switch
    {
        "maxHp" or "hp" => FusionRpg.Core.Battle.BattleRuleset.BaseHp(FamilyExpansion.ReferenceLevel),
        "atk" => FusionRpg.Core.Battle.BattleRuleset.BaseAtk(FamilyExpansion.ReferenceLevel),
        "defense" => FusionRpg.Core.Battle.BattleRuleset.BaseDefense(FamilyExpansion.ReferenceLevel),
        _ => null,
    };

    static readonly Lazy<IReadOnlyList<AtomRow>> RealAtoms = new(() =>
    {
        var itemsRoot = Seed("items");
        var tierBands = TierBandsFile.Read(
            File.ReadAllText(Path.Combine(itemsRoot, "_tuning", "tier-bands.v1.json")));

        var families = new List<FamilyEntryInput>();
        foreach (var file in Directory.GetFiles(Path.Combine(itemsRoot, "affix-families"), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            families.AddRange(AffixFamilyFile.Read(Path.GetFileName(file), File.ReadAllText(file)));
        }

        return FamilyExpansion.Expand(families, tierBands, FlatReferenceBase).Rows;
    });

    static readonly Lazy<IReadOnlyList<DisplayTemplateRow>> Templates = new(() =>
        Directory.EnumerateFiles(Seed("items", "display-templates"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => DisplayTemplates.Parse(File.ReadAllText(f)))
            .ToList());

    static SocketTuning Sockets() =>
        SocketTuning.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    static ItemSurfaceTuning Surfaces() =>
        ItemSurfaceTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "item-surfaces.v1.json")));

    static EnhancementTuning Enhancement() =>
        EnhancementTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "enhancement.v1.json")));

    // ---- fixture ---------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);

        var atoms = RealAtoms.Value;
        Assert.NotEmpty(atoms);
        _store.UpsertAtoms(atoms);

        var byId = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        AtomRow? LookupAtomLocal(string id) => byId.TryGetValue(id, out var a) ? a : null;

        var affixes = AffixLibraryGenerator.Generate(atoms);
        foreach (var affix in affixes) Assert.True(_store.UpsertAffix(affix, LookupAtomLocal).IsOk);

        _store.SeedItemDisplayTemplates(Templates.Value);

        var templated = Templates.Value
            .Where(t => t.Status == "live")
            .Select(t => t.RuntimeFamily)
            .ToHashSet(StringComparer.Ordinal);

        var hpBase = atoms.Where(a => a.ParamsJson.Contains("\"maxHp\"", StringComparison.Ordinal))
            .OrderByDescending(a => a.Tier).First();

        var pool = affixes
            .Where(a => a.Refs.Count == 1 && a.Refs[0].AtomId is { } id && byId.TryGetValue(id, out var at)
                        && !string.Equals(at.FamilyId, hpBase.FamilyId, StringComparison.Ordinal)
                        && templated.Contains(at.FamilyId))
            .OrderBy(a => a.AffixId, StringComparer.Ordinal)
            .Select(a => new ContainerPoolRow(a.AffixId, 100))
            .ToList();
        Assert.NotEmpty(pool);

        // ⛔ Deliberately NEVER upserted. The whole point of the module is that this container does not
        // exist in the store, and `Preview_persistsNothing` asserts it still does not afterwards.
        _container = new ContainerRow
        {
            ContainerId = PreviewContainer,
            Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(ItemRole.ArmamentPrimary),
            Rarity = Rung,
            LevelReq = LevelReq,
            PrefixRolls = PrefixRolls,
            SuffixRolls = 0,
            Atoms = new[] { new ContainerAtomRow(0, hpBase.AtomId) },
            Pool = pool,
        };

        _baseType = new CardBaseType(
            "base.preview-proof-blade", "class.blade", Frame, "role.armament-primary", null);

        _corpus = new ItemCardCorpus(
            ItemBaseTypeCorpus.From(new Dictionary<string, CardBaseType>(StringComparer.Ordinal)
            {
                [PreviewContainer] = _baseType,
            }),
            GemInsertCorpus.Load(Seed("items", "gems")),
            Sockets(), Surfaces(), Enhancement());

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapItemPreview(new ItemPreviewService(_store, _corpus, Power));
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

    // ---- the posted body, mirroring `ContainerRow` field for field ---------------------------------

    object Body(object? overrides = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["containerId"] = _container.ContainerId,
            ["kind"] = _container.Kind.ToString(),
            ["slot"] = _container.Slot,
            ["rarity"] = _container.Rarity,
            ["levelReq"] = _container.LevelReq,
            ["prefixRolls"] = _container.PrefixRolls,
            ["suffixRolls"] = _container.SuffixRolls,
            ["atoms"] = _container.Atoms.Select(a => new { seq = a.Seq, atomId = a.AtomId }).ToList(),
            ["pool"] = _container.Pool.Select(p => new { affixId = p.AffixId, weight = p.Weight }).ToList(),
            ["rollSeed"] = RollSeed,
            ["itemLevel"] = ItemLevel,
        };

        if (overrides is not null)
            foreach (var p in overrides.GetType().GetProperties())
                body[p.Name] = p.GetValue(overrides);

        return body;
    }

    async Task<(HttpStatusCode Status, JsonElement Json)> Post(object body)
    {
        var resp = await _http.PostAsJsonAsync("/api/items/preview/card", body);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    static JsonElement Card(JsonElement preview, string mode) =>
        preview.GetProperty("cards").EnumerateArray()
            .Single(c => c.GetProperty("mode").GetString() == mode)
            .GetProperty("card");

    static IReadOnlyList<JsonElement> Lines(JsonElement card, string blockKey) =>
        card.GetProperty("blocks").EnumerateArray()
            .Single(b => b.GetProperty("blockKey").GetString() == blockKey)
            .GetProperty("lines").EnumerateArray().ToList();

    // ---- the in-process reference render -----------------------------------------------------------

    /// <summary>
    /// The SAME input the route builds, assembled here independently: the real
    /// <c>Instantiator</c> over the same unsaved container, seed and Θ, then the store's own atom,
    /// template and rarity reads. Two paths to one <see cref="DisplayModel"/> — which is the only way
    /// "the route is a thin wrapper" can be proven rather than asserted.
    /// </summary>
    DisplayModel RenderInProcess()
    {
        AtomRow? LookupAtom(string id) => _store.GetAtom(id);
        AffixRow? LookupAffix(string id) => _store.GetAffix(id);
        DisplayTemplateRow? LookupTemplate(string family) => _store.GetDisplayTemplate(family);

        var mint = Instantiator.TryInstantiate(
            _container, LookupAtom, LookupAffix, RollSeed, PinTheta, Power, out var instance,
            InstanceOrigin.Drop, catalogRevision: _store.GetCatalogRevision());
        Assert.True(mint.IsOk, mint.ToString());

        var input = new ItemCardInput(
            instance!, _container, LookupAtom, LookupTemplate, _baseType,
            _store.ReadRarity(_container.Rarity, _corpus.Palette),
            ItemName: _baseType.NameKey)
        {
            ItemLevel = ItemLevel,
            LevelReq = _container.LevelReq,
            Registry = _corpus.Registry,
        };

        return ItemCardRenderer.Render(input);
    }

    // ====================================================================================================
    // T8's acceptance test
    // ====================================================================================================

    /// <summary>
    /// ⭐ <b>The acceptance test.</b> The route's <c>rolled</c> card must be byte-identical to the Core
    /// renderer's own model over the same unsaved container — same fingerprint, same blocks, same
    /// lines, same args, in the same order. A route that reshaped, re-rounded or re-ordered anything
    /// fails here.
    /// </summary>
    [Fact]
    public async Task Preview_isTheCoreRenderersOwnModelOverTheSameInput()
    {
        var expected = RenderInProcess();

        var (status, body) = await Post(Body());

        Assert.Equal(HttpStatusCode.OK, status);
        var card = Card(body, ItemPreviewService.ModeRolled);
        Assert.Equal(expected.Fingerprint(), card.GetProperty("fingerprint").GetString());

        // The fingerprint is the identity claim; this is the payload itself, block for block and line
        // for line, so a projection that dropped a field could not hide behind a matching digest.
        Assert.Equal(
            expected.Blocks.Select(b => b.BlockKey).ToList(),
            card.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("blockKey").GetString()!).ToList());

        foreach (var block in expected.Blocks)
        {
            var wire = Lines(card, block.BlockKey);
            Assert.Equal(block.Lines.Count, wire.Count);
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var line = block.Lines[i];
                Assert.Equal(line.Key, wire[i].GetProperty("key").GetString());
                Assert.Equal(line.GroupOrder, wire[i].GetProperty("groupOrder").GetInt32());
                Assert.Equal(line.Unit?.ToString(), wire[i].GetProperty("unit").GetString());
                Assert.Equal(line.SourceKind?.ToString(), wire[i].GetProperty("sourceKind").GetString());

                var args = wire[i].GetProperty("args");
                Assert.Equal(line.Args.Count, args.EnumerateObject().Count());
                foreach (var (name, value) in line.Args)
                    Assert.Equal(value, args.GetProperty(name).GetString());
            }
        }
    }

    /// <summary>
    /// The whole point of an author-facing preview: real rendered affix sentences off the real corpus,
    /// with no drop, no instance and no ownership row anywhere.
    /// </summary>
    [Fact]
    public async Task Preview_rendersRealAffixSentencesForAnUnsavedContainer()
    {
        var (status, body) = await Post(Body());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(PreviewContainer, body.GetProperty("containerId").GetString());
        Assert.Equal(PinTheta, body.GetProperty("thetaContent").GetInt32());

        var card = Card(body, ItemPreviewService.ModeRolled);
        Assert.Equal(
            CardBlocks.Order,
            card.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("blockKey").GetString()!).ToList());

        Assert.NotEmpty(Lines(card, CardBlocks.BaseStats));
        var affixes = Lines(card, CardBlocks.Affixes);
        Assert.Equal(PrefixRolls, affixes.Count);

        // Every affix line is the RENDERER's own finished sentence, never a raw id.
        foreach (var line in affixes)
        {
            var rendered = line.GetProperty("args").GetProperty("__rendered").GetString();
            Assert.False(string.IsNullOrWhiteSpace(rendered));
            Assert.DoesNotContain("atom.", rendered!, StringComparison.Ordinal);
        }

        // The header comes off the base-type corpus, not the container id (§2.4).
        var header = Assert.Single(Lines(card, CardBlocks.Header));
        Assert.Equal("base.preview-proof-blade", header.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal(ItemLevel.ToString(), header.GetProperty("args").GetProperty("ilvl").GetString());
    }

    /// <summary>
    /// ⭐ spec-atom-preview.md §2 criterion 4: the same container at both ends of its authored range,
    /// so an author sees a family's display template render at <c>Min</c> and at <c>Max</c> without
    /// waiting for a drop to land there.
    ///
    /// <para>The three cards share one draw — same seed, same affixes, same order — so the only thing
    /// that moves between them is the magnitude, which is what makes the comparison readable.</para>
    /// </summary>
    [Fact]
    public async Task Preview_rendersTheAuthoredRangeAtBothEnds()
    {
        var (status, body) = await Post(Body());
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal(
            ItemPreviewService.RollModes,
            body.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("mode").GetString()!).ToList());

        var rolled = Lines(Card(body, ItemPreviewService.ModeRolled), CardBlocks.Affixes);
        var min = Lines(Card(body, ItemPreviewService.ModeMin), CardBlocks.Affixes);
        var max = Lines(Card(body, ItemPreviewService.ModeMax), CardBlocks.Affixes);

        // Same draw on all three — the range is the only axis that moves.
        Assert.Equal(rolled.Select(l => l.GetProperty("key").GetString()).ToList(),
            min.Select(l => l.GetProperty("key").GetString()).ToList());
        Assert.Equal(rolled.Select(l => l.GetProperty("key").GetString()).ToList(),
            max.Select(l => l.GetProperty("key").GetString()).ToList());

        static string? Value(JsonElement line) =>
            line.GetProperty("args").TryGetProperty("value", out var v) ? v.GetString() : null;

        var moved = 0;
        for (var i = 0; i < min.Count; i++)
        {
            var lo = Value(min[i]);
            var hi = Value(max[i]);
            if (lo is null || hi is null || lo == hi) continue;
            moved++;

            // The rolled card shows the BAND its authored spec really is; each end shows one bound of
            // that same band, so both bounds must appear inside the band's own rendering.
            var band = Value(rolled[i]);
            Assert.NotNull(band);
            Assert.Contains(lo, band!, StringComparison.Ordinal);
            Assert.Contains(hi, band!, StringComparison.Ordinal);
        }

        Assert.True(moved > 0,
            "no affix on this container has a range to show at both ends — the fixture, not the route, is wrong");
    }

    /// <summary>
    /// ⛔ It previews; it never persists. The posted container is not in <c>effect_container</c>
    /// afterwards, and no instance was saved — spec-atom-preview.md §7's second rule, asserted rather
    /// than promised in a comment.
    /// </summary>
    [Fact]
    public async Task Preview_persistsNothing()
    {
        Assert.Null(_store.GetContainer(PreviewContainer));
        var before = _store.GetCatalogRevision();

        var (status, _) = await Post(Body());
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Null(_store.GetContainer(PreviewContainer));
        Assert.Equal(before, _store.GetCatalogRevision());
    }

    /// <summary>Same body, same seed, same Θ — same card. A preview an author cannot reproduce is a
    /// preview they cannot trust.</summary>
    [Fact]
    public async Task Preview_isReproducibleFromThePostedBodyAlone()
    {
        var (_, first) = await Post(Body());
        var (_, second) = await Post(Body());

        Assert.Equal(
            Card(first, ItemPreviewService.ModeRolled).GetProperty("fingerprint").GetString(),
            Card(second, ItemPreviewService.ModeRolled).GetProperty("fingerprint").GetString());
    }

    // ====================================================================================================
    // Refusals: named, and never a 500
    // ====================================================================================================

    /// <summary>⭐ T8's refusal case: a referentially-invalid container — an atom id the catalog does
    /// not have — is <c>ContainerValidator</c>'s own named rejection at 409, never a 500 and never a
    /// silently-empty preview.</summary>
    [Fact]
    public async Task Preview_withAnAtomTheCatalogDoesNotHave_is409WithANamedReason()
    {
        var (status, body) = await Post(Body(new
        {
            atoms = new[] { new { seq = 0, atomId = "atom.not-a-real-atom.t9" } },
            pool = Array.Empty<object>(),
            prefixRolls = 0,
        }));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("item.preview.container-rejected", body.GetProperty("reason").GetString());
        Assert.Contains("atom.not-a-real-atom.t9", body.GetProperty("reason").GetString());
        Assert.Equal(PreviewContainer, body.GetProperty("containerId").GetString());
    }

    /// <summary>A missing required field is a 400 with the rule's own name — the request is malformed,
    /// not the content.</summary>
    [Fact]
    public async Task Preview_withNoFixedCore_is400WithANamedReason()
    {
        var (status, body) = await Post(Body(new { atoms = Array.Empty<object>() }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("item.preview.atoms-required", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Preview_withNoContainerId_is400WithANamedReason()
    {
        var (status, body) = await Post(Body(new { containerId = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("item.preview.container-id-required", body.GetProperty("reason").GetString());
    }

    /// <summary>A base type the corpus does not carry is a 409, not a 500 and not a nameless card —
    /// exactly what <c>GET /api/items/{id}/card</c> already does for a stored item.</summary>
    [Fact]
    public async Task Preview_withABaseTypeTheCorpusDoesNotCarry_is409NotA500()
    {
        var (status, body) = await Post(Body(new { baseTypeId = "item.not-in-the-corpus" }));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("item.preview.base-type-unknown", body.GetProperty("reason").GetString());
        Assert.Contains("item.not-in-the-corpus", body.GetProperty("reason").GetString());
    }

    /// <summary>A rung the stored ladder does not carry is refused by name rather than rendered
    /// colourless.</summary>
    [Fact]
    public async Task Preview_withARarityOffTheStoredLadder_is409WithANamedReason()
    {
        var (status, body) = await Post(Body(new { rarity = "not-a-rung" }));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("item.preview.rarity-unknown", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Preview_withAnUnknownContainerKind_is400WithANamedReason()
    {
        var (status, body) = await Post(Body(new { kind = "Spaceship" }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("item.preview.kind-unknown", body.GetProperty("reason").GetString());
    }
}
