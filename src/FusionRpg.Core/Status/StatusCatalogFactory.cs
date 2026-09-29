using System.Text.Json;
using FusionRpg.Core.ActorSurface;

namespace FusionRpg.Core.Status;

/// <summary>
/// Builds combat <see cref="StatusCatalog"/> from injected <see cref="StatusSurfaceCatalog"/>
/// (status-ssot.md / tunables T8). <see cref="StatusCatalogBootstrap"/> remains the migration golden.
/// </summary>
public static class StatusCatalogFactory
{
    public static StatusCatalog FromSurface(StatusSurfaceCatalog surface)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        var catalog = new StatusCatalog();
        foreach (var e in surface.Entries)
        {
            var family = string.IsNullOrWhiteSpace(e.Family) ? "overlay" : e.Family!;
            catalog.Register(new StatusDef(
                e.Id,
                e.Kind,
                family,
                e.Categories,
                Array.Empty<string>(),
                e.Stacking,
                e.PayloadKinds,
                Element: null,
                PulseHealsAttacker: e.PulseHealsAttacker));
        }
        return catalog;
    }

    public static StatusCatalog FromJson(string json) =>
        FromSurface(StatusSurfaceCatalogLoader.Parse(json));
}

/// <summary>Host-injected combat catalog. Falls back to Bootstrap until Configure runs.</summary>
public static class StatusCatalogHub
{
    static StatusCatalog? _catalog;

    public static void Configure(StatusCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public static void Clear() => _catalog = null;

    public static bool IsConfigured => _catalog is not null;

    /// <summary>Injected catalog, or Bootstrap default when hosts have not configured yet (tests).</summary>
    public static StatusCatalog Current => _catalog ?? StatusCatalogBootstrap.CreateDefault();
}
