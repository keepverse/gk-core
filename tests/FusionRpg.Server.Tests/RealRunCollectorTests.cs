using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FusionRpg.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>class-system-todo.md P9.1 — scripts/collect_class_system_realrun.py against a REAL,
/// minimal in-process host exposing the shipped PerfEndpoints.MapPerf/PerfWindowBuffer (no change to
/// either — this collector is a new, class-system-owned CONSUMER of the already-public GET /api/perf/
/// recent, per decisions.md "Class system real-data collection", 2026-08-27). Posts synthetic windows
/// with controlled "t" timestamps (including a duplicate, to prove dedup, and a planted gap, to prove
/// the drop-rate estimate) and runs the real PowerShell script as a subprocess — not a re-implementation
/// of its logic — against the real, live endpoint.</summary>
[Trait("VerificationId", "server.perf-ingest")]
public class RealRunCollectorTests
{
    [Fact]
    public async Task Collector_writesOneJsonlLinePerDistinctWindow_dedupingOnT_andReportsAPlantedDrop()
    {
        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<PerfWindowBuffer>();
        builder.WebHost.UseUrls(baseUrl);
        await using var app = builder.Build();
        app.MapPerf();
        await app.StartAsync();

        try
        {
            using var http = new HttpClient();
            // Cadence matches the test's own -ExpectedIntervalSec below (1s) so "expected" windows are
            // predictable. Four windows one second apart, THEN a deliberate 4-second gap (3 missed
            // windows), then one more — the planted drop the summary must report, not silently absorb.
            var baseTime = DateTime.UtcNow;
            var offsetsSec = new[] { 0.0, 1.0, 2.0, 3.0, 7.0 };
            foreach (var offset in offsetsSec)
            {
                var t = baseTime.AddSeconds(offset).ToString("o");
                await PostWindow(http, baseUrl, t, offset);
            }
            // A duplicate of the FIRST window (same "t") — the collector must not double-count it.
            await PostWindow(http, baseUrl, baseTime.AddSeconds(0.0).ToString("o"), 0.0);

            var runId = "test-" + Guid.NewGuid().ToString("N");
            var (exit, stdout, stderr) = await RunCollector(baseUrl, runId, durationSec: 3, pollIntervalSec: 0.5, expectedIntervalSec: 1.0);
            Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");

            var repoRoot = FindRepoRoot();
            var outDir = Path.Combine(repoRoot, "docs", "research", "class-system", "real-runs");
            var jsonlPath = Path.Combine(outDir, $"{runId}.jsonl");
            var summaryPath = Path.Combine(outDir, $"{runId}.summary.json");
            try
            {
                Assert.True(File.Exists(jsonlPath), $"missing {jsonlPath}\n{stdout}");
                Assert.True(File.Exists(summaryPath), $"missing {summaryPath}\n{stdout}");

                var lines = File.ReadAllLines(jsonlPath).Where(l => l.Length > 0).ToArray();
                Assert.Equal(5, lines.Length); // 5 distinct "t" values; the duplicate must NOT add a 6th line
                foreach (var line in lines)
                {
                    using var doc = JsonDocument.Parse(line);
                    Assert.Equal(runId, doc.RootElement.GetProperty("runId").GetString());
                    Assert.True(doc.RootElement.TryGetProperty("t", out _));
                    Assert.True(doc.RootElement.TryGetProperty("window", out _));
                }

                using var summaryDoc = JsonDocument.Parse(File.ReadAllText(summaryPath));
                var root = summaryDoc.RootElement;
                Assert.Equal(runId, root.GetProperty("runId").GetString());
                Assert.Equal(5, root.GetProperty("windowsCaptured").GetInt32());
                // span 0s..7s at 1s expected cadence = 8 expected windows; 5 captured; 3 dropped, matching
                // the planted gap exactly (offsets 4,5,6 never posted).
                Assert.Equal(8, root.GetProperty("expectedWindows").GetInt32());
                Assert.Equal(3, root.GetProperty("estimatedDropped").GetInt32());
                Assert.True(root.GetProperty("dropRatePct").GetDouble() > 0);
            }
            finally
            {
                if (File.Exists(jsonlPath)) File.Delete(jsonlPath);
                if (File.Exists(summaryPath)) File.Delete(summaryPath);
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Collector_exitsOne_whenNothingArrives()
    {
        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<PerfWindowBuffer>();
        builder.WebHost.UseUrls(baseUrl);
        await using var app = builder.Build();
        app.MapPerf();
        await app.StartAsync();

        try
        {
            var runId = "test-" + Guid.NewGuid().ToString("N");
            var (exit, _, _) = await RunCollector(baseUrl, runId, durationSec: 1, pollIntervalSec: 0.5, expectedIntervalSec: 1.0);
            Assert.Equal(1, exit);

            var repoRoot = FindRepoRoot();
            var outDir = Path.Combine(repoRoot, "docs", "research", "class-system", "real-runs");
            var jsonlPath = Path.Combine(outDir, $"{runId}.jsonl");
            var summaryPath = Path.Combine(outDir, $"{runId}.summary.json");
            try
            {
                Assert.False(File.Exists(jsonlPath)); // nothing ever arrived -- no line was ever written
                Assert.True(File.Exists(summaryPath));
                using var summaryDoc = JsonDocument.Parse(File.ReadAllText(summaryPath));
                Assert.Equal(0, summaryDoc.RootElement.GetProperty("windowsCaptured").GetInt32());
            }
            finally
            {
                if (File.Exists(jsonlPath)) File.Delete(jsonlPath);
                if (File.Exists(summaryPath)) File.Delete(summaryPath);
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    static async Task PostWindow(HttpClient http, string baseUrl, string t, double offsetSec)
    {
        var body = JsonSerializer.Serialize(new { t, sections = new { }, emits = new { }, frames = new { total = 1, lt8ms = 1, lt17ms = 0, lt33ms = 0, gte33ms = 0 }, offsetSecForTest = offsetSec });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync(baseUrl + "/api/perf", content);
        resp.EnsureSuccessStatusCode();
    }

    static async Task<(int Exit, string Stdout, string Stderr)> RunCollector(string baseUrl, string runId, int durationSec, double pollIntervalSec, double expectedIntervalSec)
    {
        var repoRoot = FindRepoRoot();
        // THE PORTED TOOL, AND THE PORTED TOOL'S FLAGS. This still shelled out to
        // `collect-class-system-realrun.ps1` with `-BaseUrl`/`-DurationSec`/`-PollIntervalSec`/
        // `-RunId`/`-ExpectedIntervalSec` - the PowerShell spellings - for a script that no longer
        // exists anywhere in the workspace, so both tests failed on a missing file rather than on
        // anything about the collector. The port landed `collect_class_system_realrun.py` WITH ITS
        // OWN SUITE (31 tests, passing), and this caller was never moved across, so the coverage
        // silently stopped covering the thing it was written for.
        //
        // Every default lines up, which is why `--out-dir` is not passed: the ported tool's
        // `DEFAULT_OUT` is literally ("docs","research","class-system","real-runs") and it writes
        // `<out>/<runId>.jsonl` and `<out>/<runId>.summary.json` - the two paths this test asserts
        // on, at the same place.
        var script = Path.Combine(repoRoot, "scripts", "collect_class_system_realrun.py");
        Assert.True(File.Exists(script), $"the ported collector is missing: {script}");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --base-url \"{baseUrl}\" --duration {durationSec} "
                + $"--poll-interval {pollIntervalSec.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + $"--run-id \"{runId}\" "
                + $"--expected-interval {expectedIntervalSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var exited = p.WaitForExit((durationSec + 30) * 1000);
        Assert.True(exited, "collector script timed out");
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (p.ExitCode, stdout, stderr);
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
