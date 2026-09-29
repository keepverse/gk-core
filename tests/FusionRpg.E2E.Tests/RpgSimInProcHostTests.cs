using System.Net;
using System.Text;
using System.Text.Json;
using FusionRpg.Tools.RpgSim;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// rpg-simulator RS2.4 — the default in-process host: the same scenario file, on
/// <c>WebApplicationFactory&lt;Program&gt;</c>, twice, on two fresh data sources.
///
/// <para><b>Why this is where the in-process host lives.</b> The factory that boots the real
/// <c>Program</c> is <see cref="RpgApiFactory"/>, in this test project. A tool may not reference a test
/// project, so the reference direction is this one: the host supplies the <c>HttpClient</c> and the tool
/// supplies the runner. That is the plan's own §7 risk 2 fallback ("the runner then hosts from the E2E
/// assembly and the CLI is a front end") taken deliberately rather than stumbled into; the CLI says so
/// by name (RS-F6).</para>
///
/// <para><b>"Settled" is polled.</b> Both ends of a run go through <see cref="SimSettler"/> — the
/// server's ingest writer and boot catch-up tick on their own, and a reading taken mid-flight is a
/// reading of a race. The verdict prints the settle report, and a run that could not settle fails
/// rather than reporting a number.</para>
/// </summary>
[Collection("e2e")]
public class RpgSimInProcHostTests
{
    readonly ITestOutputHelper _out;

    public RpgSimInProcHostTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Two_consecutive_runs_on_fresh_hosts_report_the_readings_the_scenario_declares_stable()
    {
        var doc = ScenarioFile.Read(Path.Combine(FindScenariosDir(), "first-session-forward.json"));

        var first = await RunOnFreshHost(doc);
        var second = await RunOnFreshHost(doc);

        Assert.True(first.Settle!.Settled, first.Settle.Reason);
        Assert.True(second.Settle!.Settled, second.Settle.Reason);
        _out.WriteLine($"settle: run1 polls={first.Settle.Polls} {first.Settle.ElapsedMs}ms, " +
                       $"run2 polls={second.Settle.Polls} {second.Settle.ElapsedMs}ms");

        // Both runs are the SAME scenario on the SAME host shape, so both must conclude the same thing
        // about it before the digest question is even asked.
        Assert.True(first.Ok, string.Join("\n", first.Failures));
        Assert.True(second.Ok, string.Join("\n", second.Failures));

        // The declared digest: the readings this scenario claims are run-stable.
        Assert.NotNull(first.Digest);
        Assert.NotNull(second.Digest);
        _out.WriteLine($"declared digest: run1={first.Digest} run2={second.Digest}");
        Assert.Equal(first.Digest, second.Digest);

        // The falsifier over EVERY reading, reported rather than smoothed (readback-verdict.md section 4).
        // This is the measurement that keeps the declared digest honest: whatever moves here is either a
        // determinism break or a field the exclusion list has not named yet, and either way it is a
        // finding, never a number to be averaged away.
        var all = ReadingDigest.CompareVerdicts(first, second);
        _out.WriteLine("whole-reading comparison: " + all.Report());

        // The reading set itself must be the same shape in both runs, or "the digest matched" would be a
        // statement about two different scenarios.
        Assert.Equal(
            first.Readings.Select(r => r.Name).OrderBy(n => n),
            second.Readings.Select(r => r.Name).OrderBy(n => n));
        Assert.Equal(first.Readings.Count, second.Readings.Count);
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public async Task RS_F27_measures_progression_ledger_non_vacuity_across_real_runs()
    {
        var doc = ScenarioFile.Read(Path.Combine(FindScenariosDir(), "first-session-forward.json"));
        // The default keeps this focused check short; the report command widens it to the measured sample.
        var sampleCount = int.TryParse(Environment.GetEnvironmentVariable("FUSIONRPG_RSF27_SAMPLES"),
            out var configured) && configured > 0
            ? configured
            : 3;
        var samples = new List<(string Outcomes, int LedgerLength)>(sampleCount);

        for (var i = 0; i < sampleCount; i++)
        {
            var verdict = await RunOnFreshHost(doc);
            Assert.True(verdict.Ok, $"sample {i + 1} failed:\n  {string.Join("\n  ", verdict.Failures)}");

            var runs = Reading(verdict, "read.runs");
            var ledger = Reading(verdict, "read.progression.ledger");
            var outcomes = runs.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("result").GetString())
                .Where(result => !string.IsNullOrWhiteSpace(result))
                .Select(result => result!)
                .ToArray();
            Assert.NotEmpty(outcomes);
            Assert.All(outcomes, result => Assert.Contains(result, new[] { "victory", "defeat", "stalemate" }));
            var ledgerLength = ledger.GetProperty("items").GetArrayLength();
            var ledgerRows = string.Join(',', ledger.GetProperty("items").EnumerateArray()
                .Select(item => $"{item.GetProperty("kind").GetString()}:{item.GetProperty("reason").GetString()}"));
            var hasPlayerDefeatAward = ledger.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "player" &&
                             item.GetProperty("reason").GetString() == "defeat");
            samples.Add((string.Join(',', outcomes), ledgerLength));

            // The floor is conditional on a REAL read-back outcome, not a random notEmpty guard:
            // RpgXpAwardMap awards player XP for a defeat, while victory and stalemate are legal
            // empty outcomes for this web-mode scenario. If a sampled run actually lost, its
            // progression ledger must contain the exact player/defeat award that the map promises.
            if (outcomes.Contains("defeat", StringComparer.Ordinal))
                Assert.True(hasPlayerDefeatAward,
                    $"sample {i + 1} read a defeat from {runs.GetProperty("items")[0].GetProperty("result")} " +
                    $"but read no player/defeat progression-ledger row from {ledger.GetProperty("items")}");

            _out.WriteLine(
                $"RS-F27 sample {i + 1}/{sampleCount}: outcomes=[{string.Join(',', outcomes)}] " +
                $"read.progression.ledger.length={ledgerLength} rows=[{ledgerRows}] " +
                $"runs.source={ReadingSource(verdict, "read.runs")} " +
                $"ledger.source={ReadingSource(verdict, "read.progression.ledger")}");
        }

        var distribution = samples
            .SelectMany(sample => sample.Outcomes.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(outcome => outcome, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}");
        _out.WriteLine($"RS-F27 samples={sampleCount}; outcome distribution: {string.Join(", ", distribution)}; " +
                        $"ledger lengths: {string.Join(", ", samples.Select(sample => sample.LedgerLength))}");
    }

    [Fact]
    public async Task A_host_that_cannot_settle_is_a_failure_not_a_verdict()
    {
        // The settler is a client-side predicate over /health, so its timeout path is proven against a
        // stub that never goes quiet. Nothing about the server is fabricated here: what is under test is
        // the client's refusal to call "I could not tell" a settled host.
        using var http = new HttpClient(new NeverSettles())
        {
            BaseAddress = new Uri("http://stub.invalid/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        var report = await SimSettler.WaitAsync(http, timeout: TimeSpan.FromMilliseconds(400),
            interval: TimeSpan.FromMilliseconds(50));

        Assert.False(report.Settled);
        Assert.True(report.Polls >= 2, $"expected several polls, got {report.Polls}");
        Assert.Contains("did not settle within", report.Reason);
        _out.WriteLine(report.Reason);
    }

    [Fact]
    public async Task A_host_that_dropped_ingest_events_is_refused_immediately()
    {
        using var http = new HttpClient(new DroppedEvents())
        {
            BaseAddress = new Uri("http://stub.invalid/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        var report = await SimSettler.WaitAsync(http, timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(10));

        Assert.False(report.Settled);
        Assert.Contains("dropped", report.Reason);
        _out.WriteLine(report.Reason);
    }

    static JsonElement Reading(ScenarioVerdict verdict, string name) =>
        verdict.Readings.Single(reading => reading.Name == name).Value
        ?? throw new InvalidOperationException($"reading '{name}' has no value");

    static string ReadingSource(ScenarioVerdict verdict, string name) =>
        verdict.Readings.Single(reading => reading.Name == name).Source;

    /// <summary>One run of the corpus on its own fresh host. The host is disposed before the next is
    /// built: the data source is per-fixture-instance (a shared-memory database name), so "fresh" is real
    /// rather than a reset, and disposing the keeper IS the cleanup — nothing on disk to leak.</summary>
    async Task<ScenarioVerdict> RunOnFreshHost(ScenarioDocument doc)
    {
        using var factory = new RpgApiFactory();
        using var client = factory.CreateClient();
        var verdict = await new ScenarioRunner(client, $"inproc(dataSource={factory.DataSource})",
                new RpgApiFactory.ClockControl())
            .RunAsync(doc);
        _out.WriteLine($"{verdict.ScenarioId}: ok={verdict.Ok} readings={verdict.Readings.Count} " +
                       $"captures={verdict.Captures.Count} digest={verdict.Digest}");
        return verdict;
    }

    sealed class NeverSettles : HttpMessageHandler
    {
        int _queued;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Json($$"""
                { "ok": true, "simEnabled": true, "injectorConnected": false,
                  "ingestQueued": {{++_queued}}, "ingestDroppedEvents": 0 }
                """));
    }

    sealed class DroppedEvents : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Json("""
                { "ok": true, "simEnabled": true, "injectorConnected": false,
                  "ingestQueued": 0, "ingestDroppedEvents": 3 }
                """));
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

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
