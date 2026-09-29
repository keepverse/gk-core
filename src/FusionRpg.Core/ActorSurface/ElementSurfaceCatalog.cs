using System.Text.Json;

namespace FusionRpg.Core.ActorSurface;

public sealed record ElementSurfaceEntry(
    string Id,
    string DisplayName,
    int Ordinal,
    string Color,
    bool PresentationOnly,
    string? HudGlyph);

public sealed record ElementSurfaceCatalog(
    int SchemaVersion,
    int Version,
    IReadOnlyList<ElementSurfaceEntry> Entries);

/// <summary>Pure parser, no file I/O (tunables-ssot.md T7.2).</summary>
public static class ElementSurfaceCatalogLoader
{
    const string Catalog = "element-catalog";

    public static ElementSurfaceCatalog Parse(string json)
    {
        using var doc = ActorSurfaceJson.ParseDocument(json, Catalog);
        var root = doc.RootElement;
        var schemaVersion = ActorSurfaceJson.Int(root, "schemaVersion", "$", Catalog);
        var version = ActorSurfaceJson.Int(root, "version", "$", Catalog);
        var arr = ActorSurfaceJson.Arr(root, "entries", "$", Catalog);

        var entries = new List<ElementSurfaceEntry>(arr.GetArrayLength());
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ActorSurfaceCatalogRejection($"{Catalog}: non-object '$.entries[{i}]'");
            var path = $"entries[{i}]";
            entries.Add(new ElementSurfaceEntry(
                Id: ActorSurfaceJson.Str(el, "id", path, Catalog),
                DisplayName: ActorSurfaceJson.Str(el, "displayName", path, Catalog),
                Ordinal: ActorSurfaceJson.Int(el, "ordinal", path, Catalog),
                Color: ActorSurfaceJson.Str(el, "color", path, Catalog),
                PresentationOnly: ActorSurfaceJson.Bool(el, "presentationOnly", path, Catalog),
                HudGlyph: ActorSurfaceJson.OptionalStr(el, "hudGlyph", path, Catalog)));
            i++;
        }

        return new ElementSurfaceCatalog(schemaVersion, version, entries);
    }
}

public static class ElementSurfaceCatalogHub
{
    static ElementSurfaceCatalog? _catalog;

    public static void Configure(ElementSurfaceCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public static ElementSurfaceCatalog Catalog => _catalog ?? throw new InvalidOperationException(
        "ElementSurfaceCatalogHub.Configure(...) has not run. Hosts read data/tuning/element-catalog.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
