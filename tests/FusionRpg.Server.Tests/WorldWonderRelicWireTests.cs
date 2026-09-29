using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.TestSupport;
using FusionRpg.Server.Tests.Notifications;

namespace FusionRpg.Server.Tests;

/// <summary>
/// Task 4A.2 (`wonder-rest` §Design 1-3, §Testing strategy): the Wonder REST entry — C#
/// <c>WorldCommandRequest.RelicInstanceIds</c> + the endpoint mapping line + the FE mirror.
/// Proves at the real HTTP wire (the layer <c>WorldCommandRoundTripPropertyTests</c> bypasses):
/// the picked list reaches <c>WorldCommand.RelicInstanceIds</c> order-preserved, absent/null reads
/// as empty, malformed lists refuse at submit-time admission with admission's own strings
/// per-command, and nothing is silently cleaned. Then the owed live proof at
/// store-integration level: file over HTTP → commit → <c>build.started:</c> → disposition
/// <c>'consumed'</c> + overlays gone → slot completes after <c>BuildTurns</c>, every step read
/// back through the normal path (never the response body alone).
///
/// <para>BOUNDARY (stated, not hidden): this probe uses synthetic test-corpus Wonder rows
/// (superset fixture below — the <c>WonderBuildTests</c> proven shape), not real seed rows.
/// The full RPG-server-debug HTTP probe against the real <c>standing-stones</c> /
/// <c>sunspire-throne</c> rows, with yield + upkeep + persistence, is Task 4B.4's acceptance —
/// this file proves the wire it will run over.</para>
///
/// <para>Fixture notes: all stores are in-memory (<see cref="DataTestStore.Create"/>) and the corpus is
/// the real rows plus two synthetic ones appended **in memory** (<c>StructureCorpus.WithRows</c>), so this
/// file writes nothing at all. The real defs come through that entrance unchanged, so any
/// concurrently-running suite reading a real structure id observes the same answer; this assembly is
/// serialised anyway (<c>AssemblyParallelism.cs</c>). No engine/seed/tuning files change here — and no endpoint
/// count/dup pre-check exists here either (deleted by the strengthen pass; re-adding one is a
/// defect — admission owns both strings).</para>
/// </summary>
[Collection("NotificationHub")]
public class WorldWonderRelicWireTests : IAsyncLifetime
{
    const long PlayerId = 1;
    const string PlayerIdStr = "1";
    const string Commander = "dave";
    const string WorldId = "w-4a2-wonder-wire";
    const string Legion1 = "e-4a2-legion-1";
    const string WonderOneId = "test-wonder-4a2-one";
    const string WonderTwoId = "test-wonder-4a2-two";
    const int WonderOneBuildTurns = 1;
    const string RelicContainer = "relic.4a2-cost-token";

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    readonly string _realCorpusRoot = RealCorpusRoot();
    string _sector = null!;
    int _slotW;
    int _slotX;
    int _relicSeq;

    public async Task InitializeAsync()
    {
        WorldPolicyTestBootstrap.EnsureConfigured();
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 21, worldId: WorldId);
        var home = built.Sectors.FirstOrDefault(s =>
            string.Equals(s.OwnerFactionId, Commander, StringComparison.Ordinal)
            && s.Slots.Any(sl => sl.StructureId is null
                && SlotTypeCatalog.IsKnown(sl.SlotTypeId)
                && SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Wildland)
            && s.Slots.Any(sl => sl.StructureId is null
                && SlotTypeCatalog.IsKnown(sl.SlotTypeId)
                && SlotTypeCatalog.Get(sl.SlotTypeId).Kind != SlotKind.Wildland
                && SlotTypeCatalog.Get(sl.SlotTypeId).Kind != SlotKind.Seat));
        Assert.True(home is not null, "need a dave sector with an empty Wildland slot and another empty non-Seat slot");
        _sector = home!.SectorId;
        _slotW = home.Slots.First(sl => sl.StructureId is null
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Wildland).SlotIndex;
        var x = home.Slots.First(sl => sl.StructureId is null
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind != SlotKind.Wildland
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind != SlotKind.Seat);
        _slotX = x.SlotIndex;
        var kindX = SlotTypeCatalog.Get(x.SlotTypeId).Kind.ToString();

        ConfigureInMemoryCorpus(kindX);

        WorldEntity Legion(string id) => new()
        {
            EntityId = id,
            Kind = WorldEntityKind.Legion,
            OwnerFactionId = Commander,
            AtSectorId = _sector,
            Stance = "march",
            MovementRemaining = 1000,
            Members = new WorldEntityMember[]
            {
                new() { SpeciesId = "peashooterzombie", Level = 1, Hp = 110 },
                new() { SpeciesId = "conezombie", Level = 1, Hp = 110 },
            },
        };

        var customized = built with
        {
            Entities = built.Entities
                .Append(Legion(Legion1))
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList(),
        };

        var (ok, reason, _) = _store.CreateWorld(PlayerId, customized);
        Assert.True(ok, reason);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = RelicContainer, Kind = ContainerKind.Relic,
        }).IsOk);

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.WebHost.UseUrls(baseUrl);
        builder.Services.AddNotificationEndpointStubs(); // NS3.4 regression: MapWorld's /commit route now needs these to resolve
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        _app.MapWorld();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        StructureCatalog.Configure(StructureCorpus.Load(_realCorpusRoot));
        _testStore.Dispose();
    }

    // ---- fixture helpers -------------------------------------------------------------

    static string RealCorpusRoot() => Path.Combine(ContentRoot.Path, "data", "seed", "structures");

    /// <summary>
    /// The shipped corpus plus this suite's two synthetic wonders, built **in memory** — the corpus is data,
    /// and the catalog only ever needed the rows (`StructureCorpus.FromRows`/`WithRows`, the entrance added
    /// by `data-test-substrate` BU8). This fixture used to copy all 28 files of `gk-data/packs/fusion/data/seed/structures` into
    /// `%TEMP%/wonder-wire-4a2-{guid}`, write two row files beside the copy and delete the directory in
    /// dispose. The copy, the writes and the delete are gone; the rows are identical, so the superset
    /// property is preserved — a suite reading this static catalog concurrently still sees every shipped row.
    /// </summary>
    void ConfigureInMemoryCorpus(string secondKind) =>
        StructureCatalog.Configure(StructureCorpus.Load(_realCorpusRoot).WithRows(
            // One relic, and a build that takes turns — the reachability list has to agree with the commit
            // on both facts.
            Row(WonderOneId, role: "Store", requiredSlotKind: "Wildland",
                magnitudes: DefaultMagnitudes with
                {
                    BuildTurns = WonderOneBuildTurns,
                    WonderScope = "Sector",
                    WonderRarity = "Common",
                    WonderEffects = new[] { new WonderEffectMagnitude("LoamGenerationRate", "Sector", 0) },
                    RelicCost = 1,
                }),
            // The second wonder on the sector's other empty kind, so the double-spend case can file two
            // kind-compatible orders that both name relic rows.
            Row(WonderTwoId, role: "Store", requiredSlotKind: secondKind,
                magnitudes: DefaultMagnitudes with
                {
                    WonderScope = "Sector",
                    WonderRarity = "Common",
                    WonderEffects = new[] { new WonderEffectMagnitude("LoamGenerationRate", "Sector", 0) },
                    RelicCost = 2,
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

    /// <summary>A real relic instance via the real mint host — never a hand-written rpg_item row.</summary>
    string MintRelic()
    {
        var n = _relicSeq++;
        var instanceId = $"w4a2-relic-{Guid.NewGuid().ToString("N")[..8]}";
        var grant = new LootGrant(n, DropEntryKind.Relic, RelicContainer, 1, AffixChannels.Drop,
            BaseTypeId: null, Frame: null, Role: null, RarityId: null,
            ItemLevel: 20, RollSeed: 6000UL + (ulong)n);
        var result = _store.MintRelic(grant, PlayerIdStr, thetaContent: 20, Tuning, instanceId: instanceId);
        Assert.True(result.Rejection.IsOk, result.Rejection.ToString());
        Assert.Equal(instanceId, result.InstanceId);
        return instanceId;
    }

    void LoadAboard(string instanceId)
    {
        var (ok, reason) = _store.LoadCargo(WorldId, Legion1, PlayerId,
            "instance", instanceId, null, 0, weightEach: 10);
        Assert.True(ok, reason);
    }

    int OpenTurn() => _store.GetWorldHeader(WorldId)!.CurrentTurn;

    async Task<JsonElement> FileCommandsAsync(object commands)
    {
        var filed = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commands", new
        {
            commanderId = Commander,
            commands
        });
        Assert.True(filed.IsSuccessStatusCode, await filed.Content.ReadAsStringAsync());
        return await filed.Content.ReadFromJsonAsync<JsonElement>();
    }

    static JsonElement ResultFor(JsonElement filedResult, string commandId)
    {
        foreach (var r in filedResult.GetProperty("results").EnumerateArray())
            if (r.GetProperty("commandId").GetString() == commandId)
                return r;
        throw new Xunit.Sdk.XunitException($"no result for command {commandId}: {filedResult}");
    }

    static void AssertOk(JsonElement filedResult, string commandId)
    {
        var r = ResultFor(filedResult, commandId);
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
    }

    static void AssertRefused(JsonElement filedResult, string commandId, string reason)
    {
        var r = ResultFor(filedResult, commandId);
        Assert.False(r.GetProperty("ok").GetBoolean(), r.ToString());
        Assert.Equal(reason, r.GetProperty("reason").GetString());
    }

    // ---- mapping round-trip: order preserved, over the real HTTP wire -----------------

    [Fact]
    public async Task Relic_list_survives_the_real_wire_order_preserved()
    {
        var turn = OpenTurn();
        var filed = await FileCommandsAsync(new object[]
        {
            new
            {
                commandId = "4a2-order-kept",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotX,
                structureId = WonderTwoId,
                relicInstanceIds = new[] { "relic-b", "relic-a" }
            }
        });
        AssertOk(filed, "4a2-order-kept");

        // Read back through the normal path (the command log), never the response body alone.
        var stored = _store.ListWorldCommands(WorldId, turn).First(c => c.CommandId == "4a2-order-kept");
        Assert.Equal(new[] { "relic-b", "relic-a" }, stored.RelicInstanceIds);
    }

    // ---- mapping null-tolerance: absent/null reads as "spends no relic" ----------------

    [Fact]
    public async Task Absent_relic_field_reads_as_empty_and_a_non_wonder_build_stores_empty()
    {
        var turn = OpenTurn();
        // A non-Wonder build (RelicCost == 0) files with NO relicInstanceIds property at all.
        var plainId = StructureCatalog.All.First(s => s.RelicCost is not > 0).StructureId;
        var filed = await FileCommandsAsync(new object[]
        {
            new
            {
                commandId = "4a2-no-relics",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotX,
                structureId = plainId
            }
        });
        AssertOk(filed, "4a2-no-relics");

        var stored = _store.ListWorldCommands(WorldId, turn).First(c => c.CommandId == "4a2-no-relics");
        Assert.Empty(stored.RelicInstanceIds);

        // And the same absent field on a Wonder order refuses as count 0, never a null crash:
        // null → empty → `relic.count-mismatch` at submit-time admission.
        var refused = await FileCommandsAsync(new object[]
        {
            new
            {
                commandId = "4a2-null-relics",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotW,
                structureId = WonderOneId,
                relicInstanceIds = (string[]?)null
            }
        });
        AssertRefused(refused, "4a2-null-relics", "relic.count-mismatch");
    }

    // ---- admission over HTTP: per-command refusals, batch intact, unknown never Gets -----

    [Fact]
    public async Task Admission_refusals_arrive_per_command_with_admissions_own_strings()
    {
        var filed = await FileCommandsAsync(new object[]
        {
            new
            {
                commandId = "4a2-batch-good",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotX,
                structureId = WonderTwoId,
                relicInstanceIds = new[] { "adm-a", "adm-b" }
            },
            new
            {
                commandId = "4a2-batch-count",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotX,
                structureId = WonderTwoId,
                relicInstanceIds = new[] { "only-one-of-two" }
            },
            new
            {
                commandId = "4a2-batch-dup",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotX,
                structureId = WonderTwoId,
                relicInstanceIds = new[] { "same-id", "same-id" }
            },
            new
            {
                commandId = "4a2-batch-unknown",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotW,
                structureId = "no-such-structure-4a2",
                relicInstanceIds = new[] { "x" }
            }
        });

        AssertOk(filed, "4a2-batch-good");
        AssertRefused(filed, "4a2-batch-count", "relic.count-mismatch");
        AssertRefused(filed, "4a2-batch-dup", "relic.duplicate");
        // Unknown structureId is admission's `structure.unknown` — the mapping never Gets, never 500s.
        AssertRefused(filed, "4a2-batch-unknown", "structure.unknown");
    }

    // ---- no silent cleaning: blanks/duplicates refuse, whitespace passes through --------

    [Fact]
    public async Task Blank_ids_are_never_cleaned_away()
    {
        var filed = await FileCommandsAsync(new object[]
        {
            // Two blanks are a DUPLICATE pair, not an empty list: the endpoint dropped nothing.
            // (Had it filtered blanks, this would read as count 0 → `relic.count-mismatch` instead.)
            new
            {
                commandId = "4a2-blank-pair",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotX,
                structureId = WonderTwoId,
                relicInstanceIds = new[] { "", "" }
            },
            // One whitespace id passes submit-time admission untouched (count 1, distinct 1) —
            // the endpoint trimmed nothing. It will drop at commit, never succeed.
            new
            {
                commandId = "4a2-whitespace-one",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotW,
                structureId = WonderOneId,
                relicInstanceIds = new[] { "   " }
            }
        });

        AssertRefused(filed, "4a2-blank-pair", "relic.duplicate");
        AssertOk(filed, "4a2-whitespace-one");
    }

    // ---- owed live proof (store-integration level): file → started → consumed → done ----

    [Fact]
    public async Task Live_proof_file_commit_started_consumed_completed()
    {
        var relic = MintRelic();
        LoadAboard(relic);
        var turn = OpenTurn();

        var filed = await FileCommandsAsync(new object[]
        {
            new
            {
                commandId = "4a2-live",
                kind = "build",
                entityId = Legion1,
                sectorId = _sector,
                slotIndex = _slotW,
                structureId = WonderOneId,
                relicInstanceIds = new[] { relic }
            }
        });
        AssertOk(filed, "4a2-live");

        // The filed list is what the store holds (normal-path read-back).
        var stored = _store.ListWorldCommands(WorldId, turn).First(c => c.CommandId == "4a2-live");
        Assert.Equal(new[] { relic }, stored.RelicInstanceIds);

        var commit = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commit", new
        {
            commanderId = Commander, turn
        });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var commitResult = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(commitResult.GetProperty("advanced").GetBoolean(), commitResult.ToString());

        // build.started, read through the normal report path.
        var report = _store.GetWorldTurnReport(WorldId, turn);
        Assert.NotNull(report);
        Assert.Contains(report!.Entries, e =>
            e.Kind == TurnReportKinds.Event && e.Subject == "4a2-live"
            && string.Equals(e.Detail, $"build.started:{WonderOneId}", StringComparison.Ordinal));

        // Move, never copy: source cargo row gone AND item row consumed.
        Assert.DoesNotContain(_store.ListCargo(WorldId, Legion1), r => r.InstanceId == relic);
        Assert.DoesNotContain(_store.ListSectorStorage(WorldId, _sector), r => r.InstanceId == relic);
        Assert.Equal(RpgStore.RelicConsumedDisposition, _store.GetItem(relic)!.Disposition);

        // Slot carries the Wonder with remaining == BuildTurns.
        var slot = _store.LoadWorldState(WorldId)!.Sectors
            .First(s => string.Equals(s.SectorId, _sector, StringComparison.Ordinal))
            .Slots.First(sl => sl.SlotIndex == _slotW);
        Assert.Equal(WonderOneId, slot.StructureId);
        Assert.Equal(WonderOneBuildTurns, slot.ConstructionTurnsRemaining);

        // Advance BuildTurns turns: construction completes, structure stands.
        var next = OpenTurn();
        var commit2 = await _http.PostAsJsonAsync($"/api/world/{WorldId}/commit", new
        {
            commanderId = Commander, turn = next
        });
        Assert.True(commit2.IsSuccessStatusCode, await commit2.Content.ReadAsStringAsync());

        var done = _store.LoadWorldState(WorldId)!.Sectors
            .First(s => string.Equals(s.SectorId, _sector, StringComparison.Ordinal))
            .Slots.First(sl => sl.SlotIndex == _slotW);
        Assert.Equal(WonderOneId, done.StructureId);
        // Completed: the engine decrements to zero (not null) and the structure stands.
        Assert.Equal(0, done.ConstructionTurnsRemaining);
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
