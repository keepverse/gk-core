using System.Text.Json;

namespace FusionRpg.Core.World.Loam;

/// <summary>
/// Wonder existence-cap balance surface (spec-wonder-structure.md §Tunables) — loaded, not
/// hard-coded. See <see cref="World.WonderPolicy.Configure"/> and <see cref="WonderTuningLoader"/>.
///
/// <para>A policy constant, correctly a <c>gk-core/data/tuning/*.json</c> value — one number per scope, not
/// authored per-Wonder-row content. Per-row magnitudes (<c>WonderEffectDef.ValueMilli</c> and friends)
/// are seed content, never entries in this file.</para>
/// </summary>
public sealed record WonderUniqueExistenceCapTuning(long Sector, long Empire);

public sealed record WonderTuning(
    int SchemaVersion,
    int Version,
    WonderUniqueExistenceCapTuning UniqueExistenceCap);

public sealed class WonderTuningRejection : Exception
{
    public WonderTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2) — the host reads
/// <c>data/tuning/loam-relics-wonders.v{n}.json</c> and calls <see cref="Parse"/>.</summary>
public static class WonderTuningLoader
{
    public static WonderTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new WonderTuningRejection("wonder tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new WonderTuningRejection($"wonder tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            var cap = Obj(root, "uniqueExistenceCap", "$");
            var sector = Long(cap, "sector", "uniqueExistenceCap");
            var empire = Long(cap, "empire", "uniqueExistenceCap");
            if (sector < 0)
                throw new WonderTuningRejection(
                    $"wonder tuning: uniqueExistenceCap.sector must be non-negative; got {sector}");
            if (empire < 0)
                throw new WonderTuningRejection(
                    $"wonder tuning: uniqueExistenceCap.empire must be non-negative; got {empire}");

            return new WonderTuning(schemaVersion, version,
                new WonderUniqueExistenceCapTuning(sector, empire));
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new WonderTuningRejection($"wonder tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new WonderTuningRejection($"wonder tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new WonderTuningRejection($"wonder tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}
