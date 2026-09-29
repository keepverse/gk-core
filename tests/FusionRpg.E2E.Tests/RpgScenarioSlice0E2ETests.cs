using System.Net.Http.Json;
using FusionRpg.Tools.RpgSim;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// rpg-simulator RS1 (slice 0) — scenario <c>first-session-forward</c>, run in-process against the REAL
/// server (<see cref="RpgApiFactory"/>, a <c>WebApplicationFactory&lt;Program&gt;</c>): real DI, real boot
/// seeding, real HTTP, real SQLite. No product code, no new surface.
///
/// <para><b>The shape.</b> The scenario lives in
/// <c>gk-core/tests/fixtures/rpg-scenarios/first-session-forward.json</c> and the runner is
/// <c>gk-core/tools/RpgSim/ScenarioRunner.cs</c> — a tool, not a test fixture, so the same file can be driven
/// against a real server process later (RS2.5). This class is now only the <i>host</i>: it builds the
/// in-process server, hands the runner a client, and reads the verdict back. The claims themselves —
/// including the fabrication line — are declared in the scenario file, where a reviewer reads them
/// beside the routes they are about.</para>
///
/// <para><b>Every verdict is a read-back.</b> The runner drives GET read-backs through the same routes
/// the web FE calls (<c>docs/contributing/live-probe-standard.md</c> §3); a call's response body is
/// captured for sequencing only and is never asserted on. A run failure is reported as a failure with a
/// name, never as a silent skip.</para>
///
/// <para><b>Local-only (approved C3 (c)).</b> Deliberately not wired into <c>ci.yml</c>: the scenario
/// suite joins CI once the shape holds (RS7). The file is executed here by the E2E boundary because the
/// in-process host lives in this project.</para>
/// </summary>
[Collection("e2e")]
public class RpgScenarioSlice0E2ETests
{
    readonly HttpClient _http;
    readonly ITestOutputHelper _out;

    public RpgScenarioSlice0E2ETests(RpgApiFactory factory, ITestOutputHelper output)
    {
        _http = factory.CreateClient();
        _out = output;
    }

    public async Task InitializeAsync() =>
        (await _http.PostAsJsonAsync("/api/test/reset", new { })).EnsureSuccessStatusCode();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task First_session_forward_passes_every_assertion_the_scenario_declares()
    {
        var doc = ScenarioFile.Read(Path.Combine(FindScenariosDir(), "first-session-forward.json"));
        Assert.Equal("first-session-forward", doc.Id);

        // The runner validates the contract before it touches the server; a scenario that could not be
        // read the way it says it should be is a failure, not a best effort.
        Assert.Empty(ScenarioValidator.Validate(doc));

        var verdict = await new ScenarioRunner(_http, "inproc", new RpgApiFactory.ClockControl()).RunAsync(doc);

        _out.WriteLine($"{verdict.ScenarioId}: ok={verdict.Ok} host={verdict.Host} " +
                       $"readings={verdict.Readings.Count} captures={verdict.Captures.Count} digest={verdict.Digest}");
        if (!verdict.Ok) _out.WriteLine(verdict.ToJson());

        Assert.False(verdict.Refused);
        Assert.Empty(verdict.Validate());
        Assert.True(verdict.Ok, "the scenario's declared assertions failed:\n  " + string.Join("\n  ", verdict.Failures));

        // Every step got to run: a skipped step means an earlier call failed and the verdict is about a
        // shorter scenario than the file describes.
        Assert.All(verdict.Steps, s => Assert.NotEqual("skipped", s.Outcome));

        // The read-backs really are read-backs: GETs through FE-facing routes, no fixture route and no
        // POST body among them.
        Assert.NotEmpty(verdict.Readings);
        Assert.All(verdict.Readings, r =>
        {
            Assert.Equal("GET", r.Method);
            Assert.StartsWith("GET /api/", r.Source);
            Assert.DoesNotContain("/api/test/", r.Source);
            Assert.DoesNotContain("/api/sim/", r.Source);
            Assert.NotNull(r.Value);
        });

        // The digest exists and its exclusion list is printed with reasons (the artifact is
        // self-describing; a reader does not have to open the contract to know what was blanked).
        Assert.NotNull(verdict.Digest);
        Assert.NotEmpty(verdict.DigestExclusions);
        Assert.All(verdict.DigestExclusions, e => Assert.True(e.Reason.Length > 40, $"'{e.Field}' needs a reason"));

        // The fabrication line, checked from the verdict rather than from a comment: the squad capture
        // came out of the roster route, and no step failed - and the scenario's own
        // `expect.expedition.squad` (equalSet against that capture) is one of the steps that passed.
        var squad = Assert.Single(verdict.Captures, c => c.Name == "squadIds");
        Assert.StartsWith("GET /api/creatures/", squad.Source);
        Assert.Equal(2, squad.Value!.Value.GetArrayLength());
        Assert.Contains(verdict.Steps, s => s.Op == "expect.expedition.squad" && s.Outcome == "ok");
        Assert.Contains(verdict.Steps, s => s.Op == "expect.expedition.squad.from.roster" && s.Outcome == "ok");
    }

    /// <summary>Mirrors <c>SecondaryEffectE2ETests.FindScenariosDir</c>'s resolution of the shared
    /// fixture tree from the test bin output.</summary>
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
