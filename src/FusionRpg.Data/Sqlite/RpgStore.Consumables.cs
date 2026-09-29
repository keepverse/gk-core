using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Items.Consumables;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>One row of <c>rpg_run_draught</c> — what a run consumed, in the order it was sealed with.</summary>
public sealed record RunDraughtRow(string RunKind, long RunId, int Seq, string ContainerId, int Qty, string ConsumedUtc);

/// <summary>
/// The outcome of one <see cref="RpgStore.TrySpendDraughts"/> call. <c>Reason</c> is empty on a fresh
/// success and <c>"replay"</c> on an idempotent retry — the shipped <c>TrySpendSouls</c> /
/// <c>TrySpendRecipe</c> spelling, reused rather than re-invented.
/// </summary>
public sealed record DraughtSpendResult(bool Ok, string Reason, int RowsWritten);

/// <summary>
/// <c>consumable_def</c> and <c>rpg_run_draught</c> — ssot-consumables.md §5.2 and §5.3, item module 18.
///
/// <para>⛔ <b><c>consumable_def.container_id</c> carries no FK, and that is a wiring gap with a named
/// owner, not a design one.</b> §5.2 wants <c>FK → effect_container(container_id)</c> with
/// <c>container_kind = 'consumable'</c>. The kind does not exist: <c>ContainerKind</c> ships six values
/// and D27 mints four more, none of them this one (X7, the owner's, batched with D27). Adding a live FK
/// now would make the table unusable the moment anything wrote to it, and adding it later is one
/// migration. The reference is checked instead by <see cref="ConsumableValidator"/>, which refuses the
/// binding BY NAME with <c>ContentRuleViolated{consumable.container-kind-unavailable}</c>.</para>
///
/// <para>⛔ <b>No refund path exists, deliberately.</b> §5.3: draughts are SPENT at dispatch, in the
/// same transaction that decrements the stack, and recall pro-rates rewards while refunding nothing —
/// otherwise dispatch-and-instantly-recall is a free outcome preview (failure mode 7). There is no
/// <c>RefundDraughts</c> here and there must not be one; a test asserts the absence by reflection, so
/// the rule is a property of the store rather than of one call site.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureConsumableSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            -- ssot-consumables.md §5.2. Nine columns, 1:1 on an effect_container.
            --
            -- ⛔ No FK on container_id: the `consumable` container_kind does not exist yet (X7). The
            -- column is ready for the constraint the day the owner answers the fifth-kind question,
            -- and until then ConsumableValidator refuses the binding by name.
            --
            -- ⛔ NO SCALAR EFFECT COLUMN. There is no heal_amount, no duration_ms, no magnitude of any
            -- kind here, and that absence is the whole no-migration proof (§2.5): the effect is
            -- effect_container_atom rows from day one, so absorbing v1 into the action layer is "one
            -- UPDATE on two nullable columns and one INSERT" rather than a migration.
            CREATE TABLE IF NOT EXISTS consumable_def (
              container_id     TEXT PRIMARY KEY,        -- consumable.{slug}
              class_id         TEXT    NOT NULL,        -- restore|draught|ward|board|revive|utility
              use_context      TEXT    NOT NULL,        -- comma-joined: menu|dispatch|battle|lawn
              grade            INTEGER NOT NULL,        -- 1..5, and equal to every core atom's tier
              exclusion_group  TEXT    NOT NULL,        -- (family_id, variant) — the shipped group default
              manifest_cost    INTEGER NOT NULL DEFAULT 1,
              grants_action_id TEXT,                    -- the action-layer seam; NULL = menu/dispatch only
              cooldown_key     TEXT,                    -- reserved for rpg_action.cooldown_key; inert in v1
              enabled          INTEGER NOT NULL DEFAULT 1,
              revision         INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS ix_consumable_def_class ON consumable_def(class_id);
            CREATE INDEX IF NOT EXISTS ix_consumable_def_group ON consumable_def(exclusion_group);

            -- ssot-consumables.md §5.3. A DETERMINISM INPUT, NOT A LOG.
            --
            -- ExpeditionResolver is pure over (tier, squad, seed, elapsedTicks), so a draught — which
            -- changes the squad — must be part of the sealed input, and a sealed input needs a stable
            -- row order for the snapshot to be reproducible. `seq` gives that. Folding it into
            -- squad_json would hide a determinism input inside a blob.
            CREATE TABLE IF NOT EXISTS rpg_run_draught (
              run_kind     TEXT    NOT NULL,            -- expedition | battle
              run_id       INTEGER NOT NULL,
              seq          INTEGER NOT NULL,
              container_id TEXT    NOT NULL,
              qty          INTEGER NOT NULL,
              consumed_utc TEXT    NOT NULL,
              PRIMARY KEY (run_kind, run_id, seq)
            );

            CREATE INDEX IF NOT EXISTS ix_rpg_run_draught_container ON rpg_run_draught(container_id);
            """);
    }

    // ---- consumable_def -----------------------------------------------------------------------------

    public void UpsertConsumableDef(ConsumableDefRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            ExecIn(db, tx, """
                INSERT INTO consumable_def
                  (container_id, class_id, use_context, grade, exclusion_group, manifest_cost,
                   grants_action_id, cooldown_key, enabled, revision)
                VALUES ($id, $cls, $ctx, $grade, $grp, $cost, $action, $cd, $enabled, $rev)
                ON CONFLICT(container_id) DO UPDATE SET
                  class_id = excluded.class_id,
                  use_context = excluded.use_context,
                  grade = excluded.grade,
                  exclusion_group = excluded.exclusion_group,
                  manifest_cost = excluded.manifest_cost,
                  grants_action_id = excluded.grants_action_id,
                  cooldown_key = excluded.cooldown_key,
                  enabled = excluded.enabled,
                  revision = excluded.revision;
                """,
                ("$id", row.ContainerId),
                ("$cls", ConsumableClasses.Wire(row.ClassId)),
                ("$ctx", row.UseContextWire),
                ("$grade", row.Grade),
                ("$grp", row.ExclusionGroup),
                ("$cost", row.ManifestCost),
                ("$action", (object?)row.GrantsActionId ?? DBNull.Value),
                ("$cd", (object?)row.CooldownKey ?? DBNull.Value),
                ("$enabled", row.Enabled ? 1 : 0),
                ("$rev", row.Revision));
            tx.Commit();
        }
    }

    public ConsumableDefRow? GetConsumableDef(string containerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT container_id, class_id, use_context, grade, exclusion_group, manifest_cost,
                       grants_action_id, cooldown_key, enabled, revision
                FROM consumable_def WHERE container_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", containerId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadConsumableDef(r) : null;
        }
    }

    public IReadOnlyList<ConsumableDefRow> ListConsumableDefs()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT container_id, class_id, use_context, grade, exclusion_group, manifest_cost,
                       grants_action_id, cooldown_key, enabled, revision
                FROM consumable_def ORDER BY container_id;
                """;
            var list = new List<ConsumableDefRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadConsumableDef(r));
            return list;
        }
    }

    // ---- rpg_run_draught ----------------------------------------------------------------------------

    /// <summary>
    /// Spend a dispatch manifest: decrement every named stack and write its <c>rpg_run_draught</c> row,
    /// <b>in one transaction</b>.
    ///
    /// <para>Failure mode 7, closed at the write boundary rather than at the call site: a caller cannot
    /// peek at a decrement, see it fail, and keep the rows that succeeded. An insufficient stack rolls
    /// the whole thing back — no stock moves, no draught row survives, and nothing is half spent.</para>
    ///
    /// <para><b>Written before the seed resolves.</b> The caller seals the run from what this wrote;
    /// the rows are the determinism input, and <paramref name="seal"/> runs INSIDE the transaction so
    /// there is no window in which the stack is spent and the run is not sealed.</para>
    ///
    /// <para>Idempotent on <c>(run_kind, run_id)</c>: a retry that finds rows already written for the
    /// run returns <c>"replay"</c> and spends nothing. A run is sealed once.</para>
    /// </summary>
    public DraughtSpendResult TrySpendDraughts(
        string playerId,
        string runKind,
        long runId,
        IReadOnlyList<DraughtManifestEntry> entries,
        Action<SqliteConnection>? seal = null,
        string? utc = null)
    {
        if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("playerId required", nameof(playerId));
        if (string.IsNullOrWhiteSpace(runKind)) throw new ArgumentException("runKind required", nameof(runKind));
        entries ??= Array.Empty<DraughtManifestEntry>();

        var now = utc ?? ServerClock.UtcNowDateTime.ToString("O");

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            // 1. the replay gate. A sealed run's manifest is immutable; a second call is a retry.
            using (var check = db.CreateCommand())
            {
                check.CommandText =
                    "SELECT COUNT(*) FROM rpg_run_draught WHERE run_kind = $k AND run_id = $r;";
                check.Parameters.AddWithValue("$k", runKind);
                check.Parameters.AddWithValue("$r", runId);
                var existing = Convert.ToInt32(check.ExecuteScalar() ?? 0);
                if (existing > 0)
                {
                    tx.Commit();
                    return new DraughtSpendResult(true, "replay", existing);
                }
            }

            // 2. spend, in manifest order, which IS the seq order the seal reproduces from.
            var seq = 0;
            foreach (var entry in entries)
            {
                if (entry.Qty < 1)
                {
                    tx.Rollback();
                    return new DraughtSpendResult(false, "draught.nonpositive", 0);
                }

                if (!TryDecrementStockUnlocked(db, playerId, entry.ContainerId, entry.Qty, now))
                {
                    tx.Rollback();
                    return new DraughtSpendResult(false, "stock.insufficient", 0);
                }

                ExecIn(db, tx, """
                    INSERT INTO rpg_run_draught (run_kind, run_id, seq, container_id, qty, consumed_utc)
                    VALUES ($k, $r, $s, $c, $q, $t);
                    """,
                    ("$k", runKind), ("$r", runId), ("$s", seq), ("$c", entry.ContainerId),
                    ("$q", entry.Qty), ("$t", now));
                seq++;
            }

            // 3. seal — the caller's own run creation, in the SAME transaction, so the manifest can
            //    never be spent against a run that does not exist.
            seal?.Invoke(db);

            tx.Commit();
            return new DraughtSpendResult(true, "", seq);
        }
    }

    /// <summary>What a run consumed, in <c>seq</c> order — the sealed snapshot, and the audit view.</summary>
    public IReadOnlyList<RunDraughtRow> ListRunDraughts(string runKind, long runId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT run_kind, run_id, seq, container_id, qty, consumed_utc
                FROM rpg_run_draught WHERE run_kind = $k AND run_id = $r ORDER BY seq;
                """;
            cmd.Parameters.AddWithValue("$k", runKind);
            cmd.Parameters.AddWithValue("$r", runId);
            var list = new List<RunDraughtRow>();
            using var r2 = cmd.ExecuteReader();
            while (r2.Read())
                list.Add(new RunDraughtRow(
                    r2.GetString(0), r2.GetInt64(1), r2.GetInt32(2), r2.GetString(3),
                    r2.GetInt32(4), r2.GetString(5)));
            return list;
        }
    }

    // ---- the one stock decrement ---------------------------------------------------------------------

    /// <summary>
    /// ⛔ <b>The ONLY place a stock row is decremented, and every spend path goes through it.</b> The
    /// conditional <c>qty &gt;= $q</c> is what makes it safe: a stack that cannot cover the spend
    /// updates zero rows, and the caller fails its whole transaction rather than silently no-op'ing
    /// into a free item.
    ///
    /// <para>⛔ <b><see cref="AdjustStock"/> can never be a spend path</b>, and that is worth stating
    /// where someone would reach for it: its <c>MAX(0, qty + $d)</c> clamps, so
    /// <c>AdjustStock(player, id, -1)</c> on an empty stack <i>succeeds</i> and leaves 0. Clamping is
    /// right for a grant that must not go negative and catastrophic for a spend, which needs to know
    /// it failed.</para>
    ///
    /// <para>Must be called with <c>_gate</c> held and inside the caller's own transaction — it does
    /// not open either, so the decrement and whatever the caller writes beside it commit together.
    /// </para>
    /// </summary>
    static bool TryDecrementStockUnlocked(SqliteConnection db, string playerId, string containerId, long qty, string utc)
    {
        using var dec = db.CreateCommand();
        dec.CommandText = """
            UPDATE rpg_item_stock SET qty = qty - $q, updated_utc = $t
            WHERE player_id = $p AND container_id = $c AND qty >= $q;
            """;
        dec.Parameters.AddWithValue("$q", qty);
        dec.Parameters.AddWithValue("$t", utc);
        dec.Parameters.AddWithValue("$p", playerId);
        dec.Parameters.AddWithValue("$c", containerId);
        return dec.ExecuteNonQuery() != 0;
    }

    /// <summary>
    /// Spend an action's <c>holdsStock</c> demands — <b>all of them or none</b>, in one transaction.
    ///
    /// <para>This is the commit half of ssot-consumables.md §9 item 5(b)'s answer: <c>A3</c> §8 and
    /// <c>A4</c> §3a (both revised 2026-08-27) settle that consuming the item is a PRECONDITION rather
    /// than a cost, and <c>LeafId.HoldsStock</c> shipped the check 2026-08-28 — but nothing took the
    /// stack, so a battle-context consumable action fired for free. The conditional decrement above
    /// doubles as the re-check, so there is no window between the gate reading a quantity and this
    /// taking it.</para>
    ///
    /// <para>Returns the FIRST shortfall in demand order (matching <c>CostLedger</c>'s own
    /// single-detail shape) and rolls everything back, so a two-demand action that can pay one of
    /// them spends neither.</para>
    /// </summary>
    public StockSpendResult TrySpendStock(
        string playerId, IReadOnlyList<StockDemand> demands, string? utc = null)
    {
        if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("playerId required", nameof(playerId));
        if (demands is null || demands.Count == 0) return StockSpendResult.Spent;

        var now = utc ?? ServerClock.UtcNowDateTime.ToString("O");

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            foreach (var demand in demands)
            {
                // A non-positive demand is a caller bug, not a stingy stack: PredicateCompiler already
                // refuses minQty < 1 at load, so reaching here means someone hand-built a demand.
                if (demand.MinQty < 1)
                    throw new ArgumentOutOfRangeException(nameof(demands), demand.MinQty,
                        $"stock demand '{demand.StockId}' asks for {demand.MinQty}; a spend is at least one");

                if (!TryDecrementStockUnlocked(db, playerId, demand.StockId, demand.MinQty, now))
                {
                    tx.Rollback();
                    return StockSpendResult.Missing(demand.StockId);
                }
            }

            tx.Commit();
            return StockSpendResult.Spent;
        }
    }

    /// <summary>A player's current stock of one fungible container — the usability leaf's real answer
    /// once <c>LeafId.HoldsStock</c> reads a store instead of a caller-supplied quantity.
    ///
    /// <para>⚠ Returns <c>int</c> while the column is SQLite <c>INTEGER</c> (64-bit) and
    /// <see cref="StockDemand.MinQty"/> is <c>long</c>. Named rather than widened from here: the
    /// signature is module 2/18's and has callers in the Server, so widening it is that module's
    /// reviewed change. Nothing on the spend path narrows — <see cref="TrySpendStock"/> is
    /// <c>long</c> end to end.</para></summary>
    /// <summary>
    /// One container's held quantity, as <c>long</c>.
    ///
    /// <para>⚠ <b>Widened 2026-09-23 from <c>int</c>, which narrowed a 64-bit column.</b>
    /// <c>rpg_item_stock.qty</c> is SQLite <c>INTEGER</c> (64-bit) and <c>Convert.ToInt32</c> threw on
    /// anything above <c>int.MaxValue</c> — a checked narrowing, but still a narrowing on a magnitude,
    /// which AGENTS.md's numeric rule forbids ("integer magnitudes use <c>long</c> … choose a type whose
    /// range holds the value at reachable Theta"). The same layer already carries the <c>long</c> shape:
    /// <c>ReadStockQtyUnlocked</c> (`RpgStore.LegionCargo.cs:375`) is <c>long</c> end to end. The
    /// widening is safe here because this method had <b>no production caller</b> — only
    /// <c>ActionStockSpendStoreTests</c> and the cargo tests read it — and nothing on the SPEND path ever
    /// narrowed: <c>TrySpendStock</c> was already <c>long</c>.</para>
    /// </summary>
    public long StockQty(string playerId, string containerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT qty FROM rpg_item_stock WHERE player_id = $p AND container_id = $c;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$c", containerId);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
    }

    static ConsumableDefRow ReadConsumableDef(SqliteDataReader r)
    {
        var clsId = r.GetString(1);
        if (!ConsumableClasses.TryParse(clsId, out var cls))
            throw new InvalidOperationException($"consumable_def.class_id '{clsId}' is not one of the six");

        var contexts = new List<UseContext>();
        foreach (var token in r.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!UseContexts.TryParse(token.Trim(), out var u))
                throw new InvalidOperationException(
                    $"consumable_def.use_context '{token}' is not one of the closed four");
            if (!contexts.Contains(u)) contexts.Add(u);
        }

        return new ConsumableDefRow(
            r.GetString(0), cls, contexts, r.GetInt32(3), r.GetString(4), r.GetInt32(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.GetInt32(8) != 0,
            r.GetInt32(9));
    }
}
