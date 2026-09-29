using System.Diagnostics;
using System.Text.Json;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// base-defense `world-graph-diff` step 3 (spec-world-graph-diff.md) — writes only what changed
/// between two world states, instead of the clear-and-rewrite <see cref="RpgStore.WriteWorldGraphUnlocked"/>
/// still uses for world creation. Not cancelled by step 1's measurement
/// (docs/research/perf/02-world-graph-write.md): ~85% of a clear-and-rewrite commit's cost tracks row
/// count directly, and decision 19/21 (18x20-slot worlds, sectors that GROW slots) is exactly what
/// multiplies that term.
///
/// <para><b>The equivalence guard is the module's whole safety argument</b> (spec, quoting the
/// original author's own comment on why clear-and-rewrite was chosen first: "a partial-update path
/// carries a bug surface a full rewrite does not"). After every diff write, this reads the graph back
/// on the SAME connection/transaction (so it sees the write before it commits) and asserts its
/// <see cref="FusionRpg.Core.World.Turn.StateHasher"/> hash equals the target state's — a stale row
/// that should have been deleted and was not is invisible until read back, and this makes that
/// failure loud and immediate instead of a silently-wrong save. Always on in Debug builds (covers the
/// test suite, which builds Debug by default); in Release it needs an explicit opt-in,
/// <c>FUSIONRPG_WORLD_DIFF_CHECK=1</c> — structural, a diagnostic gate rather than a balance number,
/// so it is a compile-time/env toggle, not a `gk-core/data/tuning/*.json` entry (tunables-ssot.md: only
/// numbers a balance pass would change belong there).</para>
/// </summary>
public sealed partial class RpgStore
{
#if DEBUG
    internal static bool GraphWriteEquivalenceCheckEnabled = true;
#else
    internal static bool GraphWriteEquivalenceCheckEnabled =
        Environment.GetEnvironmentVariable("FUSIONRPG_WORLD_DIFF_CHECK") == "1";
#endif

    /// <summary>
    /// Writes only the rows that differ between <paramref name="previous"/> and <paramref name="next"/>
    /// — DELETE for a row present in <paramref name="previous"/> and absent from
    /// <paramref name="next"/>, <c>INSERT OR REPLACE</c> for a row that is new or whose column values
    /// changed, nothing for a row that is byte-identical in both. Same 7 tables, same columns, same
    /// values as <see cref="WriteWorldGraphUnlocked"/> — only which rows get touched differs.
    /// </summary>
    static void DiffWorldGraphUnlocked(
        SqliteConnection db, SqliteTransaction tx, WorldState previous, WorldState next)
    {
        DiffFactions(db, tx, previous, next);
        DiffSectors(db, tx, previous, next);
        DiffSlots(db, tx, previous, next);
        DiffIntel(db, tx, previous, next);
        DiffLanes(db, tx, previous, next);
        DiffEntities(db, tx, previous, next);
        DiffMembers(db, tx, previous, next);

        if (GraphWriteEquivalenceCheckEnabled)
        {
            var readBack = LoadWorldGraphUnlocked(db, next.WorldId) with
            {
                WorldId = next.WorldId, TemplateId = next.TemplateId, Seed = next.Seed, CurrentTurn = next.CurrentTurn
            };
            var gotHash = FusionRpg.Core.World.Turn.StateHasher.Hash(readBack);
            var wantHash = FusionRpg.Core.World.Turn.StateHasher.Hash(next);
            Debug.Assert(gotHash == wantHash,
                $"world-graph-diff equivalence guard failed for world '{next.WorldId}': " +
                $"diff write's own read-back hashes to {gotHash}, full state hashes to {wantHash}. " +
                "A diffed row did not change when it should have, or a DELETE was missed.");
        }
    }

    /// <summary>
    /// Test-only seam (visible via this project's own <c>InternalsVisibleTo</c>): diffs the
    /// currently-stored graph for <paramref name="worldId"/> against <paramref name="next"/> and
    /// returns the freshly-read-back state, without going through
    /// <see cref="FusionRpg.Core.World.Turn.TurnEngine"/> or a command script. Lets `world-graph-diff` 3.3's own tests (DELETE handling, grown slot lists,
    /// the unchanged-world no-op case) construct the exact before/after pair they want to prove,
    /// the same way the production commit path calls <see cref="DiffWorldGraphUnlocked"/> — real
    /// SQL, real transaction, real equivalence guard — just without needing a scripted turn to reach
    /// a particular shape.
    /// </summary>
    internal WorldState DiffCommitForTest(string worldId, WorldState next)
    {
        lock (_gate)
        {
            var previous = LoadWorldState(worldId) ?? throw new InvalidOperationException(
                $"DiffCommitForTest: world '{worldId}' does not exist -- create it first.");
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            DiffWorldGraphUnlocked(db, tx, previous, next);
            tx.Commit();
            return LoadWorldState(worldId)!;
        }
    }

    // ---- factions --------------------------------------------------------------------------------

    static void DiffFactions(SqliteConnection db, SqliteTransaction tx, WorldState previous, WorldState next)
    {
        var before = previous.Factions.ToDictionary(f => f.FactionId, StringComparer.Ordinal);
        var after = next.Factions.ToDictionary(f => f.FactionId, StringComparer.Ordinal);

        DeleteMissing(db, tx, "rpg_world_factions", "faction_id", next.WorldId,
            before.Keys.Where(k => !after.ContainsKey(k)));

        using var cmd = Prepared(db, tx, """
            INSERT OR REPLACE INTO rpg_world_factions (world_id, faction_id, kind, name, policy_id, upkeep_handicap_milli, scope_modifier_milli)
            VALUES ($w, $f, $k, $n, $pol, $handicap, $scope);
            """,
            "$w", "$f", "$k", "$n", "$pol", "$handicap", "$scope");

        foreach (var (id, f) in after)
        {
            // was == f compares the full WorldFaction record, so an unchanged ScopeModifierMilli
            // is skipped for free and a changed one is written — no extra logic needed here.
            if (before.TryGetValue(id, out var was) && was == f) continue;
            ExecuteWith(cmd, next.WorldId, f.FactionId, f.Kind.ToString(), f.Name,
                (object?)f.PolicyId, f.UpkeepHandicapMilli, f.ScopeModifierMilli);
        }
    }

    // ---- sectors (row only -- Slots is a separate table, diffed independently) -------------------

    static bool SectorRowEquals(WorldSector a, WorldSector b) =>
        a.TypeId == b.TypeId && a.Climate == b.Climate && a.DangerBand == b.DangerBand
        && a.Phase == b.Phase && a.OwnerFactionId == b.OwnerFactionId
        && a.StabilityMilli == b.StabilityMilli && a.PressureMilli == b.PressureMilli
        && a.DepletionMilli == b.DepletionMilli && a.DevelopmentLevel == b.DevelopmentLevel
        && a.AuthoredIntel == b.AuthoredIntel && a.LastSeenTurn == b.LastSeenTurn
        && a.LayoutX == b.LayoutX && a.LayoutY == b.LayoutY && a.LoamStock == b.LoamStock
        && a.FractureIntensityMilli == b.FractureIntensityMilli && a.WardenBindingId == b.WardenBindingId
        && a.NeglectedTurns == b.NeglectedTurns && a.RecruitStock == b.RecruitStock
        && a.ProjectId == b.ProjectId && a.ProjectTurnsRemaining == b.ProjectTurnsRemaining
        && a.RubbleStock == b.RubbleStock && a.IronworkStock == b.IronworkStock;

    static void DiffSectors(SqliteConnection db, SqliteTransaction tx, WorldState previous, WorldState next)
    {
        var before = previous.Sectors.ToDictionary(s => s.SectorId, StringComparer.Ordinal);
        var after = next.Sectors.ToDictionary(s => s.SectorId, StringComparer.Ordinal);

        DeleteMissing(db, tx, "rpg_world_sectors", "sector_id", next.WorldId,
            before.Keys.Where(k => !after.ContainsKey(k)));

        // scoped-inventory `sector-storage` (spec-sector-storage.md §Design 5): this is
        // INSERT ... ON CONFLICT DO UPDATE, deliberately NOT INSERT OR REPLACE. REPLACE is
        // DELETE + INSERT, and with FK enforcement on (Microsoft.Data.Sqlite enables
        // PRAGMA foreign_keys per connection) the DELETE half would cascade into
        // rpg_world_sector_storage's children — a capture's own sector-row rewrite would
        // wipe the very storage the capture must leave untouched (zero rows migrated).
        // DO UPDATE sets the identical final column values with no delete, so children
        // survive. A genuinely removed sector still cascades via DeleteMissing above.
        using var cmd = Prepared(db, tx, """
            INSERT INTO rpg_world_sectors (world_id, sector_id, type_id, climate, danger_band,
                phase, owner_faction_id, stability_milli, pressure_milli, depletion_milli,
                development_level, intel, last_seen_turn, layout_x, layout_y,
                loam_stock, fracture_intensity_milli, warden_binding_id, neglected_turns,
                recruit_stock, project_id, project_turns_remaining, rubble_stock, ironwork_stock, revision)
            VALUES ($w, $s, $type, $climate, $danger, $phase, $owner, $stab, $press, $depl,
                    $dev, $intel, $seen, $x, $y, $loam, $intensity, $warden, $neglected,
                    $recruit, $project, $projTurns, $rubble, $ironwork, 0)
            ON CONFLICT(world_id, sector_id) DO UPDATE SET
                type_id = excluded.type_id, climate = excluded.climate,
                danger_band = excluded.danger_band, phase = excluded.phase,
                owner_faction_id = excluded.owner_faction_id,
                stability_milli = excluded.stability_milli, pressure_milli = excluded.pressure_milli,
                depletion_milli = excluded.depletion_milli,
                development_level = excluded.development_level, intel = excluded.intel,
                last_seen_turn = excluded.last_seen_turn, layout_x = excluded.layout_x,
                layout_y = excluded.layout_y, loam_stock = excluded.loam_stock,
                fracture_intensity_milli = excluded.fracture_intensity_milli,
                warden_binding_id = excluded.warden_binding_id,
                neglected_turns = excluded.neglected_turns, recruit_stock = excluded.recruit_stock,
                project_id = excluded.project_id,
                project_turns_remaining = excluded.project_turns_remaining,
                rubble_stock = excluded.rubble_stock, ironwork_stock = excluded.ironwork_stock,
                revision = excluded.revision;
            """,
            "$w", "$s", "$type", "$climate", "$danger", "$phase", "$owner", "$stab", "$press", "$depl",
            "$dev", "$intel", "$seen", "$x", "$y", "$loam", "$intensity", "$warden", "$neglected",
            "$recruit", "$project", "$projTurns", "$rubble", "$ironwork");

        foreach (var (id, s) in after)
        {
            before.TryGetValue(id, out var was);
            if (was is not null && SectorRowEquals(was, s)) continue;
            ExecuteWith(cmd,
                next.WorldId, s.SectorId, s.TypeId, (object?)s.Climate?.ToString(), s.DangerBand,
                s.Phase.ToString(), (object?)s.OwnerFactionId, s.StabilityMilli, s.PressureMilli,
                s.DepletionMilli, s.DevelopmentLevel, s.AuthoredIntel.ToString(), s.LastSeenTurn,
                s.LayoutX, s.LayoutY, s.LoamStock, s.FractureIntensityMilli,
                (object?)s.WardenBindingId, s.NeglectedTurns, s.RecruitStock,
                (object?)s.ProjectId, (object?)s.ProjectTurnsRemaining, s.RubbleStock, s.IronworkStock);
            // scoped-inventory `sector-storage` capture hook (spec-sector-storage.md §Design 5):
            // inside this per-sector loop, in the same tx as the owner write above — never a
            // second transaction. The body is a documented no-op until a second real player_id
            // exists (see OnSectorCapturedUnlocked); the probe seam inside it is what the
            // forced-failure atomicity test trips to prove this fires before tx.Commit().
            if (was is not null
                && !string.Equals(was.OwnerFactionId, s.OwnerFactionId, StringComparison.Ordinal))
                OnSectorCapturedUnlocked(next.WorldId, s.SectorId, was.OwnerFactionId, s.OwnerFactionId);
        }
    }

    // ---- slots -------------------------------------------------------------------------------------

    static void DiffSlots(SqliteConnection db, SqliteTransaction tx, WorldState previous, WorldState next)
    {
        var before = previous.Sectors
            .SelectMany(s => s.Slots.Select(sl => ((s.SectorId, sl.SlotIndex), Sector: s.SectorId, Slot: sl)))
            .ToDictionary(x => x.Item1, x => x.Slot);
        var after = next.Sectors
            .SelectMany(s => s.Slots.Select(sl => ((s.SectorId, sl.SlotIndex), Sector: s.SectorId, Slot: sl)))
            .ToDictionary(x => x.Item1, x => x.Slot);

        var toDelete = before.Keys.Where(k => !after.ContainsKey(k)).ToList();
        if (toDelete.Count > 0)
        {
            using var del = Prepared(db, tx,
                "DELETE FROM rpg_world_slots WHERE world_id = $w AND sector_id = $s AND slot_index = $i;",
                "$w", "$s", "$i");
            foreach (var (sectorId, slotIndex) in toDelete)
                ExecuteWith(del, next.WorldId, sectorId, slotIndex);
        }

        using var cmd = Prepared(db, tx, """
            INSERT OR REPLACE INTO rpg_world_slots (world_id, sector_id, slot_index, slot_type_id,
                element, state, owner_faction_id, guard_wave_id, guard_state,
                structure_id, construction_turns_remaining, slot_depletion_milli, revision)
            VALUES ($w, $s, $i, $type, $elem, $state, $owner, $guard, $gstate,
                    $structure, $construction, $depletion, 0);
            """,
            "$w", "$s", "$i", "$type", "$elem", "$state", "$owner", "$guard", "$gstate",
            "$structure", "$construction", "$depletion");

        foreach (var (key, sl) in after)
        {
            if (before.TryGetValue(key, out var was) && was == sl) continue;
            ExecuteWith(cmd,
                next.WorldId, key.SectorId, key.SlotIndex, sl.SlotTypeId,
                (object?)sl.Element?.ToString(), sl.State.ToString(), (object?)sl.OwnerFactionId,
                (object?)sl.GuardWaveId, sl.GuardState.ToString(), (object?)sl.StructureId,
                (object?)sl.ConstructionTurnsRemaining, sl.SlotDepletionMilli);
        }
    }

    // ---- faction intel -- row-relevant fields only. RecruitStock/ProjectId/ProjectTurnsRemaining/
    // FractureIntensityMilli are NOT columns on rpg_world_faction_intel and are not part of
    // WorldCanonical's "intel" row either (WorldCanonical.cs:76-78) -- a pre-existing gap in what
    // that belief state persists at all, out of this module's scope. DevelopmentLevel WAS hashed by
    // WorldCanonical but had no column until this task's own fix (RpgStore.World.cs's
    // EnsureColumn(..., "development_level", ...) migration note) -- found by this module's own
    // equivalence guard, not designed in.

    static bool IntelRowEquals(IntelSnapshot a, IntelSnapshot b) =>
        a.LastSeenTurn == b.LastSeenTurn && a.Detail == b.Detail && a.OwnerFactionId == b.OwnerFactionId
        && a.Phase == b.Phase && a.Climate == b.Climate && a.DangerBand == b.DangerBand
        && a.DevelopmentLevel == b.DevelopmentLevel
        && a.Slots.SequenceEqual(b.Slots) && a.Forces.SequenceEqual(b.Forces);

    static void DiffIntel(SqliteConnection db, SqliteTransaction tx, WorldState previous, WorldState next)
    {
        var before = previous.Intel
            .SelectMany(fi => fi.Sectors.Select(snap => ((fi.FactionId, snap.SectorId), Snap: snap)))
            .ToDictionary(x => x.Item1, x => x.Snap);
        var after = next.Intel
            .SelectMany(fi => fi.Sectors.Select(snap => ((fi.FactionId, snap.SectorId), Snap: snap)))
            .ToDictionary(x => x.Item1, x => x.Snap);

        var toDelete = before.Keys.Where(k => !after.ContainsKey(k)).ToList();
        if (toDelete.Count > 0)
        {
            using var del = Prepared(db, tx,
                "DELETE FROM rpg_world_faction_intel WHERE world_id = $w AND faction_id = $f AND sector_id = $s;",
                "$w", "$f", "$s");
            foreach (var (factionId, sectorId) in toDelete)
                ExecuteWith(del, next.WorldId, factionId, sectorId);
        }

        using var cmd = Prepared(db, tx, """
            INSERT OR REPLACE INTO rpg_world_faction_intel (world_id, faction_id, sector_id, last_seen_turn,
                detail, owner_faction_id, phase, climate, danger_band, development_level, slots_json, forces_json)
            VALUES ($w, $f, $s, $turn, $detail, $owner, $phase, $climate, $danger, $dev, $slots, $forces);
            """,
            "$w", "$f", "$s", "$turn", "$detail", "$owner", "$phase", "$climate", "$danger", "$dev", "$slots", "$forces");

        foreach (var (key, snap) in after)
        {
            if (before.TryGetValue(key, out var was) && IntelRowEquals(was, snap)) continue;
            ExecuteWith(cmd,
                next.WorldId, key.FactionId, key.SectorId, snap.LastSeenTurn,
                snap.Detail.ToString(), (object?)snap.OwnerFactionId, snap.Phase.ToString(),
                (object?)snap.Climate?.ToString(), snap.DangerBand, snap.DevelopmentLevel,
                JsonSerializer.Serialize(snap.Slots), JsonSerializer.Serialize(snap.Forces));
        }
    }

    // ---- lanes -------------------------------------------------------------------------------------

    static void DiffLanes(SqliteConnection db, SqliteTransaction tx, WorldState previous, WorldState next)
    {
        var before = previous.Lanes.ToDictionary(l => l.LaneId, StringComparer.Ordinal);
        var after = next.Lanes.ToDictionary(l => l.LaneId, StringComparer.Ordinal);

        DeleteMissing(db, tx, "rpg_world_lanes", "lane_id", next.WorldId,
            before.Keys.Where(k => !after.ContainsKey(k)));

        using var cmd = Prepared(db, tx, """
            INSERT OR REPLACE INTO rpg_world_lanes (world_id, lane_id, from_sector_id, to_sector_id,
                type_id, length, width, hazard_milli, ward_level, gate_key_id, state, revision)
            VALUES ($w, $l, $from, $to, $type, $len, $width, $haz, $ward, $gate, $state, 0);
            """,
            "$w", "$l", "$from", "$to", "$type", "$len", "$width", "$haz", "$ward", "$gate", "$state");

        foreach (var (id, l) in after)
        {
            if (before.TryGetValue(id, out var was) && was == l) continue;
            ExecuteWith(cmd,
                next.WorldId, l.LaneId, l.FromSectorId, l.ToSectorId, l.TypeId, l.Length, l.Width,
                l.HazardMilli, l.WardLevel, (object?)l.GateKeyId, l.State.ToString());
        }
    }

    // ---- entities (row only -- Members is a separate table, diffed independently) -----------------

    static bool EntityRowEquals(WorldEntity a, WorldEntity b) =>
        a.Kind == b.Kind && a.OwnerFactionId == b.OwnerFactionId && a.AtSectorId == b.AtSectorId
        && a.OnLaneId == b.OnLaneId && a.OnLaneTowardSectorId == b.OnLaneTowardSectorId
        && a.LaneProgressMilli == b.LaneProgressMilli && a.Stance == b.Stance
        && a.MovementRemaining == b.MovementRemaining && a.Routed == b.Routed && a.CarriedLoam == b.CarriedLoam;

    static void DiffEntities(SqliteConnection db, SqliteTransaction tx, WorldState previous, WorldState next)
    {
        var before = previous.Entities.ToDictionary(e => e.EntityId, StringComparer.Ordinal);
        var after = next.Entities.ToDictionary(e => e.EntityId, StringComparer.Ordinal);

        // scoped-inventory `cargo-fate` (spec-cargo-fate.md §Design 2): the pre-delete move. A
        // destroyed legion's cargo must land in a corpse-cache row BEFORE DeleteMissing deletes
        // the entity row and its ON DELETE CASCADE wipes `rpg_world_entity_cargo` — the exact
        // ordering the spec's own Locked anchors name as a real, found risk. Same transaction,
        // move never copy. Last-known place reads from `previous`: the entity is already gone
        // from `next`. `WorldEntity.AtSectorId`/`OnLaneId` are mutually exclusive and one is
        // always set for a live entity (WorldState.cs:292 "never both, never neither") — there is
        // no third branch and no destroyed-outright fallback; a corrupt neither-placed row simply
        // deletes with no cache, never an invented place.
        var destroyed = before.Keys.Where(k => !after.ContainsKey(k)).ToList();
        foreach (var entityId in destroyed)
        {
            var lastKnown = before[entityId];
            if (lastKnown.AtSectorId is not null)
                MoveLegionCargoToCorpseCacheUnlocked(
                    db, tx, next.WorldId, entityId, "world_sector", lastKnown.AtSectorId);
            else if (lastKnown.OnLaneId is not null)
                MoveLegionCargoToCorpseCacheUnlocked(
                    db, tx, next.WorldId, entityId, "world_lane", lastKnown.OnLaneId);
        }

        DeleteMissing(db, tx, "rpg_world_entities", "entity_id", next.WorldId, destroyed);

        // scoped-inventory `cargo-fate` cascade fix (same shape Task 1.2b applied for sectors in
        // DiffSectors above): this is INSERT ... ON CONFLICT DO UPDATE, deliberately NOT INSERT OR
        // REPLACE. REPLACE is DELETE + INSERT, and with FK enforcement on
        // (Microsoft.Data.Sqlite enables PRAGMA foreign_keys per connection) the DELETE half would
        // cascade into `rpg_world_entity_cargo`'s children — rewriting a SURVIVING legion's row
        // (any field change: stance, position, loam) would wipe its cargo. DO UPDATE sets the
        // identical final column values with no delete, so cargo survives. A genuinely destroyed
        // legion still cascades via DeleteMissing above — but only after the hook moved its cargo.
        using var cmd = Prepared(db, tx, """
            INSERT INTO rpg_world_entities (world_id, entity_id, kind, owner_faction_id,
                at_sector_id, on_lane_id, on_lane_toward_sector_id, lane_progress_milli,
                stance, movement_remaining, routed, carried_loam, revision)
            VALUES ($w, $e, $kind, $owner, $at, $lane, $toward, $prog, $stance, $move, $routed,
                    $carried, 0)
            ON CONFLICT(world_id, entity_id) DO UPDATE SET
                kind = excluded.kind, owner_faction_id = excluded.owner_faction_id,
                at_sector_id = excluded.at_sector_id, on_lane_id = excluded.on_lane_id,
                on_lane_toward_sector_id = excluded.on_lane_toward_sector_id,
                lane_progress_milli = excluded.lane_progress_milli, stance = excluded.stance,
                movement_remaining = excluded.movement_remaining, routed = excluded.routed,
                carried_loam = excluded.carried_loam, revision = excluded.revision;
            """,
            "$w", "$e", "$kind", "$owner", "$at", "$lane", "$toward", "$prog", "$stance", "$move",
            "$routed", "$carried");

        foreach (var (id, e) in after)
        {
            if (before.TryGetValue(id, out var was) && EntityRowEquals(was, e)) continue;
            ExecuteWith(cmd,
                next.WorldId, e.EntityId, e.Kind.ToString(), e.OwnerFactionId,
                (object?)e.AtSectorId, (object?)e.OnLaneId, (object?)e.OnLaneTowardSectorId,
                e.LaneProgressMilli, e.Stance, e.MovementRemaining, e.Routed ? 1 : 0, e.CarriedLoam);
        }
    }

    // ---- entity members ------------------------------------------------------------------------

    static void DiffMembers(SqliteConnection db, SqliteTransaction tx, WorldState previous, WorldState next)
    {
        var before = previous.Entities
            .SelectMany(e => e.Members.Select((m, i) => ((e.EntityId, Index: i), Member: m)))
            .ToDictionary(x => x.Item1, x => x.Member);
        var after = next.Entities
            .SelectMany(e => e.Members.Select((m, i) => ((e.EntityId, Index: i), Member: m)))
            .ToDictionary(x => x.Item1, x => x.Member);

        var toDelete = before.Keys.Where(k => !after.ContainsKey(k)).ToList();
        if (toDelete.Count > 0)
        {
            using var del = Prepared(db, tx,
                "DELETE FROM rpg_world_entity_members WHERE world_id = $w AND entity_id = $e AND member_index = $i;",
                "$w", "$e", "$i");
            foreach (var (entityId, index) in toDelete)
                ExecuteWith(del, next.WorldId, entityId, index);
        }

        using var cmd = Prepared(db, tx, """
            INSERT OR REPLACE INTO rpg_world_entity_members (world_id, entity_id, member_index,
                instance_id, species_id, level, hp, wounds, role)
            VALUES ($w, $e, $i, $inst, $sp, $lvl, $hp, $wounds, $role);
            """,
            "$w", "$e", "$i", "$inst", "$sp", "$lvl", "$hp", "$wounds", "$role");

        foreach (var (key, m) in after)
        {
            if (before.TryGetValue(key, out var was) && was == m) continue;
            ExecuteWith(cmd,
                next.WorldId, key.EntityId, key.Index, (object?)m.InstanceId, m.SpeciesId,
                m.Level, m.Hp, m.Wounds, m.Role.ToString());
        }
    }

    // ---- shared -------------------------------------------------------------------------------------

    static void DeleteMissing(SqliteConnection db, SqliteTransaction tx, string table, string keyColumn,
        string worldId, IEnumerable<string> keys)
    {
        var list = keys as IReadOnlyCollection<string> ?? keys.ToList();
        if (list.Count == 0) return;

        using var del = Prepared(db, tx, $"DELETE FROM {table} WHERE world_id = $w AND {keyColumn} = $k;", "$w", "$k");
        foreach (var key in list)
            ExecuteWith(del, worldId, key);
    }
}
