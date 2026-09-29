using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

/// <summary>
/// Task B5 — `tree-state` (spec-tree-state.md §1, §2, §6). Persists ONE actor's owned passive-tree
/// node set and each node's soul level — INPUTS only, never a resolved budget, spend or magnitude
/// (the same "a stored channel value would be a second SSOT" invariant `RpgStore.Aptitudes.cs`
/// already carries for aptitude points, applied here to tree effort).
///
/// <para><b>Row presence means owned.</b> There is no `owned` column: a node not owned has no row; a
/// node owned but never soul-levelled has a row with `soul_level = 0` — a REAL state, not a default,
/// per §1.1. This is why the save path differs from <c>SaveAllocationUnlocked</c>'s "skip zero"
/// rule: there, a zero POINT means unspent (skip); here, a zero SOUL LEVEL on an OWNED node is a
/// fact the caller already decided by including the node id at all.</para>
///
/// <para><b>No `tree_id` column, and no per-tree read.</b> A node id already carries its tree
/// (`skill.&lt;treeId&gt;-&lt;branch&gt;-t&lt;tier&gt;-&lt;nodeKey&gt;`), so one actor is one
/// row-set (§1's closing line) — the same `(scope, scope_key)` addressing
/// <c>rpg_aptitude_allocation</c> already uses for the same four <see cref="AllocationScope"/>
/// values (D21's own precedent, not a new scheme).</para>
/// </summary>
/// <summary>Task C10's respec outcome — the tree-state sibling of `SpeciesRespecOutcome`. No `Ok`
/// flag needed for "too many respecs": D18/RespecPolicy's own discipline is "never refused for BEING
/// a respec" — the only refusal this can carry is insufficient balance, named in `Reason`.</summary>
public sealed record TreeRespecOutcome(bool Ok, string Reason, long PriceAmount, long RespecCount, SoulBalanceDto Balance);

public sealed partial class RpgStore
{
    void EnsureTreeNodeStateSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_tree_node_state (
              scope       TEXT    NOT NULL,
              scope_key   TEXT    NOT NULL,
              node_id     TEXT    NOT NULL,
              soul_level  INTEGER NOT NULL,
              PRIMARY KEY (scope, scope_key, node_id)
            );
            """);
        Exec(db, "CREATE INDEX IF NOT EXISTS ix_tree_node_state_key ON rpg_tree_node_state(scope, scope_key);");
    }

    /// <summary>Persists ONLY this `(scope, scopeKey)`'s own owned nodes — a full delete-then-insert
    /// (mirrors <c>SaveAllocationUnlocked</c>'s transaction shape exactly), so a respec that empties
    /// the set actually removes every row rather than leaving stale ones. Every entry in
    /// `soulLevelByNodeId` produces a row UNCONDITIONALLY, including `soulLevel = 0` — presence in
    /// this dictionary already means "owned"; there is no zero to skip the way aptitude points has
    /// one.</summary>
    public void SaveTreeNodeState(AllocationScope scope, string scopeKey,
                                  IReadOnlyDictionary<string, long> soulLevelByNodeId)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));
        if (soulLevelByNodeId is null) throw new ArgumentNullException(nameof(soulLevelByNodeId));
        foreach (var soulLevel in soulLevelByNodeId.Values)
            if (soulLevel < 0)
                throw new ArgumentException($"soul level must be >= 0, got {soulLevel}", nameof(soulLevelByNodeId));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            SaveTreeNodeStateUnlocked(db, tx, scope, scopeKey, soulLevelByNodeId);
            tx.Commit();
        }
    }

    void SaveTreeNodeStateUnlocked(SqliteConnection db, SqliteTransaction tx, AllocationScope scope,
                                   string scopeKey, IReadOnlyDictionary<string, long> soulLevelByNodeId)
    {
        var scopeText = ScopeToText(scope);

        ExecIn(db, tx, "DELETE FROM rpg_tree_node_state WHERE scope = $scope AND scope_key = $key;",
            ("$scope", scopeText), ("$key", scopeKey));

        foreach (var (nodeId, soulLevel) in soulLevelByNodeId)
        {
            // Every owned node gets a row, unconditionally -- soul_level = 0 is a real, persisted
            // state (§1.1), never skipped the way an unspent aptitude point is.
            ExecIn(db, tx, """
                INSERT INTO rpg_tree_node_state (scope, scope_key, node_id, soul_level)
                VALUES ($scope, $key, $node, $soul);
                """,
                ("$scope", scopeText), ("$key", scopeKey), ("$node", nodeId), ("$soul", soulLevel));
        }
    }

    /// <summary>Single-`(scope, scopeKey)` read — the EDITING SURFACE ONLY (§6 rule 3). Battle setup
    /// must use <see cref="LoadTreeStateBatch"/> instead; a named seam test
    /// (<c>TreeStateBattleSeamTests</c>) asserts the battle path never calls this in a loop, the same
    /// discipline <c>SpeciesAllocationSeamTests</c> already enforces for <c>LoadAllocation</c>.</summary>
    public IReadOnlyDictionary<string, long> LoadTreeState(AllocationScope scope, string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            return LoadTreeStateUnlocked(db, scope, scopeKey);
        }
    }

    IReadOnlyDictionary<string, long> LoadTreeStateUnlocked(SqliteConnection db, AllocationScope scope, string scopeKey)
    {
        var scopeText = ScopeToText(scope);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);

        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT node_id, soul_level FROM rpg_tree_node_state WHERE scope = $scope AND scope_key = $key;";
        cmd.Parameters.AddWithValue("$scope", scopeText);
        cmd.Parameters.AddWithValue("$key", scopeKey);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = r.GetInt64(1); // soul_level is INTEGER (64-bit) -- GetInt64, never GetInt32 (§7)

        return result;
    }

    /// <summary>§6's whole reason for existing: **one query, one lock acquisition, one connection**
    /// for an entire squad. A naive per-(actor, tree) read is 234 lock-serialised queries before the
    /// first turn at a 6-actor squad and 39 trees — this reads every actor's sparse row-set in a
    /// single round trip via an OR-chain over `(scope, scope_key)` pairs, since SQLite has no tuple
    /// `IN` and building N separate per-scope `IN (...)` queries would still be N queries, not
    /// one.</summary>
    // SQLite's default SQLITE_LIMIT_EXPR_DEPTH is 1000 -- an OR-chain of N `(scope=.. AND
    // scope_key=..)` clauses builds an expression tree roughly N deep, so a squad-sized batch (6-20
    // keys) sails through but a 2,000-actor volume run (C8) throws "Expression tree is too large"
    // before a single row comes back. Chunking keeps "one lock, one connection" (C8's own row-count
    // proof exercises exactly this path) while trading "one query" for "a bounded few" only once the
    // key count actually demands it -- the six-actor squad case still issues exactly one query.
    const int TreeStateBatchChunkSize = 400;

    /// <summary>§6's whole reason for existing: **one query, one lock acquisition, one connection**
    /// for an entire squad. A naive per-(actor, tree) read is 234 lock-serialised queries before the
    /// first turn at a 6-actor squad and 39 trees — this reads every actor's sparse row-set in a
    /// single round trip via an OR-chain over `(scope, scope_key)` pairs, since SQLite has no tuple
    /// `IN` and building N separate per-scope `IN (...)` queries would still be N queries, not
    /// one. Chunked past <see cref="TreeStateBatchChunkSize"/> keys (C8's volume proof) so the
    /// OR-chain never approaches SQLite's expression-tree depth limit — one lock and one connection
    /// either way, just more than one round trip past that size.</summary>
    public IReadOnlyDictionary<(AllocationScope Scope, string Key), IReadOnlyDictionary<string, long>>
        LoadTreeStateBatch(IReadOnlyList<(AllocationScope Scope, string Key)> keys)
    {
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        var result = new Dictionary<(AllocationScope, string), IReadOnlyDictionary<string, long>>();
        if (keys.Count == 0) return result;

        foreach (var key in keys)
            result[key] = new Dictionary<string, long>(StringComparer.Ordinal);

        lock (_gate)
        {
            using var db = OpenUnlocked();

            for (var start = 0; start < keys.Count; start += TreeStateBatchChunkSize)
            {
                // Structural, PS-8 exempt, documented: this bounds a SQL round-trip's key count against
                // SQLite's own expression-depth limit, never a price, budget or any other progression
                // magnitude -- the loop still visits every key across as many round trips as it takes.
                var chunkCount = Math.Min(TreeStateBatchChunkSize, keys.Count - start);
                using var cmd = db.CreateCommand();

                var clauses = new List<string>(chunkCount);
                for (var i = 0; i < chunkCount; i++)
                {
                    var key = keys[start + i];
                    clauses.Add($"(scope = $scope{i} AND scope_key = $key{i})");
                    cmd.Parameters.AddWithValue($"$scope{i}", ScopeToText(key.Scope));
                    cmd.Parameters.AddWithValue($"$key{i}", key.Key);
                }
                cmd.CommandText =
                    "SELECT scope, scope_key, node_id, soul_level FROM rpg_tree_node_state WHERE " +
                    string.Join(" OR ", clauses) + ";";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var scope = ScopeFromText(r.GetString(0));
                    var scopeKey = r.GetString(1);
                    var mutableRow = (Dictionary<string, long>)result[(scope, scopeKey)];
                    mutableRow[r.GetString(2)] = r.GetInt64(3);
                }
            }
        }
        return result;
    }

    // ---- C9: the state reconciler (spec-tree-state.md §4) --------------------------------------

    /// <summary>The real <see cref="TreeNodeCatalogStatus"/> lookup for one node id, reading the C4
    /// catalog tables. `db`/`tx` are the CALLER's own connection/transaction (or `tx: null` for a
    /// read-only call) — never a second connection opened mid-read.</summary>
    TreeNodeCatalogStatus ReadCatalogStatusUnlocked(SqliteConnection db, SqliteTransaction? tx, string nodeId)
    {
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT enabled FROM rpg_tree_catalog_node WHERE node_id = $id;";
            cmd.Parameters.AddWithValue("$id", nodeId);
            if (cmd.ExecuteScalar() is long enabled)
                return enabled != 0 ? TreeNodeCatalogStatus.Live : TreeNodeCatalogStatus.Retired;
        }
        using (var known = db.CreateCommand())
        {
            known.Transaction = tx;
            known.CommandText = "SELECT 1 FROM rpg_tree_catalog_known_node_id WHERE node_id = $id;";
            known.Parameters.AddWithValue("$id", nodeId);
            return known.ExecuteScalar() is not null ? TreeNodeCatalogStatus.Retired : TreeNodeCatalogStatus.NeverKnown;
        }
    }

    /// <summary>`LoadTreeState` plus <see cref="TreeStateReconciler"/>'s classification, in one call —
    /// the shape a future surface reads (§4: "the three-way result is what the surface renders").
    /// Classification happens exactly once per load, over the already-loaded row set; never throws for
    /// an id no catalog revision has ever had.</summary>
    public IReadOnlyList<ClassifiedTreeNode> LoadAndClassifyTreeState(AllocationScope scope, string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            var owned = LoadTreeStateUnlocked(db, scope, scopeKey);
            return TreeStateReconciler.Classify(owned, id => ReadCatalogStatusUnlocked(db, tx: null, id));
        }
    }

    /// <summary>I8's preview endpoint (spec-tree-surface.md §7.2 part 5) — classifies a CALLER-SUPPLIED
    /// node set (a draft that has never been through <see cref="SaveTreeNodeState"/>, and never will
    /// be) against the live catalog, read-only. Same <see cref="TreeStateReconciler.Classify"/> call
    /// <see cref="LoadAndClassifyTreeState"/> makes over the STORED row set — this is the one-line
    /// difference that lets a hypothetical draft run through the exact same live/retired/unknown rule
    /// without a round trip through <c>rpg_tree_node_state</c> first. No row is read or written for
    /// the node set itself; only the catalog tables are consulted.</summary>
    public IReadOnlyList<ClassifiedTreeNode> ClassifyTreeNodeState(IReadOnlyDictionary<string, long> ownedNodeIdToSoulLevel)
    {
        if (ownedNodeIdToSoulLevel is null) throw new ArgumentNullException(nameof(ownedNodeIdToSoulLevel));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            return TreeStateReconciler.Classify(ownedNodeIdToSoulLevel, id => ReadCatalogStatusUnlocked(db, tx: null, id));
        }
    }

    // ---- C10: tree respec (spec-tree-state.md §5, §5.1) --------------------------------------

    void EnsureTreeRespecSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_tree_respec_count (
              scope       TEXT    NOT NULL,
              scope_key   TEXT    NOT NULL,
              count       INTEGER NOT NULL,
              PRIMARY KEY (scope, scope_key)
            );
            """);
    }

    long ReadTreeRespecCountUnlocked(SqliteConnection db, string scopeText, string scopeKey)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count FROM rpg_tree_respec_count WHERE scope = $scope AND scope_key = $key;";
        cmd.Parameters.AddWithValue("$scope", scopeText);
        cmd.Parameters.AddWithValue("$key", scopeKey);
        // No row yet == never respecced -- count 0, the same "row presence is the only state" reading
        // §1.1 already establishes for rpg_tree_node_state.
        return cmd.ExecuteScalar() is long stored ? stored : 0L;
    }

    /// <summary>
    /// C10's entry point: full reset, scoped per `(scope, scopeKey)`, in ONE transaction — the price
    /// (D18, souls, `TreeRespecPolicy`'s linear-escalation-on-a-count shape), the counter advance and
    /// the full node-set clear all commit together, or none do. Never refused for BEING a respec
    /// (`RespecPolicy`'s own discipline, copied verbatim) — the one refusal this can return is
    /// insufficient balance, the same shape `TryRespecSpecies` already uses.
    ///
    /// <para>Re-buying the identical node set after a respec costs exactly what it cost before: the
    /// clear leaves `rpg_tree_node_state` with zero rows for this key, so `TreeUnlockCost`'s own
    /// `PriceOfNth`/`Cumulative` (which read `ownedCount` fresh, never a cached history) start from
    /// zero again automatically — nothing here needs to reset a second ladder.</para>
    /// </summary>
    public TreeRespecOutcome RespecTreeState(
        long playerId, AllocationScope scope, string scopeKey, string correlationId, DateTimeOffset? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            throw new ArgumentException("scopeKey must not be empty", nameof(scopeKey));
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("correlationId must not be empty", nameof(correlationId));

        var corr = correlationId.Trim();
        var scopeText = ScopeToText(scope);
        var tuning = PassiveTreeTuningHub.Tuning;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var nowText = (utcNow ?? ServerClock.UtcNow).ToString("o");

            var count = ReadTreeRespecCountUnlocked(db, scopeText, scopeKey);

            // Replay check FIRST, before pricing off the counter this very call would advance --
            // mirrors TryRespecSpecies's own reasoning (RpgStore.SpeciesRespec.cs) exactly: a stale
            // "recompute and compare" would reject a legitimate replay once the count it was
            // originally priced at has moved.
            using (var check = db.CreateCommand())
            {
                check.CommandText = "SELECT delta FROM rpg_soul_ledger WHERE player_id=$p AND reason=$r AND dedupe_key=$dk;";
                check.Parameters.AddWithValue("$p", playerId);
                check.Parameters.AddWithValue("$r", SoulEarnPolicy.Reasons.Respec);
                check.Parameters.AddWithValue("$dk", corr);
                if (check.ExecuteScalar() is long storedDelta)
                {
                    tx.Commit();
                    return new TreeRespecOutcome(true, "replay", -storedDelta, count, ReadSoulBalanceUnlocked(db, playerId));
                }
            }

            // R4 (spec-tree-catalog.md §4): "a catalog revision that retires an ALLOCATED node grants
            // a free full respec, at price zero." Eligibility is recomputed fresh from the actor's
            // CURRENT owned set on every call — never a claimed/banked flag, never a per-node
            // compensation table (the spec's own words for what NOT to build). As long as one owned
            // node classifies Retired the escape hatch applies; taking it clears every owned node
            // (including the retired one), so the very next respec attempt finds nothing retired left
            // to hold and is priced normally again.
            var owned = LoadTreeStateUnlocked(db, scope, scopeKey);
            var isForcedByRetirement = owned.Keys.Any(id => ReadCatalogStatusUnlocked(db, tx, id) == TreeNodeCatalogStatus.Retired);

            long priceAmount;
            if (isForcedByRetirement)
            {
                priceAmount = 0;
            }
            else
            {
                var price = TreeRespecPolicy.PriceOf(tuning, count);
                var balance = ReadSoulBalanceUnlocked(db, playerId);
                if (balance.Balance < price.Amount)
                {
                    tx.Rollback();
                    return new TreeRespecOutcome(false, "souls.insufficient", price.Amount, count, balance);
                }
                priceAmount = price.Amount;
            }

            AppendSoulLedgerUnlocked(db, playerId, 0, -priceAmount, SoulEarnPolicy.Reasons.Respec,
                "spend", corr, corr, nowText);

            SaveTreeNodeStateUnlocked(db, tx, scope, scopeKey, new Dictionary<string, long>());

            // A free respec granted by forced retirement never advances the paid-respec ladder — it
            // is not a choice the actor made, and bumping `count` would silently inflate the price of
            // their NEXT voluntary respec. The counter row is left exactly as it was.
            var newCount = count;
            if (!isForcedByRetirement)
            {
                newCount = count + 1;
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO rpg_tree_respec_count(scope, scope_key, count)
                    VALUES ($scope, $key, $count)
                    ON CONFLICT(scope, scope_key)
                    DO UPDATE SET count = $count;
                    """;
                cmd.Parameters.AddWithValue("$scope", scopeText);
                cmd.Parameters.AddWithValue("$key", scopeKey);
                cmd.Parameters.AddWithValue("$count", newCount);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return new TreeRespecOutcome(true, isForcedByRetirement ? "forced-retirement" : "",
                priceAmount, newCount, ReadSoulBalanceUnlocked(db, playerId));
        }
    }
}
