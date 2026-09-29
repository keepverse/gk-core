using FusionRpg.Core.World;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

// scoped-inventory-hierarchy module 3 (`cargo-transfer`),
// `docs/architecture/scoped-inventory-hierarchy/spec-cargo-transfer.md` §Design 1-3.
//
// The verb that makes modules 1 and 2 one feature: a legion physically present at a sector,
// belonging to that sector's *current* owning faction, moves an item directly between its own
// cargo (`rpg_world_entity_cargo`) and that sector's storage (`rpg_world_sector_storage`) —
// deposit (legion → sector) and withdraw (sector → legion). Neither direction touches the
// empire stash (`rpg_item`/`rpg_item_stock`) at all: a move between two overlays of the same
// ownership root, never a round trip through it.
//
// Reads (never modifies) the schemas and capacity functions from RpgStore.LegionCargo.cs /
// RpgStore.SectorStorage.cs / SectorItemCapacity.cs (spec §Structure). SQL lives here, only
// here (guard-dal). Legion-to-legion transfer is module 1's own verb (RpgStore.LegionCargo.cs,
// `TransferCargoUnlocked`) and is untouched here per spec §Design 4.

/// <summary>Where a legion stands and who it answers to — the two live fields the presence
/// and faction gates read. Plain data, no <c>player_id</c> resolution.</summary>
internal sealed record TransferEntity(string? AtSectorId, string? OwnerFactionId);

public sealed partial class RpgStore
{
    /// <summary>
    /// Test probe (approach.test_probe_over_impossible): when non-null, invoked inside the
    /// deposit/withdraw transactions after the delete but before the insert. Lets the atomicity
    /// suite force a mid-transaction failure through the real code path — a crash between the
    /// delete and the insert must leave the pre-transfer state fully intact. A dedicated probe
    /// (not <c>TestProbeMidWrite</c>) so this suite never races module 1's own atomicity suite
    /// over one shared static. Production never sets this; it defaults to null (no-op).
    /// </summary>
    internal static Action? TestProbeCargoTransfer;

    /// <summary>
    /// The one new helper this module adds (spec §Design 3): deliberately a <b>string equality
    /// check on <c>FactionId</c></b>, not a resolve-to-<c>player_id</c> comparison — per both
    /// dependency specs, <c>rpg_world_factions</c> has no <c>player_id</c> column and only the
    /// <c>Player</c> faction resolves to the world's one real <c>player_id</c> today. Comparing
    /// <c>FactionId</c> strings directly is correct and sufficient for the same-faction case.
    /// </summary>
    public static bool SameFaction(string? legionFactionId, string? sectorFactionId) =>
        string.Equals(legionFactionId, sectorFactionId, StringComparison.Ordinal);

    // ---- deposit: legion cargo → sector storage (spec §Design 1) -----------------------------

    /// <summary>
    /// Moves one cargo row from a legion into a sector's storage: presence + faction gates read
    /// live, the sector's own row-count capacity gate is checked before any write, then a single
    /// delete+insert in one transaction — move, never copy. Never touches
    /// <c>rpg_item.player_id</c>/<c>disposition</c>.
    ///
    /// <para>Refusal reasons: <c>cargo.not-found</c> (unknown legion, unknown sector, or no cargo
    /// row at <paramref name="seq"/>), <c>cargo.not-present</c> (legion not at the sector),
    /// <c>cargo.wrong-faction</c> (legion faction != sector's current owner — the name the spec's
    /// own Design §1-2 and Testing strategy use; module 1's legion-to-legion verb keeps its own
    /// landed <c>cargo.cross-empire</c> for the same-shaped gate, untouched here),
    /// <c>cargo.sector-full</c> (destination at capacity).</para>
    /// </summary>
    public (bool Ok, string Reason, int NewSeq) DepositCargo(
        string worldId, string entityId, string sectorId, int seq)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = DepositUnlocked(db, tx, worldId, entityId, sectorId, seq);
            if (result.Ok) tx.Commit();
            return result;
        }
    }

    internal (bool Ok, string Reason, int NewSeq) DepositUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string worldId, string entityId, string sectorId, int seq)
    {
        var entity = ReadTransferEntityUnlocked(db, tx, worldId, entityId);
        if (entity is null)
            return (false, "cargo.not-found", -1);
        if (ReadSectorOwnerUnlocked(db, tx, worldId, sectorId) is not { } sectorOwner)
            return (false, "cargo.not-found", -1);
        if (!string.Equals(entity.AtSectorId, sectorId, StringComparison.Ordinal))
            return (false, "cargo.not-present", -1);
        if (!SameFaction(entity.OwnerFactionId, sectorOwner))
            return (false, "cargo.wrong-faction", -1);

        var row = ReadCargoRowUnlocked(db, tx, worldId, entityId, seq);
        if (row is null)
            return (false, "cargo.not-found", -1);

        var capacity = SectorItemCapacity.EffectiveCapacity(
            ReadSectorShellUnlocked(db, tx, worldId, sectorId));
        if (SectorStorageSlotCountUnlocked(db, tx, worldId, sectorId) >= capacity)
            return (false, "cargo.sector-full", -1);

        ExecInCounted(db, tx, """
            DELETE FROM rpg_world_entity_cargo WHERE world_id = $w AND entity_id = $e AND seq = $s;
            """,
            ("$w", worldId), ("$e", entityId), ("$s", seq));

        TestProbeCargoTransfer?.Invoke();

        var newSeq = NextSectorSeqUnlocked(db, tx, worldId, sectorId);
        ExecInCounted(db, tx, """
            INSERT INTO rpg_world_sector_storage
              (world_id, sector_id, seq, kind, instance_id, container_id, qty)
            VALUES ($w, $s, $seq, $k, $iid, $cid, $q);
            """,
            ("$w", worldId), ("$s", sectorId), ("$seq", newSeq), ("$k", row.Kind),
            ("$iid", (object?)row.InstanceId ?? DBNull.Value),
            ("$cid", (object?)row.ContainerId ?? DBNull.Value),
            ("$q", (object?)row.Qty ?? DBNull.Value));

        return (true, "ok", newSeq);
    }

    // ---- withdraw: sector storage → legion cargo (spec §Design 2) -----------------------------

    /// <summary>
    /// The mirror of <see cref="DepositCargoUnlocked"/>, with the spec's one asymmetry (§Design 2):
    /// legion cargo's capacity gate is <b>weight and slots</b>, and its row carries a
    /// <c>weight_each</c> sector storage never stored. <paramref name="weightEach"/> is therefore
    /// the item's base-type-derived weight <b>read fresh by the caller</b> — the same snapshot
    /// discipline <c>LoadCargoUnlocked</c> already uses (its own <c>weightEach</c> parameter; no
    /// per-item weight column exists anywhere in code, verified 2026-09-15), applied here because
    /// this is the first moment the item enters a weight-gated overlay. It is never a read of a
    /// <c>weight_each</c> column on <c>rpg_world_sector_storage</c> — that column does not exist
    /// by design (spec-sector-storage §Design 2), and the regression suite proves any such read
    /// fails loudly instead of silently returning zero.
    ///
    /// <para>Refusal reasons mirror deposit, with the legion's own gates:
    /// <c>cargo.over-weight</c> / <c>cargo.no-slots</c>.</para>
    /// </summary>
    public (bool Ok, string Reason, int NewSeq) WithdrawCargo(
        string worldId, string sectorId, string entityId, int seq, long weightEach)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = WithdrawUnlocked(db, tx, worldId, sectorId, entityId, seq, weightEach);
            if (result.Ok) tx.Commit();
            return result;
        }
    }

    internal (bool Ok, string Reason, int NewSeq) WithdrawUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string worldId, string sectorId, string entityId, int seq, long weightEach)
    {
        if (weightEach < 0)
            throw new ArgumentOutOfRangeException(nameof(weightEach), "cargo weight_each must be non-negative");

        var entity = ReadTransferEntityUnlocked(db, tx, worldId, entityId);
        if (entity is null)
            return (false, "cargo.not-found", -1);
        if (ReadSectorOwnerUnlocked(db, tx, worldId, sectorId) is not { } sectorOwner)
            return (false, "cargo.not-found", -1);
        if (!string.Equals(entity.AtSectorId, sectorId, StringComparison.Ordinal))
            return (false, "cargo.not-present", -1);
        if (!SameFaction(entity.OwnerFactionId, sectorOwner))
            return (false, "cargo.wrong-faction", -1);

        var row = ReadSectorStorageRowUnlocked(db, tx, worldId, sectorId, seq);
        if (row is null)
            return (false, "cargo.not-found", -1);

        var addedWeight = row.Kind == "stack" ? checked((row.Qty ?? 0) * weightEach) : weightEach;
        if (checked(WeightUsedUnlocked(db, tx, worldId, entityId) + addedWeight)
            > WeightCapacityUnlocked(db, worldId, entityId))
            return (false, "cargo.over-weight", -1);
        if (SlotsUsedUnlocked(db, tx, worldId, entityId) + 1 > SlotCapacityUnlocked(db, worldId, entityId))
            return (false, "cargo.no-slots", -1);

        ExecInCounted(db, tx, """
            DELETE FROM rpg_world_sector_storage WHERE world_id = $w AND sector_id = $s AND seq = $seq;
            """,
            ("$w", worldId), ("$s", sectorId), ("$seq", seq));

        TestProbeCargoTransfer?.Invoke();

        var newSeq = NextSeqUnlocked(db, tx, worldId, entityId);
        ExecInCounted(db, tx, """
            INSERT INTO rpg_world_entity_cargo
              (world_id, entity_id, seq, kind, instance_id, container_id, qty, weight_each)
            VALUES ($w, $e, $s, $k, $iid, $cid, $q, $wt);
            """,
            ("$w", worldId), ("$e", entityId), ("$s", newSeq), ("$k", row.Kind),
            ("$iid", (object?)row.InstanceId ?? DBNull.Value),
            ("$cid", (object?)row.ContainerId ?? DBNull.Value),
            ("$q", (object?)row.Qty ?? DBNull.Value), ("$wt", weightEach));

        return (true, "ok", newSeq);
    }

    // ---- live-state reads (presence + faction + capacity inputs, never cached) -----------------

    internal TransferEntity? ReadTransferEntityUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string entityId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT at_sector_id, owner_faction_id FROM rpg_world_entities
            WHERE world_id = $w AND entity_id = $e;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$e", entityId);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new TransferEntity(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1))
            : null;
    }

    internal SectorStorageRow? ReadSectorStorageRowUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string sectorId, int seq)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT world_id, sector_id, seq, kind, instance_id, container_id, qty
            FROM rpg_world_sector_storage WHERE world_id = $w AND sector_id = $s AND seq = $seq;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        cmd.Parameters.AddWithValue("$seq", seq);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new SectorStorageRow(
                r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt64(6))
            : null;
    }

    /// <summary>
    /// The sector's slot shell — just enough <see cref="WorldSector"/> for
    /// <see cref="SectorItemCapacity.EffectiveCapacity"/>, which reads only
    /// <c>Slots</c> (structure id + construction state through the catalog). Read live on the
    /// same transaction, never cached.
    /// </summary>
    internal WorldSector ReadSectorShellUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string sectorId)
    {
        var slots = new List<WorldSlot>();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT slot_index, structure_id, construction_turns_remaining
            FROM rpg_world_slots WHERE world_id = $w AND sector_id = $s ORDER BY slot_index;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            slots.Add(new WorldSlot
            {
                SlotIndex = r.GetInt32(0),
                StructureId = r.IsDBNull(1) ? null : r.GetString(1),
                ConstructionTurnsRemaining = r.IsDBNull(2) ? null : r.GetInt32(2),
            });
        return new WorldSector { SectorId = sectorId, Slots = slots };
    }

    internal int NextSectorSeqUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, string sectorId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COALESCE(MAX(seq), -1) + 1 FROM rpg_world_sector_storage
            WHERE world_id = $w AND sector_id = $s;
            """;
        cmd.Parameters.AddWithValue("$w", worldId);
        cmd.Parameters.AddWithValue("$s", sectorId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
