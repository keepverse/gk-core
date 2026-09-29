using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Derived;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>Task C4's outcome — the corpus-level batched refusal R5 asks for (spec-tree-catalog.md
/// §4, §6): every offending id or content refusal named in ONE report, never a per-row throw inside a
/// reader loop (the shipped `AptitudeAllocation.Single:39` defect this whole discipline exists to not
/// repeat at catalog scale). `Revision` is the revision AFTER a successful import, or the unchanged
/// revision on refusal — a partial failure never advances it.</summary>
public sealed record TreeCatalogImportOutcome(bool Ok, IReadOnlyList<string> Refusals, int TreesImported, long Revision);

public sealed partial class RpgStore
{
    void EnsureTreeCatalogSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_tree_catalog_meta (
              id       INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
              revision INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO rpg_tree_catalog_meta(id, revision) VALUES (1, 0);

            CREATE TABLE IF NOT EXISTS rpg_tree_catalog_tree (
              tree_id           TEXT    NOT NULL PRIMARY KEY,
              category          TEXT    NOT NULL,
              gate_quantity     TEXT    NOT NULL,
              shape_archetype   TEXT    NOT NULL,
              tiers             INTEGER NOT NULL,
              branches          INTEGER NOT NULL,
              nodes_per_tier    TEXT    NOT NULL,
              catalog_version   INTEGER NOT NULL,
              enabled           INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS rpg_tree_catalog_node (
              node_id            TEXT    NOT NULL PRIMARY KEY,
              tree_id            TEXT    NOT NULL,
              branch             TEXT    NOT NULL,
              tier               INTEGER NOT NULL,
              node_key           TEXT    NOT NULL,
              prereq_node_ids    TEXT    NOT NULL,
              node_class         TEXT    NOT NULL,
              affix_ids          TEXT    NOT NULL,
              budget_share_milli INTEGER NOT NULL,
              exclude_props      TEXT    NOT NULL,
              exclusion_form     TEXT    NOT NULL,
              tags_json          TEXT    NULL,
              enabled            INTEGER NOT NULL,
              retired_at_revision INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tree_catalog_node_tree ON rpg_tree_catalog_node(tree_id);

            CREATE TABLE IF NOT EXISTS rpg_tree_catalog_atom (
              node_id     TEXT    NOT NULL,
              ordinal     INTEGER NOT NULL,
              kind_id     TEXT    NOT NULL,
              attach_point TEXT   NOT NULL,
              channel_id  TEXT    NOT NULL,
              op          TEXT    NOT NULL,
              trigger     TEXT    NULL,
              when_json   TEXT    NULL,
              k_micro     INTEGER NOT NULL,
              scale_axis  TEXT    NOT NULL,
              unit_class  TEXT    NOT NULL,
              soul_curve_id TEXT  NULL,
              PRIMARY KEY (node_id, ordinal)
            );

            -- Accumulates EVERY node id any catalog revision has ever had -- insert-only, never
            -- deleted, so a later revision that retires a node (removes it from the current corpus,
            -- C5) does not make an actor's existing allocation of that node look like it names an id
            -- "no catalog revision has ever had" (R5's exact phrase). A row here is a historical fact.
            CREATE TABLE IF NOT EXISTS rpg_tree_catalog_known_node_id (
              node_id TEXT NOT NULL PRIMARY KEY
            );
            """);
    }

    /// <summary>Current catalog revision -- 0 before any import has ever succeeded.</summary>
    public long GetTreeCatalogRevision()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadTreeCatalogRevisionUnlocked(db);
        }
    }

    long ReadTreeCatalogRevisionUnlocked(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT revision FROM rpg_tree_catalog_meta WHERE id = 1;";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// The boot-time importer (spec-tree-catalog.md §6, task C4): each element of
    /// <paramref name="treeJsonDocs"/> is one tree's committed JSON. Validates every tree through the
    /// ALREADY-CORRECT <see cref="PassiveTreeCatalogLoader"/> (never re-validates independently — the
    /// whole §6 refusal table is already implemented there, task B2/C3) and batches every refusal
    /// across the WHOLE corpus into one report before touching any row. All-or-nothing: a content
    /// refusal, or an existing allocation naming an id no catalog revision has ever had (R5), rolls
    /// back the ENTIRE import and leaves `catalog_revision` unchanged -- every actor stays loadable
    /// either way, since a refused import never touches `rpg_tree_node_state` at all.
    /// </summary>
    public TreeCatalogImportOutcome ImportTreeCatalog(IReadOnlyList<string> treeJsonDocs, PassiveTreeTuning tuning)
    {
        if (treeJsonDocs is null) throw new ArgumentNullException(nameof(treeJsonDocs));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        return ImportTreeCatalogCore(treeJsonDocs, fileNames: null, tuning);
    }

    /// <summary>
    /// Task C5's own entry point — R6's "classes.v2.json trap" (spec-tree-catalog.md §4): pairs each
    /// document with the file it came from so a filename `vN` that disagrees with that document's own
    /// `catalogVersion` field refuses the WHOLE import, batched into the same report as every other
    /// content refusal — never silently accepted the way `classes.v2.json`'s internal
    /// `"registryVersion": 4` already disagrees with its own name elsewhere in this repo. This is the
    /// shape spec-tree-catalog.md §6 step 2's real boot-time importer (reading actual files) calls.
    /// A distinct method name rather than an overload of <see cref="ImportTreeCatalog"/> on purpose:
    /// `null!` (used by <c>ImportTreeCatalog</c>'s own existing null-argument test) is ambiguous
    /// between two <c>IReadOnlyList&lt;T&gt;</c> overloads whose element type the compiler cannot
    /// infer from a null literal, and this repo does not touch a passing test's call site to make a
    /// new one compile.
    /// </summary>
    public TreeCatalogImportOutcome ImportTreeCatalogFiles(IReadOnlyList<(string FileName, string Json)> treeFiles, PassiveTreeTuning tuning)
    {
        if (treeFiles is null) throw new ArgumentNullException(nameof(treeFiles));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        var jsons = new List<string>(treeFiles.Count);
        var fileNames = new List<string>(treeFiles.Count);
        foreach (var f in treeFiles)
        {
            jsons.Add(f.Json);
            fileNames.Add(f.FileName);
        }
        return ImportTreeCatalogCore(jsons, fileNames, tuning);
    }

    TreeCatalogImportOutcome ImportTreeCatalogCore(IReadOnlyList<string> treeJsonDocs, IReadOnlyList<string>? fileNames, PassiveTreeTuning tuning)
    {
        var refusals = new List<string>();
        var loaded = new List<LoadedTree>();
        for (var i = 0; i < treeJsonDocs.Count; i++)
        {
            var (tree, report) = PassiveTreeCatalogLoader.Load(treeJsonDocs[i], tuning);
            refusals.AddRange(report.Refusals);
            if (tree is null) continue;

            loaded.Add(tree);
            if (fileNames is not null)
            {
                var filenameRefusal = PassiveTreeCatalogLoader.CheckFilenameVersion(fileNames[i], tree.Tree.CatalogVersion);
                if (filenameRefusal is not null) refusals.Add(filenameRefusal);
            }
        }

        lock (_gate)
        {
            using var db = OpenUnlocked();

            if (refusals.Count > 0)
                return new TreeCatalogImportOutcome(false, refusals, 0, ReadTreeCatalogRevisionUnlocked(db));

            var newNodeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in loaded)
                foreach (var n in t.Nodes)
                    newNodeIds.Add(n.NodeId);

            using var tx = db.BeginTransaction();

            // R5: every id any EXISTING allocation names must be an id SOME catalog revision has ever
            // had -- checked BEFORE a single row is written, so a rejected import leaves every actor's
            // stored allocation exactly as it was.
            var unknownIds = new List<string>();
            using (var check = db.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT DISTINCT node_id FROM rpg_tree_node_state;";
                using var r = check.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    if (newNodeIds.Contains(id)) continue;
                    using var known = db.CreateCommand();
                    known.Transaction = tx;
                    known.CommandText = "SELECT 1 FROM rpg_tree_catalog_known_node_id WHERE node_id = $id;";
                    known.Parameters.AddWithValue("$id", id);
                    if (known.ExecuteScalar() is null) unknownIds.Add(id);
                }
            }
            if (unknownIds.Count > 0)
            {
                tx.Rollback();
                return new TreeCatalogImportOutcome(false,
                    unknownIds.ConvertAll(id => $"existing allocation names node id '{id}' — no catalog revision has ever had it (R5)"),
                    0, ReadTreeCatalogRevisionUnlocked(db));
            }

            // R1/R2: "a retired id must never be reissued to a new node with different content" —
            // checked BEFORE any row is written, batched like every other refusal here. A generator
            // that correctly tracks retirement never re-emits a dropped id at all, so this only ever
            // fires on a real authoring mistake; a genuinely different node belongs at a new id, never
            // this one reused (the same "not after a deletion, not ever" standing every other content
            // id in this repo already carries — item/seed-contract.md:135).
            var reissuedRetiredIds = new List<string>();
            foreach (var id in newNodeIds)
            {
                using var q = db.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "SELECT retired_at_revision FROM rpg_tree_catalog_node WHERE node_id = $id AND retired_at_revision IS NOT NULL;";
                q.Parameters.AddWithValue("$id", id);
                if (q.ExecuteScalar() is long retiredAtRevision)
                    reissuedRetiredIds.Add($"node '{id}': retired at revision {retiredAtRevision} and must never be reissued (R1/R2)");
            }
            if (reissuedRetiredIds.Count > 0)
            {
                tx.Rollback();
                return new TreeCatalogImportOutcome(false, reissuedRetiredIds, 0, ReadTreeCatalogRevisionUnlocked(db));
            }

            var newRevision = ReadTreeCatalogRevisionUnlocked(db) + 1;

            // R1/R2: retire, never delete. Any node the OLD corpus carried as `enabled = 1` that this
            // import's corpus does NOT name is stamped `retired_at_revision = newRevision` and flipped
            // to `enabled = 0` -- the row itself, its atoms, and every field describing what it WAS
            // stay exactly as they were, so a surface can still render "what this node used to grant"
            // greyed out. A node already retired (its `retired_at_revision` already set by an earlier
            // import) is left alone here on purpose: retirement happens once, at the revision that
            // first dropped it, and is never re-stamped by a later revision that still doesn't carry
            // it. This replaces the DELETE-then-reinsert C4 shipped with, which wiped a retired node's
            // row on the very next import instead of preserving it — a real gap C4 left open, closed
            // here.
            var toRetire = new List<string>();
            using (var findLive = db.CreateCommand())
            {
                findLive.Transaction = tx;
                findLive.CommandText = "SELECT node_id FROM rpg_tree_catalog_node WHERE enabled = 1;";
                using var r = findLive.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    if (!newNodeIds.Contains(id)) toRetire.Add(id);
                }
            }
            foreach (var id in toRetire)
            {
                ExecIn(db, tx,
                    "UPDATE rpg_tree_catalog_node SET enabled = 0, retired_at_revision = $rev WHERE node_id = $id;",
                    ("$rev", newRevision), ("$id", id));
            }

            foreach (var t in loaded)
            {
                // Per-tree upsert, scoped to THIS tree_id only -- never the whole table, so a tree the
                // new corpus doesn't mention keeps its existing row untouched rather than vanishing.
                ExecIn(db, tx, "DELETE FROM rpg_tree_catalog_tree WHERE tree_id = $id;", ("$id", t.Tree.TreeId));
                ExecIn(db, tx, """
                    INSERT INTO rpg_tree_catalog_tree(
                      tree_id, category, gate_quantity, shape_archetype, tiers, branches,
                      nodes_per_tier, catalog_version, enabled)
                    VALUES ($id, $cat, $gate, $shape, $tiers, $branches, $npt, $ver, $en);
                    """,
                    ("$id", t.Tree.TreeId), ("$cat", t.Tree.Category.ToString()),
                    ("$gate", t.Tree.GateQuantity), ("$shape", t.Tree.ShapeArchetype),
                    ("$tiers", (long)t.Tree.Tiers), ("$branches", (long)t.Tree.Branches),
                    ("$npt", JsonSerializer.Serialize(t.Tree.NodesPerTier)),
                    ("$ver", (long)t.Tree.CatalogVersion), ("$en", t.Tree.Enabled ? 1L : 0L));

                foreach (var n in t.Nodes)
                {
                    // Per-node upsert, scoped to THIS node_id only -- covers a brand-new node (the
                    // delete is a no-op) and a magnitude retune on an existing id alike (R6: same id,
                    // fresh coefficients, and `rpg_tree_node_state` is a completely different table
                    // this method never opens, so zero per-actor rows are ever touched by a retune).
                    ExecIn(db, tx, "DELETE FROM rpg_tree_catalog_node WHERE node_id = $id;", ("$id", n.NodeId));
                    ExecIn(db, tx, "DELETE FROM rpg_tree_catalog_atom WHERE node_id = $id;", ("$id", n.NodeId));

                    ExecIn(db, tx, """
                        INSERT INTO rpg_tree_catalog_node(
                          node_id, tree_id, branch, tier, node_key, prereq_node_ids, node_class,
                          affix_ids, budget_share_milli, exclude_props, exclusion_form, tags_json,
                          enabled, retired_at_revision)
                        VALUES ($id, $tree, $branch, $tier, $key, $prereq, $class, $affix, $share,
                                $exclude, $form, $tags, $en, $retired);
                        """,
                        ("$id", n.NodeId), ("$tree", n.TreeId), ("$branch", n.Branch.ToString()),
                        ("$tier", (long)n.Tier), ("$key", n.NodeKey),
                        ("$prereq", JsonSerializer.Serialize(n.PrereqNodeIds)),
                        ("$class", n.NodeClass.ToString()),
                        ("$affix", JsonSerializer.Serialize(n.AffixIds)),
                        ("$share", (long)n.BudgetShareMilli),
                        ("$exclude", JsonSerializer.Serialize(n.ExcludeProps)),
                        ("$form", n.ExclusionForm.ToString()), ("$tags", (object?)n.TagsJson ?? DBNull.Value),
                        ("$en", n.Enabled ? 1L : 0L),
                        ("$retired", n.RetiredAtRevision.HasValue ? (long)n.RetiredAtRevision.Value : (object)DBNull.Value));

                    for (var ordinal = 0; ordinal < n.Atoms.Count; ordinal++)
                    {
                        var a = n.Atoms[ordinal];
                        // D58/J12 (spec-soul-curve-resolution.md, 2026-09-07): soul_curve_id stopped
                        // being read/written here -- NodeAtom.SoulCurveId had zero consumers that
                        // acted on its value. The column itself stays in the CREATE TABLE (no drop-
                        // column precedent exists in this repo, RpgStore.cs's own EnsureColumn is
                        // additive-only) and every row it holds from here on is NULL by omission --
                        // harmless dead schema, never read back (see the SELECT below).
                        ExecIn(db, tx, """
                            INSERT INTO rpg_tree_catalog_atom(
                              node_id, ordinal, kind_id, attach_point, channel_id, op, trigger,
                              when_json, k_micro, scale_axis, unit_class)
                            VALUES ($node, $ord, $kind, $attach, $channel, $op, $trig, $when,
                                    $kmicro, $axis, $unit);
                            """,
                            ("$node", n.NodeId), ("$ord", (long)ordinal), ("$kind", a.KindId),
                            ("$attach", a.AttachPoint.ToString()), ("$channel", a.ChannelId),
                            ("$op", a.Op.ToString()), ("$trig", (object?)a.Trigger ?? DBNull.Value),
                            ("$when", (object?)a.WhenJson ?? DBNull.Value),
                            ("$kmicro", a.KMicro), ("$axis", a.ScaleAxis.ToString()),
                            ("$unit", a.UnitClass.ToString()));
                    }

                    ExecIn(db, tx, "INSERT OR IGNORE INTO rpg_tree_catalog_known_node_id(node_id) VALUES ($id);",
                        ("$id", n.NodeId));
                }
            }

            ExecIn(db, tx, "UPDATE rpg_tree_catalog_meta SET revision = $r WHERE id = 1;", ("$r", newRevision));

            tx.Commit();
            return new TreeCatalogImportOutcome(true, Array.Empty<string>(), loaded.Count, newRevision);
        }
    }

    // ---- I2's own gap, closed here: a read back OUT of the catalog tables ----------------------
    //
    // C4/C5 shipped the importer (JSON -> rows) but never the inverse (rows -> LoadedTree). Every
    // resolve-side consumer (TierGate, CrossUnlock, Concentration, TreeResolveReport.Build,
    // TreeAtomSource) takes a LoadedTree, and nothing in FusionRpg.Data produced one until now --
    // a real wiring gap the passive-tree-todo.md I2 task named explicitly rather than asking this
    // module to invent a workaround (e.g. re-parsing JSON a second time, or a partial DTO).

    /// <summary>Every tree's id and category, with no node/atom rows read — the cheap half of a
    /// caller that needs to decide WHICH tree ids to resolve (e.g. "every shared, non-species tree")
    /// before paying for <see cref="LoadTreeCatalog"/>'s heavier per-node/per-atom queries.</summary>
    public IReadOnlyList<TreeRecord> ListTreeCatalogTrees()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT tree_id, category, gate_quantity, shape_archetype, tiers, branches,
                       nodes_per_tier, catalog_version, enabled
                FROM rpg_tree_catalog_tree;
                """;
            var result = new List<TreeRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(ReadTreeRecordRow(r));
            return result;
        }
    }

    /// <summary>
    /// Reconstructs one <see cref="LoadedTree"/> per row-set in the catalog tables — the exact shape
    /// <see cref="PassiveTreeCatalogLoader.Load"/> builds from JSON, so a caller downstream of import
    /// (tree-resolve assembly, `PassiveTreeEndpoints`) never re-parses a tree's committed JSON or
    /// duplicates the loader's own record shapes to get one back.
    ///
    /// <para><paramref name="treeIds"/> narrows both the tree and the node/atom queries to exactly
    /// those ids (a `WHERE tree_id IN (...)` filter, never an OR-chain of ANDs — SQLite's expression
    /// tree stays flat regardless of how many ids are named). <c>null</c> or empty loads the WHOLE
    /// catalog — every tree, every node, every atom — which is the right call for tooling/tests and
    /// the wrong one for a per-request read at species scale (35,280 nodes, D51 2026-09-06: 24
    /// statuses not 21 — was 35,160): a caller serving one actor's shared-corpus resolve should pass
    /// the 42 shared tree ids it already knows from
    /// <see cref="ListTreeCatalogTrees"/>, not the unfiltered form.</para>
    ///
    /// <para>Every node — enabled and retired alike — is included; retirement is a property on the
    /// row (<see cref="NodeRecord.Enabled"/>/<see cref="NodeRecord.RetiredAtRevision"/>), not a
    /// filter this read applies. <see cref="TreeResolveReport.Build"/> and
    /// <see cref="TreeAtomSource.BoundAtomsFor"/> already skip a disabled node themselves (R2) — this
    /// read does not have to duplicate that decision to be correct.</para>
    /// </summary>
    public IReadOnlyList<LoadedTree> LoadTreeCatalog(IReadOnlyCollection<string>? treeIds = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return LoadTreeCatalogUnlocked(db, treeIds is { Count: > 0 } ? treeIds : null);
        }
    }

    IReadOnlyList<LoadedTree> LoadTreeCatalogUnlocked(SqliteConnection db, IReadOnlyCollection<string>? treeIds)
    {
        var idFilter = treeIds is null ? null : new HashSet<string>(treeIds, StringComparer.Ordinal);

        var trees = new Dictionary<string, TreeRecord>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT tree_id, category, gate_quantity, shape_archetype, tiers, branches, " +
                "nodes_per_tier, catalog_version, enabled FROM rpg_tree_catalog_tree" +
                (idFilter is null ? ";" : " WHERE tree_id IN (" + InClause(idFilter, cmd, "t") + ");");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = ReadTreeRecordRow(r);
                trees[row.TreeId] = row;
            }
        }
        if (trees.Count == 0) return Array.Empty<LoadedTree>();

        // One atom pass, grouped by node id in memory -- never one query per node (35,280 nodes,
        // D51 2026-09-06: was 35,160, would be that many round trips). Joined against
        // rpg_tree_catalog_node so the SAME tree_id filter applies without a second parameter set.
        var atomsByNode = new Dictionary<string, List<NodeAtom>>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT a.node_id, a.kind_id, a.attach_point, a.channel_id, a.op, a.trigger, " +
                "a.when_json, a.k_micro, a.scale_axis, a.unit_class " +
                "FROM rpg_tree_catalog_atom a" +
                (idFilter is null
                    ? ""
                    : " JOIN rpg_tree_catalog_node n ON n.node_id = a.node_id WHERE n.tree_id IN (" + InClause(idFilter, cmd, "a") + ")") +
                " ORDER BY a.node_id, a.ordinal;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var nodeId = r.GetString(0);
                var atom = new NodeAtom(
                    r.GetString(1), Enum.Parse<AttachPoint>(r.GetString(2)), r.GetString(3),
                    Enum.Parse<NodeAtomOp>(r.GetString(4)),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetInt64(7), Enum.Parse<ScaleAxis>(r.GetString(8)), Enum.Parse<UnitClass>(r.GetString(9)));
                if (!atomsByNode.TryGetValue(nodeId, out var list)) atomsByNode[nodeId] = list = new List<NodeAtom>();
                list.Add(atom);
            }
        }

        var nodesByTree = new Dictionary<string, List<NodeRecord>>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT node_id, tree_id, branch, tier, node_key, prereq_node_ids, node_class, " +
                "affix_ids, budget_share_milli, exclude_props, exclusion_form, tags_json, enabled, " +
                "retired_at_revision FROM rpg_tree_catalog_node" +
                (idFilter is null ? ";" : " WHERE tree_id IN (" + InClause(idFilter, cmd, "n") + ");");
            using var r = cmd.ExecuteReader();
            // Column ordinals for the SELECT above -- schema-positional, not a balance value
            // (AGENTS.md: "indices" are the named exemption from the balance-surface rule). Named
            // anyway, past index 10, so a *Catalog.cs filename's own audit heuristic reads a
            // deliberate ordinal rather than an unexplained literal.
            const int ColTagsJson = 11, ColEnabled = 12, ColRetiredAtRevision = 13;
            while (r.Read())
            {
                var nodeId = r.GetString(0);
                var treeId = r.GetString(1);
                var node = new NodeRecord(
                    nodeId, treeId, Enum.Parse<TreeBranch>(r.GetString(2)), (int)r.GetInt64(3),
                    r.GetString(4),
                    JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? new List<string>(),
                    Enum.Parse<NodeClass>(r.GetString(6)),
                    JsonSerializer.Deserialize<List<string>>(r.GetString(7)) ?? new List<string>(),
                    (int)r.GetInt64(8),
                    atomsByNode.TryGetValue(nodeId, out var atoms) ? atoms : Array.Empty<NodeAtom>(),
                    JsonSerializer.Deserialize<List<string>>(r.GetString(9)) ?? new List<string>(),
                    Enum.Parse<ExclusionForm>(r.GetString(10)),
                    r.IsDBNull(ColTagsJson) ? null : r.GetString(ColTagsJson),
                    r.GetInt64(ColEnabled) != 0,
                    r.IsDBNull(ColRetiredAtRevision) ? (int?)null : (int)r.GetInt64(ColRetiredAtRevision));

                if (!nodesByTree.TryGetValue(treeId, out var list)) nodesByTree[treeId] = list = new List<NodeRecord>();
                list.Add(node);
            }
        }

        var result = new List<LoadedTree>(trees.Count);
        foreach (var (treeId, tree) in trees)
            result.Add(new LoadedTree(tree, nodesByTree.TryGetValue(treeId, out var nodes) ? nodes : Array.Empty<NodeRecord>()));
        return result;
    }

    static TreeRecord ReadTreeRecordRow(SqliteDataReader r)
    {
        var nodesPerTier = JsonSerializer.Deserialize<List<int>>(r.GetString(6)) ?? new List<int>();
        return new TreeRecord(
            r.GetString(0), Enum.Parse<TreeCategory>(r.GetString(1)), r.GetString(2), r.GetString(3),
            (int)r.GetInt64(4), (int)r.GetInt64(5), nodesPerTier, (int)r.GetInt64(7), r.GetInt64(8) != 0);
    }

    /// <summary>Builds a parameterized `$prefix0,$prefix1,...` list for a `WHERE col IN (...)` clause
    /// AND adds each parameter to <paramref name="cmd"/> — a single list expression, not an OR-chain
    /// of ANDs (`LoadTreeStateBatch`'s own reason for avoiding that shape at scale): SQLite's
    /// expression-tree depth limit is what an OR-chain approaches at a few hundred keys, and a flat
    /// `IN (...)` list never does.</summary>
    static string InClause(IEnumerable<string> ids, SqliteCommand cmd, string prefix)
    {
        var names = new List<string>();
        var i = 0;
        foreach (var id in ids)
        {
            var name = $"${prefix}{i++}";
            cmd.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return string.Join(",", names);
    }
}
