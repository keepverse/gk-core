using FusionRpg.Core.World;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.SectorStorage;

/// <summary>
/// Task 1.2b (spec-sector-storage.md §Design 4-5, §Testing strategy): the
/// <c>rpg_world_sector_storage</c> table and the capture hook inside
/// <c>DiffSectors</c>'s per-sector loop. All stores are in-memory
/// (<see cref="DataTestStore.Create"/>); no temp dir, nothing to delete.
///
/// <para>Rows are written with raw SQL on purpose: Load/Unload verbs are module 3
/// (<c>cargo-transfer</c>)'s own deliverable, not built here — the same way
/// <c>legion-cargo</c>'s own tests write member rows directly for capacity changes
/// that module does not own.</para>
/// </summary>
public class SectorStorageTests : IDisposable
{
    const long PlayerId = 1;
    const string WorldId = "w-sector-storage";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SectorStorageTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 11, worldId: WorldId);
        var (ok, reason, _) = _store.CreateWorld(PlayerId, built);
        Assert.True(ok, reason);
    }

    public void Dispose()
    {
        RpgStore.TestProbeSectorCapture = null;
        _testStore.Dispose();
    }

    // ---- fixture helpers ------------------------------------------------------------------

    void InsertStorageRow(string sectorId, int seq, string kind,
        string? instanceId = null, string? containerId = null, long? qty = null)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_world_sector_storage
              (world_id, sector_id, seq, kind, instance_id, container_id, qty)
            VALUES ($w, $s, $seq, $k, $iid, $cid, $q);
            """;
        cmd.Parameters.AddWithValue("$w", WorldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$iid", (object?)instanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cid", (object?)containerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$q", (object?)qty ?? DBNull.Value);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    (string SectorId, string OldOwner, string NewOwner) OwnedSectorPlusRival()
    {
        var state = _store.LoadWorldState(WorldId)!;
        var sector = state.Sectors.First(s => s.OwnerFactionId is not null);
        var oldOwner = sector.OwnerFactionId!;
        var newOwner = state.Factions.Select(f => f.FactionId)
            .First(f => !string.Equals(f, oldOwner, StringComparison.Ordinal));
        return (sector.SectorId, oldOwner, newOwner);
    }

    WorldState WithSectorOwner(WorldState state, string sectorId, string newOwner) =>
        state with
        {
            Sectors = state.Sectors
                .Select(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal)
                    ? s with { OwnerFactionId = newOwner }
                    : s)
                .ToList()
        };

    // ---- acceptance criterion 1: exact column shape ------------------------------------------

    [Fact]
    public void Schema_has_exact_column_shape_no_weight_each_no_owner()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('rpg_world_sector_storage') ORDER BY cid;";
        using var r = cmd.ExecuteReader();
        var columns = new List<string>();
        while (r.Read()) columns.Add(r.GetString(0));

        // Spec §Design 4, verbatim: seven columns, no weight_each (§Design 2), no
        // owner/faction column of any kind (§Design 5 — the absence is the mechanism).
        Assert.Equal(
            new[] { "world_id", "sector_id", "seq", "kind", "instance_id", "container_id", "qty" },
            columns);
    }

    // ---- spec §Design 3: row count, not summed qty, is the capacity unit ---------------------

    [Fact]
    public void Slot_count_counts_rows_not_summed_qty()
    {
        var (sectorId, _, _) = OwnedSectorPlusRival();
        InsertStorageRow(sectorId, seq: 0, kind: "instance", instanceId: "inst-1");
        InsertStorageRow(sectorId, seq: 1, kind: "stack", containerId: "mat-iron", qty: 500);

        // One stack row holding 500 occupies exactly one slot, same as one instance row.
        Assert.Equal(2, _store.SectorStorageSlotCount(WorldId, sectorId));
        Assert.Equal(2, _store.ListSectorStorage(WorldId, sectorId).Count);
    }

    // ---- acceptance criterion 2: capture flips reachability, zero rows touched ---------------

    [Fact]
    public void Capture_flips_reachability_with_zero_rows_touched()
    {
        var (sectorId, oldOwner, newOwner) = OwnedSectorPlusRival();
        InsertStorageRow(sectorId, seq: 0, kind: "instance", instanceId: "inst-1");
        InsertStorageRow(sectorId, seq: 1, kind: "stack", containerId: "mat-iron", qty: 500);

        var before = _store.ListSectorStorage(WorldId, sectorId).ToList();
        Assert.True(_store.SectorStorageReachableBy(WorldId, sectorId, oldOwner), "pre-capture: reachable by the old owner");
        Assert.False(_store.SectorStorageReachableBy(WorldId, sectorId, newOwner), "pre-capture: unreachable by the rival");

        var state = _store.LoadWorldState(WorldId)!;
        _store.DiffCommitForTest(WorldId, WithSectorOwner(state, sectorId, newOwner));

        // The capture itself touched ZERO storage rows: byte-identical before/after.
        var after = _store.ListSectorStorage(WorldId, sectorId).ToList();
        Assert.Equal(before, after);

        // Reachability flipped automatically — no migration code ran, there is nothing
        // on the row that could record an owner (Schema test above proves the absence).
        Assert.False(_store.SectorStorageReachableBy(WorldId, sectorId, oldOwner), "post-capture: unreachable by the old owner");
        Assert.True(_store.SectorStorageReachableBy(WorldId, sectorId, newOwner), "post-capture: reachable by the new owner");
    }

    // ---- acceptance criterion 3: the hook fires inside the turn-commit transaction -----------

    [Fact]
    public void Capture_hook_fires_inside_the_turn_commit_transaction()
    {
        var (sectorId, oldOwner, newOwner) = OwnedSectorPlusRival();
        InsertStorageRow(sectorId, seq: 0, kind: "instance", instanceId: "inst-1");

        var before = _store.ListSectorStorage(WorldId, sectorId).ToList();
        var state = _store.LoadWorldState(WorldId)!;

        // Crash immediately after DiffSectors's sector-owner write but before tx.Commit(),
        // through the real code path (the probe fires inside OnSectorCapturedUnlocked,
        // called synchronously from the per-sector loop inside the transaction).
        RpgStore.TestProbeSectorCapture = () => throw new InvalidOperationException("forced mid-capture crash");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                _store.DiffCommitForTest(WorldId, WithSectorOwner(state, sectorId, newOwner)));
        }
        finally
        {
            RpgStore.TestProbeSectorCapture = null;
        }

        // Same pre-capture state for BOTH halves — never one flipped and the other not:
        // the sector's OwnerFactionId rolled back with the transaction...
        var reloaded = _store.LoadWorldState(WorldId)!;
        var sector = reloaded.Sectors.First(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal));
        Assert.Equal(oldOwner, sector.OwnerFactionId);

        // ...and the storage rows' observable reachability with it.
        Assert.Equal(before, _store.ListSectorStorage(WorldId, sectorId).ToList());
        Assert.True(_store.SectorStorageReachableBy(WorldId, sectorId, oldOwner));
        Assert.False(_store.SectorStorageReachableBy(WorldId, sectorId, newOwner));
    }
}
