using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// DM-F2 (`tasks/debug-mcp-todo.md`): `Program.cs` called `app.MapSimEffect()` OUTSIDE the
/// `if (SimFlags.Enabled)` block mapping `/api/sim` + `/api/test`, so the six `/api/sim/effect/*`
/// routes existed on every server — a live owner run included. Owner ruling 2026-09-22
/// (`tasks/rpg-simulator-decisions.md`, F1 -&gt; (b)): drift, not intent; gate it like its siblings.
///
/// <para>Both halves enter through the REAL bootstrap (`RpgApiFactory : WebApplicationFactory&lt;Program&gt;`,
/// the route-reachability proof `SpecimenLoadoutEndpointsHostTests` and `UnlockTuningActivationTests`
/// already use). A test that built its own minimal host, the way `WorldCommandReasonGateTests` does for
/// its *request-time* gate, would pass unchanged with `Program.cs` still mapping the route
/// unconditionally — it can prove nothing about a registration-time regression.</para>
///
/// <para><b>Why absence is asserted as 405 + an empty route table, not 404.</b> `Program.cs` ends with
/// `MapFallbackToFile("index.html")`, whose catch-all pattern matches every non-dotted path and whose
/// method metadata is GET/HEAD; a POST to a path this host does not serve therefore answers
/// <c>405 Method Not Allowed</c> with <c>Allow: GET,HEAD</c>, exactly like any other absent route
/// (pinned by the control request below). 404 is unreachable on a host with an SPA fallback, so the
/// status check is stated against that control rather than against a number that would only be true on
/// a fallback-less host, and the route table is read directly for the precise claim.</para>
///
/// <para>The class sits in the `"e2e"` collection deliberately: the second half mutates the
/// process-wide `FUSIONRPG_SIM` variable for the length of one `CreateClient()`, and xUnit never runs
/// two classes of one collection in parallel, so no other host can boot inside that window. The
/// previous value is restored in a `finally` — `SimFlags.Enabled` is still read live by `/health`'s
/// decorator, so leaving it cleared would leak into later tests in this collection.</para>
/// </summary>
[Collection("e2e")]
public class SimEffectGateHostTests
{
    const string EffectClearRoute = "/api/sim/effect/clear";
    const string SiblingSimRoute = "/api/sim/board/start";
    const string ControlRoute = "/api/definitely-not-a-route-xyz";

    readonly RpgApiFactory _factory;

    public SimEffectGateHostTests(RpgApiFactory factory) => _factory = factory;

    /// <summary>Gate ON. The shared collection fixture boots with `FUSIONRPG_SIM=1` from its own
    /// constructor, so the registered effect surface answers on the real route.</summary>
    [Fact]
    public async Task With_the_sim_flag_set_the_effect_routes_serve()
    {
        using var http = _factory.CreateClient();

        var res = await http.PostAsJsonAsync(EffectClearRoute, new { });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await res.Content.ReadFromJsonAsync<Dictionary<string, object>>())!.ContainsKey("revision"));
    }

    /// <summary>Gate OFF. A second real Program boot with the flag cleared: the route table, not a
    /// per-request check, is what differs, so the effect surface must be absent exactly like its
    /// sibling. `GET /health` is the control that the host itself booted, and the control POST pins
    /// what "absent" looks like on this host, so the assertion cannot silently pass by comparing
    /// against an SPA-fallback status that means nothing.</summary>
    [Fact]
    public async Task Without_the_sim_flag_the_effect_routes_are_absent()
    {
        var previous = Environment.GetEnvironmentVariable("FUSIONRPG_SIM");
        Environment.SetEnvironmentVariable("FUSIONRPG_SIM", null);
        try
        {
            using var factory = new SimDisabledApiFactory();
            using var http = factory.CreateClient();

            var health = await http.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var absent = (await http.PostAsJsonAsync(ControlRoute, new { })).StatusCode;
            Assert.NotEqual(HttpStatusCode.OK, absent);

            var effect = await http.PostAsJsonAsync(EffectClearRoute, new { });
            Assert.Equal(absent, effect.StatusCode);

            var sibling = await http.PostAsJsonAsync(SiblingSimRoute, new { });
            Assert.Equal(absent, sibling.StatusCode);

            // The precise claim: no `/api/sim/effect/*` pattern is in the booted host's route table.
            var effectPatterns = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText ?? "")
                .Where(p => p.StartsWith("/api/sim/effect", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(effectPatterns);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSIONRPG_SIM", previous);
        }
    }
}

/// <summary>The real `Program` bootstrap with `FUSIONRPG_SIM` unset, seeded with the committed species
/// roster the same way the shared fixture is — the only difference is the one flag under test.</summary>
sealed class SimDisabledApiFactory : RpgApiFactory
{
    public SimDisabledApiFactory() : base(simEnabled: false) { }
}
