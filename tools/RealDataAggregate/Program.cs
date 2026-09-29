using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Tools.RealDataAggregate;

/// <summary>
/// class-system-todo.md P9.2 — win rate per matchup, over the REAL corpus P9.1's wire produces.
/// spec-residual-fit.md §13 is the design record: matchup = (wave id, dominant aptitude), win rate
/// and nothing else (never fight length/damage/kill time), a run with no aptitude.snapshot event
/// (any battle recorded before that wire existed) excluded and reported as excluded rather than
/// silently dropped, a sparse matchup flagged `insufficient` rather than imputed.
/// </summary>
public static class Program
{
    const string Usage = """
        RealDataAggregate — win rate per (wave, dominant aptitude) matchup, over real webrpg-1 battles.

          --data <path>          path to rpg-hot.sqlite (default: dist/FusionRpg.Server/data/rpg-hot.sqlite)
          --min-samples <N>      matchups below this many battles are flagged insufficient (default: 5)
          --out <path>           write JSON here instead of stdout

        Example: RealDataAggregate --min-samples 10 --out docs/research/class-system/_real-matchups.json
        """;

    public static int Main(string[] args)
    {
        Options opts;
        try { opts = Options.Parse(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }

        if (opts.ShowHelp)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (!File.Exists(opts.DbPath))
        {
            Console.Error.WriteLine($"error: database not found at '{opts.DbPath}'");
            return 1;
        }

        var result = Aggregate(opts.DbPath, opts.MinSamples);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (opts.OutPath is { } outPath)
        {
            File.WriteAllText(outPath, json);
            Console.WriteLine($"wrote {outPath}");
        }
        else
        {
            Console.WriteLine(json);
        }
        return 0;
    }

    public static AggregateResult Aggregate(string dbPath, int minSamples)
    {
        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
        db.Open();

        using var cmd = db.CreateCommand();
        // LEFT JOIN, not INNER: a run with no matching aptitude.snapshot event must still be counted
        // (as excluded), never silently vanish from totalRuns.
        cmd.CommandText = """
            SELECT r.match_key, r.level_name, r.result, e.payload
            FROM runs r
            LEFT JOIN events e ON e.match_key = r.match_key AND e.kind = 'aptitude.snapshot'
            WHERE r.game = 'webrpg-1' AND r.match_key IS NOT NULL
            ORDER BY r.id;
            """;

        var buckets = new Dictionary<(string Wave, string Aptitude), (int Wins, int Total)>();
        var excludedNoSnapshot = 0;
        var totalRuns = 0;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            totalRuns++;
            var wave = reader.IsDBNull(1) ? "unknown" : reader.GetString(1);
            var result = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var payloadJson = reader.IsDBNull(3) ? null : reader.GetString(3);

            if (payloadJson is null)
            {
                excludedNoSnapshot++;
                continue;
            }

            var aptitude = DominantAptitude(payloadJson);
            var key = (wave, aptitude);
            var (wins, total) = buckets.GetValueOrDefault(key, (0, 0));
            var isWin = string.Equals(result, "victory", StringComparison.OrdinalIgnoreCase);
            buckets[key] = (wins + (isWin ? 1 : 0), total + 1);
        }

        var matchups = buckets
            .Select(kv => new MatchupResult(
                WaveId: kv.Key.Wave,
                Aptitude: kv.Key.Aptitude,
                Wins: kv.Value.Wins,
                Total: kv.Value.Total,
                WinRate: (double)kv.Value.Wins / kv.Value.Total,
                Insufficient: kv.Value.Total < minSamples))
            .OrderBy(m => m.WaveId, StringComparer.Ordinal)
            .ThenBy(m => m.Aptitude, StringComparer.Ordinal)
            .ToList();

        return new AggregateResult(minSamples, totalRuns, excludedNoSnapshot, matchups);
    }

    /// <summary>Argmax over the twelve shares, with an explicit "mixed"/"unfunded" fallback rather
    /// than an arbitrary pick — spec-residual-fit.md §13: "never fabricate a label the data doesn't
    /// support."</summary>
    internal static string DominantAptitude(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var shares = doc.RootElement.GetProperty("shares");

        string? best = null;
        double bestShare = 0;
        var tie = false;
        const double epsilon = 1e-9;

        foreach (var prop in shares.EnumerateObject())
        {
            var value = prop.Value.GetDouble();
            if (value > bestShare + epsilon)
            {
                best = prop.Name;
                bestShare = value;
                tie = false;
            }
            else if (Math.Abs(value - bestShare) <= epsilon && value > epsilon)
            {
                tie = true;
            }
        }

        if (best is null || bestShare <= epsilon) return "unfunded";
        return tie ? "mixed" : best;
    }
}

public sealed record MatchupResult(string WaveId, string Aptitude, int Wins, int Total, double WinRate, bool Insufficient);

public sealed record AggregateResult(int MinSamples, int TotalRuns, int ExcludedNoSnapshot, List<MatchupResult> Matchups);

sealed class Options
{
    public string DbPath = "";
    public int MinSamples = 5;
    public string? OutPath;
    public bool ShowHelp;

    public static Options Parse(string[] args)
    {
        var o = new Options { DbPath = DefaultDbPath() };
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": o.ShowHelp = true; break;
                case "--data": o.DbPath = args[++i]; break;
                case "--min-samples": o.MinSamples = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": o.OutPath = args[++i]; break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }
        return o;
    }

    static string DefaultDbPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return Path.Combine(dir.FullName, "dist", "FusionRpg.Server", "data", "rpg-hot.sqlite");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
