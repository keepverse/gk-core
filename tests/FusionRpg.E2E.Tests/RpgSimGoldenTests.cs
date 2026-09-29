using System.Text.Json;
using FusionRpg.Tools.RpgSim;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// rpg-simulator RS2.2 §5 — the golden artifact (owner ruling **C2 (a)**: golden *and* hash).
/// `gk-core/tests/fixtures/rpg-scenarios/golden/first-session-forward.verdict.json` is the stored verdict of the
/// corpus's last accepted run, and its `digest` is what this test compares a fresh run against.
///
/// <para><b>What the golden pins, and what it deliberately does not.</b> The digest, the seed, the scenario
/// id, the set of read-backs (name, method, route) and the digest's exclusion FIELDS. Not the readings'
/// values: an outcome-dependent row set is asserted by the corpus's own <c>expect.*</c> rules, which pin the
/// rule and the attribution rather than a count, so a balance change must not fail the golden for a reason
/// that is not a regression.</para>
///
/// <para><b>Refreshing it.</b> `FUSIONRPG_BLESS_RPGSIM_GOLDEN=1` rewrites the file — the same house pattern
/// `ContractFixtureTests` (`FUSIONRPG_BLESS_CONTRACT_FIXTURES`) and `WorldTurnFixtureTests`
/// (`FUSIONRPG_BLESS_WORLD_FIXTURE`) use in this project, because this project is where an in-process host
/// exists. The CLI has the same two verbs for a process or `--base-url` lane (`--golden` /
/// `--update-golden`), and neither is ever the default: a golden moves only when a distinguishable reading
/// moved, and the commit that moves it says which pointer and why.</para>
/// </summary>
[Collection("e2e")]
public class RpgSimGoldenTests
{
    readonly ITestOutputHelper _out;

    public RpgSimGoldenTests(ITestOutputHelper output) => _out = output;

    static string CorpusPath => Path.Combine(FindScenariosDir(), "first-session-forward.json");

    static string GoldenPath => Path.Combine(FindScenariosDir(), "golden", "first-session-forward.verdict.json");

    [Fact]
    public async Task The_corpus_run_matches_the_checked_in_golden()
    {
        var doc = ScenarioFile.Read(CorpusPath);

        using var factory = new RpgApiFactory();
        using var client = factory.CreateClient();
        // A STABLE host label, not the fixture's own data-source name: the golden is a checked-in artifact,
        // and a per-run GUID in it would be noise a reviewer has to ignore on every read.
        var verdict = await new ScenarioRunner(client, "inproc", new RpgApiFactory.ClockControl()).RunAsync(doc);
        _out.WriteLine($"run: ok={verdict.Ok} digest={verdict.Digest} readings={verdict.Readings.Count}");
        Assert.True(verdict.Ok, string.Join("\n", verdict.Failures));

        if (Environment.GetEnvironmentVariable("FUSIONRPG_BLESS_RPGSIM_GOLDEN") == "1")
        {
            // §5's discipline, in the test lane: a refresh REPORTS the move it is blessing, before it writes,
            // so the commit that moves the golden can quote it.
            if (File.Exists(GoldenPath))
            {
                _out.WriteLine(GoldenVerdict.Report(
                    GoldenVerdict.Compare(ScenarioVerdict.FromJson(await File.ReadAllTextAsync(GoldenPath)), verdict),
                    GoldenPath));
            }
            else
            {
                _out.WriteLine($"no golden at {GoldenPath} — writing the first one");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            await File.WriteAllTextAsync(GoldenPath, verdict.ToJson());
            _out.WriteLine($"golden REFRESHED — {GoldenPath} (quote the move above in the commit)");
        }

        Assert.True(File.Exists(GoldenPath),
            $"missing golden {GoldenPath} — run with FUSIONRPG_BLESS_RPGSIM_GOLDEN=1 (or the CLI's --update-golden)");

        var golden = ScenarioVerdict.FromJson(await File.ReadAllTextAsync(GoldenPath));
        var comparison = GoldenVerdict.Compare(golden, verdict);
        _out.WriteLine(GoldenVerdict.Report(comparison, GoldenPath));
        Assert.True(comparison.Same,
            "the golden moved — a distinguishable reading moved, so say which pointer and why in the commit " +
            "that refreshes it:\n" + GoldenVerdict.Report(comparison, GoldenPath));
    }

    /// <summary>
    /// The comparison is SEEN TO FAIL: a moved digest, a dropped reading and a changed exclusion field each
    /// have to be reported by name. A guard nobody has watched fail is a guard nobody knows works.
    /// </summary>
    [Fact]
    public void A_moved_digest_a_dropped_reading_or_a_changed_exclusion_is_reported_by_name()
    {
        static ScenarioVerdict Verdict(string digest, params string[] readings) => new()
        {
            ScenarioId = "s",
            Seed = 7,
            Host = "inproc",
            Clock = "ambient — test",
            Ok = true,
            Settle = new SettleRecord { Settled = true, Polls = 1, ElapsedMs = 1 },
            Digest = digest,
            DigestExclusions = new List<ExclusionRecord> { new() { Field = "*Utc", Reason = "wall clock" } },
            Readings = readings.Select(name => new ScenarioReading
            {
                Name = name, Method = "GET", Source = $"GET /api/{name}"
            }).ToList()
        };

        var baseline = Verdict("abc", "read.one", "read.two");
        Assert.True(GoldenVerdict.Compare(baseline, Verdict("abc", "read.one", "read.two")).Same);

        var movedDigest = GoldenVerdict.Compare(baseline, Verdict("def", "read.one", "read.two"));
        Assert.False(movedDigest.Same);
        Assert.Contains(movedDigest.Moved, m => m.StartsWith("digest: abc -> def", StringComparison.Ordinal));

        var dropped = GoldenVerdict.Compare(baseline, Verdict("abc", "read.one"));
        Assert.False(dropped.Same);
        Assert.Contains(dropped.Moved, m => m.StartsWith("readings:", StringComparison.Ordinal));

        var exclusionMoved = Verdict("abc", "read.one", "read.two");
        exclusionMoved.DigestExclusions = new List<ExclusionRecord> { new() { Field = "*At", Reason = "wall clock" } };
        var changed = GoldenVerdict.Compare(exclusionMoved, Verdict("abc", "read.one", "read.two"));
        Assert.False(changed.Same);
        Assert.Contains(changed.Moved, m => m.StartsWith("digestExclusions: [*At] -> [*Utc]", StringComparison.Ordinal));

        // And a VALUES-only move is NOT reported: §5 says the golden is not the outcome oracle, so a
        // reading whose payload changed under the same digest must not fail it.
        var valuesOnly = Verdict("abc", "read.one", "read.two");
        valuesOnly.Readings[0].Value = JsonDocument.Parse("""{"anything":true}""").RootElement.Clone();
        Assert.True(GoldenVerdict.Compare(baseline, valuesOnly).Same);
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
