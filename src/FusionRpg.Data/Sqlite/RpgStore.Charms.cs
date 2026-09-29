using FusionRpg.Core.Items.Thresholds;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One <c>charm_pouch</c> row — durable intent, and no binding.</summary>
public sealed record CharmPouchRow(string PlayerId, string InstanceId, string ContainerId, string AttunedUtc);

/// <summary>One <c>charm_run_hold</c> row. <c>Active</c> is what the partial unique index keys on.</summary>
public sealed record CharmRunHoldRow(
    string RunKind, long RunId, string PlayerId, string InstanceId, string ContainerId,
    string Axis, long ApCost, int Seq, bool Active);

/// <summary>
/// The outcome of one pouch write. <c>Reason</c> is the <see cref="CharmCarryRefusalReason"/> name on a
/// refusal and empty on success — the shipped <c>DraughtSpendResult</c> spelling, reused rather than
/// re-invented.
/// </summary>
public sealed record CharmPouchResult(bool Ok, string Reason, string Detail);

/// <summary>
/// ssot-charms.md §4.2's <b>five</b> tables — item module 22 <c>charm-carry</c>, split out of 12 by D40.
///
/// <para><b>Zero columns are added to any existing atom table.</b> §4.2's own reason: <c>axis</c>,
/// <c>ap_cost</c>, <c>unique_carry</c> and <c>frame_hint</c> are meaningful for exactly one
/// <c>container_kind</c>, and repeating <c>effect_container</c>'s <c>slot</c>/<c>rarity</c> precedent
/// for a fifth kind is how a shared table becomes a union of every kind's private fields. A side table
/// keyed on <c>container_id</c> costs one join and needs no E5 column ask.</para>
///
/// <para>⛔ <b><c>charm_def.container_id</c> carries no FK, and that is a wiring gap with a named owner.</b>
/// §4.3 wants the reference against <c>effect_container</c> with <c>container_kind = 'charm'</c>. The
/// kind does not exist: <c>ContainerKind</c> ships six values and D27 mints four more including this
/// one, so the blocker is <b>X7</b> — the same one modules 11, 12, 13, 16, 18 and 21 all carry. Adding a
/// live FK now would make the table unusable the moment anything wrote to it; adding it later is one
/// migration.</para>
///
/// <para>⭐ <b>The partial unique index IS the exclusivity rule</b> (§3.8), mirroring
/// <c>ix_rpg_expedition_members_active</c> exactly. <see cref="OpenCharmRunHold"/> does not check first
/// and then insert — it inserts, and lets the index refuse. A procedural check has a window between the
/// read and the write; an index does not.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureCharmSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            -- ssot-charms.md §4.2. Consumer: the pouch gate (budget, axis cap, uniqueness) and the
            -- pouch UI. A `charm.` container with NO row here is not attunable -- that is how the ten
            -- resonance containers stay out of the pouch, and it is a design device, not an omission.
            CREATE TABLE IF NOT EXISTS charm_def (
              container_id TEXT PRIMARY KEY,            -- charm.{axisGroup}-{seq}; no FK yet (X7)
              display_name TEXT NOT NULL,
              axis         TEXT NOT NULL,               -- offense|survivability|control|utility|economy
              charm_class  TEXT NOT NULL,               -- minor|standard|signet, AUTHORED (§3.4)
              ap_cost      INTEGER NOT NULL,            -- {1,2,3,5}, base-type property, NEVER rolled
              unique_carry INTEGER NOT NULL DEFAULT 0,  -- 1 => copy cap is 1, not the default 2
              frame_hint   TEXT NOT NULL DEFAULT 'any', -- any|humanoid|plant; UI filter, no mechanics
              enabled      INTEGER NOT NULL DEFAULT 1,
              revision     INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS ix_charm_def_axis ON charm_def(axis);

            -- ssot-charms.md §4.2. Consumer: the pouch gate and the run-start binder.
            --
            -- ATTUNEMENT IS A MARKING, NOT A CONTAINER (§2). The instance row lives wherever I13 puts
            -- every other item; this says WHICH of the player's charm instances are attuned. That cut
            -- is what stops I10 and I13 both claiming to store the same object.
            CREATE TABLE IF NOT EXISTS charm_pouch (
              player_id    TEXT NOT NULL,
              instance_id  TEXT NOT NULL,
              container_id TEXT NOT NULL,
              attuned_utc  TEXT NOT NULL,
              PRIMARY KEY (player_id, instance_id)
            );
            CREATE INDEX IF NOT EXISTS ix_charm_pouch_container ON charm_pouch(container_id);

            -- ssot-charms.md §4.2. Consumer: the run-start binder, the CharmInUse check, and replay.
            --
            -- `seq` is a DETERMINISM INPUT, not a display order: a run is sealed at dispatch by recorded
            -- seed, so the snapshot needs a stable row order to reproduce from.
            CREATE TABLE IF NOT EXISTS charm_run_hold (
              run_kind     TEXT    NOT NULL,            -- match | expedition | battle
              run_id       INTEGER NOT NULL,
              player_id    TEXT    NOT NULL,
              instance_id  TEXT    NOT NULL,
              container_id TEXT    NOT NULL,
              axis         TEXT    NOT NULL,
              ap_cost      INTEGER NOT NULL,
              seq          INTEGER NOT NULL,
              active       INTEGER NOT NULL DEFAULT 1,
              PRIMARY KEY (run_kind, run_id, instance_id)
            );

            -- ⭐ THE RULE IS THE INDEX. One live hold per charm instance across every run, mirroring
            -- ix_rpg_expedition_members_active. Cross-run exclusivity is what makes the AP budget a real
            -- cost when expeditions run in parallel (§3.2) -- and a procedural check would have a window
            -- between its read and its write that this does not.
            CREATE UNIQUE INDEX IF NOT EXISTS ix_charm_run_hold_active
              ON charm_run_hold(instance_id) WHERE active = 1;
            CREATE INDEX IF NOT EXISTS ix_charm_run_hold_player ON charm_run_hold(player_id, active);

            -- ssot-charms.md §4.2. Consumer: the run-start binder -- count the snapshot by axis, bind
            -- every satisfied tier. THIS IS A BREAKPOINT TABLE, so it is module 12's evaluator input
            -- verbatim; the rows deliberately point at ordinary `charm.` containers that carry no
            -- charm_def row.
            CREATE TABLE IF NOT EXISTS charm_resonance (
              axis         TEXT    NOT NULL,
              count_req    INTEGER NOT NULL,
              container_id TEXT    NOT NULL,
              authored_id  TEXT    NOT NULL,            -- the corpus spelling; see below
              PRIMARY KEY (axis, count_req)
            );

            -- ssot-charms.md §4.2. Consumer: the pouch gate. Growth is written by progression (§8/11).
            --
            -- ⛔ NO CEILING COLUMN, AND NO CHECK CONSTRAINT ON capacity. AGENTS.md forbids a hard
            -- progression ceiling: data/tuning/charm-attunement.v1.json's capacityLadder is the last
            -- AUTHORED rung, not a maximum, and nothing in SQL or in Core refuses a capacity above it.
            CREATE TABLE IF NOT EXISTS charm_attunement (
              player_id   TEXT PRIMARY KEY,
              capacity    INTEGER NOT NULL,
              updated_utc TEXT NOT NULL
            );
            """);
    }

    // ---- charm_def / charm_resonance (the authored catalog) ------------------------------------------

    /// <summary>
    /// Replace the whole charm catalog in one transaction — the corpus is authored, not edited live.
    /// Mirrors <see cref="ImportSetCorpus"/> exactly, including replace-not-accumulate.
    ///
    /// <para>⚠ <b>The resonance rows keep BOTH spellings.</b> <c>container_id</c> is module 12's
    /// canonical zero-padded id and <c>authored_id</c> is what the corpus actually ships
    /// (<c>charm.res-offense-2</c>). Module 12 measured that divergence rather than normalising it away
    /// — the rename is four moving parts, one of them a frozen registry — so this table carries the
    /// fact instead of picking a winner.</para>
    /// </summary>
    public void ImportCharmCorpus(
        IReadOnlyList<CharmDef> defs, IReadOnlyList<CharmResonanceRow> resonance)
    {
        if (defs is null) throw new ArgumentNullException(nameof(defs));
        if (resonance is null) throw new ArgumentNullException(nameof(resonance));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            LootExec(db, tx, "DELETE FROM charm_resonance;");
            LootExec(db, tx, "DELETE FROM charm_def;");

            foreach (var d in defs)
                LootExec(db, tx, """
                    INSERT INTO charm_def
                      (container_id, display_name, axis, charm_class, ap_cost, unique_carry, frame_hint,
                       enabled, revision)
                    VALUES ($id, $name, $axis, $cls, $ap, $uc, $frame, 1, 1);
                    """,
                    ("$id", d.ContainerId), ("$name", d.DisplayName), ("$axis", d.Axis),
                    ("$cls", d.Class.ToString().ToLowerInvariant()), ("$ap", d.ApCost),
                    ("$uc", d.UniqueCarry ? 1 : 0), ("$frame", "any"));

            foreach (var r in resonance)
                LootExec(db, tx, """
                    INSERT INTO charm_resonance (axis, count_req, container_id, authored_id)
                    VALUES ($axis, $count, $cid, $authored);
                    """,
                    ("$axis", r.Axis), ("$count", r.CountRequired),
                    ("$cid", r.ContainerId), ("$authored", r.AuthoredContainerId));

            tx.Commit();
        }
    }

    /// <summary>Every attunable charm — §4.2's "the def table IS the attunable list".</summary>
    public IReadOnlyList<CharmDef> ListCharmDefs()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT container_id, display_name, axis, charm_class, ap_cost, unique_carry
                FROM charm_def WHERE enabled = 1 ORDER BY container_id;
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<CharmDef>();
            while (r.Read())
            {
                var cls = r.GetString(3) switch
                {
                    "signet" => CharmClass.Signet,
                    "standard" => CharmClass.Standard,
                    _ => CharmClass.Minor,
                };
                // PrefixRolls / SuffixRolls / HasNegativeAtom are the CORPUS's facts, not the pouch's:
                // module 12 validates the class rules at parse time and this table exists for the gate,
                // which reads axis, ap_cost and unique_carry only. Reporting 0/0/false here would be a
                // second, weaker source for a rule module 12 already enforces -- so the round trip is
                // deliberately partial and a test says so rather than pretending otherwise.
                list.Add(new CharmDef(r.GetString(0), r.GetString(1), r.GetString(2), cls,
                    r.GetInt32(4), r.GetInt32(5) != 0, 0, 0, cls == CharmClass.Signet));
            }
            return list;
        }
    }

    /// <summary>The resonance breakpoint table, in module 12's own row shape.</summary>
    public IReadOnlyList<CharmResonanceRow> ListCharmResonance()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT axis, count_req, container_id, authored_id FROM charm_resonance ORDER BY axis, count_req;";
            using var r = cmd.ExecuteReader();
            var list = new List<CharmResonanceRow>();
            while (r.Read())
                list.Add(new CharmResonanceRow(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    // ---- charm_attunement (capacity) -----------------------------------------------------------------

    /// <summary>
    /// The player's capacity, or <c>null</c> when progression has never written one — the caller then
    /// uses the tuning's first rung. ⛔ Never defaulted to a number here: a store that invents a
    /// capacity is a balance decision made in the DAL.
    /// </summary>
    public long? GetCharmCapacity(string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT capacity FROM charm_attunement WHERE player_id = $p;";
            cmd.Parameters.AddWithValue("$p", playerId);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? null : Convert.ToInt64(v);
        }
    }

    /// <summary>
    /// Write the player's capacity. <b>No upper bound is enforced</b> — the ladder in
    /// `charm-attunement.v1.json` is the last authored rung, and AGENTS.md forbids turning it into a
    /// ceiling. A negative capacity throws (it is <c>BadParamValue</c> by §5.1, and a clamp to zero
    /// would silently empty a pouch).
    /// </summary>
    public void SetCharmCapacity(string playerId, long capacity, string? utc = null)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity),
                $"capacity {capacity} is negative; §5.1 maps that to BadParamValue and a clamp would " +
                "silently empty the player's pouch");

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            ExecIn(db, tx, """
                INSERT INTO charm_attunement (player_id, capacity, updated_utc)
                VALUES ($p, $c, $u)
                ON CONFLICT(player_id) DO UPDATE SET capacity = excluded.capacity,
                                                     updated_utc = excluded.updated_utc;
                """,
                ("$p", playerId), ("$c", capacity),
                ("$u", utc ?? ServerClock.UtcNowDateTime.ToString("O")));
            tx.Commit();
        }
    }

    // ---- charm_pouch (durable intent) -----------------------------------------------------------------

    /// <summary>
    /// Attune one instance. <b>No binding is created</b> (§3.8) — attunement is durable intent, and the
    /// only moment the bonus reaches an actor is run start.
    ///
    /// <para>Refuses <c>CharmNotCarryable</c> when the container has no <c>charm_def</c> row, and
    /// <c>CharmInUse</c> when a live run already holds the instance. It does NOT re-run the budget or
    /// axis gate — that is <see cref="CharmPouchGate"/>'s, over the whole pouch, and the caller runs it
    /// with the tuning it loaded. Splitting them keeps the balance numbers out of SQL.</para>
    /// </summary>
    public CharmPouchResult Attune(string playerId, string instanceId, string containerId, string? utc = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            using (var check = db.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT COUNT(*) FROM charm_def WHERE container_id = $c AND enabled = 1;";
                check.Parameters.AddWithValue("$c", containerId);
                if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                {
                    tx.Rollback();
                    return new CharmPouchResult(false, nameof(CharmCarryRefusalReason.CharmNotCarryable),
                        $"'{containerId}' has no charm_def row — §4.2 keeps resonance containers, and " +
                        "anything else that is not a carryable charm, out of the pouch that way");
                }
            }

            using (var held = db.CreateCommand())
            {
                held.Transaction = tx;
                held.CommandText =
                    "SELECT run_kind, run_id FROM charm_run_hold WHERE instance_id = $i AND active = 1;";
                held.Parameters.AddWithValue("$i", instanceId);
                using var r = held.ExecuteReader();
                if (r.Read())
                {
                    var label = $"{r.GetString(0)}#{r.GetInt64(1)}";
                    r.Close();
                    tx.Rollback();
                    return new CharmPouchResult(false, nameof(CharmCarryRefusalReason.CharmInUse),
                        $"a live run ({label}) holds instance '{instanceId}'");
                }
            }

            ExecIn(db, tx, """
                INSERT INTO charm_pouch (player_id, instance_id, container_id, attuned_utc)
                VALUES ($p, $i, $c, $u)
                ON CONFLICT(player_id, instance_id) DO UPDATE SET container_id = excluded.container_id;
                """,
                ("$p", playerId), ("$i", instanceId), ("$c", containerId),
                ("$u", utc ?? ServerClock.UtcNowDateTime.ToString("O")));

            tx.Commit();
            return new CharmPouchResult(true, "", "");
        }
    }

    /// <summary>
    /// Un-attune. Refuses <c>CharmInUse</c> while a live run holds the instance — <b>refuse, never
    /// silently hold</b> (§3.8): equipment holds a mid-run change because gearing is sticky, but a charm
    /// is a per-run dial and a silently held edit is a player believing they made a decision that did
    /// nothing.
    /// </summary>
    public CharmPouchResult Unattune(string playerId, string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            using (var held = db.CreateCommand())
            {
                held.Transaction = tx;
                held.CommandText =
                    "SELECT run_kind, run_id FROM charm_run_hold WHERE instance_id = $i AND active = 1;";
                held.Parameters.AddWithValue("$i", instanceId);
                using var r = held.ExecuteReader();
                if (r.Read())
                {
                    var label = $"{r.GetString(0)}#{r.GetInt64(1)}";
                    r.Close();
                    tx.Rollback();
                    return new CharmPouchResult(false, nameof(CharmCarryRefusalReason.CharmInUse),
                        $"a live run ({label}) holds instance '{instanceId}'; the pouch UI names it");
                }
            }

            ExecIn(db, tx, "DELETE FROM charm_pouch WHERE player_id = $p AND instance_id = $i;",
                ("$p", playerId), ("$i", instanceId));
            tx.Commit();
            return new CharmPouchResult(true, "", "");
        }
    }

    /// <summary>The player's attuned set, joined to its defs — the gate's own input shape.</summary>
    public IReadOnlyList<AttunedCharm> ListPouch(string playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT p.instance_id, p.container_id, d.axis, d.ap_cost, d.unique_carry
                FROM charm_pouch p
                LEFT JOIN charm_def d ON d.container_id = p.container_id
                WHERE p.player_id = $p
                ORDER BY p.instance_id ASC;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<AttunedCharm>();
            while (r.Read())
                list.Add(new AttunedCharm(
                    r.GetString(0), r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    !r.IsDBNull(4) && r.GetInt32(4) != 0));
            return list;
        }
    }

    /// <summary>Which of these instances a live run already holds → the run that holds it (§7.5).</summary>
    public IReadOnlyDictionary<string, string> HeldByLiveRun(IEnumerable<string> instanceIds)
    {
        var ids = instanceIds?.Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Count == 0) return map;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            foreach (var id in ids)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText =
                    "SELECT run_kind, run_id FROM charm_run_hold WHERE instance_id = $i AND active = 1;";
                cmd.Parameters.AddWithValue("$i", id);
                using var r = cmd.ExecuteReader();
                if (r.Read()) map[id] = $"{r.GetString(0)}#{r.GetInt64(1)}";
            }
            return map;
        }
    }

    // ---- charm_run_hold (the run-start snapshot) ------------------------------------------------------

    /// <summary>
    /// Seal one run's snapshot. <b>All or nothing</b>: if any instance is already held by a live run the
    /// whole transaction rolls back, so a run is never half sealed.
    ///
    /// <para>⭐ <b>It does not check first.</b> The <c>UNIQUE INDEX … WHERE active = 1</c> refuses the
    /// second hold, and the <see cref="SqliteException"/> is translated into <c>CharmInUse</c>. That is
    /// the point of the index: a read-then-write check has a window, and this does not.</para>
    ///
    /// <para>Idempotent on <c>(run_kind, run_id)</c>: a retry that finds rows already written returns
    /// <c>"replay"</c> and writes nothing, matching <c>TrySpendDraughts</c>. A run is sealed once.</para>
    /// </summary>
    public CharmPouchResult OpenCharmRunHold(
        string runKind, long runId, string playerId, IReadOnlyList<CharmHold> snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            using (var existing = db.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText =
                    "SELECT COUNT(*) FROM charm_run_hold WHERE run_kind = $k AND run_id = $r;";
                existing.Parameters.AddWithValue("$k", runKind);
                existing.Parameters.AddWithValue("$r", runId);
                if (Convert.ToInt64(existing.ExecuteScalar() ?? 0L) > 0)
                {
                    tx.Rollback();
                    return new CharmPouchResult(true, "replay", $"{runKind}#{runId} is already sealed");
                }
            }

            try
            {
                foreach (var h in snapshot)
                    ExecIn(db, tx, """
                        INSERT INTO charm_run_hold
                          (run_kind, run_id, player_id, instance_id, container_id, axis, ap_cost, seq, active)
                        VALUES ($k, $r, $p, $i, $c, $axis, $ap, $seq, 1);
                        """,
                        ("$k", runKind), ("$r", runId), ("$p", playerId), ("$i", h.InstanceId),
                        ("$c", h.ContainerId), ("$axis", h.Axis), ("$ap", h.ApCost), ("$seq", h.Seq));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                tx.Rollback();
                return new CharmPouchResult(false, nameof(CharmCarryRefusalReason.CharmInUse),
                    "another live run already holds one of these charms — the partial unique index " +
                    $"refused it, which is the rule itself rather than a check around it ({ex.Message})");
            }

            tx.Commit();
            return new CharmPouchResult(true, "", "");
        }
    }

    /// <summary>
    /// Run end: the holds go inactive and <b>stay for audit</b> (§3.8). Deleting them would take the
    /// replay input with them.
    /// </summary>
    public int CloseCharmRunHold(string runKind, long runId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE charm_run_hold SET active = 0 WHERE run_kind = $k AND run_id = $r AND active = 1;";
            cmd.Parameters.AddWithValue("$k", runKind);
            cmd.Parameters.AddWithValue("$r", runId);
            var n = cmd.ExecuteNonQuery();
            tx.Commit();
            return n;
        }
    }

    /// <summary>One run's snapshot, in its sealed order — active rows and audit rows alike.</summary>
    public IReadOnlyList<CharmRunHoldRow> ListCharmRunHold(string runKind, long runId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT run_kind, run_id, player_id, instance_id, container_id, axis, ap_cost, seq, active
                FROM charm_run_hold WHERE run_kind = $k AND run_id = $r ORDER BY seq ASC;
                """;
            cmd.Parameters.AddWithValue("$k", runKind);
            cmd.Parameters.AddWithValue("$r", runId);
            using var r2 = cmd.ExecuteReader();
            var list = new List<CharmRunHoldRow>();
            while (r2.Read())
                list.Add(new CharmRunHoldRow(r2.GetString(0), r2.GetInt64(1), r2.GetString(2),
                    r2.GetString(3), r2.GetString(4), r2.GetString(5), r2.GetInt64(6),
                    r2.GetInt32(7), r2.GetInt32(8) != 0));
            return list;
        }
    }
}
