using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The command line. Named explicitly (never top-level statements) so this assembly declares no
/// global-namespace <c>Program</c> — see <c>RpgSim.csproj</c>.
///
/// <para>Two verbs, and each says what it is:</para>
/// <list type="bullet">
/// <item><c>--validate</c> reads a scenario and prints every refusal the format makes (RS2.1).</item>
/// <item><c>--run</c> drives it against a target and writes the verdict JSON (RS2.3). Two transports,
/// one runner: <c>--base-url http://127.0.0.1:53111</c> against a server someone else started, or
/// <c>--host process</c> (RS2.5), which boots a real <c>FusionRpg.Server.exe</c> itself and stops it
/// again. The in-process host is a run parameter of the embedding host
/// (<c>--host inproc</c> is refused by name, RS-F6), because the factory that boots the real
/// <c>Program</c> lives in the E2E test project the tool cannot reference.</item>
/// </list>
/// <para>A flag that does nothing is refused by name rather than accepted silently: a tool whose
/// documented surface drifts from its behaviour is the failure this program exists to measure.</para>
/// </summary>
internal static class RpgSimCli
{
    const string Usage = """
        rpg-sim — the RPG feature simulator (rpg-simulator RS2)

          rpg-sim --scenario <file> --validate
              validate a scenario against the format contract

          rpg-sim --scenario <file> --run --base-url <url> [--out <file>] [--double-run]
              drive the scenario against a running sim server and write the verdict JSON

          rpg-sim --scenario <file> --run --host process --data-dir <dir> [--server-exe <exe>]
                  [--out <file>] [--double-run]
              boot a real FusionRpg.Server.exe (FUSIONRPG_SIM=1, its own FUSIONRPG_DATA, a free
              loopback port), drive the scenario against it, then stop it and remove the data dir

          ... [--golden <verdict.json>] [--update-golden]
              compare this run against the golden artifact (its digest, the reading set and the
              exclusion fields — readback-verdict.md section 5), or REFRESH it with --update-golden.
              Refreshing is never the default: a golden moves only when a distinguishable reading
              moved, and the commit that moves it says which pointer and why

        The scenario format and the read-back rules: tools/RpgSim/scenario-format.md
        The verdict and digest:                    tools/RpgSim/readback-verdict.md
        The real-process host:                     tools/RpgSim/ProcessHost.cs
        """;

    public static int Main(string[] args)
    {
        var opts = ParseArgs(args);

        if (opts.ContainsKey("--help") || opts.ContainsKey("-h") || args.Length == 0)
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        if (!opts.TryGetValue("--scenario", out var scenarioPath) || string.IsNullOrWhiteSpace(scenarioPath))
        {
            Console.Error.WriteLine("rpg-sim: --scenario <file> is required");
            return 2;
        }

        if (!File.Exists(scenarioPath))
        {
            Console.Error.WriteLine($"rpg-sim: scenario not found: {scenarioPath}");
            return 2;
        }

        if (opts.ContainsKey("--validate"))
            return Validate(scenarioPath);

        if (!opts.ContainsKey("--run"))
        {
            Console.Error.WriteLine("rpg-sim: nothing to do — pass --validate or --run");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        return Run(scenarioPath, opts).GetAwaiter().GetResult();
    }

    static int Validate(string scenarioPath)
    {
        ScenarioDocument doc;
        try
        {
            doc = ScenarioFile.Read(scenarioPath);
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine($"rpg-sim: {scenarioPath} is not JSON: {e.Message}");
            return 1;
        }

        var errors = ScenarioValidator.Validate(doc);
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"rpg-sim: scenario '{doc.Id}' is invalid ({errors.Count}):");
            foreach (var error in errors) Console.Error.WriteLine("  " + error);
            return 1;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            scenario = doc.Id,
            seed = doc.Seed,
            clock = doc.Clock?.Mode,
            steps = doc.Steps.Count,
            calls = doc.Steps.Count(s => ScenarioVocabulary.Kind(s.Op) == ScenarioVocabulary.Call),
            reads = doc.Steps.Count(s => ScenarioVocabulary.Kind(s.Op) == ScenarioVocabulary.Read),
            expects = doc.Steps.Count(s => ScenarioVocabulary.Kind(s.Op) == ScenarioVocabulary.Expect),
            digests = doc.Steps.Count(s => ScenarioVocabulary.Kind(s.Op) == ScenarioVocabulary.Digest)
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    static async Task<int> Run(string scenarioPath, Dictionary<string, string> opts)
    {
        var host = opts.TryGetValue("--host", out var requested) ? requested.Trim() : "";
        if (host.Length > 0 && !string.Equals(host, "process", StringComparison.OrdinalIgnoreCase))
        {
            // Named, not ignored, and a DELIBERATE boundary rather than a gap (RS-F6, decided 2026-09-23):
            // the in-process factory is RpgApiFactory, which lives in the E2E test project this tool must
            // not reference — a tool that pulled in the app assembly plus Microsoft.AspNetCore.Mvc.Testing
            // would carry the whole server to run what the embedding host already runs. The in-process host
            // is a RUN PARAMETER of that host, which supplies its HttpClient; `--host process` boots a real
            // server, and `--base-url` drives one somebody else booted.
            Console.Error.WriteLine(
                $"rpg-sim: --host '{host}' is refused by design. This CLI is a real-process front end only " +
                "(RS-F6, docs/architecture/rpg-simulator-map.md module 4): '--host process' boots a real " +
                "FusionRpg.Server.exe (RS2.5), and '--base-url' drives a server somebody else booted. The " +
                "in-process host is a run parameter of the EMBEDDING host, which supplies the HttpClient — " +
                "tests/FusionRpg.E2E.Tests/RpgApiFactory.cs boots it that way (RS2.4/RS2.5), and a tool may " +
                "not reference a test project.");
            return 2;
        }

        ScenarioDocument doc;
        try
        {
            doc = ScenarioFile.Read(scenarioPath);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"rpg-sim: {scenarioPath} could not be read: {e.Message}");
            return 1;
        }

        try
        {
            ScenarioValidator.ValidateOrThrow(doc);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine("rpg-sim: " + e.Message);
            return 1;
        }

        return host.Length > 0
            ? await RunAgainstProcessAsync(doc, opts)
            : await RunAgainstBaseUrlAsync(doc, opts);
    }

    static async Task<int> RunAgainstBaseUrlAsync(ScenarioDocument doc, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("--base-url", out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl))
        {
            Console.Error.WriteLine("rpg-sim: --run needs --base-url <url> or --host process");
            return 2;
        }

        // RS3 increment 5a: a declared offset is applied by the HOST at boot, so a target this tool did
        // not boot cannot be told the clock. Refusing by name is the honest answer — silently running an
        // offset scenario against an ambient host would make the verdict's clock declaration a lie.
        if (doc.Clock?.Mode == "offset" && doc.Clock.OffsetSeconds != 0)
        {
            Console.Error.WriteLine(
                $"rpg-sim: the scenario declares clock.mode 'offset' ({doc.Clock.OffsetSeconds}s), but " +
                "--base-url targets a host this tool did not boot, so the offset cannot be applied. Boot the " +
                "host with --host process (which passes FUSIONRPG_CLOCK_OFFSET), or declare 'ambient'.");
            return 2;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine($"rpg-sim: --base-url '{baseUrl}' is not an absolute URL");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(5) };
        return await DriveAsync(doc, opts, http, $"process:{uri}");
    }
    /// <summary>
    /// RS2.5's slow lane from the CLI: boot a real server, drive the same file, stop it again. The data
    /// directory is the caller's — this tool never opens a store, so it cannot provision a world, and a
    /// fresh directory has no species roster for the real server to boot on.
    /// </summary>
    static async Task<int> RunAgainstProcessAsync(ScenarioDocument doc, Dictionary<string, string> opts)
    {
        if (opts.ContainsKey("--base-url"))
        {
            Console.Error.WriteLine("rpg-sim: --host process boots its own server — drop --base-url");
            return 2;
        }

        if (!opts.TryGetValue("--data-dir", out var dataDir) || string.IsNullOrWhiteSpace(dataDir))
        {
            Console.Error.WriteLine(
                "rpg-sim: --host process needs --data-dir <dir> — the server's own FUSIONRPG_DATA. Since " +
                "CS-F3 (owner ruling 2026-09-23) a FRESH directory boots: the species tree ships beside the " +
                "exe and the server self-heals the roster on its first boot (Program.cs:683). Pass a real " +
                "data dir when you want a specific roster — this tool never opens a store, so it does not " +
                "provision one; use `species-import` for that.");
            return 2;
        }

        var serverExe = opts.TryGetValue("--server-exe", out var exe) && !string.IsNullOrWhiteSpace(exe)
            ? exe
            : Path.Combine(AppContext.BaseDirectory, "FusionRpg.Server.exe");
        if (!File.Exists(serverExe))
        {
            Console.Error.WriteLine(
                $"rpg-sim: no server executable at {serverExe}. Pass --server-exe <path>, or run this from a " +
                "directory that has FusionRpg.Server.exe beside it.");
            return 2;
        }

        var hostOptions = new ProcessHostOptions
        {
            ServerExePath = serverExe,
            DataDir = dataDir,
            ClockOffsetSeconds = doc.Clock?.Mode == "offset" ? doc.Clock.OffsetSeconds ?? 0 : 0
        };

        ProcessHostClockControl clock;
        try
        {
            clock = await ProcessHostClockControl.StartAsync(hostOptions);
        }
        catch (InvalidOperationException e)
        {
            // The server's own reason, not a stack trace: a boot failure is a named result.
            Console.Error.WriteLine("rpg-sim: " + e.Message);
            return 1;
        }

        // The control owns teardown from here: it stops the process and removes the data directory once, at
        // the end, after any number of mid-run reboots (RS3 increment 5b). A scenario whose only clock
        // declaration is a mid-run `clock.set` never reboots, and the control is a no-op wrapper.
        await using (clock)
        {
            Console.WriteLine(
                $"rpg-sim: booted {Path.GetFileName(serverExe)} pid={clock.Host.ProcessId} at {clock.Host.BaseUrl} " +
                $"(simEnabled={clock.Host.SimEnabledReported}, clockOffset={hostOptions.ClockOffsetSeconds}s, data={clock.Host.DataDir})");

            using var http = new HttpClient { BaseAddress = new Uri(clock.Host.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
            return await DriveAsync(doc, opts, http, $"process:{clock.Host.BaseUrl}", clock);
        }
    }

    /// <summary>The one run loop, whatever the transport: same runner, same checks, same verdict.</summary>
    static async Task<int> DriveAsync(ScenarioDocument doc, Dictionary<string, string> opts,
        HttpClient http, string hostLabel, IClockControl? clockControl = null)
    {
        var runner = new ScenarioRunner(http, hostLabel, clockControl);

        var first = await runner.RunAsync(doc);
        var exit = Write(first, opts, out var outPath);

        if (first.Refused)
        {
            Console.Error.WriteLine($"rpg-sim: REFUSED — {first.Failures.FirstOrDefault()}");
            return 3;
        }

        if (!first.Ok)
        {
            Console.Error.WriteLine($"rpg-sim: {doc.Id} FAILED ({first.Failures.Count} failure(s)):");
            foreach (var failure in first.Failures) Console.Error.WriteLine("  " + failure);
        }

        if (opts.ContainsKey("--double-run"))
        {
            var second = await runner.RunAsync(doc);
            var comparison = ReadingDigest.CompareVerdicts(first, second);
            Console.WriteLine(comparison.Report());
            if (!comparison.Same)
            {
                Console.Error.WriteLine("rpg-sim: DIGEST MISMATCH — reported, never smoothed (readback-verdict.md §4).");
                exit |= 4;
            }
        }

        if (outPath is not null) Console.WriteLine($"rpg-sim: verdict written to {outPath}");

        // The golden artifact (readback-verdict.md §5, owner ruling C2 (a)): the stored verdict a later run
        // is compared against, and the one place a refresh happens. `--update-golden` is NEVER the default —
        // a golden moves only when a distinguishable reading moved.
        if (opts.TryGetValue("--golden", out var goldenPath) && !string.IsNullOrWhiteSpace(goldenPath))
        {
            if (opts.ContainsKey("--update-golden"))
            {
                // §5's discipline, enforced where it is cheapest: a refresh REPORTS the move it is blessing,
                // before it writes, so the commit that moves the golden can quote it. A silent re-bless is how
                // a golden stops being evidence.
                if (File.Exists(goldenPath))
                    Console.WriteLine(GoldenVerdict.Report(
                        GoldenVerdict.Compare(ScenarioVerdict.FromJson(File.ReadAllText(goldenPath)), first),
                        goldenPath));
                else
                    Console.WriteLine($"rpg-sim: no golden at {goldenPath} — writing the first one");

                var directory = Path.GetDirectoryName(Path.GetFullPath(goldenPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(goldenPath, first.ToJson());
                Console.WriteLine($"rpg-sim: golden REFRESHED — {goldenPath} (quote the move above in the commit)");
            }
            else if (!File.Exists(goldenPath))
            {
                Console.Error.WriteLine($"rpg-sim: no golden at {goldenPath} — pass --update-golden to write the first one");
                exit |= 4;
            }
            else
            {
                var comparison = GoldenVerdict.Compare(ScenarioVerdict.FromJson(File.ReadAllText(goldenPath)), first);
                Console.WriteLine(GoldenVerdict.Report(comparison, goldenPath));
                if (!comparison.Same)
                {
                    Console.Error.WriteLine("rpg-sim: GOLDEN MOVED — reported, never smoothed (readback-verdict.md §5).");
                    exit |= 4;
                }
            }
        }

        return exit;
    }

    static int Write(ScenarioVerdict verdict, Dictionary<string, string> opts, out string? path)
    {
        path = opts.TryGetValue("--out", out var requested) ? requested : null;
        if (path is null) return verdict.Ok ? 0 : 1;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, verdict.ToJson());
        return verdict.Ok ? 0 : 1;
    }

    static Dictionary<string, string> ParseArgs(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith('-')) continue;
            var value = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : "";
            opts[a] = value;
        }
        return opts;
    }
}
