using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;
using FusionRpg.Core.World.LegionCargo;
using FusionRpg.Core.World.StructureSeed;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Data.Tests.WonderBuild;

/// <summary>
/// Serializes every fixture that wholesale-replaces the static <c>StructureCatalog</c> (this
/// suite + the CargoTransfer suite): neither superset contains the other's test ids, so parallel
/// execution lets one suite's dispose-restore blank the other's rows mid-test.
/// </summary>
[CollectionDefinition("StructureCatalogSwap")]
public class StructureCatalogSwapCollection { }

/// <summary>
/// Task 3.1 (plan "Task 3.1" all 5 AC bullets; spec-wonder-build-flow.md §Design 4/6/7, §Testing
/// strategy): the final integration — relic reachability pre-check + spend inside the real
/// turn-commit transaction. All stores are in-memory (<see cref="DataTestStore.Create"/>); no temp
/// dir backs a store, nothing to delete.
///
/// <para>The fixture layers two in-memory rows over the real structure corpus through
/// <c>StructureCorpusOverlay</c> and restores the real corpus on dispose: a Sector/Common Wonder
/// costing exactly one relic, and an ItemStorage depot (sector storage has no base allowance — zero
/// structures, zero slots — so the storage-sourced test needs a live depot the same way the
/// cargo-transfer suite does). The rows are a superset with identical real defs, so a
/// concurrently-running suite reading a real structure id observes the same answer under either
/// catalog; the only deltas are two inert test ids nothing else references. This fixture used to
/// materialise that corpus in a temp directory and delete it in dispose — the write was removed,
/// not cleaned up (docs/contributing/testing-standard.md R1/R2).</para>
///
/// <para>Relics are real: a zero-atom Relic-kind container (the RelicMint suite's own proven
/// shape — many relics exist purely as Wonder-build cost tokens) minted through the real
/// <c>MintRelic</c> host, so each spent row carries <c>origin_kind='drop'</c> + a resolvable
/// Relic container ref, the exact provenance the pre-check verifies.</para>
///
/// <para>Shares the <c>StructureCatalogSwap</c> xUnit collection with the CargoTransfer suite
/// (Task 2.1): both fixtures wholesale-replace the static catalog, and neither superset contains
/// the other's test ids — running them in parallel lets one suite's restore blank the other's
/// depot/wonder mid-test. Same collection = sequential, no race.</para>
/// </summary>
[Collection("StructureCatalogSwap")]
public class WonderBuildTests : IDisposable
{
    const long PlayerId = 1;
    const string PlayerIdStr = "1";
    const string WorldId = "w-wonder-build";
    const string Commander = "dave";
    const string Legion1 = "e-wb-legion-1";
    const string Legion2 = "e-wb-legion-2";
    const string TestWonderId = "test-wonder-wb";
    const string TestWonder2Id = "test-wonder-wb2";
    const string TestDepotId = "test-depot-wb";
    const long TestDepotBonus = 6;
    const string RelicContainer = "relic.wb-cost-token";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _sector;
    readonly int _slotW;
    readonly int _slotX;
    readonly string _kindX;

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    public WonderBuildTests()
    {
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId);

        // The template's dave ground is a single sector (homeworld): one Wildland slot, one
        // Market slot, one Rootbed slot, one Seat slot — all empty. So the double-spend test
        // cannot file two same-kind orders through one legion; instead it files two DIFFERENT
        // Wonder rows (Wildland + a second kind) on two different slots, both naming the same
        // relic — which is exactly the batch double-spend shape regardless of kind.
        var home = built.Sectors.FirstOrDefault(s =>
            string.Equals(s.OwnerFactionId, Commander, StringComparison.Ordinal)
            && s.Slots.Any(sl => sl.StructureId is null
                && SlotTypeCatalog.IsKnown(sl.SlotTypeId)
                && SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Wildland)
            && s.Slots.Any(sl => sl.StructureId is null
                && SlotTypeCatalog.IsKnown(sl.SlotTypeId)
                && SlotTypeCatalog.Get(sl.SlotTypeId).Kind != SlotKind.Wildland
                && SlotTypeCatalog.Get(sl.SlotTypeId).Kind != SlotKind.Seat)
            && s.Slots.Any(sl => sl.StructureId is null
                && SlotTypeCatalog.IsKnown(sl.SlotTypeId)
                && SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Seat));
        Assert.True(home is not null, "need a dave sector with an empty Wildland slot, another empty non-Seat slot, and an empty Seat slot");
        _sector = home!.SectorId;
        _slotW = home.Slots.First(sl => sl.StructureId is null
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Wildland).SlotIndex;
        var x = home.Slots.First(sl => sl.StructureId is null
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind != SlotKind.Wildland
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind != SlotKind.Seat);
        _slotX = x.SlotIndex;
        _kindX = SlotTypeCatalog.Get(x.SlotTypeId).Kind.ToString();
        var seatSlot = home.Slots.First(sl => sl.StructureId is null
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Seat).SlotIndex;

        StructureCatalog.Configure(StructureCorpusOverlay.LoadWithRows(
            StructureCorpusOverlay.Wonder(TestWonderId, "Wildland", "Sector", "Common", relicCost: 1),
            StructureCorpusOverlay.Wonder(TestWonder2Id, _kindX, "Sector", "Common", relicCost: 1),
            StructureCorpusOverlay.Depot(TestDepotId, TestDepotBonus)));

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

        // Two members × tuning (100 weight, 2 slots) = 200 weight, 4 slots per legion.
        var customized = built with
        {
            Entities = built.Entities
                .Append(Legion(Legion1))
                .Append(Legion(Legion2))
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList(),
        };

        var (ok, reason, _) = _store.CreateWorld(PlayerId, customized);
        Assert.True(ok, reason);

        // Sector storage has no base allowance, so the storage-sourced test needs a live depot.
        // The Seat slot is never a build target (Seat needs waystation range; nothing about this
        // module is Seat-shaped), so it carries the depot without disturbing any build slot.
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_slots SET structure_id = $id, construction_turns_remaining = NULL
            WHERE world_id = $w AND sector_id = $s AND slot_index = $i;
            """;
        cmd.Parameters.AddWithValue("$id", TestDepotId);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", _sector);
        cmd.Parameters.AddWithValue("$i", seatSlot);
        Assert.Equal(1, cmd.ExecuteNonQuery());

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = RelicContainer, Kind = ContainerKind.Relic,
        }).IsOk);
    }

    public void Dispose()
    {
        StructureCatalog.Configure(StructureCorpus.Load(StructureCorpusOverlay.RealCorpusRoot()));
        _testStore.Dispose();
    }

    // ---- fixture helpers ------------------------------------------------------------------


    int _relicSeq;

    /// <summary>A real relic instance via the real mint host — never a hand-written rpg_item row.</summary>
    string MintRelic()
    {
        var n = _relicSeq++;
        var instanceId = $"wb-relic-{Guid.NewGuid().ToString("N")[..8]}";
        var grant = new LootGrant(n, DropEntryKind.Relic, RelicContainer, 1, AffixChannels.Drop,
            BaseTypeId: null, Frame: null, Role: null, RarityId: null,
            ItemLevel: 20, RollSeed: 5000UL + (ulong)n);
        var result = _store.MintRelic(grant, PlayerIdStr, thetaContent: 20, Tuning, instanceId: instanceId);
        Assert.True(result.Rejection.IsOk, result.Rejection.ToString());
        Assert.Equal(instanceId, result.InstanceId);
        return instanceId;
    }

    void LoadAboard(string legion, string instanceId)
    {
        var (ok, reason) = _store.LoadCargo(WorldId, legion, PlayerId,
            "instance", instanceId, null, 0, weightEach: 10);
        Assert.True(ok, reason);
    }

    int CargoSeq(string legion, string instanceId) =>
        _store.ListCargo(WorldId, legion).First(r => r.InstanceId == instanceId).Seq;

    WorldCommand BuildCmd(string cmdId, string legion, string sector, int slot, string structureId, params string[] relics) => new()
    {
        CommanderId = Commander, CommandId = cmdId, Kind = WorldCommandKinds.Build,
        EntityId = legion, SectorId = sector, SlotIndex = slot,
        StructureId = structureId, RelicInstanceIds = relics,
    };

    void SubmitOk(params WorldCommand[] commands)
    {
        var outcomes = _store.SubmitWorldCommands(WorldId, commands);
        Assert.Equal(commands.Length, outcomes.Count);
        foreach (var o in outcomes)
            Assert.True(o.Ok, $"submit {o.CommandId} refused: {o.Reason}");
    }

    /// <summary>Ends the open turn: AI factions first, the human last (their commit releases it).</summary>
    void CommitAll()
    {
        var world = _store.LoadWorldState(WorldId)!;
        var open = _store.GetWorldHeader(WorldId)!.CurrentTurn;
        WorldTurnCommitResult last = default!;
        foreach (var f in world.Factions.OrderBy(f => f.PolicyId is null).Select(f => f.FactionId))
            last = _store.CommitWorldTurn(WorldId, f, open);
        Assert.True(last.Advanced, $"turn did not advance: {last.Reason}");
    }

    IReadOnlyList<TurnReportEntry> ReportEntries(int turn = 0) =>
        _store.GetWorldTurnReport(WorldId, turn)!.Entries;

    static bool IsStarted(TurnReportEntry e, string cmdId, string structureId = TestWonderId) =>
        e.Kind == TurnReportKinds.Event && e.Subject == cmdId
        && string.Equals(e.Detail, $"build.started:{structureId}", StringComparison.Ordinal);

    static bool IsDropped(TurnReportEntry e, string cmdId, string reason) =>
        e.Kind == TurnReportKinds.CommandDropped && e.Subject == cmdId
        && string.Equals(e.Detail, reason, StringComparison.Ordinal);

    string? SlotStructure(string sector, int slot) =>
        _store.LoadWorldState(WorldId)!.Sectors
            .First(s => string.Equals(s.SectorId, sector, StringComparison.Ordinal))
            .Slots.First(sl => sl.SlotIndex == slot).StructureId;

    // ---- AC1: end-to-end Wonder build from legion cargo, in one turn commit -----------------

    [Fact]
    public void cargo_relic_spends_end_to_end_in_one_commit()
    {
        var relic = MintRelic();
        LoadAboard(Legion1, relic);
        SubmitOk(BuildCmd("wb-cargo-1", Legion1, _sector, _slotW, TestWonderId, relic));

        CommitAll();

        var entries = ReportEntries();
        Assert.Contains(entries, e => IsStarted(e, "wb-cargo-1"));
        Assert.Equal(TestWonderId, SlotStructure(_sector, _slotW));

        // Move, never copy, asserted as mutually exclusive outcomes: the source row is gone AND
        // the item row is consumed — never both still present, never neither changed.
        Assert.DoesNotContain(_store.ListCargo(WorldId, Legion1), r => r.InstanceId == relic);
        Assert.DoesNotContain(_store.ListSectorStorage(WorldId, _sector), r => r.InstanceId == relic);
        Assert.Equal(RpgStore.RelicConsumedDisposition, _store.GetItem(relic)!.Disposition);
    }

    // ---- AC1 (storage half) + AC2: move-never-copy from sector storage ---------------------

    [Fact]
    public void storage_relic_spend_moves_never_copies()
    {
        var relic = MintRelic();
        LoadAboard(Legion2, relic);
        var (depOk, depReason, _) = _store.DepositCargo(WorldId, Legion2, _sector, CargoSeq(Legion2, relic));
        Assert.True(depOk, depReason);
        SubmitOk(BuildCmd("wb-storage-1", Legion2, _sector, _slotW, TestWonderId, relic));

        CommitAll();

        var entries = ReportEntries();
        Assert.Contains(entries, e => IsStarted(e, "wb-storage-1"));
        Assert.Equal(TestWonderId, SlotStructure(_sector, _slotW));

        Assert.DoesNotContain(_store.ListSectorStorage(WorldId, _sector), r => r.InstanceId == relic);
        Assert.DoesNotContain(_store.ListCargo(WorldId, Legion2), r => r.InstanceId == relic);
        Assert.Equal(RpgStore.RelicConsumedDisposition, _store.GetItem(relic)!.Disposition);
    }

    // ---- AC3: same-batch double-spend refusal ----------------------------------------------

    [Fact]
    public void two_wonder_orders_cannot_spend_the_same_relic_in_one_turn()
    {
        var relic = MintRelic();
        LoadAboard(Legion1, relic);
        // Two kind-compatible orders on two different slots, both naming the same relic: the
        // first claims it in the pre-check, the second is emptied there and refuses at commit.
        // The refusal surfaces at Reveal's re-admission (`relic.count-mismatch`) before the
        // order reaches Snapshot — the direct-resolver reason (`relic.not-reachable`) is proven
        // Core-side instead (see RpgStore.WonderBuild's own pipeline note).
        SubmitOk(
            BuildCmd("wb-first", Legion1, _sector, _slotW, TestWonderId, relic),
            BuildCmd("wb-second", Legion1, _sector, _slotX, TestWonder2Id, relic));

        CommitAll();

        var entries = ReportEntries();
        Assert.Contains(entries, e => IsStarted(e, "wb-first"));
        Assert.Contains(entries, e => IsDropped(e, "wb-second", "relic.count-mismatch"));

        // Exactly one spend: the second slot stayed empty and the disposition flipped once.
        Assert.Equal(TestWonderId, SlotStructure(_sector, _slotW));
        Assert.Null(SlotStructure(_sector, _slotX));
        Assert.DoesNotContain(_store.ListCargo(WorldId, Legion1), r => r.InstanceId == relic);
        Assert.Equal(RpgStore.RelicConsumedDisposition, _store.GetItem(relic)!.Disposition);
    }

    // ---- AC4: a dropped command leaves every named relic untouched --------------------------

    [Fact]
    public void dropped_command_leaves_its_relic_completely_untouched()
    {
        var spent = MintRelic();
        var spared = MintRelic();
        LoadAboard(Legion1, spent);
        LoadAboard(Legion1, spared);
        // Both orders are valid and both relics reachable — but they name the SAME slot, so the
        // second in (commander, command) order drops at resolution (build.occupied) after the
        // first is accepted. Command ids are chosen so the winner sorts first.
        SubmitOk(
            BuildCmd("wb-1-wins", Legion1, _sector, _slotW, TestWonderId, spent),
            BuildCmd("wb-2-drops", Legion1, _sector, _slotW, TestWonderId, spared));

        CommitAll();

        var entries = ReportEntries();
        Assert.Contains(entries, e => IsStarted(e, "wb-1-wins"));
        // The occupied reason names the occupying structure (`build.occupied:{id}`).
        Assert.Contains(entries, e => IsDropped(e, "wb-2-drops", $"build.occupied:{TestWonderId}"));

        // The winner spent exactly once...
        Assert.Equal(RpgStore.RelicConsumedDisposition, _store.GetItem(spent)!.Disposition);
        // ...and the dropped order's relic is byte-for-byte where it was: cargo row present,
        // disposition still the aboard marker — the spend never fired for it.
        Assert.Contains(_store.ListCargo(WorldId, Legion1), r => r.InstanceId == spared);
        Assert.Equal(RpgStore.CargoAboardDisposition, _store.GetItem(spared)!.Disposition);
    }

    // ---- AC5: mid-batch reachability loss refuses at commit, not at admission ----------------

    [Fact]
    public void relic_moved_out_of_reach_before_commit_refuses_at_commit_time()
    {
        var relic = MintRelic();
        LoadAboard(Legion1, relic);
        // Admission passes: the relic IS aboard the issuing legion when the order is filed.
        // The refusal lands at commit (Reveal's re-admission inside the commit transaction —
        // same pipeline note as the double-spend test), never at submit-time admission.
        SubmitOk(BuildCmd("wb-stale", Legion1, _sector, _slotW, TestWonderId, relic));

        // An unrelated move between filing and commit takes the relic out of reach.
        var (moveOk, moveReason, _) =
            _store.TransferCargo(WorldId, Legion1, Legion2, CargoSeq(Legion1, relic), PlayerId);
        Assert.True(moveOk, moveReason);

        CommitAll();

        var entries = ReportEntries();
        Assert.DoesNotContain(entries, e => IsStarted(e, "wb-stale"));
        Assert.Contains(entries, e => IsDropped(e, "wb-stale", "relic.count-mismatch"));
        Assert.Null(SlotStructure(_sector, _slotW));

        // Refused, not spent: the relic sits aboard the other legion, disposition untouched.
        Assert.Contains(_store.ListCargo(WorldId, Legion2), r => r.InstanceId == relic);
        Assert.Equal(RpgStore.CargoAboardDisposition, _store.GetItem(relic)!.Disposition);
    }
}
