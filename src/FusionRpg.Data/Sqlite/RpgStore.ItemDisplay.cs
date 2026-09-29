using FusionRpg.Core.Items.Display;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

public partial class RpgStore
{
    void EnsureItemDisplaySchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            -- item-ideal.md, item-card (module 10): N1's item_display_template, one row per shipped
            -- affix family. A family with atoms and no row here is a MissingDisplayTemplate rejection
            -- at load, never a silent blank -- enforced by the caller, not this schema.
            CREATE TABLE IF NOT EXISTS item_display_template (
              runtime_family TEXT PRIMARY KEY,
              name_key TEXT NOT NULL,
              template TEXT NOT NULL,
              plant_override_key TEXT,
              plant_override_template TEXT,
              group_id TEXT NOT NULL,
              status TEXT NOT NULL
            );
            """);
    }

    /// <summary>Seed `item_display_template` from the parsed rows — idempotent, safe on every boot.
    /// The rows themselves come from `gk-data/packs/fusion/data/seed/items/display-templates/*.json` via
    /// <see cref="DisplayTemplates.Parse"/>, never hand-typed here.</summary>
    public void SeedItemDisplayTemplates(IEnumerable<DisplayTemplateRow> rows)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            foreach (var r in rows)
            {
                ExecIn(db, tx, """
                    INSERT INTO item_display_template
                      (runtime_family, name_key, template, plant_override_key, plant_override_template, group_id, status)
                    VALUES ($family, $nameKey, $template, $plantKey, $plantTemplate, $group, $status)
                    ON CONFLICT(runtime_family) DO UPDATE SET
                      name_key = excluded.name_key, template = excluded.template,
                      plant_override_key = excluded.plant_override_key,
                      plant_override_template = excluded.plant_override_template,
                      group_id = excluded.group_id, status = excluded.status;
                    """,
                    ("$family", r.RuntimeFamily), ("$nameKey", r.NameKey), ("$template", r.Template),
                    ("$plantKey", (object?)r.PlantOverrideKey ?? DBNull.Value),
                    ("$plantTemplate", (object?)r.PlantOverrideTemplate ?? DBNull.Value),
                    ("$group", r.GroupId), ("$status", r.Status));
            }

            tx.Commit();
        }
    }

    public DisplayTemplateRow? GetDisplayTemplate(string runtimeFamily)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT runtime_family, name_key, template, plant_override_key, plant_override_template, group_id, status
                FROM item_display_template WHERE runtime_family = $family;
                """;
            cmd.Parameters.AddWithValue("$family", runtimeFamily);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            return new DisplayTemplateRow(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(5), r.GetString(6));
        }
    }

    public IReadOnlyList<DisplayTemplateRow> ListDisplayTemplates()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT runtime_family, name_key, template, plant_override_key, plant_override_template, group_id, status
                FROM item_display_template ORDER BY runtime_family;
                """;
            using var r = cmd.ExecuteReader();
            var result = new List<DisplayTemplateRow>();
            while (r.Read())
                result.Add(new DisplayTemplateRow(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetString(5), r.GetString(6)));
            return result;
        }
    }
}
