using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Server.Tests.Notifications;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// Plan Task 4A.4 (empire-wonder-surfaces `wonder-wire` §Design 1–4, §Testing strategy): every wire
/// field resolves over HTTP against real catalog rows — the two 4B.1 seed wonders
/// (`standing-stones`, `sunspire-throne`) plus three synthetic test rows that carry the nonzero
/// magnitudes the shipped seed keeps at placeholder 0 (an Empire/Unique 250-valueMilli wonder for
/// the upkeep/production flow proof, a Sector/Unique one for the sector-count proof, and an
/// ItemStorage depot — sector storage has no base allowance without one).
///
/// <para>Corpus discipline: the real corpus is loaded read-only and the synthetic rows are appended
/// **in memory** (`StructureCorpus.FromRows`/`WithRows`), so this fixture writes nothing and restores
/// the real corpus on dispose — no temp directory, no copy, no delete, which is the shape
/// `docs/contributing/testing-standard.md` bans. Safe without a shared collection: this assembly is
/// serialized (<c>AssemblyParallelism.cs</c>), and the Data assembly runs in a separate process.</para>
///
/// <para>Slot planting is raw-SQL fixture setup (`UPDATE rpg_world_slots`, the WonderBuild suite's
/// own precedent): it bypasses slot-kind/admission/gate checks — those are 4A.2/admission +
/// BuildResolver's modules, proven elsewhere — because the scan/projection under test reads
/// `StructureId` only. The honest end-to-end build planting is 4B.4's live probe, explicitly out
/// of scope here (spec §Testing strategy "Not covered here").</para>
/// </summary>
[Collection("NotificationHub")]
public class WorldWonderWireTests : IAsyncLifetime
{
    const string WorldId = "w-wonder-wire";
    const string EmpireWonderId = "test-empire-wire";
    const long EmpireWonderValueMilli = 250;
    const string SectorWonderId = "test-sector-wire";
    const string TestDepotId = "test-depot-wire";
    const long TestDepotBonus = 6;
    const string RelicContainer = "relic.wire-cost-token";

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    readonly string _realCorpusRoot = Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures");
    static bool _policiesConfigured;

    static void ConfigurePoliciesOnce()
    {
        if (_policiesConfigured) return;
        WorldPolicyTestBootstrap.EnsureConfigured();
        var tuningDir = Path.Combine(FindRepoRootStatic(), "data", "tuning");
        WonderPolicy.Configure(WonderTuningLoader.Parse(
            File.ReadAllText(Path.Combine(tuningDir, "loam-relics-wonders.v1.json"))));
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));
        _policiesConfigured = true;
    }

    public async Task InitializeAsync()
    {
        ConfigurePoliciesOnce();
        ConfigureInMemoryCorpus();

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<DelveBattleSessionManager>();
        builder.WebHost.UseUrls(baseUrl);
        builder.Services.AddNotificationEndpointStubs(); // NS3.4 regression: MapWorld's /commit route now needs these to resolve
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapWorld();
        var test = _app.MapGroup("/api/test");
        test.MapWorldTest();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        var created = await _http.PostAsJsonAsync("/api/test/world/create", new
        {
            worldId = WorldId, templateId = "two-hearths", seed = "7"
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = RelicContainer, Kind = ContainerKind.Relic,
        }).IsOk);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        StructureCatalog.Configure(StructureCorpus.Load(_realCorpusRoot));
        _testStore.Dispose();
    }

    // ---- helpers -------------------------------------------------------------------------------

    async Task<JsonElement> State(string faction)
    {
        var state = await _http.GetFromJsonAsync<JsonElement>($"/api/world/{WorldId}/state?asFaction={faction}");
        return state;
    }

    async Task<JsonElement> Sector(string faction, string sectorId)
    {
        var state = await State(faction);
        return state.GetProperty("sectors").EnumerateArray().Single(s => s.GetProperty("sectorId").GetString() == sectorId);
    }

    static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    /// <summary>Empty slot indexes on one owned sector, discovered live off the wire.</summary>
    static List<int> EmptySlots(JsonElement sector) =>
        sector.GetProperty("slots").EnumerateArray()
            .Where(sl => sl.GetProperty("structureId").ValueKind == JsonValueKind.Null)
            .Select(sl => sl.GetProperty("slotIndex").GetInt32())
            .OrderBy(i => i)
            .ToList();

    void PlantSlot(string sectorId, int slotIndex, string structureId, int? constructionTurnsRemaining)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_slots SET structure_id = $id, construction_turns_remaining = $ctr
            WHERE world_id = $w AND sector_id = $s AND slot_index = $i;
            """;
        cmd.Parameters.AddWithValue("$id", structureId);
        cmd.Parameters.AddWithValue("$ctr", (object?)constructionTurnsRemaining ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.Parameters.AddWithValue("$i", slotIndex);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    /// <summary>
    /// Advances the open turn with no orders. Required after <see cref="PlantSlot"/> whenever the
    /// assertion reads BELIEF (`StructureId`/facet off `believed.Slots`, persisted as
    /// `slots_json` and re-snapshotted only by the commit diff): counts/upkeep/contribution read
    /// live truth and need no commit, the slot display does.
    /// </summary>
    void CommitTurn()
    {
        var world = _store.LoadWorldState(WorldId)!;
        var open = _store.GetWorldHeader(WorldId)!.CurrentTurn;
        WorldTurnCommitResult last = default!;
        foreach (var f in world.Factions.OrderBy(f => f.PolicyId is null).Select(f => f.FactionId))
            last = _store.CommitWorldTurn(WorldId, f, open);
        Assert.True(last.Advanced, $"turn did not advance: {last.Reason}");
    }

    int _relicSeq;

    string MintRelic(string playerStr)
    {
        var n = _relicSeq++;
        var instanceId = $"wire-relic-{Guid.NewGuid().ToString("N")[..8]}";
        var grant = new LootGrant(n, DropEntryKind.Relic, RelicContainer, 1, AffixChannels.Drop,
            BaseTypeId: null, Frame: null, Role: null, RarityId: null,
            ItemLevel: 20, RollSeed: 5000UL + (ulong)n);
        var result = _store.MintRelic(grant, playerStr, thetaContent: 20, Tuning, instanceId: instanceId);
        Assert.True(result.Rejection.IsOk, result.Rejection.ToString());
        return instanceId;
    }

    void LoadAboard(string legion, long playerId, string instanceId)
    {
        var (ok, reason) = _store.LoadCargo(WorldId, legion, playerId,
            "instance", instanceId, null, 0, weightEach: 10);
        Assert.True(ok, reason);
    }

    /// <summary>
    /// The shipped corpus plus this suite's three synthetic rows, built **in memory** — the corpus is data,
    /// and the catalog only ever needed the rows (`StructureCorpus.FromRows`/`WithRows`, the entrance added
    /// by `data-test-substrate` BU8). This fixture used to copy all 28 files of `gk-data/packs/fusion/data/seed/structures` into
    /// `%TEMP%/wonder-wire-test-{guid}`, write three row files beside the copy and delete the directory in
    /// dispose. The copy, the writes and the delete are gone; the rows are identical, so the superset
    /// property is preserved — a suite reading this static catalog concurrently still sees every shipped row.
    /// </summary>
    void ConfigureInMemoryCorpus() =>
        StructureCatalog.Configure(StructureCorpus.Load(_realCorpusRoot).WithRows(
            // Nonzero-magnitude Empire wonder: the shipped seed keeps valueMilli at placeholder 0, which
            // would let an always-zero projection pass. This row proves the term FLOWS.
            Row(EmpireWonderId, role: "Bank", requiredSlotKind: "Shrine", strengthBand: "stone",
                magnitudes: DefaultMagnitudes with
                {
                    StructureKind = "Yield",
                    WonderScope = "Empire",
                    WonderRarity = "Unique",
                    WonderEffects = new[]
                    {
                        new WonderEffectMagnitude("LoamGenerationRate", "Empire", EmpireWonderValueMilli),
                    },
                    RelicCost = 1,
                }),
            // The sector-scoped LoamSource wonder, under this suite's own id so a projection cannot pass on
            // the shipped row that happens to share its scope.
            Row(SectorWonderId, role: "Extract", requiredSlotKind: "Rootbed", strengthBand: "stone",
                magnitudes: DefaultMagnitudes with
                {
                    StructureKind = "LoamSource",
                    WonderScope = "Sector",
                    WonderRarity = "Unique",
                    WonderEffects = new[] { new WonderEffectMagnitude("LoamGenerationRate", "Sector", 0) },
                    RelicCost = 1,
                }),
            Row(TestDepotId, role: "Store", requiredSlotKind: "Wildland",
                magnitudes: DefaultMagnitudes with
                {
                    StructureKind = "ItemStorage",
                    ItemStorageCapacityBonus = TestDepotBonus,
                })));

    /// <summary>Every magnitude these rows do not name, at its structural default — field for field what
    /// the hand-written corpus JSON said.</summary>
    static readonly StructureMagnitudes DefaultMagnitudes = new(
        StructureKind: "Storage",
        Cost: 0,
        YieldMultiplierMilli: 1000,
        BuildTurns: 0,
        CapacityBonus: 0,
        ItemStorageCapacityBonus: 0,
        FlatYieldPerTurn: 0,
        ConstructRubbleCost: 0,
        ConstructIronworkCost: 0,
        MaterialTier: 0,
        BlocksMovement: false,
        BlocksLineOfFire: false,
        ObstacleKind: "None",
        CoverPowerMilli: 0,
        CoverRadius: 0,
        EntryStaminaMultiplierMilli: 1000,
        VisionRangeTiles: null,
        ContainerId: null);

    /// <summary>The one row shape these fixtures author: identity plus the magnitudes the caller names.</summary>
    static StructureCorpusRow Row(
        string id, string role, string requiredSlotKind, StructureMagnitudes magnitudes,
        string strengthBand = "rubble") =>
        new(
            StructureId: id,
            Name: id,
            Role: role,
            RequiredSlotKind: requiredSlotKind,
            StrengthBand: strengthBand,
            AcquisitionPaths: new[] { "built" },
            ControlPoint: true,
            Magnitudes: magnitudes);

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string FindRepoRootStatic()
    {
        return KeepverseRoots.Core();
    }

    // ---- catalog identity: every row names scope/rarity/cost/cap ----------------------------------

    [Fact]
    public async Task Catalog_names_wonder_identity_cost_and_cap_per_row()
    {
        var catalog = await _http.GetFromJsonAsync<JsonElement>("/api/world/catalog");
        var rows = catalog.GetProperty("structures").EnumerateArray().ToList();
        JsonElement Row(string id) => rows.Single(r => r.GetProperty("structureId").GetString() == id);

        // standing-stones (4B.1, real seed): Sector/Common/1/uncapped.
        var stones = Row("standing-stones");
        Assert.Equal("Sector", stones.GetProperty("wonderScope").GetString());
        Assert.Equal("Common", stones.GetProperty("wonderRarity").GetString());
        Assert.Equal(1, stones.GetProperty("relicCost").GetInt64());
        Assert.Equal(long.MaxValue, stones.GetProperty("existenceCap").GetInt64());

        // sunspire-throne (4B.1, real seed): Empire/Unique/3/tuning number.
        var throne = Row("sunspire-throne");
        Assert.Equal("Empire", throne.GetProperty("wonderScope").GetString());
        Assert.Equal("Unique", throne.GetProperty("wonderRarity").GetString());
        Assert.Equal(3, throne.GetProperty("relicCost").GetInt64());
        Assert.Equal(WonderPolicy.ExistenceCapFor(WonderScope.Empire, WonderRarity.Unique),
            throne.GetProperty("existenceCap").GetInt64());

        // A non-Wonder row: null/null/0/uncapped (the pairing invariant, on the wire).
        var ordinary = rows.First(r =>
            r.GetProperty("wonderScope").ValueKind == JsonValueKind.Null
            && r.GetProperty("structureId").GetString() != EmpireWonderId
            && r.GetProperty("structureId").GetString() != SectorWonderId);
        Assert.Equal(JsonValueKind.Null, ordinary.GetProperty("wonderRarity").ValueKind);
        Assert.Equal(0, ordinary.GetProperty("relicCost").GetInt64());
        Assert.Equal(long.MaxValue, ordinary.GetProperty("existenceCap").GetInt64());

        // Envelope + closed vocabulary (validation-ssot): every row carries all four fields;
        // scopes/rarities are closed-vocab members — reserved tiers can never reach the wire
        // (Core's Validate refuses them at startup, proven by WonderCatalogTests).
        foreach (var r in rows)
        {
            Assert.True(r.TryGetProperty("wonderScope", out _));
            Assert.True(r.TryGetProperty("wonderRarity", out _));
            Assert.True(r.TryGetProperty("relicCost", out _));
            Assert.True(r.TryGetProperty("existenceCap", out _));
            var scope = r.GetProperty("wonderScope");
            var rarity = r.GetProperty("wonderRarity");
            Assert.True(scope.ValueKind == JsonValueKind.Null
                || scope.GetString() is "Sector" or "Empire");
            Assert.True(rarity.ValueKind == JsonValueKind.Null
                || rarity.GetString() is "Common" or "Unique");
            // Null-together: never one set and the other missing.
            Assert.Equal(
                scope.ValueKind == JsonValueKind.Null,
                rarity.ValueKind == JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Existence_cap_follows_the_tuning_file_never_the_code()
    {
        var tuningDir = Path.Combine(FindRepoRootStatic(), "data", "tuning");
        try
        {
            // A different cap with no code change: the wire must move with the file.
            WonderPolicy.Configure(new WonderTuning(1, 99, new WonderUniqueExistenceCapTuning(7, 9)));
            var catalog = await _http.GetFromJsonAsync<JsonElement>("/api/world/catalog");
            var throne = catalog.GetProperty("structures").EnumerateArray()
                .Single(r => r.GetProperty("structureId").GetString() == "sunspire-throne");
            Assert.Equal(9, throne.GetProperty("existenceCap").GetInt64());
        }
        finally
        {
            WonderPolicy.Configure(WonderTuningLoader.Parse(
                File.ReadAllText(Path.Combine(tuningDir, "loam-relics-wonders.v1.json"))));
        }
    }

    // ---- slot facet: identity inline, fog-parity with StructureId -----------------------------------

    [Fact]
    public async Task Slot_facet_rides_inline_with_fog_parity_to_structure_id()
    {
        var dave = await State("dave");
        var host = dave.GetProperty("sectors").EnumerateArray()
            .First(s => string.Equals(s.GetProperty("ownerFactionId").GetString(), "dave", StringComparison.Ordinal)
                && EmptySlots(s).Count >= 3);
        var hostId = Str(host, "sectorId");
        var slots = EmptySlots(host);
        PlantSlot(hostId, slots[0], "standing-stones", null);
        PlantSlot(hostId, slots[1], "no-such-structure", null);
        PlantSlot(hostId, slots[2], "well", null);
        // Belief refresh: the slot display reads the persisted intel snapshot, not live truth.
        CommitTurn();

        // Fog parity, both viewers, every slot: the facet never leaks beyond the visible
        // StructureId — null for empty slots (never a default), null for unknown ids and
        // non-Wonder rows (spec §Testing strategy), and byte-exact with the catalog row otherwise.
        foreach (var faction in new[] { "dave", "zomboss" })
        {
            var view = await State(faction);
            foreach (var sector in view.GetProperty("sectors").EnumerateArray())
                foreach (var slot in sector.GetProperty("slots").EnumerateArray())
                {
                    var id = slot.GetProperty("structureId");
                    var scope = slot.GetProperty("wonderScope");
                    var rarity = slot.GetProperty("wonderRarity");
                    string? expectedScope = null;
                    string? expectedRarity = null;
                    if (id.ValueKind != JsonValueKind.Null && StructureCatalog.IsKnown(id.GetString()!))
                    {
                        var row = StructureCatalog.Get(id.GetString()!);
                        expectedScope = row.WonderScope?.ToString();
                        expectedRarity = row.WonderRarity?.ToString();
                    }
                    Assert.Equal(expectedScope, scope.ValueKind == JsonValueKind.Null ? null : scope.GetString());
                    Assert.Equal(expectedRarity, rarity.ValueKind == JsonValueKind.Null ? null : rarity.GetString());
                }
        }

        var after = await Sector("dave", hostId);
        var byIndex = after.GetProperty("slots").EnumerateArray()
            .ToDictionary(sl => sl.GetProperty("slotIndex").GetInt32());
        // Wonder row: scope/rarity inline, no second fetch.
        Assert.Equal("Sector", byIndex[slots[0]].GetProperty("wonderScope").GetString());
        Assert.Equal("Common", byIndex[slots[0]].GetProperty("wonderRarity").GetString());
        // Unknown id: the structure shows (belief), the facet stays null.
        Assert.Equal("no-such-structure", byIndex[slots[1]].GetProperty("structureId").GetString());
        Assert.Equal(JsonValueKind.Null, byIndex[slots[1]].GetProperty("wonderScope").ValueKind);
        // Non-Wonder row: structure shows, facet stays null.
        Assert.Equal("well", byIndex[slots[2]].GetProperty("structureId").GetString());
        Assert.Equal(JsonValueKind.Null, byIndex[slots[2]].GetProperty("wonderScope").ValueKind);
        Assert.Equal(JsonValueKind.Null, byIndex[slots[2]].GetProperty("wonderRarity").ValueKind);
    }

    // ---- live counts: acceptance, not completion; Common absent --------------------------------------

    [Fact]
    public async Task Live_counts_count_acceptance_not_completion_and_common_stays_absent()
    {
        var dave = await State("dave");
        var owned = dave.GetProperty("sectors").EnumerateArray()
            .Where(s => string.Equals(s.GetProperty("ownerFactionId").GetString(), "dave", StringComparison.Ordinal)
                && EmptySlots(s).Count >= 3)
            .Take(2)
            .ToList();
        Assert.Equal(2, owned.Count);
        var s1 = Str(owned[0], "sectorId");
        var s2 = Str(owned[1], "sectorId");
        var e1 = EmptySlots(owned[0]);
        var e2 = EmptySlots(owned[1]);

        // s1: one active + one under-construction Empire Unique, plus an active Common (absent
        // from every count by contract). s2: one under-construction Sector Unique.
        PlantSlot(s1, e1[0], EmpireWonderId, null);
        PlantSlot(s1, e1[1], EmpireWonderId, 2);
        PlantSlot(s1, e1[2], "standing-stones", null);
        PlantSlot(s2, e2[0], SectorWonderId, 3);

        var after1 = await Sector("dave", s1);
        Assert.Equal(0, after1.GetProperty("wonderLiveCountSector").GetInt64());
        Assert.Equal(2, after1.GetProperty("wonderLiveCountEmpire").GetInt64());

        var after2 = await Sector("dave", s2);
        Assert.Equal(1, after2.GetProperty("wonderLiveCountSector").GetInt64());
        // Empire scope-unit is the whole holdings: identical on every sector of one faction.
        Assert.Equal(2, after2.GetProperty("wonderLiveCountEmpire").GetInt64());

        // Unowned/unseen: structurally zero, never another faction's numbers.
        var foreign1 = await Sector("zomboss", s1);
        Assert.Equal(0, foreign1.GetProperty("wonderLiveCountSector").GetInt64());
        Assert.Equal(0, foreign1.GetProperty("wonderLiveCountEmpire").GetInt64());
        Assert.Equal(0, foreign1.GetProperty("wonderProductionContribution").GetInt64());
    }

    // ---- upkeep: the fifth operand reconciles through the four-factor Total ---------------------------

    [Fact]
    public async Task Upkeep_operand_reconciles_with_a_nonzero_wonder_term()
    {
        var dave = await State("dave");
        var host = dave.GetProperty("sectors").EnumerateArray()
            .First(s => string.Equals(s.GetProperty("ownerFactionId").GetString(), "dave", StringComparison.Ordinal)
                && EmptySlots(s).Count >= 2);
        var hostId = Str(host, "sectorId");
        var slots = EmptySlots(host);
        // Active 250-valueMilli Empire wonder + an under-construction one (counted by the scan,
        // correctly EXCLUDED from upkeep — the two consumers differ on purpose).
        PlantSlot(hostId, slots[0], EmpireWonderId, null);
        PlantSlot(hostId, slots[1], EmpireWonderId, 2);

        var state = await State("dave");
        var sector = state.GetProperty("sectors").EnumerateArray()
            .Single(s => s.GetProperty("sectorId").GetString() == hostId);
        var breakdown = sector.GetProperty("upkeepBreakdown");

        var expectedWonder = checked(EmpireWonderValueMilli * LoamPolicy.EmpireWonderUpkeepRateMilli) / 1000;
        Assert.True(expectedWonder > 0);
        Assert.Equal(expectedWonder, breakdown.GetProperty("wonderUpkeep").GetInt64());

        var sum = breakdown.GetProperty("base").GetInt64()
            + breakdown.GetProperty("garrison").GetInt64()
            + breakdown.GetProperty("development").GetInt64()
            + breakdown.GetProperty("danger").GetInt64()
            + breakdown.GetProperty("wonderUpkeep").GetInt64();
        var season = state.GetProperty("calendar").GetProperty("season").GetInt32();
        var seasonMilli = FusionRpg.Core.World.WorldTuningHub.Tuning.Seasons.UpkeepMilli[season];
        var recombined = checked(sum
            * breakdown.GetProperty("intensityMilli").GetInt64()
            * breakdown.GetProperty("handicapMilli").GetInt64()
            * seasonMilli / 1_000_000_000);
        Assert.Equal(sector.GetProperty("loamUpkeep").GetInt64(), recombined);
    }

    // ---- production: attributable contribution reconciles ------------------------------------------------

    [Fact]
    public async Task Production_attribution_reconciles_and_zeroes_without_wonders()
    {
        var dave = await State("dave");
        var host = dave.GetProperty("sectors").EnumerateArray()
            .First(s => string.Equals(s.GetProperty("ownerFactionId").GetString(), "dave", StringComparison.Ordinal)
                && EmptySlots(s).Count >= 1);
        var hostId = Str(host, "sectorId");
        PlantSlot(hostId, EmptySlots(host)[0], EmpireWonderId, null);

        // The stored modifier refreshes through the real Production phase, never a rescan.
        var world = _store.LoadWorldState(WorldId)!;
        var open = _store.GetWorldHeader(WorldId)!.CurrentTurn;
        WorldTurnCommitResult last = default!;
        foreach (var f in world.Factions.OrderBy(f => f.PolicyId is null).Select(f => f.FactionId))
            last = _store.CommitWorldTurn(WorldId, f, open);
        Assert.True(last.Advanced, $"turn did not advance: {last.Reason}");

        var stored = _store.LoadWorldState(WorldId)!;
        var daveModifier = stored.Factions.Single(f => f.FactionId == "dave").ScopeModifierMilli;
        Assert.Equal(1000 + EmpireWonderValueMilli, daveModifier);
        var zombossModifier = stored.Factions.Single(f => f.FactionId == "zomboss").ScopeModifierMilli;
        Assert.Equal(1000, zombossModifier);

        var sector = await Sector("dave", hostId);
        var live = stored.Sectors.Single(s => s.SectorId == hostId);
        var expected = checked(
            LoamProduction.For(live, daveModifier) - LoamProduction.For(live, 1000));
        Assert.Equal(expected, sector.GetProperty("wonderProductionContribution").GetInt64());

        // A Wonder-free faction reads exactly 0 (stored 1000, byte-identical worlds).
        var zombossSector = (await State("zomboss")).GetProperty("sectors").EnumerateArray()
            .First(s => string.Equals(s.GetProperty("ownerFactionId").GetString(), "zomboss", StringComparison.Ordinal));
        Assert.Equal(0, zombossSector.GetProperty("wonderProductionContribution").GetInt64());
    }

    // ---- reachability GET: the gate's predicate as a planning-time list -----------------------------------

    [Fact]
    public async Task Reachability_matches_the_gate_over_http_and_writes_nothing()
    {
        var playerId = _store.GetCurrentPlayerId();
        var playerStr = playerId.ToString();
        var dave = await State("dave");
        var legion = dave.GetProperty("entities").EnumerateArray().First();
        var legionId = Str(legion, "entityId");
        var atSector = legion.GetProperty("atSectorId").GetString()!;
        var depotSlot = EmptySlots(await Sector("dave", atSector))[0];
        PlantSlot(atSector, depotSlot, TestDepotId, null);

        var aboard = MintRelic(playerStr);
        var stored = MintRelic(playerStr);
        var armoury = MintRelic(playerStr);
        LoadAboard(legionId, playerId, aboard);
        LoadAboard(legionId, playerId, stored);
        var seq = _store.ListCargo(WorldId, legionId).First(r => r.InstanceId == stored).Seq;
        var (depOk, depReason, _) = _store.DepositCargo(WorldId, legionId, atSector, seq);
        Assert.True(depOk, depReason);

        var reach = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/world/{WorldId}/relic-reachability?entityId={legionId}&sectorId={atSector}&asFaction=dave");
        Assert.Equal(WorldId, reach.GetProperty("worldId").GetString());
        Assert.Equal(legionId, reach.GetProperty("entityId").GetString());
        Assert.Equal(atSector, reach.GetProperty("sectorId").GetString());
        var ids = reach.GetProperty("reachableInstanceIds").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        Assert.Equal(
            new[] { aboard, stored }.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            ids.OrderBy(id => id, StringComparer.Ordinal).ToList());
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(armoury, ids);

        // Unknown faction refuses — never silent omniscience (same gate as /state).
        var denied = await _http.GetAsync(
            $"/api/world/{WorldId}/relic-reachability?entityId={legionId}&sectorId={atSector}&asFaction=no-such-faction");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("faction.unknown", await denied.Content.ReadAsStringAsync());

        // Unknown pair reads [], inert — and the read performs no write of any kind.
        var cargoBefore = _store.ListCargo(WorldId, legionId).Select(r => (r.Seq, r.InstanceId)).ToList();
        var storageBefore = _store.ListSectorStorage(WorldId, atSector).Select(r => (r.Seq, r.InstanceId)).ToList();
        var empty = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/world/{WorldId}/relic-reachability?entityId=no-such-legion&sectorId=no-such-sector&asFaction=dave");
        Assert.Empty(empty.GetProperty("reachableInstanceIds").EnumerateArray());
        Assert.Equal(cargoBefore, _store.ListCargo(WorldId, legionId).Select(r => (r.Seq, r.InstanceId)).ToList());
        Assert.Equal(storageBefore, _store.ListSectorStorage(WorldId, atSector).Select(r => (r.Seq, r.InstanceId)).ToList());
    }
}
