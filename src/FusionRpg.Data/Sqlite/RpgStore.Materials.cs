using System.Text;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Items.Materials;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>The outcome of one <see cref="RpgStore.TrySpendRecipe"/> call. <c>Reason</c> is empty on a
/// fresh success and <c>"replay"</c> on an idempotent retry — the shipped <c>TrySpendSouls</c>
/// spelling, reused rather than re-invented.</summary>
public sealed record MaterialSpendResult(bool Ok, string Reason, string OutcomeRef);

/// <summary>One <c>rpg_material_spend_log</c> row — what a given correlation already paid, and for what.</summary>
public sealed record MaterialSpendRecord(string RecipeId, string CostJson, string OutcomeRef, string CreatedUtc);

public sealed partial class RpgStore
{
    // ---- materials + recipes (module 14, salvage-craft) ---------------------------------------------

    /// <summary>
    /// ⚠ Ships against the SHIPPED table name <c>rpg_creature_materials</c>. The
    /// <c>rpg_creature_materials → rpg_materials</c> rename is RULED but deliberately NOT in this
    /// module's task list (`spec-salvage-craft.md` §"ask-first and not scheduled"); the SQL sites it
    /// touches are recorded in tasks/item-todo.md P4.1 for the day the owner says go.
    /// </summary>
    void EnsureMaterialSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            -- I9 §6.1–6.2 with the three changes verification forced (spec-salvage-craft.md
            -- §"Recipes as data"): `operation` is a TEN-verb vocabulary, `variant_from` speaks
            -- output-rung rather than output-band, and qty_curve_id stays restricted to
            -- input: rarity / input: tier (CurveInput has no actor to read for input: level).
            --
            -- SC7: adding a base type's forge recipe is one material_recipe row plus two or three
            -- material_recipe_cost rows and NO CODE. Adding an operation VERB is code.
            CREATE TABLE IF NOT EXISTS material_recipe (
              recipe_id       TEXT PRIMARY KEY,
              operation       TEXT NOT NULL,
              output_kind     TEXT NOT NULL,
              output_ref      TEXT,
              output_qty      INTEGER NOT NULL DEFAULT 1,
              frame           TEXT NOT NULL,
              souls_cost_band TEXT,
              variant_from    TEXT,
              qty_curve_id    TEXT
            );

            CREATE TABLE IF NOT EXISTS material_recipe_cost (
              recipe_id   TEXT NOT NULL,
              seq         INTEGER NOT NULL,
              material_id TEXT NOT NULL,
              cost_band   TEXT NOT NULL,
              PRIMARY KEY (recipe_id, seq),
              FOREIGN KEY (recipe_id) REFERENCES material_recipe(recipe_id) ON DELETE CASCADE
            );

            -- The replay net. UNIQUE(player_id, correlation_id) is the SECOND net under the gate's
            -- own check, exactly as item_drop_log is under the loot pipeline's: a race that slips
            -- past the read still cannot double-spend.
            CREATE TABLE IF NOT EXISTS rpg_material_spend_log (
              player_id      INTEGER NOT NULL,
              correlation_id TEXT NOT NULL,
              recipe_id      TEXT NOT NULL,
              cost_digest    TEXT NOT NULL,
              cost_json      TEXT NOT NULL,
              outcome_ref    TEXT NOT NULL,
              created_utc    TEXT NOT NULL,
              PRIMARY KEY (player_id, correlation_id)
            );

            CREATE INDEX IF NOT EXISTS ix_material_spend_log_recipe
              ON rpg_material_spend_log(recipe_id);
            """);
    }

    /// <summary>Import a resolved recipe catalog. Replaces every row for the ids it carries; never
    /// touches a recipe id it was not given, so an incremental corpus add does not delete history.</summary>
    public int ImportRecipeCatalog(MaterialRecipeCatalog catalog)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            var n = 0;
            foreach (var recipe in catalog.Recipes.Values.OrderBy(r => r.RecipeId, StringComparer.Ordinal))
            {
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO material_recipe
                          (recipe_id, operation, output_kind, output_ref, output_qty, frame, souls_cost_band)
                        VALUES ($id, $op, $ok, $orf, $oq, $fr, $sb)
                        ON CONFLICT(recipe_id) DO UPDATE SET
                          operation = excluded.operation, output_kind = excluded.output_kind,
                          output_ref = excluded.output_ref, output_qty = excluded.output_qty,
                          frame = excluded.frame, souls_cost_band = excluded.souls_cost_band;
                        """;
                    cmd.Parameters.AddWithValue("$id", recipe.RecipeId);
                    cmd.Parameters.AddWithValue("$op", CraftOperations.Id(recipe.Operation));
                    cmd.Parameters.AddWithValue("$ok", recipe.OutputKind);
                    cmd.Parameters.AddWithValue("$orf", (object?)recipe.OutputRef ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$oq", recipe.OutputQty);
                    cmd.Parameters.AddWithValue("$fr", recipe.Frame);
                    cmd.Parameters.AddWithValue("$sb", (object?)recipe.SoulsCostBand ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                using (var del = db.CreateCommand())
                {
                    del.CommandText = "DELETE FROM material_recipe_cost WHERE recipe_id = $id;";
                    del.Parameters.AddWithValue("$id", recipe.RecipeId);
                    del.ExecuteNonQuery();
                }

                for (var i = 0; i < recipe.CostLines.Count; i++)
                {
                    using var line = db.CreateCommand();
                    line.CommandText = """
                        INSERT INTO material_recipe_cost (recipe_id, seq, material_id, cost_band)
                        VALUES ($id, $seq, $m, $b);
                        """;
                    line.Parameters.AddWithValue("$id", recipe.RecipeId);
                    line.Parameters.AddWithValue("$seq", i);
                    line.Parameters.AddWithValue("$m", recipe.CostLines[i].MaterialId);
                    line.Parameters.AddWithValue("$b", recipe.CostLines[i].CostBand);
                    line.ExecuteNonQuery();
                }

                n++;
            }

            tx.Commit();
            return n;
        }
    }

    public int CountRecipes()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM material_recipe;";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    /// <summary>Read a player's balance of one material id. Zero when the row does not exist.</summary>
    public long GetMaterialQty(long playerId, string materialId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT qty FROM rpg_creature_materials WHERE player_id = $p AND material_id = $m;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$m", materialId);
            return cmd.ExecuteScalar() is long q ? q : 0L;
        }
    }

    /// <summary>Grant materials (test + faucet seam). Throws on an id outside the closed vocabulary,
    /// at the write boundary, exactly like the spend path — a typo must never create a phantom row.</summary>
    public void GrantMaterials(long playerId, IReadOnlyList<(string MaterialId, long Qty)> grants)
    {
        var now = ServerClock.UtcNowDateTime.ToString("o");
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            GrantMaterialsUnlocked(db, playerId, grants, now);
            tx.Commit();
        }
    }

    /// <summary>
    /// The same grant on a connection the CALLER owns, so a mint (upcycle's output) or a salvage yield
    /// lands in the same transaction as everything else the operation did. Same write boundary: an id
    /// outside the closed vocabulary THROWS rather than creating a phantom row.
    /// </summary>
    internal void GrantMaterialsUnlocked(
        SqliteConnection db, long playerId, IReadOnlyList<(string MaterialId, long Qty)> grants, string nowUtc)
    {
        foreach (var (materialId, qty) in grants)
        {
            if (!MaterialCatalog.IsKnown(materialId) && !CreatureMaterialCatalog.IsKnown(materialId))
                throw new ArgumentException($"Unknown material id '{materialId}'.");
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rpg_creature_materials(player_id, material_id, qty, updated_utc)
                VALUES ($p, $m, $q, $t)
                ON CONFLICT(player_id, material_id) DO UPDATE SET qty = qty + $q, updated_utc = $t;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$m", materialId);
            cmd.Parameters.AddWithValue("$q", qty);
            cmd.Parameters.AddWithValue("$t", nowUtc);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The spend transaction — copied from shipped paths, not invented
    /// (`spec-salvage-craft.md` §"The spend transaction"):
    /// <list type="number">
    /// <item>replay check — <c>rpg_material_spend_log</c>, <c>UNIQUE(player_id, correlation_id)</c>;
    ///   a hit returns the stored outcome and spends nothing, and a hit whose arguments DIFFER is
    ///   refused with <c>correlation.mismatch</c> rather than silently replayed.</item>
    /// <item>gate — refusals write nothing, so a retried refusal re-evaluates.</item>
    /// <item>spend, in the FIXED class order souls → shard → substrate → essence → catalyst, so two
    ///   logs of one refusal are byte-comparable.</item>
    /// <item>perform — the owning module's mutation runs inside the SAME transaction, via
    ///   <paramref name="perform"/>; throwing from it rolls every leg back.</item>
    /// <item>log — one spend-log row with the resolved lines and the outcome ref.</item>
    /// </list>
    /// </summary>
    /// <param name="lines">Already resolved by <see cref="MaterialRecipeCatalog.Resolve"/> — Core owns
    /// pricing, the store owns atomicity. Must arrive in fixed class order; a caller that reorders
    /// them is refused at the boundary rather than trusted.</param>
    /// <param name="perform">Step 5. Runs after every leg is debited and before the log row is
    /// written, on the same open transaction (connection AND transaction — a mint that saves must
    /// join this transaction, not open its own). Returns the outcome ref recorded in the log.</param>
    public MaterialSpendResult TrySpendRecipe(
        long playerId,
        string recipeId,
        IReadOnlyList<MaterialCostLine> lines,
        string correlationId,
        Func<SqliteConnection, SqliteTransaction, string>? perform = null)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("correlationId required");

        for (var i = 1; i < lines.Count; i++)
        {
            if (MaterialCatalog.ClassRank(lines[i].Class) < MaterialCatalog.ClassRank(lines[i - 1].Class))
                throw new ArgumentException(
                    "cost lines must arrive in the fixed spend order souls -> shard -> substrate -> essence -> catalyst " +
                    "(spec-salvage-craft.md step 4) — two logs of one refusal have to be byte-comparable");
        }

        var corr = correlationId.Trim();
        var digest = CostDigest(recipeId, lines);
        var now = ServerClock.UtcNowDateTime.ToString("o");

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            // 1. replay check
            using (var check = db.CreateCommand())
            {
                check.CommandText =
                    "SELECT cost_digest, outcome_ref FROM rpg_material_spend_log WHERE player_id = $p AND correlation_id = $c;";
                check.Parameters.AddWithValue("$p", playerId);
                check.Parameters.AddWithValue("$c", corr);
                using var r = check.ExecuteReader();
                if (r.Read())
                {
                    var storedDigest = r.GetString(0);
                    var storedOutcome = r.GetString(1);
                    r.Close();
                    tx.Commit();
                    return storedDigest == digest
                        ? new MaterialSpendResult(true, "replay", storedOutcome)
                        : new MaterialSpendResult(false, "correlation.mismatch", "");
                }
            }

            // 2-4. spend, in the order the caller handed them, which the check above pinned.
            foreach (var line in lines)
            {
                if (line.Qty <= 0)
                {
                    tx.Rollback();
                    return new MaterialSpendResult(false, "cost.nonpositive", "");
                }

                if (line.Class == MaterialClass.Souls)
                {
                    var balance = ReadSoulBalanceUnlocked(db, playerId);
                    if (balance.Balance < line.Qty)
                    {
                        tx.Rollback();
                        return new MaterialSpendResult(false, "souls.insufficient", "");
                    }

                    if (!AppendSoulLedgerUnlocked(db, playerId, 0, -line.Qty, SoulReasonForRecipe(recipeId),
                            "spend", corr, corr, now))
                    {
                        // The shipped ExecuteFusion shape: a dedupe collision OUTSIDE this log means
                        // the correlation was reused by a different subsystem, which is a caller bug.
                        throw new InvalidOperationException(
                            "material spend dedupe collision — correlation reused outside the material spend log");
                    }

                    continue;
                }

                // The conditional decrement: a zero row count fails the WHOLE transaction, and an
                // unknown id THROWS at the write boundary rather than silently no-op'ing.
                if (!MaterialCatalog.IsKnown(line.MaterialId))
                    throw new ArgumentException($"Unknown material id '{line.MaterialId}'.");

                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    UPDATE rpg_creature_materials SET qty = qty - $q, updated_utc = $t
                    WHERE player_id = $p AND material_id = $m AND qty >= $q;
                    """;
                cmd.Parameters.AddWithValue("$q", line.Qty);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$m", line.MaterialId);
                if (cmd.ExecuteNonQuery() == 0)
                {
                    tx.Rollback();
                    return new MaterialSpendResult(false, "materials.insufficient", "");
                }
            }

            // 5. perform — the owning module's mutation or mint, in the SAME transaction.
            var outcomeRef = perform is null ? "" : perform(db, tx);

            // 6. log
            using (var log = db.CreateCommand())
            {
                log.CommandText = """
                    INSERT INTO rpg_material_spend_log
                      (player_id, correlation_id, recipe_id, cost_digest, cost_json, outcome_ref, created_utc)
                    VALUES ($p, $c, $r, $d, $j, $o, $t);
                    """;
                log.Parameters.AddWithValue("$p", playerId);
                log.Parameters.AddWithValue("$c", corr);
                log.Parameters.AddWithValue("$r", recipeId);
                log.Parameters.AddWithValue("$d", digest);
                log.Parameters.AddWithValue("$j", CostJson(lines));
                log.Parameters.AddWithValue("$o", outcomeRef);
                log.Parameters.AddWithValue("$t", now);
                log.ExecuteNonQuery();
            }

            tx.Commit();
            return new MaterialSpendResult(true, "", outcomeRef);
        }
    }

    /// <summary>
    /// ⭐ The recorded spend for one correlation, or <c>null</c>. <b>A caller that re-derives its cost
    /// from the item's CURRENT state must read this first</b>, because a successful operation moves the
    /// state the price is derived from: re-pricing a retried enhance against the new <c>+n</c> resolves
    /// a different cost, and <see cref="TrySpendRecipe"/> would then correctly refuse it as
    /// <c>correlation.mismatch</c> rather than replaying it. The mismatch rule is right — the fix is to
    /// short-circuit before re-pricing, which is what this read is for (D2 §9 clause 8: a replayed
    /// correlation returns the RECORDED result).
    /// </summary>
    public MaterialSpendRecord? FindMaterialSpend(long playerId, string correlationId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT recipe_id, cost_json, outcome_ref, created_utc
                FROM rpg_material_spend_log WHERE player_id = $p AND correlation_id = $c;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$c", correlationId.Trim());
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new MaterialSpendRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
        }
    }

    public int CountMaterialSpendLog(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rpg_material_spend_log WHERE player_id = $p;";
            cmd.Parameters.AddWithValue("$p", playerId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    /// <summary>
    /// Seed the `salvage_yield` budget key for all ten rungs, from module 14's own tuning. Separate
    /// from <c>SeedRarityLadder</c> on purpose: module 7 seeds the five keys it owns and must not
    /// grow a dependency on a later module's tuning file to keep doing so.
    /// </summary>
    public void SeedSalvageYield(IReadOnlyDictionary<string, SalvageCoefficient> salvage)
    {
        foreach (var rarityId in FusionRpg.Core.Items.RarityLadder.RungIds)
        {
            if (!salvage.TryGetValue(rarityId, out var c))
                throw new InvalidOperationException($"seeding salvage_yield: materials tuning has no rung '{rarityId}'");
            SetRarityBudget(rarityId, "salvage_yield", (int)c.SubstrateBase);
        }
    }

    /// <summary>The soul-ledger reason a recipe spend records. Prefixed so a material spend is never
    /// mistaken for a fusion or a summon in the ledger.</summary>
    static string SoulReasonForRecipe(string recipeId) => "craft:" + recipeId;

    static string CostJson(IReadOnlyList<MaterialCostLine> lines)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"class\":\"").Append(lines[i].Class).Append("\",\"id\":\"")
              .Append(lines[i].MaterialId).Append("\",\"qty\":").Append(lines[i].Qty).Append('}');
        }

        return sb.Append(']').ToString();
    }

    /// <summary>
    /// A canonical digest of (recipe, resolved lines). The replay check compares THIS, not the
    /// recipe id alone — so a reused correlation whose arguments differ is refused
    /// (<c>correlation.mismatch</c>) instead of returning someone else's outcome, which is the
    /// contract <c>TrySpendSouls</c> keeps against a differing amount.
    /// </summary>
    static string CostDigest(string recipeId, IReadOnlyList<MaterialCostLine> lines)
    {
        var sb = new StringBuilder(recipeId).Append('|');
        foreach (var l in lines)
            sb.Append((int)l.Class).Append(':').Append(l.MaterialId).Append(':').Append(l.Qty).Append(';');
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
