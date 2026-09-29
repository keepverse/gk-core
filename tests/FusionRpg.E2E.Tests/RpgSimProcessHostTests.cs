using System.Diagnostics;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Tools.RpgSim;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// rpg-simulator RS2.5 — the real-process slow lane: the same scenario file, a real
/// <c>FusionRpg.Server.exe</c> as its own process on its own data directory and an ephemeral loopback
/// port, real HTTP and SignalR transport.
///
/// <para><b>Why this lives in the E2E project.</b> The runner is a tool and takes an
/// <see cref="HttpClient"/>; what boots the server is <see cref="ProcessHost"/> (in the tool, and the
/// CLI's <c>--host process</c>). The E2E project is the one place that can both launch the process and
/// run the same file in-process, which is what owner ruling E2 (a) asks for. Nothing here re-implements
/// the runner.</para>
///
/// <para><b>Provisioning is the caller's step, deliberately — and since CS-F3 an empty directory also works.</b>
/// The owner ruled on 2026-09-23 that the species tree ships and the boot self-heals the roster
/// (<c>Program.cs:683</c>'s <c>SpeciesImportRunner.RunSelfHealing</c>), so a fresh data directory boots. This
/// test still seeds the corpus the same way <see cref="RpgApiFactory"/> seeds its in-process host, because a
/// *chosen* roster is what makes the run reproducible; the tool itself never opens a store.</para>
///
/// <para><c>DiskSemantics</c>: this is the one E2E case where a real directory is the thing under test
/// (a real server process on real SQLite files). The tag is what the default profile skips; the slow
/// lane is run explicitly and in CI's unfiltered profile.</para>
/// </summary>
[Collection("e2e")]
[Trait("Category", "DiskSemantics")]
public class RpgSimProcessHostTests
{
    readonly ITestOutputHelper _out;

    public RpgSimProcessHostTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task The_same_scenario_file_runs_on_the_real_process_and_in_process_and_the_digests_are_compared()
    {
        var doc = ScenarioFile.Read(Path.Combine(FindScenariosDir(), "first-session-forward.json"));
        var dataDir = NewDataDir();
        SeedSpeciesRoster(dataDir);

        var hostOptions = new ProcessHostOptions
        {
            ServerExePath = ServerExe(),
            DataDir = dataDir
        };
        // The clock control owns the host's whole lifecycle (RS3 increment 5b): the corpus's `clock.set` step
        // makes the runner reboot this process on the SAME data dir and port, so the control — not a raw host
        // — is what stops the final process and removes the directory.
        var clock = await ProcessHostClockControl.StartAsync(hostOptions);

        ScenarioVerdict onProcess;
        var pid = clock.Host.ProcessId;
        try
        {
            Assert.True(clock.Host.SimEnabledReported, "the booted process must report simEnabled:true");

            // The acceptance line the transport has to meet, proven against the real process rather than
            // asserted about it: a SignalR connection over the real socket.
            var hub = new HubConnectionBuilder()
                .WithUrl(new Uri(clock.Host.BaseUrl + "/hub/rpg"))
                .Build();
            await hub.StartAsync();
            Assert.Equal(HubConnectionState.Connected, hub.State);
            _out.WriteLine($"signalr: connected to {clock.Host.BaseUrl}/hub/rpg");
            await hub.StopAsync();
            await hub.DisposeAsync();

            using var client = new HttpClient { BaseAddress = new Uri(clock.Host.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
            onProcess = await new ScenarioRunner(client, $"process:{clock.Host.BaseUrl}", clock).RunAsync(doc);
        }
        finally
        {
            await clock.DisposeAsync();
        }

        _out.WriteLine($"process host: pid={pid} ok={onProcess.Ok} readings={onProcess.Readings.Count} " +
                       $"digest={onProcess.Digest}");
        Assert.True(onProcess.Ok, string.Join("\n", onProcess.Failures));
        Assert.True(onProcess.Settle!.Settled, onProcess.Settle.Reason);

        // Teardown is contract: the process is gone and its data directory is gone, with no catch to
        // hide a failure (ProcessHost.DisposeAsync throws on either).
        Assert.True(Process.GetProcesses().All(p => p.Id != pid), $"server process {pid} is still alive");
        Assert.False(Directory.Exists(dataDir), $"data directory leaked: {dataDir}");

        // The SAME file on the in-process host (RS2.4), for the cross-host comparison.
        var onInProcess = await RunOnFreshInProcessHost(doc);

        _out.WriteLine($"in-process host: ok={onInProcess.Ok} readings={onInProcess.Readings.Count} " +
                       $"digest={onInProcess.Digest}");

        // The whole-reading falsifier across hosts, reported rather than smoothed (readback-verdict.md
        // section 4): the pointers that move here are the measurement, not a number to average away.
        var all = ReadingDigest.CompareVerdicts(onInProcess, onProcess);
        _out.WriteLine("cross-host whole-reading comparison: " + all.Report());

        // The declared digest: the readings the scenario claims are host-stable. Both hosts are fresh
        // worlds with the same file, so these must agree; a disagreement is a finding, and this test
        // fails rather than reporting it as acceptable.
        Assert.NotNull(onProcess.Digest);
        Assert.NotNull(onInProcess.Digest);
        Assert.Equal(onInProcess.Digest, onProcess.Digest);
    }

    [Fact]
    public async Task The_runner_refuses_a_real_process_that_does_not_report_sim_enabled()
    {
        var doc = ScenarioFile.Read(Path.Combine(FindScenariosDir(), "first-session-forward.json"));
        var dataDir = NewDataDir();
        SeedSpeciesRoster(dataDir);

        await using var host = await ProcessHost.StartAsync(new ProcessHostOptions
        {
            ServerExePath = ServerExe(),
            DataDir = dataDir,
            SimEnabled = false
        });

        Assert.False(host.SimEnabledReported, "a server booted without FUSIONRPG_SIM must report simEnabled:false");

        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        var verdict = await new ScenarioRunner(client, $"process:{host.BaseUrl}").RunAsync(doc);

        Assert.True(verdict.Refused, "the runner must refuse a player install");
        Assert.False(verdict.Ok);
        Assert.Contains("simEnabled:false", string.Join("\n", verdict.Failures));
        // Nothing ran: the refusal happens before the first step.
        Assert.Empty(verdict.Readings);
        _out.WriteLine("refusal: " + verdict.Failures[0]);
    }

    [Fact]
    public async Task A_server_that_cannot_boot_is_a_failure_carrying_its_own_reason_and_its_data_dir_is_still_removed()
    {
        // CS-F3 (owner ruling 2026-09-23) RETIRED this test's original premise: the species tree now ships
        // and the boot self-heals the roster (Program.cs:683), so an empty data directory boots. The failure
        // path still has to be proven, so the boot is made to fail DETERMINISTICALLY instead of by content:
        // the port the child is told to bind is already held by this test.
        var dataDir = NewDataDir();
        Directory.CreateDirectory(dataDir);

        using var squatter = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        squatter.Start();
        var takenPort = ((System.Net.IPEndPoint)squatter.LocalEndpoint).Port;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessHost.StartAsync(new ProcessHostOptions
            {
                ServerExePath = ServerExe(),
                DataDir = dataDir,
                Url = $"http://127.0.0.1:{takenPort}",
                // Short: a boot that cannot bind must fail on its own, not by exhausting a long budget.
                ReadyTimeout = TimeSpan.FromSeconds(30)
            }));

        _out.WriteLine(error.Message);
        // The server's OWN reason, not a bare timeout: the message carries the child's log tail, and the
        // log names the address it could not bind.
        Assert.Contains("Log tail:", error.Message, StringComparison.Ordinal);
        Assert.Contains(takenPort.ToString(), error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(dataDir), $"data directory leaked after a failed boot: {dataDir}");
    }

    /// <summary>One run of the corpus on its own fresh in-process host (RS2.4's host). The memory plan
    /// means "fresh" is real rather than a reset, and disposing the keeper IS the cleanup.</summary>
    async Task<ScenarioVerdict> RunOnFreshInProcessHost(ScenarioDocument doc)
    {
        using var factory = new RpgApiFactory();
        using var client = factory.CreateClient();
        return await new ScenarioRunner(client, $"inproc(dataSource={factory.DataSource})",
            new RpgApiFactory.ClockControl()).RunAsync(doc);
    }

    /// <summary>
    /// Seed the real committed species corpus into a real data directory — the step the real server
    /// cannot do for itself, using the same reader <see cref="RpgApiFactory"/> uses. Pools are cleared
    /// afterwards because a pooled connection would keep the file handle and make the host's own
    /// data-directory delete fail.
    /// </summary>
    static void SeedSpeciesRoster(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dir = Path.Combine(RepoRoot(), "data", "generated", "creatures");
        var species = Directory.EnumerateFiles(dir, "*.json")
            .Where(p => !Path.GetFileName(p).StartsWith('_'))
            .Select(ConcreteSpeciesSeedReader.ParseFile)
            .ToList();
        if (species.Count == 0)
            throw new InvalidOperationException($"no real committed species found under {dir}");

        using (var store = new RpgStore(dataDir))
        {
            store.Init();
            var outcome = store.ImportSpecies(species);
            if (!outcome.IsOk)
                throw new InvalidOperationException(
                    "RpgSimProcessHostTests.SeedSpeciesRoster failed: " + string.Join("; ", outcome.Errors));
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>The real server the E2E project already references; MSBuild copies the apphost and the
    /// content trees (gk-core/data/tuning, gk-data/packs/fusion/data/seed/**, ...) next to it, so the process boots on its own
    /// output directory exactly as a published install does.</summary>
    static string ServerExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "FusionRpg.Server.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"FusionRpg.Server.exe is not beside the test assembly ({exe}); the E2E project " +
                "references src/FusionRpg.Server, so a project build produces it.", exe);
        return exe;
    }

    static string NewDataDir() =>
        Path.Combine(AppContext.BaseDirectory, "e2e-proc-" + Guid.NewGuid().ToString("N"));

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("could not find repo root (no src/FusionRpg.Injector above test bin)");
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
