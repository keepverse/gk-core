using System.Text.Json;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.ActorSurface;

public sealed record StatusSurfaceEntry(
    string Id,
    StatusKind Kind,
    IReadOnlyList<string> Categories,
    StatusStacking Stacking,
    IReadOnlyList<StatusPayloadKind> PayloadKinds,
    string DisplayName,
    string Reading,
    string HudToken,
    string Color,
    string? Family = null,
    bool PulseHealsAttacker = false);

public sealed record StatusSurfaceCatalog(
    int SchemaVersion,
    int Version,
    IReadOnlyList<StatusSurfaceEntry> Entries);

/// <summary>Pure parser, no file I/O (tunables-ssot.md T7.2).</summary>
public static class StatusSurfaceCatalogLoader
{
    const string Catalog = "status-catalog";

    public static StatusSurfaceCatalog Parse(string json)
    {
        using var doc = ActorSurfaceJson.ParseDocument(json, Catalog);
        var root = doc.RootElement;
        var schemaVersion = ActorSurfaceJson.Int(root, "schemaVersion", "$", Catalog);
        var version = ActorSurfaceJson.Int(root, "version", "$", Catalog);
        var arr = ActorSurfaceJson.Arr(root, "entries", "$", Catalog);

        var entries = new List<StatusSurfaceEntry>(arr.GetArrayLength());
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.entries[{i}]'");
            var path = $"entries[{i}]";
            entries.Add(new StatusSurfaceEntry(
                Id: ActorSurfaceJson.Str(el, "id", path, Catalog),
                Kind: ParseStatusKind(el, path),
                Categories: ActorSurfaceJson.StringArray(el, "categories", path, Catalog),
                Stacking: ActorSurfaceJson.EnumValue<StatusStacking>(el, "stacking", path, Catalog),
                PayloadKinds: ParsePayloadKinds(el, path),
                DisplayName: ActorSurfaceJson.Str(el, "displayName", path, Catalog),
                Reading: ActorSurfaceJson.Str(el, "reading", path, Catalog),
                HudToken: ActorSurfaceJson.Str(el, "hudToken", path, Catalog),
                Color: ActorSurfaceJson.Str(el, "color", path, Catalog),
                Family: OptionalStr(el, "family"),
                PulseHealsAttacker: OptionalBool(el, "pulseHealsAttacker")));
            i++;
        }

        return new StatusSurfaceCatalog(schemaVersion, version, entries);
    }

    static string? OptionalStr(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            return null;
        if (p.ValueKind != JsonValueKind.String)
            throw new ActorSurfaceCatalogRejection($"{Catalog}: non-string '{name}'");
        var s = p.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    static bool OptionalBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            return false;
        if (p.ValueKind == JsonValueKind.True) return true;
        if (p.ValueKind == JsonValueKind.False) return false;
        throw new ActorSurfaceCatalogRejection($"{Catalog}: non-bool '{name}'");
    }

    static StatusKind ParseStatusKind(JsonElement el, string path)
    {
        var raw = ActorSurfaceJson.Str(el, "kind", path, Catalog);
        if (!Enum.TryParse<StatusKind>(raw, ignoreCase: true, out var kind))
            throw new ActorSurfaceCatalogRejection($"{Catalog}: unknown '{path}.kind' value '{raw}'");
        return kind;
    }

    static IReadOnlyList<StatusPayloadKind> ParsePayloadKinds(JsonElement el, string path)
    {
        var arr = ActorSurfaceJson.Arr(el, "payloadKinds", path, Catalog);
        var list = new List<StatusPayloadKind>(arr.GetArrayLength());
        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-string '{path}.payloadKinds[{i}]'");
            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                throw new ActorSurfaceCatalogRejection($"{Catalog}: empty '{path}.payloadKinds[{i}]'");
            if (!Enum.TryParse<StatusPayloadKind>(raw.Trim(), ignoreCase: true, out var kind))
                throw new ActorSurfaceCatalogRejection($"{Catalog}: unknown '{path}.payloadKinds[{i}]' value '{raw}'");
            list.Add(kind);
            i++;
        }
        return list;
    }
}

public static class StatusSurfaceCatalogHub
{
    static StatusSurfaceCatalog? _catalog;
    static Dictionary<string, StatusSurfaceEntry>? _byId;

    public static void Configure(StatusSurfaceCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        var map = new Dictionary<string, StatusSurfaceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Entries)
            map[entry.Id] = entry;
        _byId = map;
    }

    /// <summary>Clears inject (tests / host teardown). Resolve returns designed placeholders while unset.</summary>
    public static void Clear()
    {
        _catalog = null;
        _byId = null;
    }

    public static bool IsConfigured => _catalog is not null;

    public static StatusSurfaceCatalog Catalog => _catalog ?? throw new InvalidOperationException(
        "StatusSurfaceCatalogHub.Configure(...) has not run. Hosts read data/tuning/status-catalog.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>Lookup by status id (ordinal ignore case). False when hub unset or id unknown.</summary>
    public static bool TryGet(string? id, out StatusSurfaceEntry entry)
    {
        entry = null!;
        if (_byId is null || string.IsNullOrWhiteSpace(id))
            return false;
        return _byId.TryGetValue(id.Trim(), out entry!);
    }
}
