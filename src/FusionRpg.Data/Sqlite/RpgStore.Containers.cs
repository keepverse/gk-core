using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// The tables that let anything own atoms: <c>effect_container</c>, <c>effect_container_atom</c>,
/// <c>effect_container_pool</c>, plus the <c>rarity</c> band table (spec-container-schema.md, E5).
///
/// <para><b>Containers are mechanism, not content.</b> These tables and the published contract ship
/// here; items, traits, skills and world buildings author their own rows when their specs land.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureContainerSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS effect_container (
              container_id TEXT NOT NULL PRIMARY KEY,
              container_kind TEXT NOT NULL,
              slot TEXT,
              rarity TEXT,
              frame TEXT,
              base_type_id TEXT,
              min_tier INTEGER,
              max_tier INTEGER,
              level_req INTEGER,
              prefix_rolls INTEGER NOT NULL DEFAULT 0,
              suffix_rolls INTEGER NOT NULL DEFAULT 0,
              tags_json TEXT NOT NULL DEFAULT '{}',
              enabled INTEGER NOT NULL DEFAULT 1,
              revision INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_effect_container_kind ON effect_container(container_kind);

            CREATE TABLE IF NOT EXISTS effect_container_atom (
              container_id TEXT NOT NULL,
              seq INTEGER NOT NULL,
              atom_id TEXT NOT NULL,
              overrides_json TEXT,
              PRIMARY KEY (container_id, seq)
            );

            CREATE TABLE IF NOT EXISTS effect_container_pool (
              container_id TEXT NOT NULL,
              affix_id TEXT NOT NULL,
              weight INTEGER NOT NULL DEFAULT 0,
              group_key TEXT,
              PRIMARY KEY (container_id, affix_id)
            );

            -- Ordinals are explicit and append-only: they are load-bearing for sorting and for the
            -- budget lookup, so a reorder silently re-prices every container naming one.
            CREATE TABLE IF NOT EXISTS rarity (
              rarity_id TEXT NOT NULL PRIMARY KEY,
              ordinal INTEGER NOT NULL UNIQUE,
              prefix_rolls INTEGER NOT NULL DEFAULT 0,
              suffix_rolls INTEGER NOT NULL DEFAULT 0,
              min_tier INTEGER NOT NULL DEFAULT 1,
              max_tier INTEGER NOT NULL DEFAULT 1
            );

            -- affix-schema (T3.1, definitions.md §4a): the pool's roll unit. The overwhelming
            -- majority are single-ref, rule-generated 1:1 from the atom catalog (affix-library,
            -- module 3); a hand-authored multi-ref bundle is the exception.
            CREATE TABLE IF NOT EXISTS effect_affix (
              affix_id TEXT NOT NULL PRIMARY KEY,
              affix_class TEXT NOT NULL,
              revision INTEGER NOT NULL DEFAULT 0
            );

            -- Exactly one of atom_id or (slot_name, slot_domain) is set per row — a concrete ref or
            -- a slot ref, never both, never neither (ContainerValidator enforces this; SQL only
            -- guards nullability, not the exclusivity).
            CREATE TABLE IF NOT EXISTS effect_affix_ref (
              affix_id TEXT NOT NULL,
              seq INTEGER NOT NULL,
              atom_id TEXT,
              slot_name TEXT,
              slot_domain TEXT,
              slot_pick INTEGER NOT NULL DEFAULT 0,
              slot_atom_pattern TEXT,
              PRIMARY KEY (affix_id, seq)
            );
            """);
    }

    /// <summary>
    /// Whole-container equality, ignoring <c>revision</c> — that is the field being decided.
    /// Record equality would compare the child lists by reference and call every write a change.
    /// </summary>
    static bool SameContent(ContainerRow? stored, ContainerRow incoming)
    {
        if (stored is null) return false;

        if (stored.Kind != incoming.Kind
            || stored.Slot != incoming.Slot
            || stored.Rarity != incoming.Rarity
            || stored.Frame != incoming.Frame
            || stored.BaseTypeId != incoming.BaseTypeId
            || stored.MinTier != incoming.MinTier
            || stored.MaxTier != incoming.MaxTier
            || stored.LevelReq != incoming.LevelReq
            || stored.PrefixRolls != incoming.PrefixRolls
            || stored.SuffixRolls != incoming.SuffixRolls
            || !string.Equals(stored.TagsJson, incoming.TagsJson, StringComparison.Ordinal)
            || stored.Enabled != incoming.Enabled)
            return false;

        return stored.Atoms.SequenceEqual(incoming.Atoms)
            && stored.Pool.SequenceEqual(incoming.Pool);
    }

    // ---- rarity ---------------------------------------------------------------------------------

    /// <summary>
    /// Insert or update a rarity band. An ordinal already in use by a <b>different</b> id is refused:
    /// append-only means a band may be added, never renumbered underneath the content that names it.
    /// </summary>
    public (bool Ok, string Reason) UpsertRarity(RarityRow r)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return UpsertRarityUnlocked(db, null, r, out _);
        }
    }

    /// <summary>
    /// The rarity write, on a caller-owned connection and optional transaction — see
    /// <see cref="WriteContainerUnlocked"/> for why the import needs one transaction for everything.
    /// The append-only ordinal check reads the same table, so it must run on this connection.
    /// </summary>
    /// <param name="changed">
    /// How many rows the write altered. A band re-imported unchanged reports 0 — E14a bumps the
    /// catalog revision only when an import actually changed something, and an unconditional update
    /// here would make every repeat import look like an edit.
    /// </param>
    (bool Ok, string Reason) UpsertRarityUnlocked(
        SqliteConnection db, SqliteTransaction? tx, RarityRow r, out int changed)
    {
        changed = 0;
        if (string.IsNullOrWhiteSpace(r.RarityId)) return (false, "rarity_id is empty");
        if (r.MinTier > r.MaxTier) return (false, $"tier window [{r.MinTier}, {r.MaxTier}] is inverted");

        using (var check = db.CreateCommand())
        {
            if (tx is not null) check.Transaction = tx;
            check.CommandText = "SELECT rarity_id FROM rarity WHERE ordinal = $o AND rarity_id <> $id;";
            check.Parameters.AddWithValue("$o", r.Ordinal);
            check.Parameters.AddWithValue("$id", r.RarityId);
            if (check.ExecuteScalar() is string taken)
                return (false, $"rarity.ladder-mutated: ordinal {r.Ordinal} already belongs to '{taken}' — ordinals are append-only");
        }

        // item-ideal.md, rarity-bands: the check above catches "give my ordinal to someone else" but
        // not "move MY OWN row to an ordinal nobody else holds" -- ordinals are load-bearing for
        // sorting and the budget lookup, so an id's own ordinal is exactly as frozen as anyone else's.
        using (var selfCheck = db.CreateCommand())
        {
            if (tx is not null) selfCheck.Transaction = tx;
            selfCheck.CommandText = "SELECT ordinal FROM rarity WHERE rarity_id = $id;";
            selfCheck.Parameters.AddWithValue("$id", r.RarityId);
            if (selfCheck.ExecuteScalar() is long existingOrdinal && existingOrdinal != r.Ordinal)
                return (false, $"rarity.ladder-mutated: '{r.RarityId}' is already ordinal {existingOrdinal} — " +
                    "ordinals are append-only, never renumbered, even onto an unused slot");
        }

        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO rarity (rarity_id, ordinal, prefix_rolls, suffix_rolls, min_tier, max_tier)
            VALUES ($id, $o, $prefix, $suffix, $min, $max)
            ON CONFLICT(rarity_id) DO UPDATE SET
              ordinal = excluded.ordinal, prefix_rolls = excluded.prefix_rolls,
              suffix_rolls = excluded.suffix_rolls,
              min_tier = excluded.min_tier, max_tier = excluded.max_tier
            WHERE rarity.ordinal IS NOT excluded.ordinal
               OR rarity.prefix_rolls IS NOT excluded.prefix_rolls
               OR rarity.suffix_rolls IS NOT excluded.suffix_rolls
               OR rarity.min_tier IS NOT excluded.min_tier
               OR rarity.max_tier IS NOT excluded.max_tier;
            """;
        cmd.Parameters.AddWithValue("$id", r.RarityId);
        cmd.Parameters.AddWithValue("$o", r.Ordinal);
        cmd.Parameters.AddWithValue("$prefix", r.PrefixRolls);
        cmd.Parameters.AddWithValue("$suffix", r.SuffixRolls);
        cmd.Parameters.AddWithValue("$min", r.MinTier);
        cmd.Parameters.AddWithValue("$max", r.MaxTier);
        changed = cmd.ExecuteNonQuery();
        return (true, "");
    }

    /// <summary>Bands in ordinal order — the order rarity means.</summary>
    public IReadOnlyList<RarityRow> ListRarities()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT rarity_id, ordinal, prefix_rolls, suffix_rolls, min_tier, max_tier FROM rarity ORDER BY ordinal;";
            using var r = cmd.ExecuteReader();

            var list = new List<RarityRow>();
            while (r.Read())
                list.Add(new RarityRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
            return list;
        }
    }

    // ---- containers -----------------------------------------------------------------------------

    /// <summary>
    /// Validate then write. A container is stored whole or not at all: its atoms and pool rows are
    /// replaced inside one transaction, so a half-written container cannot be observed.
    /// </summary>
    public AtomRejection UpsertContainer(ContainerRow c)
    {
        var loaded = ListAtoms();
        var byId = loaded.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var rarityIds = ListRarities().Select(r => r.RarityId).ToHashSet(StringComparer.Ordinal);

        var check = ContainerValidator.Validate(
            c, id => byId.TryGetValue(id, out var a) ? a : null, GetAffix, rarityIds.Contains);
        if (!check.IsOk) return check;

        // An identical write is a no-op, revision included. `revision` is a hashed column, so
        // bumping it for a container nobody edited would make a repeat import look exactly like a
        // content edit — the thing the content hash exists to distinguish (E14a).
        //
        // The comparison must cover the children: they are replaced wholesale, and a changed atom
        // list is a content change even when every parent column is identical.
        if (SameContent(GetContainer(c.ContainerId), c)) return AtomRejection.Ok;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            WriteContainerUnlocked(db, tx, c);
            tx.Commit();
            return AtomRejection.Ok;
        }
    }

    /// <summary>
    /// The write half, on a caller-owned transaction. E14a's import needs every atom, container and
    /// curve in <b>one</b> transaction — a per-container transaction would leave a partial catalog
    /// behind if the tenth container failed after nine had committed.
    ///
    /// <para>Validation and the identical-write check are the caller's: both read other tables, and
    /// reading through a second connection while this transaction holds the write lock would
    /// deadlock rather than answer.</para>
    /// </summary>
    void WriteContainerUnlocked(SqliteConnection db, SqliteTransaction tx, ContainerRow c)
    {
        ExecIn(db, tx, """
            INSERT INTO effect_container
              (container_id, container_kind, slot, rarity, frame, base_type_id, min_tier, max_tier, level_req,
               prefix_rolls, suffix_rolls, tags_json, enabled, revision)
            VALUES ($id, $kind, $slot, $rarity, $frame, $baseType, $min, $max, $lvl, $prefix, $suffix, $tags, $enabled, 1)
            ON CONFLICT(container_id) DO UPDATE SET
              container_kind = excluded.container_kind, slot = excluded.slot,
              rarity = excluded.rarity, frame = excluded.frame, base_type_id = excluded.base_type_id,
              min_tier = excluded.min_tier, max_tier = excluded.max_tier,
              level_req = excluded.level_req, prefix_rolls = excluded.prefix_rolls,
              suffix_rolls = excluded.suffix_rolls,
              tags_json = excluded.tags_json, enabled = excluded.enabled,
              revision = effect_container.revision + 1;
            """,
            ("$id", c.ContainerId), ("$kind", KindName(c.Kind)),
            ("$slot", (object?)c.Slot ?? DBNull.Value), ("$rarity", (object?)c.Rarity ?? DBNull.Value),
            ("$frame", (object?)c.Frame ?? DBNull.Value), ("$baseType", (object?)c.BaseTypeId ?? DBNull.Value),
            ("$min", (object?)c.MinTier ?? DBNull.Value), ("$max", (object?)c.MaxTier ?? DBNull.Value),
            ("$lvl", (object?)c.LevelReq ?? DBNull.Value), ("$prefix", c.PrefixRolls), ("$suffix", c.SuffixRolls),
            ("$tags", c.TagsJson ?? "{}"), ("$enabled", c.Enabled ? 1 : 0));

        // Replace rather than merge: a container's contents are one authored statement, and a
        // stale child row from a previous revision is content nobody wrote.
        ExecIn(db, tx, "DELETE FROM effect_container_atom WHERE container_id = $id;", ("$id", c.ContainerId));
        ExecIn(db, tx, "DELETE FROM effect_container_pool WHERE container_id = $id;", ("$id", c.ContainerId));

        foreach (var a in c.Atoms)
            ExecIn(db, tx,
                "INSERT INTO effect_container_atom (container_id, seq, atom_id, overrides_json) " +
                "VALUES ($id, $seq, $atom, $ov);",
                ("$id", c.ContainerId), ("$seq", a.Seq), ("$atom", a.AtomId),
                ("$ov", (object?)a.OverridesJson ?? DBNull.Value));

        foreach (var p in c.Pool)
            ExecIn(db, tx,
                "INSERT INTO effect_container_pool (container_id, affix_id, weight, group_key) " +
                "VALUES ($id, $affix, $w, $g);",
                ("$id", c.ContainerId), ("$affix", p.AffixId), ("$w", p.Weight),
                ("$g", (object?)p.Group ?? DBNull.Value));
    }

    public ContainerRow? GetContainer(string containerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();

            ContainerRow? head;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT container_id, container_kind, slot, rarity, frame, base_type_id, min_tier, max_tier, level_req,
                           prefix_rolls, suffix_rolls, tags_json, enabled, revision
                    FROM effect_container WHERE container_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", containerId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;

                head = new ContainerRow
                {
                    ContainerId = r.GetString(0),
                    Kind = ParseKind(r.GetString(1)),
                    Slot = r.IsDBNull(2) ? null : r.GetString(2),
                    Rarity = r.IsDBNull(3) ? null : r.GetString(3),
                    Frame = r.IsDBNull(4) ? null : r.GetString(4),
                    BaseTypeId = r.IsDBNull(5) ? null : r.GetString(5),
                    MinTier = r.IsDBNull(6) ? null : r.GetInt32(6),
                    MaxTier = r.IsDBNull(7) ? null : r.GetInt32(7),
                    LevelReq = r.IsDBNull(8) ? null : r.GetInt32(8),
                    PrefixRolls = r.GetInt32(9),
                    SuffixRolls = r.GetInt32(10),
                    TagsJson = r.GetString(11),
                    Enabled = r.GetInt32(12) != 0,
                    Revision = r.GetInt64(13),
                };
            }

            var atoms = new List<ContainerAtomRow>();
            using (var cmd = db.CreateCommand())
            {
                // seq order, always — it is authoring order and it must be stable.
                cmd.CommandText =
                    "SELECT seq, atom_id, overrides_json FROM effect_container_atom " +
                    "WHERE container_id = $id ORDER BY seq;";
                cmd.Parameters.AddWithValue("$id", containerId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    atoms.Add(new ContainerAtomRow(r.GetInt32(0), r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2)));
            }

            var pool = new List<ContainerPoolRow>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT affix_id, weight, group_key FROM effect_container_pool " +
                    "WHERE container_id = $id ORDER BY affix_id;";
                cmd.Parameters.AddWithValue("$id", containerId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    pool.Add(new ContainerPoolRow(r.GetString(0), r.GetInt32(1),
                        r.IsDBNull(2) ? null : r.GetString(2)));
            }

            return head with { Atoms = atoms, Pool = pool };
        }
    }

    /// <summary>Container ids in stable order — E8 hashes these tables.</summary>
    public IReadOnlyList<string> ListContainerIds()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT container_id FROM effect_container ORDER BY container_id;";
            using var r = cmd.ExecuteReader();

            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>
    /// Every container in the store, resolved whole (head + fixed atoms + pool). D3.9's own
    /// whole-corpus nerve-target scan (`EventDeckPreflight.CheckNoNerveTargetInAnyContainer`) is the
    /// first real caller — no "list every container" query existed before it, confirmed by grep: only
    /// <see cref="ListActionContainers"/> (action-scoped) and <see cref="GetContainer"/> (single-row)
    /// did.
    ///
    /// <para>Composes <see cref="ListContainerIds"/> and <see cref="GetContainer"/> rather than a new
    /// bulk `SELECT ... FROM effect_container` — both already lock (`_gate` is a plain CLR monitor,
    /// reentrant per-thread, so nesting is safe) and are already tested; a hand-rolled bulk query here
    /// could drift from what `GetContainer` returns for the same id, and this is an audit/preflight
    /// path, never a hot one, so the extra round trips cost nothing that matters.</para>
    /// </summary>
    public IReadOnlyList<ContainerRow> ListContainers()
    {
        var ids = ListContainerIds();
        var list = new List<ContainerRow>(ids.Count);
        foreach (var id in ids)
        {
            var c = GetContainer(id);
            if (c is not null) list.Add(c);
        }
        return list;
    }

    // ---- affixes (T3.1, affix-schema) ------------------------------------------------------------

    /// <summary>Validate then write one affix, whole — same discipline as <see cref="UpsertContainer"/>:
    /// a bad affix never reaches the tables, and a re-write of identical content is a no-op so a
    /// repeat import does not move the content hash.</summary>
    public AtomRejection UpsertAffix(AffixRow affix, Func<string, AtomRow?> lookupAtom)
    {
        var check = AffixValidator.Validate(affix, lookupAtom, DomainMembers, FamilyVariantHasAnyTierUnlocked);
        if (!check.IsOk) return check;

        // E32 (spec-affix-import-path.md §3.2): an absent class is resolved to its derived value
        // before storage — Class is written as a concrete value always, never null in the tables.
        var resolved = affix.Class is null
            ? affix with { Class = AffixValidator.ResolveClass(affix, lookupAtom) }
            : affix;

        if (SameAffixContent(GetAffix(resolved.AffixId), resolved)) return AtomRejection.Ok;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            WriteAffixUnlocked(db, tx, resolved);
            tx.Commit();
            return AtomRejection.Ok;
        }
    }

    /// <summary>Writes one affix row. <paramref name="affix"/>.Class must already be resolved
    /// (non-null) — every caller runs it through <see cref="AffixValidator.ResolveClass"/> first
    /// (E32, §3.2); the tables never carry a null class.</summary>
    void WriteAffixUnlocked(SqliteConnection db, SqliteTransaction tx, AffixRow affix)
    {
        var affixClass = affix.Class
            ?? throw new InvalidOperationException($"{affix.AffixId}: class must be resolved before writing — caller skipped ResolveClass");

        ExecIn(db, tx, """
            INSERT INTO effect_affix (affix_id, affix_class, revision)
            VALUES ($id, $class, 1)
            ON CONFLICT(affix_id) DO UPDATE SET
              affix_class = excluded.affix_class, revision = effect_affix.revision + 1;
            """,
            ("$id", affix.AffixId), ("$class", AffixClassName(affixClass)));

        ExecIn(db, tx, "DELETE FROM effect_affix_ref WHERE affix_id = $id;", ("$id", affix.AffixId));

        foreach (var r in affix.Refs)
            ExecIn(db, tx, """
                INSERT INTO effect_affix_ref
                  (affix_id, seq, atom_id, slot_name, slot_domain, slot_pick, slot_atom_pattern)
                VALUES ($id, $seq, $atom, $slot, $domain, $pick, $pattern);
                """,
                ("$id", affix.AffixId), ("$seq", r.Seq),
                ("$atom", (object?)r.AtomId ?? DBNull.Value),
                ("$slot", (object?)r.SlotName ?? DBNull.Value),
                ("$domain", (object?)r.SlotDomain ?? DBNull.Value),
                ("$pick", r.SlotPick),
                ("$pattern", (object?)r.SlotAtomPattern ?? DBNull.Value));
    }

    public AffixRow? GetAffix(string affixId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();

            AffixClass affixClass;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT affix_class FROM effect_affix WHERE affix_id = $id;";
                cmd.Parameters.AddWithValue("$id", affixId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                affixClass = ParseAffixClass(r.GetString(0));
            }

            var refs = new List<AffixRefRow>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT seq, atom_id, slot_name, slot_domain, slot_pick, slot_atom_pattern
                    FROM effect_affix_ref WHERE affix_id = $id ORDER BY seq;
                    """;
                cmd.Parameters.AddWithValue("$id", affixId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    refs.Add(new AffixRefRow(
                        r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                        r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5)));
            }

            return new AffixRow(affixId, affixClass, refs);
        }
    }

    /// <summary>Affix ids in stable order — mirrors <see cref="ListContainerIds"/>.</summary>
    public IReadOnlyList<string> ListAffixIds()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT affix_id FROM effect_affix ORDER BY affix_id;";
            using var r = cmd.ExecuteReader();

            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>The one real domain a slot may name today — `element`, the six concrete elements
    /// (`ElementRoster.Concrete`, `omni` excluded per `ActorElementTypes.cs:84`'s own refusal). An
    /// unknown domain name returns empty, which <see cref="AffixValidator"/> already turns into a
    /// clear rejection rather than a silent pass.</summary>
    static IReadOnlyList<string> DomainMembers(string domain) =>
        domain == "element" ? ElementRoster.Concrete.Select(e => e.ToElementId()).ToList() : Array.Empty<string>();

    /// <summary>Whether the loaded atom catalog has at least one row for `family+variant`, at any
    /// tier — a slot's pattern names a family/variant, never a concrete tier (tier resolves later,
    /// module 2).</summary>
    bool FamilyVariantHasAnyTierUnlocked(string family, string variant) =>
        ListAtoms().Any(a => a.FamilyId == family && a.Variant == variant);

    static bool SameAffixContent(AffixRow? stored, AffixRow incoming) =>
        stored is not null && stored.Class == incoming.Class && stored.Refs.SequenceEqual(incoming.Refs);

    static string AffixClassName(AffixClass c) => c switch
    {
        AffixClass.Prefix => "prefix",
        AffixClass.Suffix => "suffix",
        AffixClass.Mixed => "mixed",
        _ => "prefix",
    };

    static AffixClass ParseAffixClass(string name) => name switch
    {
        "prefix" => AffixClass.Prefix,
        "suffix" => AffixClass.Suffix,
        "mixed" => AffixClass.Mixed,
        _ => AffixClass.Prefix,
    };

    static void ExecIn(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    static string KindName(ContainerKind kind) => ContainerRow.PrefixOf(kind);

    // achievement-title T3: every stored prefix resolves; unknown stored kinds throw
    // naming the kind (absolute bounds throw, never a silent Item default — the old
    // `_ => ContainerKind.Item` misread enemy/gem/charm/combo/consumable rows as items).
    static ContainerKind ParseKind(string name) => name switch
    {
        "item" => ContainerKind.Item,
        "trait" => ContainerKind.Trait,
        "skill" => ContainerKind.Skill,
        "species-passive" => ContainerKind.SpeciesPassive,
        "patron" => ContainerKind.Patron,
        "world-buff" => ContainerKind.WorldBuff,
        "enemy" => ContainerKind.Enemy,
        "gem" => ContainerKind.Gem,
        "charm" => ContainerKind.Charm,
        "combo" => ContainerKind.Combo,
        "consumable" => ContainerKind.Consumable,
        "empire-title" => ContainerKind.EmpireTitle,
        "actor-title" => ContainerKind.ActorTitle,
        // relic-item-kind (empire-development Task 1.3a): the MintRelic host reads the kind back to
        // enforce Relic-only minting, so a stored "relic" must round-trip to Relic.
        "relic" => ContainerKind.Relic,
        // species-progression SP3.2: SpeciesLayerProjector.ToContainer's persisted output.
        "species-progression" => ContainerKind.SpeciesProgression,
        _ => throw new InvalidOperationException($"effect_container: unknown kind '{name}'"),
    };
}
