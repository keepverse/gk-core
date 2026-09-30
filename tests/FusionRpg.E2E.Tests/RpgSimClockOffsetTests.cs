using System.Text.Json;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Tools.RpgSim;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// rpg-simulator RS3 increment 5a (owner ruling on RS-F16, candidate 1): a scenario may declare
/// <c>clock.mode: offset</c>, and BOTH approved hosts apply that offset at boot through the ONE seam
/// (<c>FusionRpg.Core.Time.ServerClock</c>).
///
/// <para><b>What this proves, and what it deliberately does not.</b> It proves the DECLARATION reaches the
/// host and changes what the host believes the time is — by measuring the effect on a real row
/// (<c>dispatchedUtc</c>/<c>dueUtc</c> from a real dispatch), not by reading the seam back. It does not
/// prove the corpus's <c>test.expedition-due</c> step can be retired: a single static offset moves the
/// dispatch and the due check together, so it cannot make an expedition due inside one run. That is
/// candidate (2), increment 5b, and until it lands the corpus step stays and says what the bypass is.</para>
///
/// <para><b>Why the offset host is built inside the test.</b> The seam is process-global by design (one
/// clock per process), so an offset host and an ambient host cannot be alive at once. These tests live in
/// the shared <c>e2e</c> collection, which xunit serializes against every other scenario test, and
/// <see cref="RpgApiFactory.Dispose(bool)"/> gives the process clock back.</para>
/// </summary>
[Collection("e2e")]
[Trait("Category", "DiskSemantics")]
public class RpgSimClockOffsetTests
{
    /// <summary>A one-hour offset: far outside any tolerance a clock-skew comparison could swallow.</summary>
    const long OneHourSeconds = 3600;

    readonly ITestOutputHelper _out;

    public RpgSimClockOffsetTests(ITestOutputHelper output) => _out = output;

    static string CorpusPath => Path.Combine(FindScenariosDir(), "first-session-forward.json");

    /// <summary>
    /// The corpus scenario with its clock block replaced and its mid-run movement pushed PAST the boot
    /// offset: the file itself stays `ambient`. The boot declaration (5a) is what the stamps show; the
    /// `clock.set` step (5b) is what makes the expedition due — 91 minutes, so it is past the dispatch's
    /// own due time of boot+60m+30m. Both mechanisms therefore run in one file, on both hosts.
    /// </summary>
    static ScenarioDocument OffsetDeclaringCorpus()
    {
        var doc = ScenarioFile.Read(CorpusPath);
        doc.Clock = new ScenarioClock
        {
            Mode = "offset",
            OffsetSeconds = OneHourSeconds,
            Note = "RS3 increment 5a: the host is booted one hour ahead through the one clock seam, so every " +
                   "stamp this run writes carries the offset. The corpus file itself declares ambient."
        };
        var movement = doc.Steps.Single(s => ScenarioVocabulary.Kind(s.Op) == ScenarioVocabulary.Clock);
        movement.OffsetSeconds = OneHourSeconds + 31 * 60;
        return doc;
    }

    /// <summary>
    /// The measured effect: the expedition this run dispatched is stamped one hour plus its tier's duration
    /// ahead of the machine clock. Read back through the same GET the web FE calls.
    /// </summary>
    static async Task AssertOffsetVisibleAsync(HttpClient client, ScenarioVerdict verdict)
    {
        var playerId = verdict.Captures.Single(c => c.Name == "playerId").Value!.Value.GetInt64();
        using var rows = JsonDocument.Parse(
            await client.GetStringAsync($"/api/expeditions/{playerId}"));
        var row = rows.RootElement.GetProperty("items").EnumerateArray().Single();
        var dispatched = DateTimeOffset.Parse(row.GetProperty("dispatchedUtc").GetString()!);
        var due = DateTimeOffset.Parse(row.GetProperty("dueUtc").GetString()!);

        var ahead = dispatched - DateTimeOffset.UtcNow;
        Assert.True(ahead > TimeSpan.FromMinutes(55),
            $"the declared +{OneHourSeconds}s offset is not visible: dispatchedUtc is only {ahead} ahead of now");
        Assert.True(ahead < TimeSpan.FromMinutes(65),
            $"dispatchedUtc is {ahead} ahead — more than the declared offset, so something else moved the clock");

        // The DUE stamp carries the offset too, and the tier's own duration still applies ON TOP of it:
        // the seam shifts the clock, it does not shorten the expedition. This is the assertion the corpus's
        // retired rewind used to make impossible.
        var dueAhead = due - DateTimeOffset.UtcNow;
        Assert.True(dueAhead > TimeSpan.FromMinutes(85) && dueAhead < TimeSpan.FromMinutes(95),
            $"dueUtc is {dueAhead} ahead of now — it must carry the declared offset PLUS scout-30m's duration");
        Assert.Equal(TimeSpan.FromMinutes(30), due - dispatched);
    }

    [Fact]
    public async Task The_in_process_host_applies_the_declared_offset()
    {
        var doc = OffsetDeclaringCorpus();
        Assert.Empty(ScenarioValidator.Validate(doc));

        using var factory = new RpgApiFactory(OneHourSeconds);
        using var client = factory.CreateClient();
        Assert.Equal(OneHourSeconds, factory.ClockOffsetSeconds);

        var verdict = await new ScenarioRunner(client, $"inproc(offset={OneHourSeconds}s)",
            new RpgApiFactory.ClockControl()).RunAsync(doc);
        _out.WriteLine($"{verdict.ScenarioId}: ok={verdict.Ok} clock='{verdict.Clock}' " +
                       $"readings={verdict.Readings.Count} digest={verdict.Digest}");

        Assert.True(verdict.Ok, string.Join("\n", verdict.Failures));
        Assert.Contains($"offset {OneHourSeconds}s", verdict.Clock, StringComparison.Ordinal);
        await AssertOffsetVisibleAsync(client, verdict);
    }

    /// <summary>
    /// The same declaration on the REAL process: the offset travels as <c>FUSIONRPG_CLOCK_OFFSET</c> and the
    /// server's composition root reads it once. `DiskSemantics` for the same reason
    /// <c>RpgSimProcessHostTests</c> carries it — a real server process on real SQLite files is the thing
    /// under test.
    /// </summary>
    [Fact]
    public async Task The_real_process_host_applies_the_declared_offset()    {
        var doc = OffsetDeclaringCorpus();
        var dataDir = NewDataDir();
        SeedSpeciesRoster(dataDir);

        var hostOptions = new ProcessHostOptions
        {
            ServerExePath = ServerExe(),
            DataDir = dataDir,
            ClockOffsetSeconds = doc.Clock!.OffsetSeconds ?? 0
        };
        var clock = await ProcessHostClockControl.StartAsync(hostOptions);

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(clock.Host.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
            var verdict = await new ScenarioRunner(client, $"process:{clock.Host.BaseUrl}", clock).RunAsync(doc);
            _out.WriteLine($"process host: pid={clock.Host.ProcessId} ok={verdict.Ok} clock='{verdict.Clock}' " +
                           $"readings={verdict.Readings.Count} digest={verdict.Digest}");

            Assert.True(verdict.Ok, string.Join("\n", verdict.Failures));
            Assert.Contains($"offset {OneHourSeconds}s", verdict.Clock, StringComparison.Ordinal);
            await AssertOffsetVisibleAsync(client, verdict);
        }
        finally
        {
            await clock.DisposeAsync();
        }
    }

    // ── the same provisioning helpers RpgSimProcessHostTests uses: a CHOSEN roster is what makes a run
    //    reproducible (since CS-F3 an empty data dir also boots — the server self-heals the shipped tree,
    //    Program.cs:683 — but the corpus wants the committed one), and gk-core/tools/RpgSim keeps no store reference
    //    on purpose). The data dir lives
    //    under the test's own output, never %TEMP% — the test-substrate guard refuses a tests/** file that
    //    pairs a store with a temp root, and a directory beside the assembly is what this suite already
    //    does for the same purpose. ──────────────────────────────────────────────────────────────────────

    static string NewDataDir() =>
        Path.Combine(AppContext.BaseDirectory, "e2e-clock-" + Guid.NewGuid().ToString("N"));

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
                    "RpgSimClockOffsetTests.SeedSpeciesRoster failed: " + string.Join("; ", outcome.Errors));
        }

        SqliteConnection.ClearAllPools();
    }

    static string ServerExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "FusionRpg.Server.exe");
        Assert.True(File.Exists(exe), $"no server executable at {exe}");
        return exe;
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find repo root");
    }

    static string FindScenariosDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "rpg-scenarios");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find tests/fixtures/rpg-scenarios");
    }
}
