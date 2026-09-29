using System.Text.Json;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.ActorSurface;

public sealed record AptitudeSurfaceEntry(
    string Id,
    Posture Posture,
    int Ordinal,
    string DisplayName,
    string Role,
    string Reading,
    string? Icon);

public sealed record AptitudeSurfaceCatalog(
    int SchemaVersion,
    int Version,
    IReadOnlyList<AptitudeSurfaceEntry> Entries);

/// <summary>Pure parser, no file I/O (tunables-ssot.md T7.2).</summary>
public static class AptitudeSurfaceCatalogLoader
{
    const string Catalog = "aptitude-catalog";

    public static AptitudeSurfaceCatalog Parse(string json)
    {
        using var doc = ActorSurfaceJson.ParseDocument(json, Catalog);
        var root = doc.RootElement;
        var schemaVersion = ActorSurfaceJson.Int(root, "schemaVersion", "$", Catalog);
        var version = ActorSurfaceJson.Int(root, "version", "$", Catalog);
        var arr = ActorSurfaceJson.Arr(root, "entries", "$", Catalog);

        var entries = new List<AptitudeSurfaceEntry>(arr.GetArrayLength());
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.entries[{i}]'");
            var path = $"entries[{i}]";
            entries.Add(new AptitudeSurfaceEntry(
                Id: ActorSurfaceJson.Str(el, "id", path, Catalog),
                Posture: ActorSurfaceJson.EnumValue<Posture>(el, "posture", path, Catalog),
                Ordinal: ActorSurfaceJson.Int(el, "ordinal", path, Catalog),
                DisplayName: ActorSurfaceJson.Str(el, "displayName", path, Catalog),
                Role: ActorSurfaceJson.Str(el, "role", path, Catalog),
                Reading: ActorSurfaceJson.Str(el, "reading", path, Catalog),
                Icon: ActorSurfaceJson.OptionalStr(el, "icon", path, Catalog)));
            i++;
        }

        return new AptitudeSurfaceCatalog(schemaVersion, version, entries);
    }
}

public static class AptitudeSurfaceCatalogHub
{
    static AptitudeSurfaceCatalog? _catalog;

    public static void Configure(AptitudeSurfaceCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public static AptitudeSurfaceCatalog Catalog => _catalog ?? throw new InvalidOperationException(
        "AptitudeSurfaceCatalogHub.Configure(...) has not run. Hosts read data/tuning/aptitude-catalog.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
