using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>One row that failed to load, and why. The operator-facing half of whole-row rejection.</summary>
/// <param name="AtomId">The id as authored — it may itself be the thing that is wrong.</param>
public readonly record struct AtomLoadRejection(string AtomId, AtomRejectionReason Reason, string Detail)
{
    public override string ToString() => $"{AtomId}: {Reason} — {Detail}";
}

/// <summary>The outcome of a load: what came back, and what was refused.</summary>
public sealed record AtomLoadResult(
    IReadOnlyList<AtomRow> Rows,
    IReadOnlyList<AtomLoadRejection> Rejected);

/// <summary>
/// <c>effect_atom</c> — the SSOT base effect list — plus <c>content_meta</c>, the one-row table
/// holding <c>catalog_revision</c> (spec-atom-schema.md, E4).
///
/// <para>It lives in this project because <c>guard-dal.ps1</c> forbids SQL outside it. Core sees a
/// loaded, validated catalog and never a connection.</para>
/// </summary>
public sealed partial class RpgStore
{
    // aura-skill T2 (audit D6): resolves a stat.derived row's channel to its registered compose kind,
    // so AtomRowValidator can reject an op that channel never reads (e.g. `increased` on a FlatSum
    // channel) rather than accept a row that binds and silently does nothing forever. Built once per
    // call site, not per row — DerivedStatRegistry.CreateDefault() is a fresh dictionary construction
    // (PvzStatsSheetComposer.cs's own words), cheap but pointless to repeat per atom in a batch.
    static readonly DerivedStatRegistry ComposeKindRegistry = DerivedStatRegistry.CreateDefault();

    internal static DerivedComposeKind? ComposeKindOf(string channel) =>
        ComposeKindRegistry.TryResolveChannel(channel, out var def) ? def.Compose : null;

    /// <summary>Called from EnsureHotSchema so a fresh database has both tables.</summary>
    void EnsureAtomSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS effect_atom (
              atom_id TEXT NOT NULL PRIMARY KEY,
              kind_id TEXT NOT NULL,
              family_id TEXT NOT NULL,
              variant TEXT NOT NULL DEFAULT '',
              tier INTEGER NOT NULL DEFAULT 1,
              name TEXT NOT NULL DEFAULT '',
              when_json TEXT NOT NULL DEFAULT '{}',
              params_json TEXT NOT NULL DEFAULT '{}',
              tags_json TEXT NOT NULL DEFAULT '{}',
              power_json TEXT,
              power_override_json TEXT,
              power_note TEXT,
              icd_key TEXT,
              trigger_id TEXT,
              enabled INTEGER NOT NULL DEFAULT 1,
              revision INTEGER NOT NULL DEFAULT 0
            );
            -- variant is '' and never NULL on purpose: NULL does not compare equal to itself in a
            -- SQLite unique index, so two "no variant" rows would both be accepted.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_effect_atom_family_tier_variant
              ON effect_atom(family_id, tier, variant);
            CREATE INDEX IF NOT EXISTS ix_effect_atom_kind ON effect_atom(kind_id);
            -- The trigger is extracted from when_json into its own column: the bag already keeps a
            -- trigger index and the runner (E15) needs the same shape. Nullable, because permanent
            -- modifiers declare no trigger at all.
            CREATE INDEX IF NOT EXISTS ix_effect_atom_trigger ON effect_atom(trigger_id);

            CREATE TABLE IF NOT EXISTS content_meta (
              id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
              catalog_revision INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO content_meta (id, catalog_revision) VALUES (1, 0);
            """);
    }

    /// <summary>
    /// The monotonic integer E6 reproduces against and E7 keys its bake cache on. E14a bumps it once
    /// per import transaction — not once per row, or a 50-row file would move it 50 times.
    /// </summary>
    public long GetCatalogRevision()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT catalog_revision FROM content_meta WHERE id = 1;";
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt64(v);
        }
    }

    public long BumpCatalogRevision()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE content_meta SET catalog_revision = catalog_revision + 1 WHERE id = 1;
                SELECT catalog_revision FROM content_meta WHERE id = 1;
                """;
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt64(v);
        }
    }

    /// <summary>
    /// Validate then upsert. A row that fails validation never reaches the table — the caller gets
    /// the typed reason rather than a boolean.
    /// </summary>
    public AtomRejection UpsertAtom(AtomRow row)
    {
        var check = AtomRowValidator.Validate(row, CurveInputOf, ComposeKindOf);
        if (!check.IsOk) return check;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            UpsertAtomUnlocked(db, row);
            return AtomRejection.Ok;
        }
    }

    /// <summary>
    /// Import a batch. Each row is judged on its own: <b>one bad row in fifty loads forty-nine</b>,
    /// and the fiftieth comes back in <see cref="AtomLoadResult.Rejected"/> with its reason. E14a
    /// layers all-or-nothing transaction semantics on top when a seed file demands it.
    /// </summary>
    public AtomLoadResult UpsertAtoms(IEnumerable<AtomRow> rows)
    {
        var ok = new List<AtomRow>();
        var bad = new List<AtomLoadRejection>();

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            foreach (var row in rows)
            {
                var check = AtomRowValidator.Validate(row, CurveInputOf, ComposeKindOf);
                if (!check.IsOk)
                {
                    bad.Add(new AtomLoadRejection(row?.AtomId ?? "(null)", check.Reason, check.Detail));
                    continue;
                }
                UpsertAtomUnlocked(db, row, tx);
                ok.Add(row);
            }

            tx.Commit();
        }

        return new AtomLoadResult(ok, bad);
    }

    /// <summary>
    /// Curve id -> the axis it reads, for the validator's D9 check. Core cannot open a connection,
    /// so the rule lives in Core and its one fact comes from here.
    /// </summary>
    CurveInput? CurveInputOf(string curveId) => GetCurve(curveId)?.Input;

    /// <returns>How many rows the write changed — 0 when the row was already identical.</returns>
    int UpsertAtomUnlocked(SqliteConnection db, AtomRow row, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO effect_atom
              (atom_id, kind_id, family_id, variant, tier, name, when_json, params_json, tags_json,
               power_json, power_override_json, power_note, icd_key, trigger_id, enabled, revision)
            VALUES
              ($id, $kind, $family, $variant, $tier, $name, $when, $params, $tags,
               $power, $override, $note, $icd, $trigger, $enabled, 1)
            -- The update is SKIPPED when nothing differs, so `revision` counts how many
            -- times this row CHANGED rather than how many times it was written. It is a
            -- hashed column: bumping it on an identical re-import made a repeat import
            -- look exactly like a content edit (E14a: import twice, hash unchanged).
            -- `IS NOT` is SQLite's null-safe inequality; `<>` would be NULL for NULLs.
            ON CONFLICT(atom_id) DO UPDATE SET
              kind_id = excluded.kind_id, family_id = excluded.family_id,
              variant = excluded.variant, tier = excluded.tier, name = excluded.name,
              when_json = excluded.when_json, params_json = excluded.params_json,
              tags_json = excluded.tags_json, power_json = excluded.power_json,
              power_override_json = excluded.power_override_json, power_note = excluded.power_note,
              icd_key = excluded.icd_key, trigger_id = excluded.trigger_id,
              enabled = excluded.enabled,
              revision = effect_atom.revision + 1
            WHERE effect_atom.kind_id IS NOT excluded.kind_id
              OR effect_atom.family_id IS NOT excluded.family_id
              OR effect_atom.variant IS NOT excluded.variant
              OR effect_atom.tier IS NOT excluded.tier
              OR effect_atom.name IS NOT excluded.name
              OR effect_atom.when_json IS NOT excluded.when_json
              OR effect_atom.params_json IS NOT excluded.params_json
              OR effect_atom.tags_json IS NOT excluded.tags_json
              OR effect_atom.power_json IS NOT excluded.power_json
              OR effect_atom.power_override_json IS NOT excluded.power_override_json
              OR effect_atom.power_note IS NOT excluded.power_note
              OR effect_atom.icd_key IS NOT excluded.icd_key
              OR effect_atom.trigger_id IS NOT excluded.trigger_id
              OR effect_atom.enabled IS NOT excluded.enabled;
            """;
        cmd.Parameters.AddWithValue("$id", row.AtomId);
        cmd.Parameters.AddWithValue("$kind", row.KindId);
        cmd.Parameters.AddWithValue("$family", row.FamilyId);
        cmd.Parameters.AddWithValue("$variant", row.Variant ?? "");
        cmd.Parameters.AddWithValue("$tier", row.Tier);
        cmd.Parameters.AddWithValue("$name", row.Name ?? "");
        cmd.Parameters.AddWithValue("$when", row.WhenJson ?? "{}");
        cmd.Parameters.AddWithValue("$params", row.ParamsJson ?? "{}");
        cmd.Parameters.AddWithValue("$tags", row.TagsJson ?? "{}");
        cmd.Parameters.AddWithValue("$power", (object?)row.PowerJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$override", (object?)row.PowerOverrideJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)row.PowerNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$icd", (object?)row.IcdKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trigger", (object?)ExtractTrigger(row.WhenJson) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", row.Enabled ? 1 : 0);
        return cmd.ExecuteNonQuery();
    }

    public AtomRow? GetAtom(string atomId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = AtomSelect + " WHERE atom_id = $id;";
            cmd.Parameters.AddWithValue("$id", atomId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadAtom(r) : null;
        }
    }

    /// <summary>Every atom, in stable id order — E8 hashes this table, so order must not vary.</summary>
    public IReadOnlyList<AtomRow> ListAtoms(bool enabledOnly = false)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = AtomSelect
                + (enabledOnly ? " WHERE enabled = 1" : "")
                + " ORDER BY atom_id;";
            using var r = cmd.ExecuteReader();

            var list = new List<AtomRow>();
            while (r.Read()) list.Add(ReadAtom(r));
            return list;
        }
    }

    /// <summary>Atoms carrying one trigger — the shape E15's trigger index wants.</summary>
    public IReadOnlyList<AtomRow> ListAtomsByTrigger(string trigger)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = AtomSelect + " WHERE trigger_id = $t ORDER BY atom_id;";
            cmd.Parameters.AddWithValue("$t", trigger);
            using var r = cmd.ExecuteReader();

            var list = new List<AtomRow>();
            while (r.Read()) list.Add(ReadAtom(r));
            return list;
        }
    }

    /// <summary>Atoms sharing one family — T59.4's real backing for the `atomsInFamily` seam
    /// `UniqueContainerLookups`/`ActionCorpusComposer.Compose` both declare (nothing in the codebase
    /// indexed atoms by family before this, confirmed by direct search).</summary>
    public IReadOnlyList<AtomRow> ListAtomsByFamily(string familyId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = AtomSelect + " WHERE family_id = $f ORDER BY atom_id;";
            cmd.Parameters.AddWithValue("$f", familyId);
            using var r = cmd.ExecuteReader();

            var list = new List<AtomRow>();
            while (r.Read()) list.Add(ReadAtom(r));
            return list;
        }
    }

    const string AtomSelect = """
        SELECT atom_id, kind_id, family_id, variant, tier, name, when_json, params_json, tags_json,
               power_json, power_override_json, power_note, icd_key, enabled, revision
        FROM effect_atom
        """;

    static AtomRow ReadAtom(SqliteDataReader r) => new()
    {
        AtomId = r.GetString(0),
        KindId = r.GetString(1),
        FamilyId = r.GetString(2),
        Variant = r.GetString(3),
        Tier = r.GetInt32(4),
        Name = r.GetString(5),
        WhenJson = r.GetString(6),
        ParamsJson = r.GetString(7),
        TagsJson = r.GetString(8),
        PowerJson = r.IsDBNull(9) ? null : r.GetString(9),
        PowerOverrideJson = r.IsDBNull(10) ? null : r.GetString(10),
        PowerNote = r.IsDBNull(11) ? null : r.GetString(11),
        IcdKey = r.IsDBNull(12) ? null : r.GetString(12),
        Enabled = r.GetInt32(13) != 0,
        Revision = r.GetInt64(14),
    };

    /// <summary>
    /// Lift the trigger out of `when_json` into its own indexed column. Returns null when the key is
    /// absent, which is the normal case for a permanent modifier.
    /// </summary>
    static string? ExtractTrigger(string? whenJson)
    {
        if (string.IsNullOrWhiteSpace(whenJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(whenJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("trigger", out var t)
                   && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // the validator already refused this row; do not throw on a read path
        }
    }
}
