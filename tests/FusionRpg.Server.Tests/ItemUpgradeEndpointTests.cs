using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// species-gear-chain T37 — the upgrade verb through the REAL endpoint. The row's acceptance has to be
/// proven by a caller entering through `POST /api/items/workbench/upgrade` (a verb reachable only from a
/// test method is not a production host), so this fixture stands up its own app the way
/// `ItemWorkbenchEndpointsTests` does, with the two inputs the upgrade needs and the shared fixture
/// deliberately does NOT wire (`affixFamilyRoles` for Rule 1, `forgeMintCells` for the successor's
/// container — wiring those into the shared bench would break the forge refusal its own test asserts).
/// </summary>
public class ItemUpgradeEndpointTests : IAsyncLifetime
{
    const string InputBase = "item.up-test-base-a-001";
    const string SuccessorBase = "item.up-test-base-b-001";
    /// <summary>Same frame, a role the fixture's affix family may NOT roll (Rule 1's refusal).</summary>
    const string OtherRoleBase = "item.up-test-core-guard-001";
    /// <summary>A different frame (the frame check's refusal).</summary>
    const string OtherFrameBase = "item.up-test-plant-001";
    const string Family = "atom.up-core";
    const string Role = "armament-primary";
    const string Rung = "chaff";

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    long _playerId;
    string _playerKey = "";
    string _instanceId = "";
    string _containerId = "";
    WebApplication _app = null!;
    MaterialTuning _materials = null!;
    MaterialRecipeCatalog _recipes = null!;
    EnhancementTuning _enhancement = null!;
    SocketTuning _sockets = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _playerKey = _playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Both chassis exist in the store's base-type table, in the SAME frame — the verb reads each
        // one's own `(frame, role)` from here (`RpgStore.GetBaseType`).
        _store.ImportBaseTypes(new[]
        {
            new BaseTypeSeedRow(InputBase, "humanoid", Role),
            new BaseTypeSeedRow(SuccessorBase, "humanoid", Role),
            // Rule 1's refusal path: the SAME frame, a role this item's affix family cannot roll.
            new BaseTypeSeedRow(OtherRoleBase, "humanoid", "core-guard"),
            // The frame check's refusal path: a different frame entirely.
            new BaseTypeSeedRow(OtherFrameBase, "plant", Role),
        });

        _materials = MaterialTuning.Parse(File.ReadAllText(Tuning(SocketTuningFiles.Materials)));
        _enhancement = EnhancementTuning.Parse(File.ReadAllText(Tuning("enhancement.v1.json")));
        _sockets = SocketTuning.Parse(File.ReadAllText(Tuning(SocketTuningFiles.Current)));
        var rarity = ItemRarityTuning.Parse(File.ReadAllText(Tuning("item-rarity.v1.json")));
        _recipes = MaterialRecipeCatalog.Load(
            Directory.EnumerateFiles(Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "recipes"), "*.json")
                .OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText),
            _materials);
        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);
        _store.SeedRarityLadder(rarity);

        // One carried affix family, legal on the successor's role.
        var atomId = AtomRow.DeriveId(Family, "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = Family,
            Variant = "",
            Tier = 1,
            Name = "up-core",
            ParamsJson = """{"channel":"maxHp","op":"flat","amount":10}""",
        }).IsOk);

        _containerId = "item.up-test-container";
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = _containerId,
            Kind = ContainerKind.Item,
            Slot = Role,
            Rarity = Rung,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk, "container upsert");

        _instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = _containerId,
            RollSeed = 4242,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, atomId, """{"amount":10}""") },
        });
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = _instanceId,
            PlayerId = _playerKey,
            AcquiredUtc = "2026-09-21T00:00:00Z",
            OriginKind = "drop",
        }).IsOk, "acquire");
        _store.PersistLoot(
            _playerKey,
            new LootManifest("up-drop", "table.up-test", 7UL, 20, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "up-test", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(_instanceId, 0, InputBase,
                    _store.ListRarities().First(r => r.RarityId == Rung).Ordinal, 20, "humanoid", Role, "drop"),
            });

        // The upgrade's own price is souls, so the owner must be able to pay it.
        _store.AwardSouls(_playerId, 1_000_000, "test-seed", "up-test-seed-1");

        // The AUTHORED edge under test (a fabricated one: the shipped table is empty today) and Rule
        // 1's role allow-list.
        ItemUpgradeEdgeHub.Configure(new ItemUpgradeEdgeTable(1, 1,
            new Dictionary<string, string>(StringComparer.Ordinal) { [InputBase] = SuccessorBase }));

        var bench = new ItemWorkbench(
            _store, _materials, _recipes, _enhancement, _sockets,
            baseTypeSocketMax: id => id == InputBase || id == SuccessorBase ? 2 : (int?)null,
            forgeMintCells: Array.Empty<RoleFamilyCell>(),
            lookupAtom: id => _store.GetAtom(id),
            lookupAffix: id => _store.GetAffix(id),
            affixFamilyRoles: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                [Family] = new HashSet<string> { Role },
            },
            // The two implicits, so the preview can show the swap the spec's Rule 2 demands.
            baseTypeImplicitFamily: id => id == InputBase ? "implicit.cloth"
                : id == SuccessorBase ? "implicit.leather" : null);

        var port = GetFreeTcpPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapWorkbench(bench);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.StopAsync();
        _testStore?.Dispose();
    }

    [Fact]
    public async Task Upgrade_consumesTheInputAndReturnsTheSuccessorThroughTheRealEndpoint()
    {
        var res = await _http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId = _instanceId, correlationId = "corr-up-endpoint-1" });

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("ok").GetBoolean(), body.GetProperty("reason").GetString());
        Assert.Equal("upgraded", body.GetProperty("outcome").GetString());

        var successorId = body.GetProperty("instanceId").GetString()!;
        Assert.NotEqual(_instanceId, successorId);

        // The successor is a real instance wearing the AUTHORED successor chassis, and its carried
        // affixes are the consumed instance's own atom rows.
        Assert.Equal(SuccessorBase, _store.GetItemGeneration(successorId)!.BaseTypeId);
        Assert.Equal(_store.GetInstance(_instanceId)!.Atoms.Count, _store.GetInstance(successorId)!.Atoms.Count);

        // The upgrade is the CONSUMED item's own history, and the input was consumed rather than
        // deleted (the salvage-shaped disposition keeps the ledger readable).
        var op = Assert.Single(_store.ReadMutationOps(_instanceId));
        Assert.Equal("upgrade", MutationOpKinds.Id(op.Kind));
        Assert.Equal("corr-up-endpoint-1", op.CorrelationId);
        Assert.NotNull(_store.GetInstance(_instanceId));
    }

    /// <summary>
    /// species-gear-chain T37 criterion 1 on REAL corpus data: an armour piece from the SHIPPED
    /// base-type tree upgrades to the successor its AUTHORED edge names, through the real endpoint,
    /// consuming the input. Every other case in this file drives the verb with a fabricated edge; this
    /// one configures the hub from the emitted corpus the server boots with
    /// (`ItemUpgradeEdgeCorpusReader` over `gk-data/packs/fusion/data/seed/items/base-types/**`, the same read
    /// `Program.cs`'s `ItemUpgradeEdgeHub.Configure` performs) and uses two real armour ids.
    /// </summary>
    [Fact]
    public async Task A_realCorpusArmourBaseTypeUpgradesToItsAuthoredSuccessor()
    {
        const string realInput = "item.humanoid-torso-a-001";
        const string realSuccessor = "item.humanoid-torso-a-006";
        const string armourRole = "core-guard";

        var docs = Directory.EnumerateFiles(
                Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types"),
                "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText);
        ItemUpgradeEdgeHub.Configure(ItemUpgradeEdgeCorpusReader.Parse(docs));
        Assert.Equal(realSuccessor, ItemUpgradeEdgeHub.EdgeFor(realInput));

        // Both chassis in the store's own base-type table, in the SAME frame and role — the verb reads
        // each one's `(frame, role)` from here (`RpgStore.GetBaseType`).
        _store.ImportBaseTypes(new[]
        {
            new BaseTypeSeedRow(realInput, "humanoid", armourRole),
            new BaseTypeSeedRow(realSuccessor, "humanoid", armourRole),
        });

        var atomId = AtomRow.DeriveId(Family, "", 1);
        var containerId = "item.up-real-armour-container";
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Slot = armourRole,
            Rarity = Rung,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk, "container upsert");

        var instanceId = _store.SaveInstance(new InstanceRow
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
            AcquiredUtc = "2026-09-21T00:00:00Z",
            OriginKind = "drop",
        }).IsOk, "acquire");
        _store.PersistLoot(
            _playerKey,
            new LootManifest("up-real", "table.up-real", 9UL, 20, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "up-real", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(instanceId, 0, realInput,
                    _store.ListRarities().First(r => r.RarityId == Rung).Ordinal, 20, "humanoid",
                    armourRole, "drop"),
            });

        // Rule 1 is not what this case proves: declare the carried family legal on the armour role so
        // the ONLY thing under test is whether the authored edge reaches the verb.
        var realBench = new ItemWorkbench(
            _store, _materials, _recipes, _enhancement, _sockets,
            baseTypeSocketMax: id => id == realInput || id == realSuccessor ? 2 : (int?)null,
            forgeMintCells: Array.Empty<RoleFamilyCell>(),
            lookupAtom: id => _store.GetAtom(id),
            lookupAffix: id => _store.GetAffix(id),
            affixFamilyRoles: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                [Family] = new HashSet<string> { Role, armourRole },
            },
            baseTypeImplicitFamily: id => id == realInput ? "implicit.cloth"
                : id == realSuccessor ? "implicit.leather" : null);

        var port = GetFreeTcpPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.MapWorkbench(realBench);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var res = await http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId, correlationId = "corr-up-real-corpus-1" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean(), body.GetProperty("reason").GetString());

        // The successor wears the chassis the AUTHORED edge names, carried the consumed instance's own
        // atom, and the op is the consumed item's history.
        var successorId = body.GetProperty("instanceId").GetString()!;
        Assert.NotEqual(instanceId, successorId);
        Assert.Equal(realSuccessor, _store.GetItemGeneration(successorId)!.BaseTypeId);
        Assert.Equal(_store.GetInstance(instanceId)!.Atoms.Count, _store.GetInstance(successorId)!.Atoms.Count);
        Assert.Equal("upgrade", MutationOpKinds.Id(Assert.Single(_store.ReadMutationOps(instanceId)).Kind));

        await app.StopAsync();
    }

    /// <summary>
    /// species-gear-chain T60 (potential half, and the durability arithmetic beside it) — the successor
    /// inherits the CONSUMED item's used fraction of BOTH head pairs, computed at the Server layer from
    /// the two maxes the craft-wear source derives (the input's STORED pair is the truth: it is what a
    /// spend wrote, T61). The Data test pins that the write lands in the successor's own transaction;
    /// this pins the arithmetic the endpoint's caller performs, which nothing else asserted.
    /// </summary>
    [Fact]
    public async Task The_successor_carries_the_used_fraction_of_both_head_pairs()
    {
        const string realInput = "item.humanoid-torso-a-001";
        const string realSuccessor = "item.humanoid-torso-a-006";
        const string armourRole = "core-guard";

        var docs = Directory.EnumerateFiles(
                Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types"),
                "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText);
        ItemUpgradeEdgeHub.Configure(ItemUpgradeEdgeCorpusReader.Parse(docs));
        Assert.Equal(realSuccessor, ItemUpgradeEdgeHub.EdgeFor(realInput));

        _store.ImportBaseTypes(new[]
        {
            new BaseTypeSeedRow(realInput, "humanoid", armourRole),
            new BaseTypeSeedRow(realSuccessor, "humanoid", armourRole),
        });

        var atomId = AtomRow.DeriveId(Family, "", 1);
        var containerId = "item.up-potential-container";
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Slot = armourRole,
            Rarity = Rung,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk, "container upsert");

        var instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = containerId,
            RollSeed = 1_337,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, atomId, """{"amount":10}""") },
        });
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId,
            PlayerId = _playerKey,
            AcquiredUtc = "2026-09-21T00:00:00Z",
            OriginKind = "drop",
        }).IsOk, "acquire");
        _store.PersistLoot(
            _playerKey,
            new LootManifest("up-wear", "table.up-wear", 11UL, 20, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "up-wear", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(instanceId, 0, realInput,
                    _store.ListRarities().First(r => r.RarityId == Rung).Ordinal, 20, "humanoid",
                    armourRole, "drop"),
            });

        // The craft-wear source Program.cs builds: class off the shipped base-type corpus, rung off the
        // ladder, both maxes off the deployment-hierarchy derivations.
        var classForBaseType = BaseTypeSocketMaxCorpus.LoadClassById(
            Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types"));
        var deployment = DeploymentHierarchyTuningLoader.Parse(
            File.ReadAllText(Tuning("deployment-hierarchy.v5.json")));
        var rungIndex = RarityLadder.RungIndexOf(Rung);
        Assert.Equal(0, rungIndex);
        var successorClass = classForBaseType(realSuccessor);
        Assert.False(string.IsNullOrEmpty(successorClass),
            "precondition: the successor's class is in the shipped base-type corpus");
        HeadDerivationEntry Entry(string baseTypeId) =>
            new(baseTypeId, classForBaseType(baseTypeId)!, Array.Empty<string>());
        var inputDurabilityMax = DurabilityTable.DeriveMax(
            Entry(realInput), RarityLadder.RungIds[rungIndex], deployment);
        var inputPotentialMax = PotentialTable.DeriveMax(
            Entry(realInput), RarityLadder.RungIds[rungIndex], deployment);
        var successorDurabilityMax = DurabilityTable.DeriveMax(
            Entry(realSuccessor), RarityLadder.RungIds[rungIndex], deployment);
        var successorPotentialMax = PotentialTable.DeriveMax(
            Entry(realSuccessor), RarityLadder.RungIds[rungIndex], deployment);
        Assert.True(inputDurabilityMax > 3 && successorDurabilityMax > 3 &&
            inputPotentialMax > 3 && successorPotentialMax > 3, "precondition: derivable maxes");

        // Both pairs spent down, at DIFFERENT fractions (1/4 and 1/2), so a carry that crossed the wrong
        // pair or copied one value into both could not pass. The fraction is measured against the input's
        // DERIVED max, exactly as durability's landed half does — the stored max is not consulted, because
        // the derived max is the canonical ceiling for that chassis + rung.
        _store.SetDurability(instanceId, max: inputDurabilityMax, current: inputDurabilityMax / 4);
        _store.SetPotential(instanceId, max: inputPotentialMax, current: inputPotentialMax / 2);

        var wear = new CraftWearSource(
            (baseTypeId, idx) => classForBaseType(baseTypeId) is { Length: > 0 } cls &&
                    idx >= 0 && idx < RarityLadder.RungIds.Count
                ? DurabilityTable.DeriveMax(
                    new HeadDerivationEntry(baseTypeId, cls, Array.Empty<string>()),
                    RarityLadder.RungIds[idx], deployment)
                : null,
            deployment.CraftWearPerAttemptMilli,
            PotentialMaxFor: (baseTypeId, idx) => classForBaseType(baseTypeId) is { Length: > 0 } cls2 &&
                    idx >= 0 && idx < RarityLadder.RungIds.Count
                ? PotentialTable.DeriveMax(
                    new HeadDerivationEntry(baseTypeId, cls2, Array.Empty<string>()),
                    RarityLadder.RungIds[idx], deployment)
                : null);

        var bench = new ItemWorkbench(
            _store, _materials, _recipes, _enhancement, _sockets,
            baseTypeSocketMax: id => id == realInput || id == realSuccessor ? 2 : (int?)null,
            forgeMintCells: Array.Empty<RoleFamilyCell>(),
            lookupAtom: id => _store.GetAtom(id),
            lookupAffix: id => _store.GetAffix(id),
            affixFamilyRoles: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                [Family] = new HashSet<string> { Role, armourRole },
            },
            craftWear: wear);

        var (app, http) = await AppFor(bench);
        try
        {
            var res = await http.PostAsJsonAsync("/api/items/workbench/upgrade",
                new { instanceId, correlationId = "corr-up-wear-fraction-1" });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("ok").GetBoolean(), body.GetProperty("reason").GetString());
            var successorId = body.GetProperty("instanceId").GetString()!;

            // Read back through the normal path: both pairs came across as the SAME used fraction
            // (1/4), scaled onto the successor's own derived max — never a fresh full pair.
            var durability = _store.GetDurability(successorId).Current;
            var potential = _store.GetPotential(successorId).Current;
            Assert.NotNull(durability);
            Assert.NotNull(potential);
            // The contract: the input's used fraction, scaled onto the successor's OWN derived max.
            Assert.Equal(
                checked(successorDurabilityMax * (inputDurabilityMax / 4) / inputDurabilityMax),
                durability!.Value);
            Assert.Equal(
                checked(successorPotentialMax * (inputPotentialMax / 2) / inputPotentialMax),
                potential!.Value);
            // A PARTIAL carry: never pristine (a launder) and never zero (an invented pair).
            Assert.InRange(durability!.Value, 1, successorDurabilityMax - 1);
            Assert.InRange(potential!.Value, 1, successorPotentialMax - 1);
        }
        finally { http.Dispose(); await app.StopAsync(); }
    }

    [Fact]
    public async Task A_replayed_correlation_is_idempotent_and_spendsOnce()
    {
        var first = await _http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId = _instanceId, correlationId = "corr-up-endpoint-2" });
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var soulsAfterFirst = _store.GetSoulBalance(_playerId).Balance;

        var second = await _http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId = _instanceId, correlationId = "corr-up-endpoint-2" });
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(firstBody.GetProperty("ok").GetBoolean(), firstBody.GetProperty("reason").GetString());
        Assert.True(secondBody.GetProperty("ok").GetBoolean(), secondBody.GetProperty("reason").GetString());
        // Idempotent: the same op, no second one, and the souls were spent exactly once.
        Assert.Equal("upgraded", secondBody.GetProperty("outcome").GetString());
        Assert.Equal(firstBody.GetProperty("opSeq").GetInt32(), secondBody.GetProperty("opSeq").GetInt32());
        Assert.Equal(soulsAfterFirst, _store.GetSoulBalance(_playerId).Balance);
        Assert.Single(_store.ReadMutationOps(_instanceId));

        // T37: a replay names what the upgrade PRODUCED, not the input it consumed. The spend log's
        // `outcome_ref` is the ONE recorded produced id (`perform` returns the successor's instance
        // id), so the replayed DTO matches the first response's id — the consumed instance is
        // destroyed and an id pointing at it would describe an item the player no longer owns.
        Assert.Equal(firstBody.GetProperty("instanceId").GetString(), secondBody.GetProperty("instanceId").GetString());
        Assert.NotEqual(_instanceId, secondBody.GetProperty("instanceId").GetString());
        // Read back through the normal path: the id the replay names is a real, live instance.
        Assert.NotNull(_store.GetInstance(secondBody.GetProperty("instanceId").GetString()!));
    }

    [Fact]
    public async Task UpgradePreview_showsTheSuccessorAndBothImplicitsAndWritesNothing()
    {
        var soulsBefore = _store.GetSoulBalance(_playerId).Balance;

        var res = await _http.PostAsJsonAsync("/api/items/workbench/upgrade-preview",
            new { instanceId = _instanceId });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("ok").GetBoolean(), body.GetProperty("reason").GetString());
        Assert.Equal(SuccessorBase, body.GetProperty("successorBaseTypeId").GetString());
        // The swap the card must show BEFORE the input is consumed (spec § 2a Rule 2).
        Assert.Equal("implicit.cloth", body.GetProperty("outgoingImplicitFamily").GetString());
        Assert.Equal("implicit.leather", body.GetProperty("incomingImplicitFamily").GetString());
        Assert.Equal(1, body.GetProperty("carriedAffixCount").GetInt32());
        Assert.True(body.GetProperty("soulsCost").GetInt64() > 0);

        // Read-only: nothing spent, no op row, the input still owned and not disposed.
        Assert.Equal(soulsBefore, _store.GetSoulBalance(_playerId).Balance);
        Assert.Empty(_store.ReadMutationOps(_instanceId));
        Assert.NotNull(_store.GetInstance(_instanceId));
    }

    [Fact]
    public async Task UpgradePreview_refusesByNameWhenNoEdgeIsAuthored()
    {
        ItemUpgradeEdgeHub.Configure(new ItemUpgradeEdgeTable(1, 1,
            new Dictionary<string, string>(StringComparer.Ordinal)));

        var res = await _http.PostAsJsonAsync("/api/items/workbench/upgrade-preview",
            new { instanceId = _instanceId });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("upgrade.no-successor", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AnAffixTheSuccessorRoleCannotRoll_refusesThroughTheEndpointAndConsumesNothing()
    {
        // Rule 1 in production: the family is legal on the INPUT's role (armament-primary) and not on the
        // successor's (core-guard). The instance's own atom rows are what get checked, so this is the
        // real predicate, not a fixture invention.
        ItemUpgradeEdgeHub.Configure(new ItemUpgradeEdgeTable(1, 1,
            new Dictionary<string, string>(StringComparer.Ordinal) { [InputBase] = OtherRoleBase }));

        var res = await _http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId = _instanceId, correlationId = "corr-up-illegal-1" });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("upgrade.affix-illegal-on-successor", body.GetProperty("reason").GetString());
        // ⛔ Refuse, never relabel: nothing was spent, no op was written, and the piece is untouched.
        Assert.Empty(_store.ReadMutationOps(_instanceId));
        Assert.NotNull(_store.GetInstance(_instanceId));
    }

    [Fact]
    public async Task ASuccessorInAnotherFrame_refusesThroughTheEndpoint()
    {
        ItemUpgradeEdgeHub.Configure(new ItemUpgradeEdgeTable(1, 1,
            new Dictionary<string, string>(StringComparer.Ordinal) { [InputBase] = OtherFrameBase }));

        var res = await _http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId = _instanceId, correlationId = "corr-up-frame-1" });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("upgrade.successor-not-same-frame", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ReadMutationOps(_instanceId));
    }

    [Fact]
    public async Task ThePreviewQuotesExactlyWhatTheCommitSpends()
    {
        var preview = await _http.PostAsJsonAsync("/api/items/workbench/upgrade-preview",
            new { instanceId = _instanceId });
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(previewBody.GetProperty("ok").GetBoolean(), previewBody.GetProperty("reason").GetString());
        var quoted = previewBody.GetProperty("soulsCost").GetInt64();

        var commit = await _http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId = _instanceId, correlationId = "corr-up-price-1" });
        var commitBody = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(commitBody.GetProperty("ok").GetBoolean(), commitBody.GetProperty("reason").GetString());

        var spent = commitBody.GetProperty("spent").EnumerateArray()
            .Where(l => l.GetProperty("class").GetString() == "Souls")
            .Sum(l => l.GetProperty("qty").GetInt64());

        // A preview that quotes a different price from the one the commit charges is a player-facing
        // defect; both sides build the line from the same helper, and this pins that they agree.
        Assert.True(quoted > 0, "the preview must quote a price");
        Assert.Equal(quoted, spent);
    }

    [Fact]
    public async Task NoAuthoredEdge_refusesByNameThroughTheEndpoint()
    {
        ItemUpgradeEdgeHub.Configure(new ItemUpgradeEdgeTable(1, 1,
            new Dictionary<string, string>(StringComparer.Ordinal)));

        var res = await _http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId = _instanceId, correlationId = "corr-up-endpoint-3" });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("upgrade.no-successor", body.GetProperty("reason").GetString());
    }

    static string Tuning(string file) => Path.Combine(RepoRoot(), "data", "tuning", file);

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A SECOND app over a differently-wired bench, for the infrastructure refusals: each of those
    /// branches exists so a host that has not wired an input refuses BY NAME instead of guessing, and
    /// until now none of them had a test. The caller disposes both halves.
    /// </summary>
    async Task<(WebApplication app, HttpClient http)> AppFor(ItemWorkbench bench)
    {
        var port = GetFreeTcpPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.MapWorkbench(bench);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") });
    }

    ItemWorkbench BenchFor(
        bool affixRoles = true, bool containerCells = true, MaterialTuning? materials = null) =>
        new(_store, materials ?? _materials, _recipes, _enhancement, _sockets,
            baseTypeSocketMax: id => 2,
            forgeMintCells: containerCells ? Array.Empty<RoleFamilyCell>() : null,
            lookupAtom: id => _store.GetAtom(id),
            lookupAffix: id => _store.GetAffix(id),
            affixFamilyRoles: affixRoles
                ? new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    [Family] = new HashSet<string> { Role },
                }
                : null);

    [Fact]
    public async Task Upgrade_refusesByNameWhenTheAffixRoleLookupIsUnwired()
    {
        var (app, http) = await AppFor(BenchFor(affixRoles: false));
        try
        {
            var res = await http.PostAsJsonAsync("/api/items/workbench/upgrade",
                new { instanceId = _instanceId, correlationId = "corr-up-unwired-1" });
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();

            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Contains("upgrade.legality-unavailable", body.GetProperty("reason").GetString());
            Assert.Empty(_store.ReadMutationOps(_instanceId));
            Assert.NotNull(_store.GetInstance(_instanceId));
        }
        finally { http.Dispose(); await app.StopAsync(); }
    }

    [Fact]
    public async Task Upgrade_refusesByNameWhenTheContainerLookupsAreUnwired()
    {
        var (app, http) = await AppFor(BenchFor(containerCells: false));
        try
        {
            var res = await http.PostAsJsonAsync("/api/items/workbench/upgrade",
                new { instanceId = _instanceId, correlationId = "corr-up-unwired-2" });
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();

            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Contains("upgrade.container-unavailable", body.GetProperty("reason").GetString());
            Assert.Empty(_store.ReadMutationOps(_instanceId));
        }
        finally { http.Dispose(); await app.StopAsync(); }
    }

    [Fact]
    public async Task Upgrade_refusesByNameWhenTheShippedSoulsLegIsMissing()
    {
        // The row is required, the LEG is optional (I9 §7.4 leaves cells as em dashes) — so a tuning
        // with `operations.upgrade` present and its souls leg removed parses, and the verb must refuse
        // by name rather than charge nothing.
        var json = File.ReadAllText(Tuning(SocketTuningFiles.Materials));
        using var doc = JsonDocument.Parse(json);
        var ops = doc.RootElement.GetProperty("operations").GetProperty("upgrade");
        var stripped = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        stripped["operations"]!["upgrade"]!.AsObject().Remove("souls");
        var mutated = MaterialTuning.Parse(stripped.ToJsonString());
        Assert.True(ops.TryGetProperty("souls", out _), "precondition: the shipped row carries a souls leg");

        var (app, http) = await AppFor(BenchFor(materials: mutated));
        try
        {
            var res = await http.PostAsJsonAsync("/api/items/workbench/upgrade",
                new { instanceId = _instanceId, correlationId = "corr-up-unwired-3" });
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();

            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Contains("upgrade.tuning-missing-souls-leg", body.GetProperty("reason").GetString());
            Assert.Empty(_store.ReadMutationOps(_instanceId));
        }
        finally { http.Dispose(); await app.StopAsync(); }
    }

    [Fact]
    public async Task Upgrade_refusesByNameWhenTheEdgeNamesAnUnknownBaseType()
    {
        _store.ImportBaseTypes(new[] { new BaseTypeSeedRow("item.up-test-unimported-001", "humanoid", Role) });
        ItemUpgradeEdgeHub.Configure(new ItemUpgradeEdgeTable(1, 1,
            new Dictionary<string, string>(StringComparer.Ordinal) { [InputBase] = "item.up-test-not-imported-at-all" }));

        var res = await _http.PostAsJsonAsync("/api/items/workbench/upgrade",
            new { instanceId = _instanceId, correlationId = "corr-up-unwired-4" });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("upgrade.successor-unknown", body.GetProperty("reason").GetString());
        Assert.Empty(_store.ReadMutationOps(_instanceId));
    }

    [Fact]
    public async Task ThePreviewAndTheCommitRefuseTheSameWayWhenTheShippedSoulsLegIsMissing()
    {
        var json = File.ReadAllText(Tuning(SocketTuningFiles.Materials));
        var stripped = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        stripped["operations"]!["upgrade"]!.AsObject().Remove("souls");
        var mutated = MaterialTuning.Parse(stripped.ToJsonString());

        var (app, http) = await AppFor(BenchFor(materials: mutated));
        try
        {
            var preview = await http.PostAsJsonAsync("/api/items/workbench/upgrade-preview",
                new { instanceId = _instanceId });
            var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
            var commit = await http.PostAsJsonAsync("/api/items/workbench/upgrade",
                new { instanceId = _instanceId, correlationId = "corr-up-parity-1" });
            var commitBody = await commit.Content.ReadFromJsonAsync<JsonElement>();

            // One decision path means one refusal: a preview that says "ok, 0 souls" while the commit
            // refuses is a preview misquoting the price, and the player cannot audit the difference.
            Assert.False(previewBody.GetProperty("ok").GetBoolean());
            Assert.False(commitBody.GetProperty("ok").GetBoolean());
            Assert.Contains("upgrade.tuning-missing-souls-leg", previewBody.GetProperty("reason").GetString());
            Assert.Contains("upgrade.tuning-missing-souls-leg", commitBody.GetProperty("reason").GetString());
        }
        finally { http.Dispose(); await app.StopAsync(); }
    }
}
