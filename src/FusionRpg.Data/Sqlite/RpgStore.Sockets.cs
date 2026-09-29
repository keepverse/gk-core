using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// <c>item_socket</c> + the two combination-recipe tables — the socket layer's own state
/// (spec-sockets.md §5.2, module 16).
///
/// <para>⛔ <b><c>item_socket</c> IS the SSOT.</b> ssot-sockets.md §5.2 called it "a materialized view
/// of I6's operation log"; <a href="../../../docs/architecture/item/decision-d2-mutation-contract.md">D2
/// §6</a> refused that by name — <i>"`item_socket` is the SSOT for socket state. It is not a
/// materialized view of anything"</i> — and clause 13 exempts sockets from the reconstruction clauses
/// entirely. No read path here replays <c>effect_instance_op</c>; the <c>socket-*</c> ops are appended
/// for audit and idempotency only.</para>
///
/// <para><b>Nothing here writes the host's atom rows.</b> Socketing composes at the binding layer, so
/// <c>effect_instance_atom</c> and <c>InstanceRow.ContentFingerprint()</c> are untouched by every
/// method below — which is what leaves SC5's reproduction contract unstrained.</para>
///
/// <para>⛔ <b>No <c>position</c> column on the ingredient table (D41).</b> ssot-sockets.md §5.2's
/// ordered <c>(combo_id, position)</c> shape is superseded: a recipe is an unordered multiset, so the
/// key is <c>(combo_id, family_id)</c> and the row carries a quantity. A schema with a
/// position column would let a matcher read one.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureSocketSchemaUnlocked(SqliteConnection db)
    {
        // ⭐ strain-splice-host SSH7.8 (spec-tier-ladder §4, owner-approved 2026-09-21): the
        // ingredient table is RE-KEYED from (combo_id, family_id, min_tier) to (combo_id, family_id)
        // — the ladder owns a fill's floors now, so the column is dead weight and a second tier
        // source. H2: the re-key happens BEFORE any write to the table, which is why the drop is
        // here, ahead of the CREATE. `CREATE TABLE IF NOT EXISTS` cannot drop a column, so an
        // install whose table still carries `min_tier` is dropped WHOLE and rebuilt; the content is
        // derived, and CombinationBoot re-seeds every authored recipe plus the generated resonances
        // on this same boot, so nothing authored is lost. A fresh or already-migrated database has
        // no `min_tier` and skips this entirely (idempotent).
        if (HasColumn(db, "socket_combo_ingredient", "min_tier"))
            Exec(db, "DROP TABLE socket_combo_ingredient;");

        Exec(db, """
            -- The sockets on one item instance and what fills them. Consumer: the equip
            -- projection (reads it to build bindings — ApplyEquipProjection reconciles insert
            -- bindings from these rows through EquipProjector, species-gear-chain T21) and the
            -- socket UI.
            CREATE TABLE IF NOT EXISTS item_socket (
              instance_id         TEXT    NOT NULL,
              socket_index        INTEGER NOT NULL,   -- 0-based, < socketsNow
              affinity            TEXT    NOT NULL DEFAULT '',  -- one concrete element id, or '' for none
              -- 1 when socket-add opened it. D24 lets ONLY a crafted, empty socket be imbued, so this
              -- is load-bearing state rather than provenance trivia.
              crafted             INTEGER NOT NULL DEFAULT 0,
              insert_container_id TEXT,               -- the gem.* container filling it; NULL = empty
              insert_instance_id  TEXT,               -- the insert's OWN instance, bound beside the host
              PRIMARY KEY (instance_id, socket_index),
              FOREIGN KEY (instance_id) REFERENCES effect_instance(instance_id) ON DELETE CASCADE
            );

            -- One row per combination. 25 generated resonances (ResonanceGenerator) plus module 21's
            -- 102 Strains and Splices. D27: combo_id is the container_id and carries the `combo.`
            -- prefix, because definitions.md §1 forces the prefix to match the kind.
            CREATE TABLE IF NOT EXISTS socket_combo_recipe (
              combo_id     TEXT PRIMARY KEY,
              shape        TEXT    NOT NULL,   -- strain | splice | pure | ring | eclipse | diversity
              element      TEXT    NOT NULL DEFAULT '',
              threshold    INTEGER NOT NULL DEFAULT 0,
              host_role    TEXT    NOT NULL DEFAULT '',
              host_frame   TEXT    NOT NULL DEFAULT '',
              min_sockets  INTEGER NOT NULL DEFAULT 0,
              base_tier    INTEGER NOT NULL DEFAULT 0,
              -- SSH7.8: rung 1's floors, as a comma-joined int list, snapshotted from the tuning at
              -- import. `ComboIngredient` has no tier member any more, so the no-ladder matcher
              -- path (`CombinationDistance`) reads them from the RECIPE — and an install that
              -- round-trips through this table must get the same recipe the importer built.
              base_floors  TEXT,
              enabled      INTEGER NOT NULL DEFAULT 1,
              revision     INTEGER NOT NULL DEFAULT 1
            );

            -- D41: a MULTISET, not a sequence. No position column, by design. No TIER column either
            -- (SSH7.8): the rung ladder owns a fill's floors, so the key is (combo_id, family_id).
            CREATE TABLE IF NOT EXISTS socket_combo_ingredient (
              combo_id  TEXT    NOT NULL,
              family_id TEXT    NOT NULL,
              qty       INTEGER NOT NULL DEFAULT 1,
              PRIMARY KEY (combo_id, family_id),
              FOREIGN KEY (combo_id) REFERENCES socket_combo_recipe(combo_id) ON DELETE CASCADE
            );
            """);
        // Additive migration (H2): an install whose recipe table predates the floors column gains it
        // here, before any write, exactly as the re-key above precedes its own. Idempotent.
        EnsureColumn(db, "socket_combo_recipe", "base_floors", "TEXT");
    }

    /// <summary>Every socket on one item, ordered by index.</summary>
    public IReadOnlyList<SocketSlot> GetSockets(string instanceId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT socket_index, affinity, crafted, insert_container_id, insert_instance_id
                FROM item_socket WHERE instance_id = $id ORDER BY socket_index;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);

            var rows = new List<SocketSlot>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new SocketSlot(
                    r.GetInt32(0), r.GetString(1), r.GetInt32(2) != 0,
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            return rows;
        }
    }

    /// <summary>
    /// Replace one item's socket rows wholesale, in one transaction. Wholesale rather than
    /// per-socket because the Core operations already return the WHOLE next state: writing a diff
    /// would put a second, weaker copy of the transition rules in the DAL.
    /// </summary>
    public void SetSockets(string instanceId, IReadOnlyList<SocketSlot> sockets)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            SetSocketsUnlocked(db, instanceId, sockets);
            tx.Commit();
        }
    }

    /// <summary>
    /// The same wholesale replace on a connection the CALLER owns, so a socket write and the module-14
    /// material debit that paid for it commit together. See <see cref="AppendMutationOpUnlocked"/> for
    /// why every command comes from <c>db.CreateCommand()</c>.
    /// </summary>
    internal void SetSocketsUnlocked(SqliteConnection db, string instanceId, IReadOnlyList<SocketSlot> sockets)
    {
        if (sockets is null) throw new ArgumentNullException(nameof(sockets));

        for (var i = 0; i < sockets.Count; i++)
            if (sockets[i].Index != i)
                throw new ArgumentException(
                    $"socket rows must be dense and 0-based: index {sockets[i].Index} sits at position {i}. " +
                    "A gap would make socketsNow disagree with the rows that exist.", nameof(sockets));

        using (var del = db.CreateCommand())
        {
            del.CommandText = "DELETE FROM item_socket WHERE instance_id = $id;";
            del.Parameters.AddWithValue("$id", instanceId);
            del.ExecuteNonQuery();
        }

        foreach (var slot in sockets)
        {
            using var ins = db.CreateCommand();
            ins.CommandText = """
                INSERT INTO item_socket
                  (instance_id, socket_index, affinity, crafted, insert_container_id, insert_instance_id)
                VALUES ($id, $ix, $aff, $crafted, $gem, $gemInstance);
                """;
            ins.Parameters.AddWithValue("$id", instanceId);
            ins.Parameters.AddWithValue("$ix", slot.Index);
            ins.Parameters.AddWithValue("$aff", slot.Affinity ?? "");
            ins.Parameters.AddWithValue("$crafted", slot.Crafted ? 1 : 0);
            ins.Parameters.AddWithValue("$gem", (object?)slot.InsertContainerId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$gemInstance", (object?)slot.InsertInstanceId ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Seed the combination catalog. Idempotent: safe on every boot, and it never deletes a row it
    /// did not write, so module 21's 102 and this module's 25 coexist in one table.
    /// </summary>
    public void SeedComboRecipes(IReadOnlyList<ComboRecipe> recipes)
    {
        if (recipes is null) throw new ArgumentNullException(nameof(recipes));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            foreach (var recipe in recipes)
            {
                using (var cmd = db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO socket_combo_recipe
                          (combo_id, shape, element, threshold, host_role, host_frame, min_sockets, base_tier, base_floors, enabled, revision)
                        VALUES ($id, $shape, $element, $threshold, $role, $frame, $minSockets, $baseTier, $floors, 1, 1)
                        ON CONFLICT(combo_id) DO UPDATE SET
                          shape = excluded.shape, element = excluded.element, threshold = excluded.threshold,
                          host_role = excluded.host_role, host_frame = excluded.host_frame,
                          min_sockets = excluded.min_sockets, base_tier = excluded.base_tier,
                          base_floors = excluded.base_floors,
                          -- A row the corpus authors again comes back ON: DisableCombinationsNotIn is
                          -- the only writer of enabled = 0, and without this line a row retired one
                          -- boot (a cell re-authored later) would stay dark forever.
                          enabled = 1,
                          revision = socket_combo_recipe.revision + 1;
                        """;
                    cmd.Parameters.AddWithValue("$id", recipe.ComboId);
                    cmd.Parameters.AddWithValue("$shape", ComboShapes.Id(recipe.Shape));
                    cmd.Parameters.AddWithValue("$element", recipe.Element ?? "");
                    cmd.Parameters.AddWithValue("$threshold", recipe.Threshold);
                    cmd.Parameters.AddWithValue("$role", recipe.HostRole ?? "");
                    cmd.Parameters.AddWithValue("$frame", recipe.HostFrame ?? "");
                    cmd.Parameters.AddWithValue("$minSockets", recipe.MinSockets);
                    cmd.Parameters.AddWithValue("$baseTier", recipe.BaseTier);
                    cmd.Parameters.AddWithValue("$floors",
                        recipe.BaseFloors is { Count: > 0 } floors
                            ? string.Join(",", floors)
                            : (object)DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                using (var del = db.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM socket_combo_ingredient WHERE combo_id = $id;";
                    del.Parameters.AddWithValue("$id", recipe.ComboId);
                    del.ExecuteNonQuery();
                }

                foreach (var ingredient in recipe.Ingredients)
                {
                    using var ins = db.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO socket_combo_ingredient (combo_id, family_id, qty)
                        VALUES ($id, $family, $qty)
                        ON CONFLICT(combo_id, family_id) DO UPDATE SET qty = excluded.qty;
                        """;
                    ins.Parameters.AddWithValue("$id", recipe.ComboId);
                    ins.Parameters.AddWithValue("$family", ingredient.FamilyId);
                    ins.Parameters.AddWithValue("$qty", ingredient.Quantity);
                    ins.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Retire authored combination rows the corpus no longer carries, WITHOUT deleting them
    /// (spec-recipe-import §3). <see cref="SeedComboRecipes"/> upserts and never deletes, so a Strain
    /// removed from the corpus would keep firing from last boot's row; this owns the two authored id
    /// prefixes <see cref="StrainSpliceGrid"/> mints and disables any enabled row in that space that
    /// is not in <paramref name="acceptedIds"/>.
    ///
    /// <para>⛔ <b>Resonance rows are never touched.</b> <c>combo.pure-*</c> / <c>combo.ring-*</c> /
    /// <c>combo.eclipse-*</c> / <c>combo.diversity-*</c> are a different space, authored by
    /// <see cref="ResonanceGenerator"/>; this import has no standing to retire one.</para>
    ///
    /// <para>⚠ <b>A row is disabled, never deleted.</b> Live instances, bindings and the audit trail
    /// may reference it, and <see cref="GetComboRecipes"/> already filters on <c>enabled</c> — so
    /// deletion would be a destructive answer to a visibility problem.</para>
    /// </summary>
    /// <returns>The number of rows newly disabled (already-disabled rows are left alone).</returns>
    public int DisableCombinationsNotIn(IReadOnlySet<string> acceptedIds)
    {
        if (acceptedIds is null) throw new ArgumentNullException(nameof(acceptedIds));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            var stale = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT combo_id FROM socket_combo_recipe WHERE enabled = 1;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    if (IsAuthoredCombinationId(id) && !acceptedIds.Contains(id)) stale.Add(id);
                }
            }

            foreach (var id in stale)
            {
                using var upd = db.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE socket_combo_recipe
                    SET enabled = 0, revision = revision + 1
                    WHERE combo_id = $id;
                    """;
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
            }

            tx.Commit();
            return stale.Count;
        }
    }

    /// <summary>
    /// The AUTHORED combination id space — the two prefixes <see cref="StrainSpliceGrid"/> mints,
    /// asked once here rather than spelled as string literals at the call site so a third authored
    /// shape cannot leave this import silently ignoring its rows.
    /// </summary>
    static bool IsAuthoredCombinationId(string comboId) =>
        comboId.StartsWith(StrainSpliceGrid.StrainPrefix, StringComparison.Ordinal) ||
        comboId.StartsWith(StrainSpliceGrid.SplicePrefix, StringComparison.Ordinal);

    /// <summary>Rung 1's floors as stored on the recipe row (SSH7.8): a comma-joined int list. A null
    /// or empty cell is no floors, which the matcher refuses by name — never a substituted default.</summary>
    static IReadOnlyList<int>? ParseFloors(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var parts = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var floors = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var floor))
                throw new InvalidOperationException(
                    $"socket_combo_recipe.base_floors '{stored}' carries '{part}', which is not an integer — " +
                    "a corrupt floor list is never silently skipped");
            floors.Add(floor);
        }
        return floors;
    }

    /// <summary>The enabled combination catalog, in the evaluator's own resolution order.</summary>
    public IReadOnlyList<ComboRecipe> GetComboRecipes()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();

            var ingredients = new Dictionary<string, List<ComboIngredient>>(StringComparer.Ordinal);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT combo_id, family_id, qty FROM socket_combo_ingredient;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    if (!ingredients.TryGetValue(id, out var list))
                        ingredients[id] = list = new List<ComboIngredient>();
                    list.Add(new ComboIngredient(r.GetString(1), r.GetInt32(2)));
                }
            }

            var rows = new List<ComboRecipe>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT combo_id, shape, element, threshold, host_role, host_frame, min_sockets, base_tier, base_floors
                    FROM socket_combo_recipe WHERE enabled = 1;
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    if (!ComboShapes.TryParse(r.GetString(1), out var shape))
                        throw new InvalidOperationException(
                            $"socket_combo_recipe '{id}' carries shape '{r.GetString(1)}', which is not one of the six — " +
                            "an unknown shape is never silently skipped, because a skipped combination is a bonus that " +
                            "vanishes with no symptom");

                    rows.Add(new ComboRecipe(
                        id, shape, r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5),
                        r.GetInt32(6), r.GetInt32(7),
                        ingredients.TryGetValue(id, out var list)
                            ? list.OrderBy(i => i.FamilyId, StringComparer.Ordinal).ToList()
                            : Array.Empty<ComboIngredient>(),
                        BaseFloors: ParseFloors(r.IsDBNull(8) ? null : r.GetString(8))));
                }
            }

            return rows
                .OrderBy(x => (int)x.Shape)
                .ThenBy(x => x.ComboId, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// Seed <c>socket_min</c> and <c>socket_max</c> — the eighth and ninth <c>rarity_budget</c> keys,
    /// whose shape <c>ssot-rarity.md</c> §5 recorded as "awaiting I4" until this module decided it.
    /// Deliberately its own method rather than folded into <see cref="SeedRarityLadder"/>, so module
    /// 7's seeding never grows a dependency on a later module's tuning file — the precedent module 14
    /// set with <c>SeedSalvageYield</c> and module 15 followed with <c>SeedRerollCostMult</c>.
    /// Idempotent: safe on every boot.
    /// </summary>
    public void SeedSocketGrants(SocketTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        foreach (var rung in RarityLadder.RungIds)
        {
            var window = tuning.RarityGrant[rung];
            SetRarityBudget(rung, "socket_min", window.Min);
            SetRarityBudget(rung, "socket_max", window.Max);
        }
    }
}
