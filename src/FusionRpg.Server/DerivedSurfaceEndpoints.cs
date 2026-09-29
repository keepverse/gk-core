using FusionRpg.Core.ActorSurface;

namespace FusionRpg.Server;

/// <summary>Cooked tabbed derived surface — <c>GET /api/catalogs/derived-surface</c>.</summary>
public static class DerivedSurfaceEndpoints
{
    public static void MapDerivedSurface(this WebApplication app)
    {
        app.MapGet("/api/catalogs/derived-surface", (string? lang, string? side) =>
            Results.Ok(DerivedSurfaceCook.Build(lang, side)));
    }
}
