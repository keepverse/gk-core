using System.Text.Json;

namespace FusionRpg.Core.Items;

/// <summary>Items balance surface (tunables-ssot.md T1) — <see cref="ItemNameComposer.RareNameThreshold"/>
/// once lived as a bare const in <c>ItemNameComposer.cs</c>, <see cref="RoleFamilyTable.DefaultMaxTier"/>
/// as one in <c>RoleFamilyTable.cs</c>; both are balance-surface numbers a tuning pass would change, so
/// both now read through <see cref="ItemsTuningHub"/> instead.</summary>
public sealed record ItemsTuning(
    int SchemaVersion, int Version, int RareNameThreshold, int DefaultMaxTier);

public sealed class ItemsTuningRejection : Exception
{
    public ItemsTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class ItemsTuningLoader
{
    public static ItemsTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ItemsTuningRejection("items tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ItemsTuningRejection($"items tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            return new ItemsTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                RareNameThreshold: Int(root, "rareNameThreshold"),
                DefaultMaxTier: Int(root, "defaultMaxTier"));
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ItemsTuningRejection($"items tuning: missing or non-integer '{key}'");
        return v;
    }
}

/// <summary>No built-in default — hosts read <c>data/tuning/items.v{n}.json</c> and call
/// <see cref="Configure"/> once at startup (tunables-ssot.md §7.2), same as every other TuningHub.</summary>
public static class ItemsTuningHub
{
    static ItemsTuning? _tuning;

    public static void Configure(ItemsTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static ItemsTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ItemsTuningHub.Configure(...) has not run. Hosts read data/tuning/items.v{n}.json " +
        "(tunables-ssot.md T1) — there is no built-in default to fall back to.");
}
