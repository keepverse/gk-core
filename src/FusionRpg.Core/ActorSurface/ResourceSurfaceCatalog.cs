using System.Text.Json;

namespace FusionRpg.Core.ActorSurface;

public sealed record ResourceSurfaceLabels(string Plant, string Zombie);

public sealed record ResourceSurfaceEntry(
    string Id,
    string Class,
    bool Exhaustion,
    bool ActionCost,
    ResourceSurfaceLabels Labels,
    string Icon,
    string Color,
    string MeterKind);

public sealed record ResourceSurfaceCatalog(
    int SchemaVersion,
    int Version,
    IReadOnlyList<ResourceSurfaceEntry> Entries);

/// <summary>Pure parser, no file I/O (tunables-ssot.md T7.2).</summary>
public static class ResourceSurfaceCatalogLoader
{
    const string Catalog = "resource-catalog";

    public static ResourceSurfaceCatalog Parse(string json)
    {
        using var doc = ActorSurfaceJson.ParseDocument(json, Catalog);
        var root = doc.RootElement;
        var schemaVersion = ActorSurfaceJson.Int(root, "schemaVersion", "$", Catalog);
        var version = ActorSurfaceJson.Int(root, "version", "$", Catalog);
        var arr = ActorSurfaceJson.Arr(root, "entries", "$", Catalog);

        var entries = new List<ResourceSurfaceEntry>(arr.GetArrayLength());
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.entries[{i}]'");
            var path = $"entries[{i}]";
            var labelsEl = ActorSurfaceJson.Obj(el, "labels", path, Catalog);
            entries.Add(new ResourceSurfaceEntry(
                Id: ActorSurfaceJson.Str(el, "id", path, Catalog),
                Class: ActorSurfaceJson.Str(el, "class", path, Catalog),
                Exhaustion: ActorSurfaceJson.Bool(el, "exhaustion", path, Catalog),
                ActionCost: ActorSurfaceJson.Bool(el, "actionCost", path, Catalog),
                Labels: new ResourceSurfaceLabels(
                    Plant: ActorSurfaceJson.Str(labelsEl, "plant", $"{path}.labels", Catalog),
                    Zombie: ActorSurfaceJson.Str(labelsEl, "zombie", $"{path}.labels", Catalog)),
                Icon: ActorSurfaceJson.Str(el, "icon", path, Catalog),
                Color: ActorSurfaceJson.Str(el, "color", path, Catalog),
                MeterKind: ActorSurfaceJson.Str(el, "meterKind", path, Catalog)));
            i++;
        }

        return new ResourceSurfaceCatalog(schemaVersion, version, entries);
    }
}

public static class ResourceSurfaceCatalogHub
{
    static ResourceSurfaceCatalog? _catalog;

    public static void Configure(ResourceSurfaceCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public static ResourceSurfaceCatalog Catalog => _catalog ?? throw new InvalidOperationException(
        "ResourceSurfaceCatalogHub.Configure(...) has not run. Hosts read data/tuning/resource-catalog.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
