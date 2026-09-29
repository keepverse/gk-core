using System.Text.Json;
using FusionRpg.Contracts;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed class AlmanacTextDumpDto
{
    public string Side { get; set; } = "";
    public int TypeId { get; set; }
    public string? TypeName { get; set; }
    public string? DisplayName { get; set; }
    public Dictionary<string, string?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string>? Sources { get; set; }
    public string CapturedUtc { get; set; } = "";
}

public sealed partial class RpgStore
{
    static readonly JsonSerializerOptions AlmanacJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public bool HasAlmanacTextDump(string side, int typeId)
    {
        side = NormSide(side);
        lock (_gate)
        {
            using var db = OpenMediaUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM type_almanac_dump WHERE side=$s AND type_id=$t LIMIT 1;";
            cmd.Parameters.AddWithValue("$s", side);
            cmd.Parameters.AddWithValue("$t", typeId);
            return cmd.ExecuteScalar() != null;
        }
    }

    public void UpsertAlmanacTextDump(
        string side,
        int typeId,
        Dictionary<string, string?> fields,
        Dictionary<string, string>? sources)
    {
        side = NormSide(side);
        fields = ToIgnoreCaseFields(fields);
        sources = sources == null ? null : ToIgnoreCaseSources(sources);
        var fieldsJson = JsonSerializer.Serialize(fields, AlmanacJson);
        var sourcesJson = sources == null ? null : JsonSerializer.Serialize(sources, AlmanacJson);
        var t = ServerClock.UtcNowDateTime.ToString("o");
        lock (_gate)
        {
            // Media write first — never share a txn with hot.
            using (var media = OpenMediaUnlocked())
            {
                using var cmd = media.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO type_almanac_dump(side, type_id, fields_json, sources_json, captured_utc)
                    VALUES ($s,$t,$f,$src,$u)
                    ON CONFLICT(side, type_id) DO UPDATE SET
                      fields_json=excluded.fields_json,
                      sources_json=excluded.sources_json,
                      captured_utc=excluded.captured_utc;
                    """;
                cmd.Parameters.AddWithValue("$s", side);
                cmd.Parameters.AddWithValue("$t", typeId);
                cmd.Parameters.AddWithValue("$f", fieldsJson);
                cmd.Parameters.AddWithValue("$src", (object?)sourcesJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$u", t);
                cmd.ExecuteNonQuery();
            }

            // Promote title into types catalog on hot (separate connection).
            static string? F(Dictionary<string, string?> map, string key)
            {
                if (!map.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return null;
                return v.Trim();
            }
            var display = F(fields, "name") ?? F(fields, "displayName");
            var typeName = F(fields, "enumName") ?? display;
            if (!string.IsNullOrWhiteSpace(display) || !string.IsNullOrWhiteSpace(typeName))
            {
                using var hot = OpenUnlocked();
                UpsertType(hot, side, typeId, typeName, display,
                    null, null, null, null, null, null, seen: 0, killed: 0, t,
                    TypeNameMode.PreferIncoming);
            }
        }
    }

    public AlmanacTextDumpDto? GetAlmanacTextDump(string side, int typeId)
    {
        side = NormSide(side);
        lock (_gate)
        {
            using var media = OpenMediaUnlocked();
            using var hot = OpenUnlocked();
            return ReadAlmanacDumpUnlocked(media, hot, side, typeId);
        }
    }

    public List<AlmanacTextDumpDto> ListAlmanacTextDumps(string? side = null)
    {
        lock (_gate)
        {
            using var media = OpenMediaUnlocked();
            using var hot = OpenUnlocked();
            using var cmd = media.CreateCommand();
            if (string.IsNullOrWhiteSpace(side))
                cmd.CommandText = "SELECT side, type_id FROM type_almanac_dump ORDER BY side, type_id;";
            else
            {
                cmd.CommandText = "SELECT side, type_id FROM type_almanac_dump WHERE side=$s ORDER BY type_id;";
                cmd.Parameters.AddWithValue("$s", NormSide(side));
            }
            var keys = new List<(string Side, int TypeId)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    keys.Add((r.GetString(0), r.GetInt32(1)));
            }
            return keys.Select(k => ReadAlmanacDumpUnlocked(media, hot, k.Side, k.TypeId)!).Where(x => x != null).ToList()!;
        }
    }

    AlmanacTextDumpDto? ReadAlmanacDumpUnlocked(SqliteConnection media, SqliteConnection hot, string side, int typeId)
    {
        using var cmd = media.CreateCommand();
        cmd.CommandText = """
            SELECT fields_json, sources_json, captured_utc
            FROM type_almanac_dump WHERE side=$s AND type_id=$t;
            """;
        cmd.Parameters.AddWithValue("$s", side);
        cmd.Parameters.AddWithValue("$t", typeId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var fieldsJson = r.GetString(0);
        var sourcesJson = r.IsDBNull(1) ? null : r.GetString(1);
        var captured = r.GetString(2);
        r.Close();

        var rawFields = JsonSerializer.Deserialize<Dictionary<string, string?>>(fieldsJson, AlmanacJson)
                        ?? new Dictionary<string, string?>();
        var rawSources = string.IsNullOrWhiteSpace(sourcesJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(sourcesJson, AlmanacJson);

        var dto = new AlmanacTextDumpDto
        {
            Side = side,
            TypeId = typeId,
            CapturedUtc = captured,
            Fields = ToIgnoreCaseFields(rawFields),
            Sources = rawSources == null ? null : ToIgnoreCaseSources(rawSources)
        };

        // Align with progression promote: name/displayName → DisplayName, enumName → TypeName.
        static string? F(Dictionary<string, string?> map, string key)
        {
            if (!map.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return null;
            return v.Trim();
        }
        dto.DisplayName = F(dto.Fields, "name") ?? F(dto.Fields, "displayName");
        dto.TypeName = F(dto.Fields, "enumName");

        using (var tcmd = hot.CreateCommand())
        {
            tcmd.CommandText = "SELECT type_name, display_name FROM types WHERE game=$g AND side=$s AND type=$t;";
            tcmd.Parameters.AddWithValue("$g", RpgConstants.GameId);
            tcmd.Parameters.AddWithValue("$s", side);
            tcmd.Parameters.AddWithValue("$t", typeId);
            using var tr = tcmd.ExecuteReader();
            if (tr.Read())
            {
                dto.TypeName ??= tr.IsDBNull(0) ? null : tr.GetString(0);
                dto.DisplayName ??= tr.IsDBNull(1) ? null : tr.GetString(1);
            }
        }

        return dto;
    }

    static Dictionary<string, string?> ToIgnoreCaseFields(Dictionary<string, string?> src)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in src)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            if (!d.ContainsKey(kv.Key) || string.IsNullOrWhiteSpace(d[kv.Key]))
                d[kv.Key] = kv.Value;
        }
        return d;
    }

    static Dictionary<string, string> ToIgnoreCaseSources(Dictionary<string, string> src)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in src)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            if (!d.ContainsKey(kv.Key))
                d[kv.Key] = kv.Value;
        }
        return d;
    }
}
