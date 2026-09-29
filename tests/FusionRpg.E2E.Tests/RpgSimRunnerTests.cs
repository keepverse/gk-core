using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Tools.RpgSim;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// rpg-simulator RS2.3 — the runner's own behaviour, against the real server where the server is the
/// thing under test and against a stub only where the <i>client's</i> parsing is.
///
/// <para>The two refusals are the program's safety rule (owner ruling D1 (b), the property
/// <c>SimService.Guard()</c> already enforces server-side at
/// <c>gk-core/src/FusionRpg.Server/SimService.cs:25-30</c>): a scenario must not write into a player's install,
/// and it must not run beside a live game session. Both are proven through the REAL bootstrap
/// (<see cref="RpgApiFactory"/>), because a refusal that only holds on a hand-built host proves nothing
/// about the shipped <c>Program</c>.</para>
/// </summary>
[Collection("e2e")]
public class RpgSimRunnerTests
{
    readonly ITestOutputHelper _out;

    public RpgSimRunnerTests(ITestOutputHelper output) => _out = output;

    static string ScenarioPath => Path.Combine(FindScenariosDir(), "first-session-forward.json");

    [Fact]
    public async Task A_target_that_does_not_report_sim_enabled_is_refused_before_anything_runs()
    {
        // A real server built with FUSIONRPG_SIM unset: /health reports simEnabled:false. It is its own
        // factory (not the shared collection fixture) because the flag is process-wide for the length of
        // one CreateClient() — the pattern SimEffectGateHostTests already uses for the same reason.
        using var factory = new SimOffRpgApiFactory();
        using var client = factory.CreateClient();

        var verdict = await new ScenarioRunner(client, "inproc").RunAsync(ScenarioFile.Read(ScenarioPath));

        Assert.True(verdict.Refused);
        Assert.False(verdict.Ok);
        var failure = Assert.Single(verdict.Failures);
        Assert.Contains("simEnabled:false", failure);
        _out.WriteLine(failure);

        // Nothing was sequenced: a refusal is not a partial run.
        Assert.Empty(verdict.Steps);
        Assert.Empty(verdict.Readings);
        Assert.Null(verdict.Digest);
        Assert.Empty(verdict.Validate());
    }

    [Fact]
    public async Task A_target_with_a_live_injector_connected_is_refused_before_anything_runs()
    {
        using var factory = new RpgApiFactory();
        using var client = factory.CreateClient();

        // The real heartbeat route the injector itself posts (gk-core/src/FusionRpg.Server/Program.cs:1748):
        // liveness is Source == "injector" within 5 s (gk-core/src/FusionRpg.Data/Sqlite/RpgStore.cs:1206-1210).
        var beat = await client.PostAsJsonAsync("/api/heartbeat", new { source = "injector" });
        beat.EnsureSuccessStatusCode();
        using (var health = JsonDocument.Parse(await (await client.GetAsync("health")).Content.ReadAsStringAsync()))
            Assert.True(health.RootElement.GetProperty("injectorConnected").GetBoolean(),
                "the heartbeat must make the host report a live injector, or this test proves nothing");

        var verdict = await new ScenarioRunner(client, "inproc").RunAsync(ScenarioFile.Read(ScenarioPath));

        Assert.True(verdict.Refused);
        var failure = Assert.Single(verdict.Failures);
        Assert.Contains("live injector", failure);
        _out.WriteLine(failure);
        Assert.Empty(verdict.Steps);
    }

    [Fact]
    public async Task A_hub_read_is_refused_by_name_rather_than_silently_skipped()
    {
        // The format allows a hub message as a read-back; this wave ships no SignalR client, so the
        // runner says so instead of pretending the read happened. A named refusal is a finding; a silent
        // skip would be a verdict about a scenario that did not run.
        using var factory = new RpgApiFactory();
        using var client = factory.CreateClient();

        var doc = ScenarioFile.Parse("""
            {
              "id": "hub-read-refusal",
              "title": "hub read refusal",
              "description": "One hub read, which this wave cannot perform.",
              "seed": 1,
              "clock": { "mode": "ambient", "note": "ambient" },
              "steps": [
                { "op": "read.progress", "route": "/hub/rpg", "why": "the message the FE receives" }
              ]
            }
            """);

        Assert.Empty(ScenarioValidator.Validate(doc));

        var verdict = await new ScenarioRunner(client, "inproc").RunAsync(doc);

        Assert.False(verdict.Ok);
        Assert.Contains(verdict.Failures, f => f.Contains("hub reads need the SignalR client"));
        Assert.Contains(verdict.Steps, s => s.Op == "read.progress" && s.Outcome == "failed");
    }

    [Fact]
    public async Task A_call_that_answers_a_non_success_status_fails_with_the_route_and_the_status()
    {
        using var factory = new RpgApiFactory();
        using var client = factory.CreateClient();

        // No player exists: /api/souls/{id} answers 404 for an unknown player. The route is a literal,
        // so the failure under test is the status check and not a missing capture.
        var doc = ScenarioFile.Parse("""
            {
              "id": "missing-subject",
              "title": "missing subject",
              "description": "A read against a player that was never created.",
              "seed": 1,
              "clock": { "mode": "ambient", "note": "ambient" },
              "steps": [
                { "op": "read.souls", "route": "GET /api/souls/999999", "why": "no subject exists" }
              ]
            }
            """);

        var verdict = await new ScenarioRunner(client, "inproc").RunAsync(doc);

        Assert.False(verdict.Ok);
        Assert.Contains(verdict.Failures, f => f.Contains("/api/souls/999999") && f.Contains("404"));
        _out.WriteLine(string.Join("\n", verdict.Failures));
    }

    static string FindScenariosDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "rpg-scenarios");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "fixtures", "rpg-scenarios"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("fixtures/rpg-scenarios");
    }
}

/// <summary>A real <c>Program</c> with <c>FUSIONRPG_SIM</c> unset — the shape of a player's install.
/// Local to this file, the same choice (and the same reason) as <c>SimEffectGateHostTests</c>'s own
/// private factory.</summary>
sealed class SimOffRpgApiFactory : RpgApiFactory
{
    public SimOffRpgApiFactory() : base(simEnabled: false) { }
}
