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

namespace FusionRpg.Data.Tests.WonderReach;

/// <summary>
/// Plan Task 4A.4 (empire-wonder-surfaces `wonder-wire` §Design 4, §Testing strategy): the
/// planning-time reachability list behind `GET relic-reachability`. All stores are in-memory
/// (<see cref="DataTestStore.Create"/>); the fixture's two test rows (one wonder, one depot) are
/// layered over the real corpus in memory by <c>StructureCorpusOverlay</c>, so nothing is written to
/// disk.
///
/// <para>Shares the <c>StructureCatalogSwap</c> xUnit collection with the WonderBuild + CargoTransfer
/// suites: this fixture wholesale-replaces the static catalog (test wonder + test depot ids nothing
/// else references), so it serializes against theirs.</para>
///
/// <para>Load-bearing property, proven behaviorally rather than argued from shared SQL text: every
/// id the list returns passes the commit gate (a build naming it starts and spends through the
/// real commit), and every id it omits refuses there (`relic.count-mismatch`). The spend path
/// itself is WonderBuild's module; this suite proves list/gate AGREEMENT at the public
/// surface.</para>
/// </summary>
[Collection("StructureCatalogSwap")]
[Trait("VerificationId", "data.wonder-reach")]
public class WonderReachTests : IDisposable
{
    const long PlayerId = 1;
    const string PlayerIdStr = "1";
    const string WorldId = "w-wonder-reach";
    const string Commander = "dave";
    const string Legion1 = "e-wr-legion-1";
    const string Legion2 = "e-wr-legion-2";
    const string TestWonderId = "test-wonder-wr";
    const string TestDepotId = "test-depot-wr";
    const long TestDepotBonus = 6;
    const string RelicContainer = "relic.wr-cost-token";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _sector;
    readonly int _slotW;
    readonly int _seatSlot;

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    public WonderReachTests()
    {
        ScopedInventoryPolicy.Configure(new ScopedInventoryTuning(1, 1, CargoWeightPerUnit: 100, CargoSlotsPerUnit: 2));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId);

        // Same proven sector shape as the WonderBuild suite: an empty Wildland slot (the build
        // target), another empty non-Seat slot, and an empty Seat slot (carries the depot —
        // sector storage has no base allowance, so the storage-sourced case needs a live one).
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
        Assert.True(home is not null, "need a dave sector with an empty Wildland slot and an empty Seat slot");
        _sector = home!.SectorId;
        _slotW = home.Slots.First(sl => sl.StructureId is null
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Wildland).SlotIndex;
        _seatSlot = home.Slots.First(sl => sl.StructureId is null
            && SlotTypeCatalog.Get(sl.SlotTypeId).Kind == SlotKind.Seat).SlotIndex;

        StructureCatalog.Configure(StructureCorpusOverlay.LoadWithRows(
            StructureCorpusOverlay.Wonder(TestWonderId, "Wildland", "Sector", "Common", relicCost: 1),
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

        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE rpg_world_slots SET structure_id = $id, construction_turns_remaining = NULL
            WHERE world_id = $w AND sector_id = $s AND slot_index = $i;
            """;
        cmd.Parameters.AddWithValue("$id", TestDepotId);
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", _sector);
        cmd.Parameters.AddWithValue("$i", _seatSlot);
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

    // ---- fixture helpers (the WonderBuild suite's own proven shapes) ---------------------------


    int _relicSeq;

    /// <summary>A real relic instance via the real mint host — never a hand-written rpg_item row.</summary>
    string MintRelic()
    {
        var n = _relicSeq++;
        var instanceId = $"wr-relic-{Guid.NewGuid().ToString("N")[..8]}";
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

    // ---- list contents: both overlays, sorted, de-duplicated ------------------------------------

    [Fact]
    public void cargo_and_storage_relics_list_reachable_sorted_while_unplaced_ones_stay_out()
    {
        var aboard = MintRelic();
        var stored = MintRelic();
        var armoury = MintRelic();
        var otherLegion = MintRelic();
        LoadAboard(Legion1, aboard);
        LoadAboard(Legion2, stored);
        LoadAboard(Legion2, otherLegion);
        var (depOk, depReason, _) = _store.DepositCargo(WorldId, Legion2, _sector, CargoSeq(Legion2, stored));
        Assert.True(depOk, depReason);

        var listed = _store.ListReachableRelics(WorldId, Legion1, _sector);

        // Both overlays answer, sorted ordinal, de-duplicated — never a population count, just the
        // exact reachable set for this pair.
        Assert.Equal(
            new[] { aboard, stored }.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            listed.OrderBy(id => id, StringComparer.Ordinal).ToList());
        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
        // Owned and live but in neither overlay: unreachable, correctly absent.
        Assert.DoesNotContain(armoury, listed);
        // Aboard a different legion: out of reach for this pair, correctly absent.
        Assert.DoesNotContain(otherLegion, listed);
    }

    [Fact]
    public void unknown_blank_and_crossed_pairs_read_empty_inert_never_an_error()
    {
        var aboard = MintRelic();
        LoadAboard(Legion1, aboard);

        // Unknown entity + a sector holding nothing: nothing matches, inert [].
        // (Presence-only UNION: a stored relic WOULD answer here — see
        // overlays_scope_independently below. Empty here only because this fixture
        // stores nothing.)
        Assert.Empty(_store.ListReachableRelics(WorldId, "no-such-legion", _sector));
        // Blank halves match nothing (same posture as the gate's NULL handling).
        Assert.Empty(_store.ListReachableRelics(WorldId, "", _sector));
        Assert.Empty(_store.ListReachableRelics(WorldId, Legion1, ""));
        // Crossed pair is NOT empty: presence-only UNION answers cargo by entity
        // regardless of the sector half (same as overlays_scope_independently proves
        // for "elsewhere-sector"). The gate's own UNION would pass this relic for a
        // build naming (Legion1, "no-such-sector"), so the list must agree.
        Assert.Contains(aboard, _store.ListReachableRelics(WorldId, Legion1, "no-such-sector"));
        // Both halves miss at once: nothing matches, inert [].
        Assert.Empty(_store.ListReachableRelics(WorldId, "no-such-legion", "no-such-sector"));
        // Unknown world: no player row, inert [].
        Assert.Empty(_store.ListReachableRelics("no-such-world", Legion1, _sector));
    }

    [Fact]
    public void overlays_scope_independently_cargo_by_entity_storage_by_sector()
    {
        var aboard = MintRelic();
        var stored = MintRelic();
        LoadAboard(Legion1, aboard);
        LoadAboard(Legion1, stored);
        var (depOk, depReason, _) = _store.DepositCargo(WorldId, Legion1, _sector, CargoSeq(Legion1, stored));
        Assert.True(depOk, depReason);

        // The issuing legion's cargo answers whatever the sector half says — presence-only, exactly
        // like the gate's own UNION (no legion-position check on either side).
        Assert.Contains(aboard, _store.ListReachableRelics(WorldId, Legion1, "elsewhere-sector"));
        Assert.DoesNotContain(stored, _store.ListReachableRelics(WorldId, Legion1, "elsewhere-sector"));
        // And the sector's storage answers whatever the entity half says.
        Assert.Contains(stored, _store.ListReachableRelics(WorldId, "elsewhere-legion", _sector));
        Assert.DoesNotContain(aboard, _store.ListReachableRelics(WorldId, "elsewhere-legion", _sector));
    }

    // ---- list/gate agreement through the real commit -------------------------------------------

    [Fact]
    public void a_listed_relic_passes_the_commit_gate_and_spends()
    {
        var relic = MintRelic();
        LoadAboard(Legion1, relic);
        Assert.Contains(relic, _store.ListReachableRelics(WorldId, Legion1, _sector));

        var outcomes = _store.SubmitWorldCommands(WorldId, new[]
        {
            new WorldCommand
            {
                CommanderId = Commander, CommandId = "wr-listed", Kind = WorldCommandKinds.Build,
                EntityId = Legion1, SectorId = _sector, SlotIndex = _slotW,
                StructureId = TestWonderId, RelicInstanceIds = new[] { relic },
            },
        });
        Assert.True(outcomes.Single().Ok, outcomes.Single().Reason);

        CommitAll();

        var entries = _store.GetWorldTurnReport(WorldId, 0)!.Entries;
        Assert.Contains(entries, e => e.Kind == TurnReportKinds.Event && e.Subject == "wr-listed"
            && string.Equals(e.Detail, $"build.started:{TestWonderId}", StringComparison.Ordinal));
        Assert.Equal(RpgStore.RelicConsumedDisposition, _store.GetItem(relic)!.Disposition);
        // Spent means gone from both overlays — the list agrees after the fact too.
        Assert.DoesNotContain(relic, _store.ListReachableRelics(WorldId, Legion1, _sector));
    }

    [Fact]
    public void an_unlisted_relic_refuses_at_commit_and_stays_untouched()
    {
        var spare = MintRelic();
        Assert.DoesNotContain(spare, _store.ListReachableRelics(WorldId, Legion1, _sector));

        // Filed direct (bypassing submit-time admission, which would already refuse the wrong
        // count — the point here is the COMMIT gate empties unreachable ids, surfacing as
        // `relic.count-mismatch` at Reveal's re-admission per RpgStore.WonderBuild's pipeline note).
        var outcomes = _store.SubmitWorldCommands(WorldId, new[]
        {
            new WorldCommand
            {
                CommanderId = Commander, CommandId = "wr-unlisted", Kind = WorldCommandKinds.Build,
                EntityId = Legion1, SectorId = _sector, SlotIndex = _slotW,
                StructureId = TestWonderId, RelicInstanceIds = new[] { spare },
            },
        });
        Assert.True(outcomes.Single().Ok, outcomes.Single().Reason);

        CommitAll();

        var entries = _store.GetWorldTurnReport(WorldId, 0)!.Entries;
        Assert.DoesNotContain(entries, e => e.Kind == TurnReportKinds.Event && e.Subject == "wr-unlisted");
        Assert.Contains(entries, e => e.Kind == TurnReportKinds.CommandDropped && e.Subject == "wr-unlisted"
            && string.Equals(e.Detail, "relic.count-mismatch", StringComparison.Ordinal));
        // Refused, not spent: still armoury-owned, still unlisted.
        Assert.Equal("owned", _store.GetItem(spare)!.Disposition);
        Assert.DoesNotContain(spare, _store.ListReachableRelics(WorldId, Legion1, _sector));
    }

    // ---- the read performs no write of any kind --------------------------------------------------

    [Fact]
    public void the_list_read_writes_nothing()
    {
        var aboard = MintRelic();
        var stored = MintRelic();
        LoadAboard(Legion1, aboard);
        LoadAboard(Legion1, stored);
        var (depOk, depReason, _) = _store.DepositCargo(WorldId, Legion1, _sector, CargoSeq(Legion1, stored));
        Assert.True(depOk, depReason);

        var cargoBefore = _store.ListCargo(WorldId, Legion1).Select(r => (r.Seq, r.InstanceId)).ToList();
        var storageBefore = _store.ListSectorStorage(WorldId, _sector).Select(r => (r.Seq, r.InstanceId)).ToList();
        var aboardBefore = _store.GetItem(aboard)!;
        var storedBefore = _store.GetItem(stored)!;

        var listed = _store.ListReachableRelics(WorldId, Legion1, _sector);
        Assert.Equal(2, listed.Count);
        _ = _store.ListReachableRelics(WorldId, "no-such-legion", "no-such-sector");

        Assert.Equal(cargoBefore, _store.ListCargo(WorldId, Legion1).Select(r => (r.Seq, r.InstanceId)).ToList());
        Assert.Equal(storageBefore, _store.ListSectorStorage(WorldId, _sector).Select(r => (r.Seq, r.InstanceId)).ToList());
        Assert.Equal((aboardBefore.Disposition, aboardBefore.Revision), (_store.GetItem(aboard)!.Disposition, _store.GetItem(aboard)!.Revision));
        Assert.Equal((storedBefore.Disposition, storedBefore.Revision), (_store.GetItem(stored)!.Disposition, _store.GetItem(stored)!.Revision));
    }
}
