using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Bench;

/// <summary>
/// base-defense `world-graph-diff` (spec-world-graph-diff.md) step 1 — measure before choosing a
/// diffing writer. The audit's own build cost C5: turn-commit cost as arithmetic (~360 slot rows at
/// 18 sectors x ~20 slots, decision 19's scale) omits its largest term — a fresh <see
/// cref="SqliteCommand"/> per row, and `slots_json`/`forces_json` re-serialised per (faction x
/// sector) in `rpg_world_faction_intel`. This benchmark isolates row-count cost from per-row
/// overhead so the decision gate (statement reuse vs. a diffing writer) is read off a number, not
/// guessed.
///
/// <para><b>Why an isolated harness instead of calling into <c>RpgStore</c> directly:</b> every
/// write helper in <c>RpgStore.World.cs</c> (<c>ClearWorldGraphUnlocked</c>,
/// <c>WriteWorldGraphUnlocked</c>, <c>Insert</c>) is private, and widening that surface for a
/// benchmark-only caller would touch production code to answer a question production code does not
/// need to know the answer to. The schema below is the same <c>CREATE TABLE</c> text
/// <c>EnsureWorldSchemaUnlocked</c> ships (copied, not referenced — see the citation on each table),
/// and the two write patterns reproduce <c>Insert</c>'s exact calling convention (one
/// <see cref="SqliteCommand"/> per row, <c>AddWithValue</c> per parameter) and its prepared-statement
/// alternative. Representative of the real cost because the SQL text and the transaction shape are
/// identical; independent of the real cost because nothing here can move a golden or block on
/// `guard-dal` — there is no game code in this file.</para>
/// </summary>
public static class WorldGraphWriteBench
{
    const int Runs = 7;          // median of 7 -- one commit is comparatively expensive; 9 would be slow to iterate on
    const int SectorCount = 18;  // decision 19's scale
    const int SlotsPerSector = 20;
    const int LaneCount = 20;
    const int EntityCount = 30;
    const int MembersPerEntity = 3;
    const int FactionCount = 2;  // faction_intel is written per (faction x sector)

    public sealed record Phase(string Name, double MedianMs);
    public sealed record Result(int TotalRows, IReadOnlyList<Phase> Phases);

    public static Result Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "fusionrpg-bench-worldgraph-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var db = new SqliteConnection($"Data Source={path}");
            db.Open();
            CreateSchema(db);

            var world = SyntheticWorld.Build(SectorCount, SlotsPerSector, LaneCount, EntityCount, MembersPerEntity, FactionCount);

            // Warm the connection/JIT once before any recorded run, same discipline as AtomFormBench.
            WriteFreshCommandPerRow(db, world);
            ClearAll(db);

            var clearMs = new double[Runs];
            var writeFreshMs = new double[Runs];
            var writePreparedMs = new double[Runs];
            var jsonOnlyMs = new double[Runs];
            var commandConstructOnlyMs = new double[Runs];
            var commandExecuteOnlyMs = new double[Runs];
            var emptyCommitMs = new double[Runs];

            for (var i = 0; i < Runs; i++)
            {
                writeFreshMs[i] = Time(() => WriteFreshCommandPerRow(db, world));
                clearMs[i] = Time(() => ClearAll(db));

                writePreparedMs[i] = Time(() => WritePreparedPerTable(db, world));
                ClearAll(db);

                jsonOnlyMs[i] = Time(() => SerializeIntelOnly(world));
                var (constructMs, executeMs) = TimeConstructVsExecute(db, world);
                commandConstructOnlyMs[i] = constructMs;
                commandExecuteOnlyMs[i] = executeMs;
                ClearAll(db);

                // Neither candidate fix (statement reuse, a diffing writer) touches how many
                // transactions a turn commit opens -- it is always exactly one. If commit's own
                // fsync dominates clear/write above, that is a THIRD cost neither candidate reduces,
                // and it needs to be reported rather than folded silently into "row count."
                emptyCommitMs[i] = Time(() =>
                {
                    using var tx = db.BeginTransaction();
                    tx.Commit();
                });
            }

            var totalRows = world.Sectors.Count
                + world.Sectors.Sum(s => s.Slots.Count)
                + world.Lanes.Count
                + world.Entities.Count
                + world.Entities.Sum(e => e.Members)
                + world.Factions.Count
                + world.Intel.Count;

            return new Result(totalRows, new[]
            {
                new Phase("clear (7x DELETE, one command per table)", Median(clearMs)),
                new Phase("write -- fresh SqliteCommand per row (today's RpgStore.Insert)", Median(writeFreshMs)),
                new Phase("write -- one prepared SqliteCommand reused per table", Median(writePreparedMs)),
                new Phase("  of which: slots_json/forces_json serialisation alone (C5's suspect)", Median(jsonOnlyMs)),
                new Phase("  of which: SqliteCommand construction alone (fresh-per-row pattern)", Median(commandConstructOnlyMs)),
                new Phase("  of which: ExecuteNonQuery alone (fresh-per-row pattern)", Median(commandExecuteOnlyMs)),
                new Phase("control: an EMPTY transaction's own BeginTransaction+Commit", Median(emptyCommitMs)),
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // solid-enforcement tuning-immutability SE2.3: a failed temp-delete is a failure, never
            // a swallowed catch (docs/contributing/testing-standard.md) -- `path` is this bench's own
            // Path.GetTempPath() scratch file, never tracked content.
            File.Delete(path);
        }
    }

    static double Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    static double Median(double[] samples)
    {
        var copy = (double[])samples.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    // ---- schema: copied verbatim from RpgStore.World.cs EnsureWorldSchemaUnlocked -----------------

    static void CreateSchema(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE rpg_world_factions (
              world_id TEXT NOT NULL, faction_id TEXT NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL,
              policy_id TEXT, upkeep_handicap_milli INTEGER NOT NULL DEFAULT 1000,
              PRIMARY KEY (world_id, faction_id)
            );
            CREATE TABLE rpg_world_sectors (
              world_id TEXT NOT NULL, sector_id TEXT NOT NULL, type_id TEXT NOT NULL, climate TEXT,
              danger_band INTEGER NOT NULL DEFAULT 0, phase TEXT NOT NULL, owner_faction_id TEXT,
              stability_milli INTEGER NOT NULL DEFAULT 0, pressure_milli INTEGER NOT NULL DEFAULT 0,
              depletion_milli INTEGER NOT NULL DEFAULT 0, development_level INTEGER NOT NULL DEFAULT 0,
              intel TEXT NOT NULL, last_seen_turn INTEGER NOT NULL DEFAULT 0,
              layout_x INTEGER NOT NULL DEFAULT 0, layout_y INTEGER NOT NULL DEFAULT 0,
              loam_stock INTEGER NOT NULL DEFAULT 0, fracture_intensity_milli INTEGER NOT NULL DEFAULT 1000,
              warden_binding_id TEXT, neglected_turns INTEGER NOT NULL DEFAULT 0,
              recruit_stock INTEGER NOT NULL DEFAULT 0, project_id TEXT, project_turns_remaining INTEGER,
              revision INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (world_id, sector_id)
            );
            CREATE TABLE rpg_world_slots (
              world_id TEXT NOT NULL, sector_id TEXT NOT NULL, slot_index INTEGER NOT NULL,
              slot_type_id TEXT NOT NULL, element TEXT, state TEXT NOT NULL, owner_faction_id TEXT,
              guard_wave_id TEXT, guard_state TEXT NOT NULL, structure_id TEXT,
              construction_turns_remaining INTEGER, revision INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (world_id, sector_id, slot_index)
            );
            CREATE TABLE rpg_world_lanes (
              world_id TEXT NOT NULL, lane_id TEXT NOT NULL, from_sector_id TEXT NOT NULL,
              to_sector_id TEXT NOT NULL, type_id TEXT NOT NULL, length INTEGER NOT NULL DEFAULT 1000,
              width INTEGER NOT NULL DEFAULT 1000, hazard_milli INTEGER NOT NULL DEFAULT 0,
              ward_level INTEGER NOT NULL DEFAULT 0, gate_key_id TEXT, state TEXT NOT NULL,
              revision INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (world_id, lane_id)
            );
            CREATE TABLE rpg_world_entities (
              world_id TEXT NOT NULL, entity_id TEXT NOT NULL, kind TEXT NOT NULL,
              owner_faction_id TEXT NOT NULL, at_sector_id TEXT, on_lane_id TEXT,
              on_lane_toward_sector_id TEXT, lane_progress_milli INTEGER NOT NULL DEFAULT 0,
              stance TEXT NOT NULL, movement_remaining INTEGER NOT NULL DEFAULT 0,
              routed INTEGER NOT NULL DEFAULT 0, carried_loam INTEGER NOT NULL DEFAULT 0,
              revision INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (world_id, entity_id)
            );
            CREATE TABLE rpg_world_faction_intel (
              world_id TEXT NOT NULL, faction_id TEXT NOT NULL, sector_id TEXT NOT NULL,
              last_seen_turn INTEGER NOT NULL, detail TEXT NOT NULL, owner_faction_id TEXT,
              phase TEXT NOT NULL, climate TEXT, danger_band INTEGER NOT NULL,
              slots_json TEXT NOT NULL, forces_json TEXT NOT NULL,
              PRIMARY KEY (world_id, faction_id, sector_id)
            );
            CREATE TABLE rpg_world_entity_members (
              world_id TEXT NOT NULL, entity_id TEXT NOT NULL, member_index INTEGER NOT NULL,
              instance_id TEXT, species_id TEXT NOT NULL, level INTEGER NOT NULL DEFAULT 1,
              hp INTEGER NOT NULL DEFAULT 0, wounds INTEGER NOT NULL DEFAULT 0,
              role TEXT NOT NULL DEFAULT 'Fighter',
              PRIMARY KEY (world_id, entity_id, member_index)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ---- pattern A: today's RpgStore.Insert -- one fresh SqliteCommand per row --------------------

    static void WriteFreshCommandPerRow(SqliteConnection db, SyntheticWorld world)
    {
        using var tx = db.BeginTransaction();

        foreach (var f in world.Factions)
            InsertFresh(db, tx,
                "INSERT INTO rpg_world_factions (world_id, faction_id, kind, name, policy_id, upkeep_handicap_milli) VALUES ($w,$f,$k,$n,$p,$h);",
                ("$w", world.WorldId), ("$f", f), ("$k", "Faction"), ("$n", f), ("$p", DBNull.Value), ("$h", 1000));

        foreach (var s in world.Sectors)
        {
            InsertFresh(db, tx,
                """
                INSERT INTO rpg_world_sectors (world_id, sector_id, type_id, climate, danger_band, phase,
                    owner_faction_id, stability_milli, pressure_milli, depletion_milli, development_level,
                    intel, last_seen_turn, layout_x, layout_y, loam_stock, fracture_intensity_milli,
                    warden_binding_id, neglected_turns, recruit_stock, project_id, project_turns_remaining, revision)
                VALUES ($w,$s,$t,$cl,$d,$ph,$o,$st,$pr,$de,$dv,$i,$ls,$x,$y,$lo,$fr,$wa,$ne,$re,$pj,$pt,0);
                """,
                ("$w", world.WorldId), ("$s", s.SectorId), ("$t", "outpost"), ("$cl", DBNull.Value),
                ("$d", 1), ("$ph", "Held"), ("$o", "dave"), ("$st", 500), ("$pr", 0), ("$de", 0),
                ("$dv", 1), ("$i", "Known"), ("$ls", 1), ("$x", 0), ("$y", 0), ("$lo", 0L),
                ("$fr", 1000), ("$wa", DBNull.Value), ("$ne", 0), ("$re", 0L), ("$pj", DBNull.Value),
                ("$pt", DBNull.Value));

            foreach (var sl in s.Slots)
                InsertFresh(db, tx,
                    """
                    INSERT INTO rpg_world_slots (world_id, sector_id, slot_index, slot_type_id, element,
                        state, owner_faction_id, guard_wave_id, guard_state, structure_id,
                        construction_turns_remaining, revision)
                    VALUES ($w,$s,$i,$t,$e,$st,$o,$g,$gs,$str,$c,0);
                    """,
                    ("$w", world.WorldId), ("$s", s.SectorId), ("$i", sl), ("$t", "seat"),
                    ("$e", DBNull.Value), ("$st", "Open"), ("$o", "dave"), ("$g", DBNull.Value),
                    ("$gs", "Cleared"), ("$str", DBNull.Value), ("$c", DBNull.Value));
        }

        foreach (var intel in world.Intel)
            InsertFresh(db, tx,
                """
                INSERT INTO rpg_world_faction_intel (world_id, faction_id, sector_id, last_seen_turn,
                    detail, owner_faction_id, phase, climate, danger_band, slots_json, forces_json)
                VALUES ($w,$f,$s,$t,$d,$o,$ph,$cl,$db,$sj,$fj);
                """,
                ("$w", world.WorldId), ("$f", intel.FactionId), ("$s", intel.SectorId), ("$t", 1),
                ("$d", "Known"), ("$o", "dave"), ("$ph", "Held"), ("$cl", DBNull.Value), ("$db", 1),
                ("$sj", JsonSerializer.Serialize(intel.Slots)), ("$fj", JsonSerializer.Serialize(intel.Forces)));

        foreach (var l in world.Lanes)
            InsertFresh(db, tx,
                """
                INSERT INTO rpg_world_lanes (world_id, lane_id, from_sector_id, to_sector_id, type_id,
                    length, width, hazard_milli, ward_level, gate_key_id, state, revision)
                VALUES ($w,$l,$f,$t,$ty,$le,$wi,$h,$wl,$g,$s,0);
                """,
                ("$w", world.WorldId), ("$l", l.LaneId), ("$f", l.FromSectorId), ("$t", l.ToSectorId),
                ("$ty", "path"), ("$le", 1000), ("$wi", 1000), ("$h", 0), ("$wl", 0),
                ("$g", DBNull.Value), ("$s", "Open"));

        foreach (var e in world.Entities)
        {
            InsertFresh(db, tx,
                """
                INSERT INTO rpg_world_entities (world_id, entity_id, kind, owner_faction_id, at_sector_id,
                    on_lane_id, on_lane_toward_sector_id, lane_progress_milli, stance, movement_remaining,
                    routed, carried_loam, revision)
                VALUES ($w,$e,$k,$o,$at,$l,$tw,$lp,$st,$m,$r,$c,0);
                """,
                ("$w", world.WorldId), ("$e", e.EntityId), ("$k", "Legion"), ("$o", "dave"),
                ("$at", e.AtSectorId), ("$l", DBNull.Value), ("$tw", DBNull.Value), ("$lp", 0),
                ("$st", "Hold"), ("$m", 1000), ("$r", 0), ("$c", 0L));

            for (var i = 0; i < e.Members; i++)
                InsertFresh(db, tx,
                    """
                    INSERT INTO rpg_world_entity_members (world_id, entity_id, member_index, instance_id,
                        species_id, level, hp, wounds, role)
                    VALUES ($w,$e,$i,$in,$sp,$lv,$hp,$wo,$ro);
                    """,
                    ("$w", world.WorldId), ("$e", e.EntityId), ("$i", i), ("$in", DBNull.Value),
                    ("$sp", "wild-pack"), ("$lv", 1), ("$hp", 100), ("$wo", 0), ("$ro", "Fighter"));
        }

        tx.Commit();
    }

    static void InsertFresh(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ---- pattern B: one SqliteCommand per table, parameter values reassigned per row ---------------

    static void WritePreparedPerTable(SqliteConnection db, SyntheticWorld world)
    {
        using var tx = db.BeginTransaction();

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO rpg_world_factions (world_id, faction_id, kind, name, policy_id, upkeep_handicap_milli) VALUES ($w,$f,$k,$n,$p,$h);";
            var pw = cmd.Parameters.Add("$w", SqliteType.Text); var pf = cmd.Parameters.Add("$f", SqliteType.Text);
            var pk = cmd.Parameters.Add("$k", SqliteType.Text); var pn = cmd.Parameters.Add("$n", SqliteType.Text);
            var pp = cmd.Parameters.Add("$p", SqliteType.Text); var ph = cmd.Parameters.Add("$h", SqliteType.Integer);
            cmd.Prepare();
            foreach (var f in world.Factions)
            {
                pw.Value = world.WorldId; pf.Value = f; pk.Value = "Faction"; pn.Value = f;
                pp.Value = DBNull.Value; ph.Value = 1000;
                cmd.ExecuteNonQuery();
            }
        }

        using (var sectorCmd = db.CreateCommand())
        using (var slotCmd = db.CreateCommand())
        {
            sectorCmd.Transaction = tx;
            sectorCmd.CommandText = """
                INSERT INTO rpg_world_sectors (world_id, sector_id, type_id, climate, danger_band, phase,
                    owner_faction_id, stability_milli, pressure_milli, depletion_milli, development_level,
                    intel, last_seen_turn, layout_x, layout_y, loam_stock, fracture_intensity_milli,
                    warden_binding_id, neglected_turns, recruit_stock, project_id, project_turns_remaining, revision)
                VALUES ($w,$s,$t,$cl,$d,$ph,$o,$st,$pr,$de,$dv,$i,$ls,$x,$y,$lo,$fr,$wa,$ne,$re,$pj,$pt,0);
                """;
            var sw = sectorCmd.Parameters.Add("$w", SqliteType.Text); var ss = sectorCmd.Parameters.Add("$s", SqliteType.Text);
            var st = sectorCmd.Parameters.Add("$t", SqliteType.Text); var scl = sectorCmd.Parameters.Add("$cl", SqliteType.Text);
            var sd = sectorCmd.Parameters.Add("$d", SqliteType.Integer); var sph = sectorCmd.Parameters.Add("$ph", SqliteType.Text);
            var so = sectorCmd.Parameters.Add("$o", SqliteType.Text); var sst = sectorCmd.Parameters.Add("$st", SqliteType.Integer);
            var spr = sectorCmd.Parameters.Add("$pr", SqliteType.Integer); var sde = sectorCmd.Parameters.Add("$de", SqliteType.Integer);
            var sdv = sectorCmd.Parameters.Add("$dv", SqliteType.Integer); var si = sectorCmd.Parameters.Add("$i", SqliteType.Text);
            var sls = sectorCmd.Parameters.Add("$ls", SqliteType.Integer); var sx = sectorCmd.Parameters.Add("$x", SqliteType.Integer);
            var sy = sectorCmd.Parameters.Add("$y", SqliteType.Integer); var slo = sectorCmd.Parameters.Add("$lo", SqliteType.Integer);
            var sfr = sectorCmd.Parameters.Add("$fr", SqliteType.Integer); var swa = sectorCmd.Parameters.Add("$wa", SqliteType.Text);
            var sne = sectorCmd.Parameters.Add("$ne", SqliteType.Integer); var sre = sectorCmd.Parameters.Add("$re", SqliteType.Integer);
            var spj = sectorCmd.Parameters.Add("$pj", SqliteType.Text); var spt = sectorCmd.Parameters.Add("$pt", SqliteType.Integer);
            sectorCmd.Prepare();

            slotCmd.Transaction = tx;
            slotCmd.CommandText = """
                INSERT INTO rpg_world_slots (world_id, sector_id, slot_index, slot_type_id, element,
                    state, owner_faction_id, guard_wave_id, guard_state, structure_id,
                    construction_turns_remaining, revision)
                VALUES ($w,$s,$i,$t,$e,$st,$o,$g,$gs,$str,$c,0);
                """;
            var lw = slotCmd.Parameters.Add("$w", SqliteType.Text); var ls_ = slotCmd.Parameters.Add("$s", SqliteType.Text);
            var li = slotCmd.Parameters.Add("$i", SqliteType.Integer); var lt = slotCmd.Parameters.Add("$t", SqliteType.Text);
            var le = slotCmd.Parameters.Add("$e", SqliteType.Text); var lst = slotCmd.Parameters.Add("$st", SqliteType.Text);
            var lo = slotCmd.Parameters.Add("$o", SqliteType.Text); var lg = slotCmd.Parameters.Add("$g", SqliteType.Text);
            var lgs = slotCmd.Parameters.Add("$gs", SqliteType.Text); var lstr = slotCmd.Parameters.Add("$str", SqliteType.Text);
            var lc = slotCmd.Parameters.Add("$c", SqliteType.Integer);
            slotCmd.Prepare();

            foreach (var s in world.Sectors)
            {
                sw.Value = world.WorldId; ss.Value = s.SectorId; st.Value = "outpost"; scl.Value = DBNull.Value;
                sd.Value = 1; sph.Value = "Held"; so.Value = "dave"; sst.Value = 500; spr.Value = 0;
                sde.Value = 0; sdv.Value = 1; si.Value = "Known"; sls.Value = 1; sx.Value = 0; sy.Value = 0;
                slo.Value = 0L; sfr.Value = 1000; swa.Value = DBNull.Value; sne.Value = 0; sre.Value = 0L;
                spj.Value = DBNull.Value; spt.Value = DBNull.Value;
                sectorCmd.ExecuteNonQuery();

                foreach (var sl in s.Slots)
                {
                    lw.Value = world.WorldId; ls_.Value = s.SectorId; li.Value = sl; lt.Value = "seat";
                    le.Value = DBNull.Value; lst.Value = "Open"; lo.Value = "dave"; lg.Value = DBNull.Value;
                    lgs.Value = "Cleared"; lstr.Value = DBNull.Value; lc.Value = DBNull.Value;
                    slotCmd.ExecuteNonQuery();
                }
            }
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO rpg_world_faction_intel (world_id, faction_id, sector_id, last_seen_turn,
                    detail, owner_faction_id, phase, climate, danger_band, slots_json, forces_json)
                VALUES ($w,$f,$s,$t,$d,$o,$ph,$cl,$db,$sj,$fj);
                """;
            var pw = cmd.Parameters.Add("$w", SqliteType.Text); var pf = cmd.Parameters.Add("$f", SqliteType.Text);
            var ps = cmd.Parameters.Add("$s", SqliteType.Text); var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pd = cmd.Parameters.Add("$d", SqliteType.Text); var po = cmd.Parameters.Add("$o", SqliteType.Text);
            var pph = cmd.Parameters.Add("$ph", SqliteType.Text); var pcl = cmd.Parameters.Add("$cl", SqliteType.Text);
            var pdb = cmd.Parameters.Add("$db", SqliteType.Integer); var psj = cmd.Parameters.Add("$sj", SqliteType.Text);
            var pfj = cmd.Parameters.Add("$fj", SqliteType.Text);
            cmd.Prepare();

            foreach (var intel in world.Intel)
            {
                pw.Value = world.WorldId; pf.Value = intel.FactionId; ps.Value = intel.SectorId; pt.Value = 1;
                pd.Value = "Known"; po.Value = "dave"; pph.Value = "Held"; pcl.Value = DBNull.Value; pdb.Value = 1;
                psj.Value = JsonSerializer.Serialize(intel.Slots); pfj.Value = JsonSerializer.Serialize(intel.Forces);
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO rpg_world_lanes (world_id, lane_id, from_sector_id, to_sector_id, type_id,
                    length, width, hazard_milli, ward_level, gate_key_id, state, revision)
                VALUES ($w,$l,$f,$t,$ty,$le,$wi,$h,$wl,$g,$s,0);
                """;
            var pw = cmd.Parameters.Add("$w", SqliteType.Text); var pl = cmd.Parameters.Add("$l", SqliteType.Text);
            var pf = cmd.Parameters.Add("$f", SqliteType.Text); var pt = cmd.Parameters.Add("$t", SqliteType.Text);
            var pty = cmd.Parameters.Add("$ty", SqliteType.Text); var ple = cmd.Parameters.Add("$le", SqliteType.Integer);
            var pwi = cmd.Parameters.Add("$wi", SqliteType.Integer); var ph2 = cmd.Parameters.Add("$h", SqliteType.Integer);
            var pwl = cmd.Parameters.Add("$wl", SqliteType.Integer); var pg = cmd.Parameters.Add("$g", SqliteType.Text);
            var ps2 = cmd.Parameters.Add("$s", SqliteType.Text);
            cmd.Prepare();

            foreach (var l in world.Lanes)
            {
                pw.Value = world.WorldId; pl.Value = l.LaneId; pf.Value = l.FromSectorId; pt.Value = l.ToSectorId;
                pty.Value = "path"; ple.Value = 1000; pwi.Value = 1000; ph2.Value = 0; pwl.Value = 0;
                pg.Value = DBNull.Value; ps2.Value = "Open";
                cmd.ExecuteNonQuery();
            }
        }

        using (var entityCmd = db.CreateCommand())
        using (var memberCmd = db.CreateCommand())
        {
            entityCmd.Transaction = tx;
            entityCmd.CommandText = """
                INSERT INTO rpg_world_entities (world_id, entity_id, kind, owner_faction_id, at_sector_id,
                    on_lane_id, on_lane_toward_sector_id, lane_progress_milli, stance, movement_remaining,
                    routed, carried_loam, revision)
                VALUES ($w,$e,$k,$o,$at,$l,$tw,$lp,$st,$m,$r,$c,0);
                """;
            var ew = entityCmd.Parameters.Add("$w", SqliteType.Text); var ee = entityCmd.Parameters.Add("$e", SqliteType.Text);
            var ek = entityCmd.Parameters.Add("$k", SqliteType.Text); var eo = entityCmd.Parameters.Add("$o", SqliteType.Text);
            var eat = entityCmd.Parameters.Add("$at", SqliteType.Text); var el = entityCmd.Parameters.Add("$l", SqliteType.Text);
            var etw = entityCmd.Parameters.Add("$tw", SqliteType.Text); var elp = entityCmd.Parameters.Add("$lp", SqliteType.Integer);
            var est = entityCmd.Parameters.Add("$st", SqliteType.Text); var em = entityCmd.Parameters.Add("$m", SqliteType.Integer);
            var er = entityCmd.Parameters.Add("$r", SqliteType.Integer); var ec = entityCmd.Parameters.Add("$c", SqliteType.Integer);
            entityCmd.Prepare();

            memberCmd.Transaction = tx;
            memberCmd.CommandText = """
                INSERT INTO rpg_world_entity_members (world_id, entity_id, member_index, instance_id,
                    species_id, level, hp, wounds, role)
                VALUES ($w,$e,$i,$in,$sp,$lv,$hp,$wo,$ro);
                """;
            var mw = memberCmd.Parameters.Add("$w", SqliteType.Text); var me = memberCmd.Parameters.Add("$e", SqliteType.Text);
            var mi = memberCmd.Parameters.Add("$i", SqliteType.Integer); var min = memberCmd.Parameters.Add("$in", SqliteType.Text);
            var msp = memberCmd.Parameters.Add("$sp", SqliteType.Text); var mlv = memberCmd.Parameters.Add("$lv", SqliteType.Integer);
            var mhp = memberCmd.Parameters.Add("$hp", SqliteType.Integer); var mwo = memberCmd.Parameters.Add("$wo", SqliteType.Integer);
            var mro = memberCmd.Parameters.Add("$ro", SqliteType.Text);
            memberCmd.Prepare();

            foreach (var e in world.Entities)
            {
                ew.Value = world.WorldId; ee.Value = e.EntityId; ek.Value = "Legion"; eo.Value = "dave";
                eat.Value = e.AtSectorId; el.Value = DBNull.Value; etw.Value = DBNull.Value; elp.Value = 0;
                est.Value = "Hold"; em.Value = 1000; er.Value = 0; ec.Value = 0L;
                entityCmd.ExecuteNonQuery();

                for (var i = 0; i < e.Members; i++)
                {
                    mw.Value = world.WorldId; me.Value = e.EntityId; mi.Value = i; min.Value = DBNull.Value;
                    msp.Value = "wild-pack"; mlv.Value = 1; mhp.Value = 100; mwo.Value = 0; mro.Value = "Fighter";
                    memberCmd.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
    }

    // ---- isolated sub-costs ------------------------------------------------------------------------

    static void SerializeIntelOnly(SyntheticWorld world)
    {
        foreach (var intel in world.Intel)
        {
            _ = JsonSerializer.Serialize(intel.Slots);
            _ = JsonSerializer.Serialize(intel.Forces);
        }
    }

    /// <summary>Splits the fresh-per-row pattern's own cost into "build the command + bind params"
    /// vs. "execute it", over the slot rows alone (the single largest row count: 360 of them).</summary>
    static (double ConstructMs, double ExecuteMs) TimeConstructVsExecute(SqliteConnection db, SyntheticWorld world)
    {
        using var tx = db.BeginTransaction();
        var constructSw = new Stopwatch();
        var executeSw = new Stopwatch();

        foreach (var s in world.Sectors)
        foreach (var sl in s.Slots)
        {
            constructSw.Start();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO rpg_world_slots (world_id, sector_id, slot_index, slot_type_id, element,
                    state, owner_faction_id, guard_wave_id, guard_state, structure_id,
                    construction_turns_remaining, revision)
                VALUES ($w,$s,$i,$t,$e,$st,$o,$g,$gs,$str,$c,0);
                """;
            cmd.Parameters.AddWithValue("$w", world.WorldId);
            cmd.Parameters.AddWithValue("$s", s.SectorId);
            cmd.Parameters.AddWithValue("$i", sl);
            cmd.Parameters.AddWithValue("$t", "seat");
            cmd.Parameters.AddWithValue("$e", DBNull.Value);
            cmd.Parameters.AddWithValue("$st", "Open");
            cmd.Parameters.AddWithValue("$o", "dave");
            cmd.Parameters.AddWithValue("$g", DBNull.Value);
            cmd.Parameters.AddWithValue("$gs", "Cleared");
            cmd.Parameters.AddWithValue("$str", DBNull.Value);
            cmd.Parameters.AddWithValue("$c", DBNull.Value);
            constructSw.Stop();

            executeSw.Start();
            cmd.ExecuteNonQuery();
            executeSw.Stop();
        }

        // Sectors themselves must exist for a real commit, but are not part of what's being split
        // here; skip them, and skip committing since this connection is thrown away by ClearAll next.
        tx.Rollback();
        return (constructSw.Elapsed.TotalMilliseconds, executeSw.Elapsed.TotalMilliseconds);
    }

    static void ClearAll(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        foreach (var table in new[]
                 {
                     "rpg_world_faction_intel", "rpg_world_entity_members", "rpg_world_entities",
                     "rpg_world_lanes", "rpg_world_slots", "rpg_world_sectors", "rpg_world_factions"
                 })
        {
            using var del = db.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {table};";
            del.ExecuteNonQuery();
        }
        tx.Commit();
    }
}

/// <summary>A synthetic world at decision 19's scale (18 sectors x ~20 slots) -- shape only, no
/// gameplay meaning. Built directly rather than through <c>WorldTemplateCatalog</c> because every
/// shipped template is far smaller (first-light is 6 sectors) and this benchmark exists specifically
/// to answer "what happens once a world reaches base-defense's scale."</summary>
public sealed class SyntheticWorld
{
    public required string WorldId { get; init; }
    public required IReadOnlyList<string> Factions { get; init; }
    public required IReadOnlyList<SyntheticSector> Sectors { get; init; }
    public required IReadOnlyList<SyntheticLane> Lanes { get; init; }
    public required IReadOnlyList<SyntheticEntity> Entities { get; init; }
    public required IReadOnlyList<SyntheticIntel> Intel { get; init; }

    public static SyntheticWorld Build(
        int sectorCount, int slotsPerSector, int laneCount, int entityCount, int membersPerEntity, int factionCount)
    {
        var sectors = Enumerable.Range(0, sectorCount)
            .Select(i => new SyntheticSector("sector-" + i, Enumerable.Range(0, slotsPerSector).ToList()))
            .ToList();

        // A simple ring plus a couple of chords -- enough lanes to be realistic, topology is not
        // what this benchmark measures.
        var lanes = Enumerable.Range(0, laneCount)
            .Select(i => new SyntheticLane(
                "lane-" + i,
                sectors[i % sectorCount].SectorId,
                sectors[(i + 1) % sectorCount].SectorId))
            .ToList();

        var entities = Enumerable.Range(0, entityCount)
            .Select(i => new SyntheticEntity("entity-" + i, sectors[i % sectorCount].SectorId, membersPerEntity))
            .ToList();

        var factions = Enumerable.Range(0, factionCount).Select(i => "faction-" + i).ToList();

        // A representative remembered snapshot: a handful of slots and forces, the shape
        // rpg_world_faction_intel actually stores per (faction x sector) -- this is C5's suspect.
        var intel = factions.SelectMany(f => sectors.Select(s => new SyntheticIntel(
            f, s.SectorId,
            Enumerable.Range(0, Math.Min(5, s.Slots.Count)).Select(i => $"remembered-slot-{i}").ToList(),
            Enumerable.Range(0, 3).Select(i => $"remembered-force-{i}").ToList())))
            .ToList();

        return new SyntheticWorld
        {
            WorldId = "bench-world",
            Factions = factions,
            Sectors = sectors,
            Lanes = lanes,
            Entities = entities,
            Intel = intel
        };
    }
}

public sealed record SyntheticSector(string SectorId, IReadOnlyList<int> Slots);
public sealed record SyntheticLane(string LaneId, string FromSectorId, string ToSectorId);
public sealed record SyntheticEntity(string EntityId, string AtSectorId, int Members);
public sealed record SyntheticIntel(string FactionId, string SectorId, IReadOnlyList<string> Slots, IReadOnlyList<string> Forces);
