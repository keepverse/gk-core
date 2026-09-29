using FusionRpg.Core.ActorSurface;

namespace FusionRpg.Server;

/// <summary>
/// The actor-surface fan-in read — <c>GET /api/catalogs/actor-surface</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the route <c>decisions.md</c>'s <b>Actor-surface catalogs (2026-09-07)</b> row declared and
/// marked "still open": the web FE has requested it since
/// <c>gk-web/web/fusion-rpg-web/src/lib/bus/actorSurface.ts</c> called
/// <c>tryGetJson&lt;ActorSurfaceCatalog&gt;("/api/catalogs/actor-surface")</c>, and no host had ever
/// mapped it. Nothing failed loudly, which is the whole hazard:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A deployed Server has a SPA, so <c>Program.cs</c>'s <c>MapFallbackToFile("index.html")</c> answers
/// the unmapped path with <c>200 text/html</c>. <c>tryGetJson</c> sees <c>r.ok</c>, calls
/// <c>r.json()</c> on HTML, throws, and <c>fetchActorSurfaceCatalog</c>'s catch returns the fixture.
/// </description></item>
/// <item><description>
/// A Server with no published SPA answers <c>404</c>, <c>tryGetJson</c> returns <c>null</c>, and the
/// caller's <c>?? actorSurfaceFixture()</c> returns the fixture instead.
/// </description></item>
/// </list>
/// <para>
/// Both land on the same fixture, so the client was green for the route's entire missing life. The
/// fixture has no <c>hudPresentation</c> field, so the actor HUD's glyph sizing computed to 0 and
/// skipped every glyph — a missing route read as "presentation tuned to nothing".
/// </para>
/// <para>
/// <b>Pure read.</b> The handler touches no store, takes no request, and has no side effect: it
/// projects the <c>gk-core/data/tuning</c> catalogs <c>Program.cs</c> already injected into the Core hubs at
/// boot into <see cref="ActorSurfaceCatalogDto"/>. The response is therefore a function of process
/// state alone, which is what makes it cacheable later without changing its meaning — do not add a
/// cache here without also deciding what re-configuration would mean (there is no reload path today).
/// </para>
/// <para>
/// Deliberately its own route rather than a field folded into <c>/api/catalogs/derived-surface</c>:
/// that response is a shipped, versioned contract (<c>DerivedSurfaceDto</c>,
/// <c>SchemaVersion 2</c>, per-actor tab/rail cook) and a fan-in catalog is a different shape with a
/// different consumer. Adding a field there would perturb a live contract to serve a caller that
/// already asks for this one.
/// </para>
/// </remarks>
public static class ActorSurfaceEndpoints
{
    public static void MapActorSurface(this WebApplication app)
    {
        // No query parameters: the FE requests the bare path, and the DTO's only dimension (which
        // catalog set) is fixed at boot by ConfigureAll. Inventing a lang/side pair here would copy
        // DerivedSurfaceCook's shape onto a fan-in that is already localized by its own catalogs.
        app.MapGet("/api/catalogs/actor-surface", () => Results.Ok(ActorSurfaceCatalogHub.BuildDto()));
    }
}
