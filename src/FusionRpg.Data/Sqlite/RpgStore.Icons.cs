using FusionRpg.Contracts;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed class TypeIconLayerDto
{
    public string Name { get; set; } = "";
    public string? Source { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Url { get; set; } = "";
}

public sealed class TypeIconDumpDto
{
    public string Side { get; set; } = "";
    public int TypeId { get; set; }
    public string? TypeName { get; set; }
    public string? DisplayName { get; set; }
    public List<TypeIconLayerDto> Layers { get; set; } = new();
    public string? ComposedUrl { get; set; }
}

public sealed record RiftAssetSourceRow(
    string AssetId,
    string Role,
    string SourceKind,
    string? Side,
    int? TypeId,
    string? Layer,
    string? SourceUri,
    string? Sha256,
    string CapturedUtc,
    int Revision);

public sealed partial class RpgStore
{
    public bool UpsertRiftAssetSource(
        string assetId, string role, string sourceKind, string? side, int? typeId, string? layer,
        string? sourceUri, byte[]? sourceBytes, int revision = 1, string? capturedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("assetId and role are required");
        if (sourceKind is not ("pvz_dump" or "generated" or "licensed"))
            throw new ArgumentException("unsupported Rift asset source kind", nameof(sourceKind));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        side = string.IsNullOrWhiteSpace(side) ? null : NormSide(side);
        if (sourceKind == "pvz_dump" && (side is null || typeId is null || string.IsNullOrWhiteSpace(layer)))
            throw new ArgumentException("pvz_dump requires side, typeId, and layer");

        var hash = sourceBytes is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant()
            : null;
        var captured = capturedUtc ?? ServerClock.UtcNowDateTime.ToString("o");
        lock (_gate)
        {
            using var db = OpenMediaUnlocked();
            if (sourceKind == "pvz_dump")
            {
                using var exists = db.CreateCommand();
                exists.CommandText = "SELECT 1 FROM type_icon_layers WHERE side=$s AND type_id=$t AND layer=$l;";
                exists.Parameters.AddWithValue("$s", side!);
                exists.Parameters.AddWithValue("$t", typeId!.Value);
                exists.Parameters.AddWithValue("$l", layer!.Trim());
                if (exists.ExecuteScalar() is null) return false;
            }

            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rift_asset_sources(
                  asset_id, role, source_kind, side, type_id, layer, source_uri, sha256, captured_utc, revision)
                VALUES($a,$r,$k,$s,$t,$l,$u,$h,$c,$v)
                ON CONFLICT(asset_id, role, revision) DO UPDATE SET
                  source_kind=excluded.source_kind,
                  side=excluded.side,
                  type_id=excluded.type_id,
                  layer=excluded.layer,
                  source_uri=excluded.source_uri,
                  sha256=excluded.sha256,
                  captured_utc=excluded.captured_utc;
                """;
            cmd.Parameters.AddWithValue("$a", assetId.Trim());
            cmd.Parameters.AddWithValue("$r", role.Trim());
            cmd.Parameters.AddWithValue("$k", sourceKind);
            cmd.Parameters.AddWithValue("$s", (object?)side ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", (object?)typeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$l", (object?)layer?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", (object?)sourceUri ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$h", (object?)hash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", captured);
            cmd.Parameters.AddWithValue("$v", revision);
            cmd.ExecuteNonQuery();
            return true;
        }
    }

    public RiftAssetSourceRow? GetRiftAssetSource(string assetId, string role, int revision = 1)
    {
        lock (_gate)
        {
            using var db = OpenMediaUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT asset_id, role, source_kind, side, type_id, layer, source_uri, sha256, captured_utc, revision
                FROM rift_asset_sources
                WHERE asset_id=$a AND role=$r AND revision=$v;
                """;
            cmd.Parameters.AddWithValue("$a", assetId);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$v", revision);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new RiftAssetSourceRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8), reader.GetInt32(9));
        }
    }

    public bool HasTypeIconDump(string side, int typeId)
    {
        side = NormSide(side);
        lock (_gate)
        {
            using var db = OpenMediaUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM type_icon_layers WHERE side=$s AND type_id=$t LIMIT 1;";
            cmd.Parameters.AddWithValue("$s", side);
            cmd.Parameters.AddWithValue("$t", typeId);
            return cmd.ExecuteScalar() != null;
        }
    }

    public int UpsertTypeIconLayers(
        string side,
        int typeId,
        IReadOnlyList<(string Name, string? Source, int Width, int Height, byte[] Png)> layers)
    {
        side = NormSide(side);
        if (layers.Count == 0) return 0;
        var t = ServerClock.UtcNowDateTime.ToString("o");
        lock (_gate)
        {
            using var db = OpenMediaUnlocked();
            using var tx = db.BeginTransaction();
            var n = 0;
            foreach (var layer in layers)
            {
                if (string.IsNullOrWhiteSpace(layer.Name) || layer.Png is not { Length: >= 8 }) continue;
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO type_icon_layers(side, type_id, layer, source, width, height, png, captured_utc)
                    VALUES ($s,$t,$l,$src,$w,$h,$png,$u)
                    ON CONFLICT(side, type_id, layer) DO UPDATE SET
                      source=excluded.source,
                      width=excluded.width,
                      height=excluded.height,
                      png=excluded.png,
                      captured_utc=excluded.captured_utc;
                    """;
                cmd.Parameters.AddWithValue("$s", side);
                cmd.Parameters.AddWithValue("$t", typeId);
                cmd.Parameters.AddWithValue("$l", layer.Name.Trim());
                cmd.Parameters.AddWithValue("$src", (object?)layer.Source ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$w", layer.Width);
                cmd.Parameters.AddWithValue("$h", layer.Height);
                cmd.Parameters.AddWithValue("$png", layer.Png);
                cmd.Parameters.AddWithValue("$u", t);
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return n;
        }
    }

    public TypeIconDumpDto? GetTypeIconDump(string side, int typeId)
    {
        side = NormSide(side);
        lock (_gate)
        {
            using var media = OpenMediaUnlocked();
            if (!HasDumpUnlocked(media, side, typeId)) return null;
            using var hot = OpenUnlocked();
            return ReadDumpUnlocked(media, hot, side, typeId);
        }
    }

    public List<TypeIconDumpDto> ListTypeIconDumps(string? side = null)
    {
        lock (_gate)
        {
            using var media = OpenMediaUnlocked();
            using var hot = OpenUnlocked();
            using var cmd = media.CreateCommand();
            if (string.IsNullOrWhiteSpace(side))
            {
                cmd.CommandText = "SELECT DISTINCT side, type_id FROM type_icon_layers ORDER BY side, type_id;";
            }
            else
            {
                cmd.CommandText = "SELECT DISTINCT side, type_id FROM type_icon_layers WHERE side=$s ORDER BY type_id;";
                cmd.Parameters.AddWithValue("$s", NormSide(side));
            }
            var keys = new List<(string Side, int TypeId)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    keys.Add((r.GetString(0), r.GetInt32(1)));
            }
            return keys.Select(k => ReadDumpUnlocked(media, hot, k.Side, k.TypeId)!).ToList();
        }
    }

    public byte[]? GetTypeIconLayerPng(string side, int typeId, string layer)
    {
        side = NormSide(side);
        lock (_gate)
        {
            using var db = OpenMediaUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT png FROM type_icon_layers WHERE side=$s AND type_id=$t AND layer=$l;";
            cmd.Parameters.AddWithValue("$s", side);
            cmd.Parameters.AddWithValue("$t", typeId);
            cmd.Parameters.AddWithValue("$l", layer);
            var o = cmd.ExecuteScalar();
            return o is byte[] b ? b : null;
        }
    }

    public byte[]? GetComposedTypeIconPng(string side, int typeId)
    {
        side = NormSide(side);
        lock (_gate)
        {
            using var db = OpenMediaUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT png FROM type_icons WHERE side=$s AND type_id=$t;";
            cmd.Parameters.AddWithValue("$s", side);
            cmd.Parameters.AddWithValue("$t", typeId);
            var o = cmd.ExecuteScalar();
            return o is byte[] b ? b : null;
        }
    }

    public void UpsertComposedTypeIcon(string side, int typeId, byte[] png, string? recipeJson)
    {
        side = NormSide(side);
        lock (_gate)
        {
            using var db = OpenMediaUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO type_icons(side, type_id, png, recipe_json, updated_utc)
                VALUES ($s,$t,$png,$r,$u)
                ON CONFLICT(side, type_id) DO UPDATE SET
                  png=excluded.png,
                  recipe_json=excluded.recipe_json,
                  updated_utc=excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$s", side);
            cmd.Parameters.AddWithValue("$t", typeId);
            cmd.Parameters.AddWithValue("$png", png);
            cmd.Parameters.AddWithValue("$r", (object?)recipeJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", ServerClock.UtcNowDateTime.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    static string NormSide(string side) => side.Trim().ToLowerInvariant();

    static bool HasDumpUnlocked(SqliteConnection media, string side, int typeId)
    {
        using var cmd = media.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM type_icon_layers WHERE side=$s AND type_id=$t LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", side);
        cmd.Parameters.AddWithValue("$t", typeId);
        return cmd.ExecuteScalar() != null;
    }

    TypeIconDumpDto ReadDumpUnlocked(SqliteConnection media, SqliteConnection hot, string side, int typeId)
    {
        var dto = new TypeIconDumpDto { Side = side, TypeId = typeId };
        using (var cmd = media.CreateCommand())
        {
            cmd.CommandText = """
                SELECT layer, source, width, height
                FROM type_icon_layers WHERE side=$s AND type_id=$t ORDER BY layer;
                """;
            cmd.Parameters.AddWithValue("$s", side);
            cmd.Parameters.AddWithValue("$t", typeId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var layer = r.GetString(0);
                dto.Layers.Add(new TypeIconLayerDto
                {
                    Name = layer,
                    Source = r.IsDBNull(1) ? null : r.GetString(1),
                    Width = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    Height = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    Url = $"/api/icons/dump/{side}/{typeId}/layer/{Uri.EscapeDataString(layer)}"
                });
            }
        }

        using (var cmd = hot.CreateCommand())
        {
            cmd.CommandText = "SELECT type_name, display_name FROM types WHERE game=$g AND side=$s AND type=$t;";
            cmd.Parameters.AddWithValue("$g", RpgConstants.GameId);
            cmd.Parameters.AddWithValue("$s", side);
            cmd.Parameters.AddWithValue("$t", typeId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                dto.TypeName = r.IsDBNull(0) ? null : r.GetString(0);
                dto.DisplayName = r.IsDBNull(1) ? null : r.GetString(1);
            }
        }

        if (GetComposedExistsUnlocked(media, side, typeId))
            dto.ComposedUrl = $"/api/icons/{side}/{typeId}.png";

        return dto;
    }

    static bool GetComposedExistsUnlocked(SqliteConnection media, string side, int typeId)
    {
        using var cmd = media.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM type_icons WHERE side=$s AND type_id=$t LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", side);
        cmd.Parameters.AddWithValue("$t", typeId);
        return cmd.ExecuteScalar() != null;
    }
}
