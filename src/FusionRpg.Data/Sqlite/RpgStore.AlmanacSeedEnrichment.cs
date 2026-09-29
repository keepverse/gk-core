using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed class AlmanacSeedEnrichmentDto
{
    public string[]? Qualities { get; set; }
    public string? UnlockCondition { get; set; }
    public string? TypeClass { get; set; }
    public string? WeaknessesText { get; set; }
    public string? DamageVsText { get; set; }
    public string? Description { get; set; }
    public string Source { get; set; } = "";
}

public sealed class AlmanacEnrichmentImportRow
{
    public string Name { get; set; } = "";
    public string Side { get; set; } = "";
    public string[]? Qualities { get; set; }
    public string? Unlock { get; set; }
    public string? TypeClass { get; set; }
    public string? Weaknesses { get; set; }
    public string? DamageVs { get; set; }
    public string? Description { get; set; }
}

public sealed class AlmanacEnrichmentImportSummary
{
    public int Matched { get; set; }
    public List<string> Unmatched { get; set; } = new();
}

public sealed partial class RpgStore
{
    public AlmanacEnrichmentImportSummary ImportAlmanacEnrichment(IEnumerable<AlmanacEnrichmentImportRow> rows, string source)
    {
        lock (_gate)
        {
            using var hot = OpenUnlocked();

            // Build a normalized-name → (side, type_id) lookup from the current almanac_seed corpus.
            var byNormName = new Dictionary<(string Side, string NormName), int>();
            using (var cmd = hot.CreateCommand())
            {
                cmd.CommandText = "SELECT side, type_id, type_name, display_name FROM almanac_seed;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var side = r.GetString(0);
                    var typeId = r.GetInt32(1);
                    var typeName = r.IsDBNull(2) ? null : r.GetString(2);
                    var displayName = r.IsDBNull(3) ? null : r.GetString(3);
                    if (!string.IsNullOrWhiteSpace(typeName))
                        byNormName.TryAdd((side, NormalizeName(typeName)), typeId);
                    if (!string.IsNullOrWhiteSpace(displayName))
                        byNormName.TryAdd((side, NormalizeName(displayName)), typeId);
                }
            }

            var summary = new AlmanacEnrichmentImportSummary();
            var nowUtc = ServerClock.UtcNowDateTime.ToString("o");

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Side))
                {
                    summary.Unmatched.Add(row.Name ?? "");
                    continue;
                }
                var side = NormSide(row.Side);
                if (!byNormName.TryGetValue((side, NormalizeName(row.Name)), out var typeId))
                {
                    summary.Unmatched.Add(row.Name);
                    continue;
                }

                using var cmd = hot.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO almanac_seed_enrichment(
                      side, type_id, qualities_json, unlock_condition, type_class,
                      weaknesses_text, damage_vs_text, description_text, source, matched_by, imported_utc)
                    VALUES($s,$t,$q,$u,$tc,$w,$dv,$desc,$src,'name',$iu)
                    ON CONFLICT(side, type_id) DO UPDATE SET
                      qualities_json=excluded.qualities_json, unlock_condition=excluded.unlock_condition,
                      type_class=excluded.type_class, weaknesses_text=excluded.weaknesses_text,
                      damage_vs_text=excluded.damage_vs_text, description_text=excluded.description_text,
                      source=excluded.source, matched_by=excluded.matched_by, imported_utc=excluded.imported_utc;
                    """;
                cmd.Parameters.AddWithValue("$s", side);
                cmd.Parameters.AddWithValue("$t", typeId);
                cmd.Parameters.AddWithValue("$q", (object?)(row.Qualities is { Length: > 0 } q
                    ? System.Text.Json.JsonSerializer.Serialize(q) : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$u", (object?)row.Unlock ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tc", (object?)row.TypeClass ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$w", (object?)row.Weaknesses ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dv", (object?)row.DamageVs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$desc", (object?)row.Description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$src", source);
                cmd.Parameters.AddWithValue("$iu", nowUtc);
                cmd.ExecuteNonQuery();
                summary.Matched++;
            }

            return summary;
        }
    }

    static string NormalizeName(string s)
    {
        var chars = s.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    AlmanacSeedEnrichmentDto? ReadAlmanacSeedEnrichmentUnlocked(SqliteConnection hot, string side, int typeId)
    {
        using var cmd = hot.CreateCommand();
        cmd.CommandText = """
            SELECT qualities_json, unlock_condition, type_class, weaknesses_text, damage_vs_text, description_text, source
            FROM almanac_seed_enrichment WHERE side=$s AND type_id=$t;
            """;
        cmd.Parameters.AddWithValue("$s", side);
        cmd.Parameters.AddWithValue("$t", typeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        string[]? qualities = null;
        if (!r.IsDBNull(0))
        {
            try { qualities = System.Text.Json.JsonSerializer.Deserialize<string[]>(r.GetString(0)); }
            catch { qualities = null; }
        }

        return new AlmanacSeedEnrichmentDto
        {
            Qualities = qualities,
            UnlockCondition = r.IsDBNull(1) ? null : r.GetString(1),
            TypeClass = r.IsDBNull(2) ? null : r.GetString(2),
            WeaknessesText = r.IsDBNull(3) ? null : r.GetString(3),
            DamageVsText = r.IsDBNull(4) ? null : r.GetString(4),
            Description = r.IsDBNull(5) ? null : r.GetString(5),
            Source = r.GetString(6)
        };
    }
}
