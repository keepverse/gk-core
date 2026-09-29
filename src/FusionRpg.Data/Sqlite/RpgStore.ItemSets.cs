using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Thresholds;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    // ---- item sets (module 12, threshold-grants) ----------------------------------------------------

    /// <summary>
    /// ssot-sets.md §4.2's three tables, verbatim. <b>Zero columns are added to any existing atom
    /// table</b> — the tier bonuses reuse `effect_container` / `effect_instance` / `effect_binding`
    /// exactly as an item does.
    ///
    /// <para>Named consumers, per SC7: all three are read by
    /// <see cref="FusionRpg.Core.Items.Thresholds.SetEvaluator"/>, and `item_set_member` additionally by
    /// module 20's tooltip, which renders "3 / 4" from it. No row here is inert.</para>
    /// </summary>
    void EnsureItemSetSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS item_set (
              set_id       TEXT NOT NULL PRIMARY KEY,   -- kebab, and must not end in -NN (tier collision)
              display_name TEXT NOT NULL,
              level_req    INTEGER,                     -- informational; the real gate is I11's, per member
              enabled      INTEGER NOT NULL DEFAULT 1,
              revision     INTEGER NOT NULL DEFAULT 1
            );

            -- role and frame are DENORMALIZED copies of what the base type declares, kept locally so the
            -- UNIQUE below can exist in SQL at all. They are checked against the declaration at import.
            CREATE TABLE IF NOT EXISTS item_set_member (
              set_id       TEXT NOT NULL,
              container_id TEXT NOT NULL,               -- the member base type, kind 'item'
              role         TEXT NOT NULL,
              frame        TEXT NOT NULL,               -- humanoid | plant; 'hybrid' is a body, not a ladder
              PRIMARY KEY (set_id, container_id),
              UNIQUE (set_id, role, frame)
            );

            -- pieces_required counts DISTINCT MEMBER ROLES, never items: two copies of one set ring in
            -- jewel-minor-a and jewel-minor-b are one piece, because the member row declares one role.
            CREATE TABLE IF NOT EXISTS item_set_tier (
              set_id          TEXT NOT NULL,
              pieces_required INTEGER NOT NULL,
              container_id    TEXT NOT NULL UNIQUE,     -- set.{set_id}-{pieces:D2}; the pad is load-bearing
              is_capability   INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (set_id, pieces_required)
            );

            CREATE INDEX IF NOT EXISTS ix_item_set_member_container ON item_set_member(container_id);
            CREATE INDEX IF NOT EXISTS ix_item_set_member_role ON item_set_member(role);
            """);

        // item-lore T7 (owner decision 2026-09-06): a set gets the card's Flavour block too. A KEY,
        // never a literal -- the same rule `item_unique.flavour_key` already states, and the sentence
        // itself lives in N2's string catalog. NULL for the 24 of 30 shipped sets that authored none.
        EnsureColumn(db, "item_set", "flavour_key", "TEXT");
        // species-gear-chain T28 (set-species-binding b): the theme's own species, lower-case runtime
        // catalog spelling. NULL for a build.*/theme.* themed set -- explicit absence, never a guess.
        // Identity only: SetEvaluator never reads this column, so no bonus magnitude moves.
        EnsureColumn(db, "item_set", "species_id", "TEXT");
    }

    /// <summary>Replace the whole set catalog in one transaction — the corpus is authored, not edited live.</summary>
    public void ImportSetCorpus(IReadOnlyList<SetDef> sets)
    {
        if (sets is null) throw new ArgumentNullException(nameof(sets));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            LootExec(db, tx, "DELETE FROM item_set_tier;");
            LootExec(db, tx, "DELETE FROM item_set_member;");
            LootExec(db, tx, "DELETE FROM item_set;");

            foreach (var set in sets)
            {
                LootExec(db, tx, """
                    INSERT INTO item_set (set_id, display_name, level_req, enabled, revision, flavour_key, species_id)
                    VALUES ($id, $name, NULL, 1, 1, $flavour, $species);
                    """,
                    ("$id", set.SetId), ("$name", set.DisplayName),
                    ("$flavour", (object?)set.FlavourKey ?? DBNull.Value),
                    ("$species", (object?)set.SpeciesId ?? DBNull.Value));

                foreach (var m in set.Members)
                    LootExec(db, tx, """
                        INSERT INTO item_set_member (set_id, container_id, role, frame)
                        VALUES ($id, $cid, $role, $frame);
                        """,
                        ("$id", set.SetId), ("$cid", m.ContainerId),
                        ("$role", ItemRoles.Id(m.Role)),
                        ("$frame", m.Frame == ItemFrame.Humanoid ? "humanoid" : "plant"));

                foreach (var t in set.Tiers)
                    LootExec(db, tx, """
                        INSERT INTO item_set_tier (set_id, pieces_required, container_id, is_capability)
                        VALUES ($id, $pieces, $cid, $cap);
                        """,
                        ("$id", set.SetId), ("$pieces", t.PiecesRequired),
                        ("$cid", t.ContainerId), ("$cap", t.IsCapability ? 1 : 0));
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// species-gear-chain T4: is this base-type container a member of any shipped set? The step-10
    /// kind read — the same <c>item_set_member</c> membership <c>SocketHost.IsSetPiece</c> already
    /// answers off, shaped as the query <c>IsUniqueSetMember</c> already is. A set member rolls the
    /// reduced window; everything else rolls ordinary (unique/boss kinds arrive through their own
    /// paths, never this one).
    /// </summary>
    public bool IsSetMember(string containerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM item_set_member WHERE container_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", containerId);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>The whole catalog, back in the shape the pure evaluator takes.</summary>
    public IReadOnlyList<SetDef> ListSets()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();

            var members = new Dictionary<string, List<SetMemberDef>>(StringComparer.Ordinal);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT set_id, container_id, role, frame FROM item_set_member ORDER BY set_id, container_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var setId = r.GetString(0);
                    if (!ItemRoles.TryParse(r.GetString(2), out var role)) continue;
                    var frame = r.GetString(3) == "plant" ? ItemFrame.Plant : ItemFrame.Humanoid;
                    if (!members.TryGetValue(setId, out var list)) members[setId] = list = new List<SetMemberDef>();
                    list.Add(new SetMemberDef(r.GetString(1), role, frame));
                }
            }

            var tiers = new Dictionary<string, List<SetTierDef>>(StringComparer.Ordinal);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT set_id, pieces_required, container_id, is_capability FROM item_set_tier ORDER BY set_id, pieces_required;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var setId = r.GetString(0);
                    if (!tiers.TryGetValue(setId, out var list)) tiers[setId] = list = new List<SetTierDef>();
                    list.Add(new SetTierDef(r.GetInt32(1), r.GetString(2), r.GetInt32(3) != 0));
                }
            }

            var result = new List<SetDef>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT set_id, display_name, flavour_key, species_id FROM item_set WHERE enabled = 1 ORDER BY set_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var setId = r.GetString(0);
                    result.Add(new SetDef(setId, r.GetString(1),
                        members.TryGetValue(setId, out var m) ? m : new List<SetMemberDef>(),
                        tiers.TryGetValue(setId, out var t) ? t : new List<SetTierDef>(),
                        // The sentence is NOT stored -- only its key. `SetDef.FlavourText` is the
                        // corpus reader's field and stays null on a DB round trip by design.
                        FlavourKey: r.IsDBNull(2) ? null : r.GetString(2),
                        SpeciesId: r.IsDBNull(3) ? null : r.GetString(3)));
                }
            }

            return result;
        }
    }

    /// <summary>
    /// The container ids currently bound to <paramref name="owner"/> under one consumer's
    /// <c>source</c> — the exact input <see cref="ThresholdEvaluator.Reconcile"/> takes.
    ///
    /// <para>Scoped to ONE source on purpose: withdrawing by source as a group is what keeps two partial
    /// sets independent, and handing the reconcile the owner's whole binding list would let one set's
    /// diff withdraw the other's tiers.</para>
    /// </summary>
    public IReadOnlyList<string> ListBoundContainerIdsBySource(FusionRpg.Core.Effects.Atoms.OwnerScope owner, string source)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT i.container_id
                FROM effect_binding b
                JOIN effect_instance i ON i.instance_id = b.instance_id
                WHERE b.owner_kind = $kind AND b.owner_key = $key AND b.source = $src
                ORDER BY i.container_id ASC;
                """;
            cmd.Parameters.AddWithValue("$kind", FusionRpg.Core.Effects.Atoms.OwnerScope.Name(owner.Kind));
            cmd.Parameters.AddWithValue("$key", owner.Key ?? "");
            cmd.Parameters.AddWithValue("$src", source);

            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>
    /// ssot-sets.md §4.5 step 2, as SQL: how many DISTINCT member roles the wearer has filled, per set.
    /// One indexed lookup on the owner; never per frame.
    ///
    /// <para><b>⛔ The default source is <c>equip-assign</c>, not §4.5's <c>equip</c>.</b> The lane doc's
    /// recount SQL was written before module 4 shipped, and the shipped writer is
    /// <c>ApplyEquipProjection</c> (`RpgStore.Items.cs`), which tags every equip binding
    /// <c>equip-assign</c>. Verified facts win: against the doc's spelling this query returns zero rows
    /// for every real wearer, which is the worst kind of wrong — a set that silently never completes.
    /// The parameter stays so a caller with a different tag can say so.</para>
    /// </summary>
    public IReadOnlyDictionary<string, int> CountSetPieces(
        FusionRpg.Core.Effects.Atoms.OwnerScope owner, string equipSource = "equip-assign")
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT m.set_id, COUNT(DISTINCT m.role)
                FROM effect_binding b
                JOIN effect_instance i ON i.instance_id  = b.instance_id
                JOIN item_set_member m ON m.container_id = i.container_id AND m.role = b.slot
                WHERE b.owner_kind = $kind AND b.owner_key = $key AND b.source = $src
                GROUP BY m.set_id;
                """;
            cmd.Parameters.AddWithValue("$kind", FusionRpg.Core.Effects.Atoms.OwnerScope.Name(owner.Kind));
            cmd.Parameters.AddWithValue("$key", owner.Key ?? "");
            cmd.Parameters.AddWithValue("$src", equipSource);

            using var r = cmd.ExecuteReader();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            return counts;
        }
    }
}
