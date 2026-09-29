using System.Text.Json;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// Almanac icon dumps: SQLite BLOBs only (type_icon_layers + type_icons).
/// Composed finals use layer <c>image</c> (AlmanacCardUI.image portrait).
/// </summary>
public sealed class TypeIconStore
{
    readonly RpgStore _store;

    public const string PortraitLayer = "image";

    public TypeIconStore(RpgStore store) => _store = store;

    public static bool IsValidSide(string side) =>
        side.Equals("plant", StringComparison.OrdinalIgnoreCase)
        || side.Equals("zombie", StringComparison.OrdinalIgnoreCase);

    public string NormalizeSide(string side) => side.Trim().ToLowerInvariant();

    public bool HasDump(string side, int typeId) => _store.HasTypeIconDump(side, typeId);

    public TypeIconDumpDto? GetDump(string side, int typeId) => _store.GetTypeIconDump(side, typeId);

    public List<TypeIconDumpDto> ListDumps(string? side = null) => _store.ListTypeIconDumps(side);

    public byte[]? GetLayerPng(string side, int typeId, string layer) =>
        _store.GetTypeIconLayerPng(side, typeId, layer);

    public byte[]? GetComposedPng(string side, int typeId)
    {
        var existing = _store.GetComposedTypeIconPng(side, typeId);
        if (existing != null) return existing;
        // Backfill from dump layer "image" for dumps captured before this recipe.
        PromotePortraitFromDump(side, typeId);
        return _store.GetComposedTypeIconPng(side, typeId);
    }

    public bool PromotePortraitFromDump(string side, int typeId)
    {
        var png = _store.GetTypeIconLayerPng(side, typeId, PortraitLayer);
        if (png is not { Length: >= 8 }) return false;
        _store.UpsertComposedTypeIcon(
            side,
            typeId,
            png,
            """{"recipe":"layer:image","note":"AlmanacCardUI.image portrait"}""");
        return true;
    }

    /// <summary>Promote portrait for every dump that has layer image but no composed row yet.</summary>
    public int BackfillPortraitsFromDumps()
    {
        var n = 0;
        foreach (var dump in _store.ListTypeIconDumps(null))
        {
            if (_store.GetComposedTypeIconPng(dump.Side, dump.TypeId) != null) continue;
            if (PromotePortraitFromDump(dump.Side, dump.TypeId)) n++;
        }
        return n;
    }

    public Task<(bool Created, int LayerCount, string Url, bool PortraitSet)> SaveDumpAsync(
        string side,
        int typeId,
        JsonElement body,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsValidSide(side)) throw new ArgumentException("side must be plant or zombie", nameof(side));
        if (typeId < 0) throw new ArgumentOutOfRangeException(nameof(typeId));
        if (!body.TryGetProperty("layers", out var layersEl) || layersEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("layers required");

        var created = !_store.HasTypeIconDump(side, typeId);
        var parsed = new List<(string Name, string? Source, int Width, int Height, byte[] Png)>();
        foreach (var el in layersEl.EnumerateArray())
        {
            var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var b64 = el.TryGetProperty("pngBase64", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(b64)) continue;
            byte[] png;
            try { png = Convert.FromBase64String(b64); }
            catch { continue; }
            if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50) continue;
            if (png.Length > 2_000_000) continue;
            var source = el.TryGetProperty("source", out var s) ? s.GetString() : null;
            var w = el.TryGetProperty("width", out var ww) && ww.TryGetInt32(out var wi) ? wi : 0;
            var h = el.TryGetProperty("height", out var hh) && hh.TryGetInt32(out var hi) ? hi : 0;
            parsed.Add((name.Trim(), source, w, h, png));
        }

        if (parsed.Count == 0) throw new ArgumentException("no valid layers");

        _store.UpsertTypeIconLayers(side, typeId, parsed);

        var sNorm = NormalizeSide(side);
        var portrait = parsed.FirstOrDefault(l =>
            l.Name.Equals(PortraitLayer, StringComparison.OrdinalIgnoreCase));
        var portraitSet = false;
        if (portrait.Png is { Length: >= 8 })
        {
            _store.UpsertComposedTypeIcon(
                side,
                typeId,
                portrait.Png,
                """{"recipe":"layer:image","note":"AlmanacCardUI.image portrait"}""");
            portraitSet = true;
        }

        return Task.FromResult((created, parsed.Count, $"/api/icons/dump/{sNorm}/{typeId}", portraitSet));
    }
}
