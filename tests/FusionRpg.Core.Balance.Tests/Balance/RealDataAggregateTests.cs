using System.Text.Json;
using FusionRpg.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P9.2 — gk-core/tools/RealDataAggregate against a hand-authored, seeded
/// SQLite fixture (just the two tables this tool reads: runs, events), so the aggregation MATH is
/// proven against a hand-computed expectation (the verify line's own requirement), independent of
/// whatever the real live corpus happens to contain at any given moment. Runs the tool as a real
/// subprocess (same cold-start-fixture pattern as DominanceBaselineTests/ResolverMatchesSimulatorTests),
/// not a re-implementation of its aggregation logic in the test.</summary>
public class RealDataAggregateTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "fusionrpg-realdataagg-" + Guid.NewGuid().ToString("N") + ".sqlite");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void SeededCorpus_matchesTheHandComputedExpectation()
    {
        // A known, hand-authored corpus, chosen to exercise every branch spec-residual-fit.md §13
        // names: a matchup with enough samples, one without, a mixed-signal build resolving to its
        // real dominant aptitude, a perfect tie ("mixed"), an all-zero allocation ("unfunded"), and a
        // run with no aptitude.snapshot event at all (excluded, not silently dropped and not folded
        // into any matchup).
        //   rift-skirmish / Might: 3 battles, 2 wins -> winRate 0.6667, sufficient at min=2
        //   rift-skirmish / Vigor: 1 battle, 0 wins  -> insufficient at min=2
        //   rift-warband  / Might: 2 battles, 2 wins -> winRate 1.0 (0.9/0.1 split still resolves to Might)
        //   rift-warband  / mixed: 1 battle (exact 0.5/0.5 tie)
        //   rift-warband  / unfunded: 1 battle (all-zero shares)
        BuildFixture(_dbPath, new[]
        {
            Row("m1", "rift-skirmish", "victory", Shares(("Might", 1.0))),
            Row("m2", "rift-skirmish", "victory", Shares(("Might", 1.0))),
            Row("m3", "rift-skirmish", "defeat", Shares(("Might", 1.0))),
            Row("v1", "rift-skirmish", "defeat", Shares(("Vigor", 1.0))),
            Row("w1", "rift-warband", "victory", Shares(("Might", 0.9), ("Vigor", 0.1))),
            Row("w2", "rift-warband", "victory", Shares(("Might", 0.9), ("Vigor", 0.1))),
            Row("tie1", "rift-warband", "defeat", Shares(("Might", 0.5), ("Vigor", 0.5))),
            Row("zero1", "rift-warband", "defeat", Shares(("Might", 0.0), ("Vigor", 0.0))),
            Row("nosnap", "rift-skirmish", "victory", Shares: null),
        });

        var (exit, stdout, stderr) = RunTool("--min-samples 2");
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal(9, root.GetProperty("totalRuns").GetInt32());
        Assert.Equal(1, root.GetProperty("excludedNoSnapshot").GetInt32());

        var matchups = root.GetProperty("matchups").EnumerateArray().ToList();
        JsonElement Find(string wave, string apt) =>
            Assert.Single(matchups, m => m.GetProperty("waveId").GetString() == wave && m.GetProperty("aptitude").GetString() == apt);

        var skirmishMight = Find("rift-skirmish", "Might");
        Assert.Equal(2, skirmishMight.GetProperty("wins").GetInt32());
        Assert.Equal(3, skirmishMight.GetProperty("total").GetInt32());
        Assert.Equal(2.0 / 3.0, skirmishMight.GetProperty("winRate").GetDouble(), precision: 6);
        Assert.False(skirmishMight.GetProperty("insufficient").GetBoolean());

        var skirmishVigor = Find("rift-skirmish", "Vigor");
        Assert.Equal(0, skirmishVigor.GetProperty("wins").GetInt32());
        Assert.Equal(1, skirmishVigor.GetProperty("total").GetInt32());
        Assert.True(skirmishVigor.GetProperty("insufficient").GetBoolean());

        var warbandMight = Find("rift-warband", "Might");
        Assert.Equal(2, warbandMight.GetProperty("wins").GetInt32());
        Assert.Equal(2, warbandMight.GetProperty("total").GetInt32());
        Assert.Equal(1.0, warbandMight.GetProperty("winRate").GetDouble(), precision: 6);
        Assert.False(warbandMight.GetProperty("insufficient").GetBoolean());

        var warbandMixed = Find("rift-warband", "mixed");
        Assert.Equal(1, warbandMixed.GetProperty("total").GetInt32());

        var warbandUnfunded = Find("rift-warband", "unfunded");
        Assert.Equal(1, warbandUnfunded.GetProperty("total").GetInt32());

        Assert.Equal(5, matchups.Count); // no 6th matchup invented for the no-snapshot run
    }

    [Fact]
    public void EmptyDatabase_reportsZeroRuns_notAnError()
    {
        BuildFixture(_dbPath, Array.Empty<(string, string, string, string?)>());
        var (exit, stdout, stderr) = RunTool("--min-samples 5");
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(0, doc.RootElement.GetProperty("totalRuns").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("matchups").EnumerateArray());
    }

    static string? Shares(params (string Aptitude, double Share)[] entries) =>
        JsonSerializer.Serialize(new { scope = "commander", shares = entries.ToDictionary(e => e.Aptitude, e => e.Share) });

    static (string MatchKey, string Wave, string Result, string? SharesJson) Row(string matchKey, string wave, string result, string? Shares) =>
        (matchKey, wave, result, Shares);

    static void BuildFixture(string path, (string MatchKey, string Wave, string Result, string? SharesJson)[] rows)
    {
        // Pooling=False -- Microsoft.Data.Sqlite pools the native connection by default, which keeps
        // a file handle open past this method's own `using` disposal and makes Dispose()'s cleanup
        // delete fail with a sharing violation. This fixture is written once and read only by the
        // tool's own subprocess afterward, so pooling buys nothing here.
        using var db = new SqliteConnection($"Data Source={path};Pooling=False;");
        db.Open();
        using (var create = db.CreateCommand())
        {
            // Minimal schema: only the columns gk-core/tools/RealDataAggregate's own SELECT references, not
            // the full production rpg_hot.sqlite shape -- this fixture proves the AGGREGATION MATH,
            // not the real schema (which BuildFixture would just be re-declaring a second time).
            create.CommandText = """
                CREATE TABLE runs (id INTEGER PRIMARY KEY, match_key TEXT, level_name TEXT, result TEXT, game TEXT);
                CREATE TABLE events (match_key TEXT, kind TEXT, payload TEXT);
                """;
            create.ExecuteNonQuery();
        }

        var id = 1;
        foreach (var row in rows)
        {
            using var insertRun = db.CreateCommand();
            insertRun.CommandText = "INSERT INTO runs(id, match_key, level_name, result, game) VALUES ($id,$k,$w,$r,'webrpg-1');";
            insertRun.Parameters.AddWithValue("$id", id++);
            insertRun.Parameters.AddWithValue("$k", row.MatchKey);
            insertRun.Parameters.AddWithValue("$w", row.Wave);
            insertRun.Parameters.AddWithValue("$r", row.Result);
            insertRun.ExecuteNonQuery();

            if (row.SharesJson is null) continue;
            using var insertEvent = db.CreateCommand();
            insertEvent.CommandText = "INSERT INTO events(match_key, kind, payload) VALUES ($k,'aptitude.snapshot',$p);";
            insertEvent.Parameters.AddWithValue("$k", row.MatchKey);
            insertEvent.Parameters.AddWithValue("$p", row.SharesJson);
            insertEvent.ExecuteNonQuery();
        }
    }

    (int Exit, string Stdout, string Stderr) RunTool(string args)
    {
        var repoRoot = FindRepoRoot();
        // ProjectReference + apphost (see ToolProcess) — no implicit build, no stale Release output.
        return ToolProcess.Run(repoRoot, "RealDataAggregate", $"--data \"{_dbPath}\" {args}", 120_000);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
