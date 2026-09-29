using System.Text.Json;

namespace FusionRpg.Core.World.LegionCargo;

/// <summary>
/// Legion cargo balance surface (spec-legion-cargo.md §Tunables) — loaded, not hard-coded. See
/// <see cref="ScopedInventoryPolicy.Configure"/> and <see cref="ScopedInventoryTuningLoader"/>.
///
/// <para>Both values are flat per-member defaults, standing in for a real per-species stat that does
/// not exist yet (no seedsmith unit pipeline — spec-legion-cargo.md "Real gap"). A later program adds
/// the per-species generator; the aggregation formula (<c>memberCount × per-unit</c>) does not change
/// when it lands.</para>
/// </summary>
public sealed record ScopedInventoryTuning(
    int SchemaVersion,
    int Version,
    long CargoWeightPerUnit,
    int CargoSlotsPerUnit);

public sealed class ScopedInventoryTuningRejection : Exception
{
    public ScopedInventoryTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2) — the host reads
/// <c>data/tuning/scoped-inventory.v{n}.json</c> and calls <see cref="Parse"/>.</summary>
public static class ScopedInventoryTuningLoader
{
    public static ScopedInventoryTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ScopedInventoryTuningRejection("scoped-inventory tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ScopedInventoryTuningRejection($"scoped-inventory tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            return new ScopedInventoryTuning(
                schemaVersion,
                version,
                CargoWeightPerUnit: Long(root, "cargoWeightPerUnit", "$"),
                CargoSlotsPerUnit: Int(root, "cargoSlotsPerUnit", "$"));
        }
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ScopedInventoryTuningRejection($"scoped-inventory tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new ScopedInventoryTuningRejection($"scoped-inventory tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}
