using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `data-tests-sharding` H1: the shard manifest `gk-core/scripts/test-shards.v1.json` partitions one
/// project's tests into disjoint process shards plus exactly one remainder, so `test_sharded.py`
/// (TVB1.2) can run each shard as its own `dotnet test` process and route around the process-global
/// SQLite in-memory VFS mutex (`test-architecture-audit.md` §3) instead of a thread cap.
///
/// H-T1–H-T4 assert the manifest's shape against the committed file and the real
/// `FusionRpg.Data.Tests` source tree — no store, no disk writes, no `dotnet test` invocation (that
/// is H-T6, over planted TRX files, once the runner exists in TVB1.2). Every falsifier builds its
/// manifest/registry fragment in memory; nothing here writes to disk.
///
/// Never asserted: shard count, tests per shard, or any wall-clock reading — those are re-measured
/// (TVB1.3), not pinned (`validation-ssot.md`).
/// </summary>
[Trait("VerificationId", "guard.test-shards")]
public sealed class TestShardManifestTests
{
    static readonly Regex NamespaceDeclaration =
        new(@"^namespace\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline | RegexOptions.Compiled);

    static readonly Regex TypeDeclaration = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|\s)*\b(?:class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "test-shards.v1.json"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static JsonDocument LoadManifest(string root) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "test-shards.v1.json")));

    static JsonDocument LoadRegistry(string root) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "verification-boundaries.v1.json")));

    static string Join(IReadOnlyList<string> violations) => string.Join("\n  ", violations);

    // ── H-T1: exactly one remainder shard per project ──────────────────────────────────────────

    [Fact]
    public void HT1_every_project_has_exactly_one_remainder_shard()
    {
        using var manifest = LoadManifest(RepoRoot());
        var violations = HT1Violations(manifest.RootElement);
        Assert.True(violations.Count == 0, "H-T1: " + Join(violations));
    }

    [Fact]
    public void HT1_falsifier_zero_or_two_remainder_shards_are_refused()
    {
        using var zero = JsonDocument.Parse("""{"projects":{"p":{"shards":[{"id":"a","prefixes":["X."]}]}}}""");
        Assert.Single(HT1Violations(zero.RootElement));

        using var two = JsonDocument.Parse("""{"projects":{"p":{"shards":[{"id":"a","remainder":true},{"id":"b","remainder":true}]}}}""");
        Assert.Single(HT1Violations(two.RootElement));
    }

    static List<string> HT1Violations(JsonElement root)
    {
        var violations = new List<string>();
        foreach (var project in root.GetProperty("projects").EnumerateObject())
        {
            var remainders = project.Value.GetProperty("shards").EnumerateArray()
                .Count(s => s.TryGetProperty("remainder", out var r) && r.ValueKind == JsonValueKind.True);
            if (remainders != 1) violations.Add($"{project.Name}: {remainders} remainder shards (want exactly 1)");
        }
        return violations;
    }

    // ── H-T2: root prefix, trailing '.', no prefix a substring of another ─────────────────────

    [Fact]
    public void HT2_every_prefix_is_rooted_and_none_is_a_substring_of_another()
    {
        var root = RepoRoot();
        using var manifest = LoadManifest(root);
        using var registry = LoadRegistry(root);
        var violations = HT2Violations(manifest.RootElement, registry.RootElement);
        Assert.True(violations.Count == 0, "H-T2: " + Join(violations));
    }

    [Fact]
    public void HT2_falsifier_unrooted_missing_dot_and_substring_collision_are_refused()
    {
        using var registry = JsonDocument.Parse("""{"projects":{"p":"tests/Fake.Tests/Fake.Tests.csproj"}}""");

        using var unrooted = JsonDocument.Parse("""{"projects":{"p":{"shards":[{"id":"a","prefixes":["Other.Thing."]},{"id":"rest","remainder":true}]}}}""");
        Assert.Single(HT2Violations(unrooted.RootElement, registry.RootElement));

        using var noDot = JsonDocument.Parse("""{"projects":{"p":{"shards":[{"id":"a","prefixes":["Fake.Tests.Thing"]},{"id":"rest","remainder":true}]}}}""");
        Assert.Single(HT2Violations(noDot.RootElement, registry.RootElement));

        using var collide = JsonDocument.Parse("""{"projects":{"p":{"shards":[{"id":"a","prefixes":["Fake.Tests.Items."]},{"id":"b","prefixes":["Fake.Tests.Items.Sub."]},{"id":"rest","remainder":true}]}}}""");
        Assert.Single(HT2Violations(collide.RootElement, registry.RootElement));
    }

    static List<string> HT2Violations(JsonElement manifestRoot, JsonElement registryRoot)
    {
        var violations = new List<string>();
        foreach (var project in manifestRoot.GetProperty("projects").EnumerateObject())
        {
            if (!registryRoot.GetProperty("projects").TryGetProperty(project.Name, out var csprojEl))
            {
                violations.Add($"{project.Name}: not a registered verification-boundaries project id");
                continue;
            }
            var rootNamespace = Path.GetFileNameWithoutExtension(csprojEl.GetString()!) + ".";

            var prefixes = new List<string>();
            foreach (var shard in project.Value.GetProperty("shards").EnumerateArray())
                if (shard.TryGetProperty("prefixes", out var arr))
                    foreach (var pfx in arr.EnumerateArray())
                        prefixes.Add(pfx.GetString()!);

            foreach (var prefix in prefixes)
            {
                if (!prefix.EndsWith('.')) violations.Add($"{project.Name}: prefix '{prefix}' does not end with '.'");
                if (!prefix.StartsWith(rootNamespace, StringComparison.Ordinal))
                    violations.Add($"{project.Name}: prefix '{prefix}' does not start with root namespace '{rootNamespace}'");
            }
            for (var i = 0; i < prefixes.Count; i++)
            for (var j = 0; j < prefixes.Count; j++)
            {
                if (i == j || prefixes[i] == prefixes[j]) continue;
                if (prefixes[i].Contains(prefixes[j], StringComparison.Ordinal))
                    violations.Add($"{project.Name}: prefix '{prefixes[j]}' is a substring of '{prefixes[i]}'");
            }
        }
        return violations;
    }

    // ── H-T3: every prefix names a real namespace or (namespace, class) pair ──────────────────

    [Fact]
    public void HT3_every_prefix_matches_a_real_namespace_or_class_in_source()
    {
        var root = RepoRoot();
        using var manifest = LoadManifest(root);
        using var registry = LoadRegistry(root);
        var violations = HT3Violations(root, manifest.RootElement, registry.RootElement);
        Assert.True(violations.Count == 0, "H-T3: " + Join(violations));
    }

    [Fact]
    public void HT3_falsifier_a_prefix_naming_no_real_namespace_or_class_is_refused()
    {
        var root = RepoRoot();
        using var registry = LoadRegistry(root);
        using var manifest = JsonDocument.Parse("""{"projects":{"data":{"shards":[{"id":"a","prefixes":["FusionRpg.Data.Tests.NoSuchNamespace."]},{"id":"rest","remainder":true}]}}}""");
        Assert.Single(HT3Violations(root, manifest.RootElement, registry.RootElement));
    }

    static List<string> HT3Violations(string root, JsonElement manifestRoot, JsonElement registryRoot)
    {
        var violations = new List<string>();
        foreach (var project in manifestRoot.GetProperty("projects").EnumerateObject())
        {
            if (!registryRoot.GetProperty("projects").TryGetProperty(project.Name, out var csprojEl))
            {
                violations.Add($"{project.Name}: not a registered verification-boundaries project id");
                continue;
            }
            var csprojPath = Path.Combine(root, csprojEl.GetString()!.Replace('/', Path.DirectorySeparatorChar));
            var projectDir = Path.GetDirectoryName(csprojPath)!;

            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            var classesByNamespace = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                var text = File.ReadAllText(file);
                var nsMatch = NamespaceDeclaration.Match(text);
                if (!nsMatch.Success) continue;
                var ns = nsMatch.Groups[1].Value;
                namespaces.Add(ns);
                if (!classesByNamespace.TryGetValue(ns, out var set))
                    classesByNamespace[ns] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in TypeDeclaration.Matches(text)) set.Add(m.Groups[1].Value);
            }

            foreach (var shard in project.Value.GetProperty("shards").EnumerateArray())
            {
                if (!shard.TryGetProperty("prefixes", out var arr)) continue;
                foreach (var pfxEl in arr.EnumerateArray())
                {
                    var raw = pfxEl.GetString()!;
                    var prefix = raw.TrimEnd('.');
                    if (namespaces.Contains(prefix)) continue;

                    var lastDot = prefix.LastIndexOf('.');
                    if (lastDot > 0)
                    {
                        var ns = prefix[..lastDot];
                        var cls = prefix[(lastDot + 1)..];
                        if (classesByNamespace.TryGetValue(ns, out var set) && set.Contains(cls)) continue;
                    }
                    violations.Add($"{project.Name}: prefix '{raw}' matches no namespace or class in source");
                }
            }
        }
        return violations;
    }

    // ── H-T4: shard ids unique; project ids exist in the verification registry ─────────────────

    [Fact]
    public void HT4_shard_ids_are_unique_and_project_ids_are_registered()
    {
        var root = RepoRoot();
        using var manifest = LoadManifest(root);
        using var registry = LoadRegistry(root);
        var violations = HT4Violations(manifest.RootElement, registry.RootElement);
        Assert.True(violations.Count == 0, "H-T4: " + Join(violations));
    }

    [Fact]
    public void HT4_falsifier_duplicate_shard_id_and_unknown_project_are_refused()
    {
        using var registry = JsonDocument.Parse("""{"projects":{}}""");
        using var manifest = JsonDocument.Parse("""{"projects":{"nope":{"shards":[{"id":"a","remainder":true},{"id":"a","prefixes":["X."]}]}}}""");
        Assert.Equal(2, HT4Violations(manifest.RootElement, registry.RootElement).Count);
    }

    static List<string> HT4Violations(JsonElement manifestRoot, JsonElement registryRoot)
    {
        var violations = new List<string>();
        foreach (var project in manifestRoot.GetProperty("projects").EnumerateObject())
        {
            if (!registryRoot.GetProperty("projects").TryGetProperty(project.Name, out _))
                violations.Add($"{project.Name}: not a registered verification-boundaries project id");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shard in project.Value.GetProperty("shards").EnumerateArray())
            {
                var id = shard.GetProperty("id").GetString()!;
                if (!seen.Add(id)) violations.Add($"{project.Name}: duplicate shard id '{id}'");
            }
        }
        return violations;
    }

    // ── H-T6: gk-core/scripts/test_sharded.py's overlap/empty-shard logic over PLANTED TRX files ───────
    //
    // No `dotnet test` runs here. `test_sharded.py --replay-results-root` skips the build and the
    // per-shard process spawn and reads a caller-supplied `<shard id>\*.trx` tree instead, so these
    // three cases exercise exactly the completeness/overlap logic H2 describes, over a tiny fixture
    // project/registry/manifest that never touches the real `data` project.

    sealed class Fixture : IDisposable
    {
        public readonly string Root;
        public readonly string RegistryPath;
        public readonly string ManifestPath;
        public readonly string ResultsRoot;
        public const string ProjectPath = "tests/Fake.Tests/Fake.Tests.csproj";

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "test-sharded-ht6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ResultsRoot = Path.Combine(Root, "results");
            Directory.CreateDirectory(ResultsRoot);

            RegistryPath = Path.Combine(Root, "registry.json");
            File.WriteAllText(RegistryPath, "{\"projects\":{\"fake\":\"" + ProjectPath + "\"}}");

            ManifestPath = Path.Combine(Root, "manifest.json");
            File.WriteAllText(ManifestPath, """
                {"schemaVersion":1,"projects":{"fake":{"maxParallelThreads":2,"shards":[
                    {"id":"a","prefixes":["Fake.Tests.A."]},
                    {"id":"b","prefixes":["Fake.Tests.B."]},
                    {"id":"rest","remainder":true}
                ]}}}
                """);
        }

        public void PlantTrx(string shardId, params string[] testFullNames)
        {
            var dir = Path.Combine(ResultsRoot, shardId);
            Directory.CreateDirectory(dir);
            var defs = string.Join("\n", testFullNames.Select((n, i) =>
            {
                var (cls, method) = SplitLast(n);
                return $"""<UnitTest id="{Guid.NewGuid():D}-{i}"><TestMethod className="{cls}" name="{method}" /></UnitTest>""";
            }));
            var ids = string.Join("\n", Enumerable.Range(0, testFullNames.Length)
                .Select(i => $"""<UnitTestResult testId="{ExtractId(defs, i)}" outcome="Passed" />"""));
            File.WriteAllText(Path.Combine(dir, $"{shardId}.trx"), $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <TestDefinitions>
                    {defs}
                  </TestDefinitions>
                  <Results>
                    {ids}
                  </Results>
                </TestRun>
                """);
        }

        static (string, string) SplitLast(string full)
        {
            var i = full.LastIndexOf('.');
            return (full[..i], full[(i + 1)..]);
        }

        // The test ids embedded in PlantTrx's own <UnitTest id="..."> text, recovered so <UnitTestResult>
        // can reference them without a second bookkeeping structure.
        static string ExtractId(string defsXml, int index)
        {
            var matches = Regex.Matches(defsXml, "id=\"([^\"]+)\"");
            return matches[index].Groups[1].Value;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    static string RepoRootForScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "test_sharded.py"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static (int Exit, string Output) RunSharded(Fixture fx)
    {
        var repo = RepoRootForScript();
        var script = Path.Combine(repo, "scripts", "test_sharded.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --project \"{Fixture.ProjectPath}\" " +
                        $"--registry-path \"{fx.RegistryPath}\" " +
                        $"--manifest-path \"{fx.ManifestPath}\" " +
                        $"--replay-results-root \"{fx.ResultsRoot}\"",
            WorkingDirectory = repo,
            CreateNoWindow = true
        };
        var (exit, stdout, stderr) = ExternalProcess.Run(psi, 60_000, "test_sharded.py replay timed out");
        return (exit, stdout + stderr);
    }

    [Fact]
    public void HT6_a_test_id_present_in_two_shards_fails_the_run()
    {
        using var fx = new Fixture();
        fx.PlantTrx("a", "Fake.Tests.A.Foo");
        fx.PlantTrx("b", "Fake.Tests.A.Foo"); // same id, wrong shard — the overlap
        fx.PlantTrx("rest", "Fake.Tests.Root.Bar");

        var (exit, output) = RunSharded(fx);
        Assert.NotEqual(0, exit);
        Assert.Contains("ran in two shards", output, StringComparison.Ordinal);
    }

    [Fact]
    public void HT6_an_empty_named_shard_fails_the_run()
    {
        using var fx = new Fixture();
        fx.PlantTrx("a", "Fake.Tests.A.Foo");
        // "b" plants nothing — a named shard with zero executed tests.
        fx.PlantTrx("rest", "Fake.Tests.Root.Bar");
        Directory.CreateDirectory(Path.Combine(fx.ResultsRoot, "b"));

        var (exit, output) = RunSharded(fx);
        Assert.NotEqual(0, exit);
        Assert.Contains("executed zero tests", output, StringComparison.Ordinal);
    }

    [Fact]
    public void HT6_an_empty_remainder_passes()
    {
        using var fx = new Fixture();
        fx.PlantTrx("a", "Fake.Tests.A.Foo");
        fx.PlantTrx("b", "Fake.Tests.B.Foo");
        // "rest" plants nothing — legal for the remainder alone.
        Directory.CreateDirectory(Path.Combine(fx.ResultsRoot, "rest"));

        var (exit, output) = RunSharded(fx);
        Assert.Equal(0, exit);
        Assert.Contains("TEST-SHARDED OK", output, StringComparison.Ordinal);
    }

    // ── H-T5: the CI Data.Tests line invokes the sharded runner with the csproj path,
    //          followed by its exit check (data-tests-sharding H3, lands with the CI commit) ──────

    [Trait("VerificationId", "guard.workflows")]
    [Fact]
    public void HT5_ci_runs_data_tests_through_the_sharded_runner()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRootForScript(), ".github", "workflows", "ci.yml"));
        var violations = HT5Violations(lines);
        Assert.True(violations.Count == 0, "H-T5: " + string.Join("\n  ", violations));
    }

    [Fact]
    public void HT5_falsifier_a_plain_dotnet_test_line_for_data_tests_is_refused()
    {
        string[] planted =
        {
            "          dotnet test tests/FusionRpg.Data.Tests/FusionRpg.Data.Tests.csproj -c Release --verbosity minimal --blame-hang --blame-hang-timeout 10min",
            "          if ($LASTEXITCODE -ne 0) { throw \"FusionRpg.Data.Tests failed\" }",
        };
        Assert.Single(HT5Violations(planted));
    }

    [Fact]
    public void HT5_falsifier_the_sharded_line_with_no_exit_check_is_refused()
    {
        string[] planted =
        {
            @"          python scripts/test_sharded.py --project tests/FusionRpg.Data.Tests/FusionRpg.Data.Tests.csproj",
            "          Write-Host \"no exit check\"",
        };
        Assert.Single(HT5Violations(planted));
    }

    // The port of the runner (test-sharded.ps1 -> gk-core/scripts/test_sharded.py) left this file naming the
    // RETIRED spelling, so H-T5 failed on a `ci.yml` that has been correct since the port: the guard
    // asked for a line that no longer exists anywhere. Recorded because the failure mode generalises --
    // a caller test that pins a tool's FILENAME goes red the moment the filename changes, and the
    // honest repair is to repoint the caller AND plant the retired spelling as a falsifier, so a
    // reintroduced PowerShell invocation is now a failure instead of an invisible pass.
    [Fact]
    public void HT5_falsifier_the_retired_powershell_invocation_is_refused()
    {
        string[] planted =
        {
            @"          .\scripts\test-sharded.ps1 -Project tests/FusionRpg.Data.Tests/FusionRpg.Data.Tests.csproj",
            "          if ($LASTEXITCODE -ne 0) { throw \"FusionRpg.Data.Tests failed (sharded)\" }",
        };
        // The exit check is correct; the invocation is a file this repository no longer has. One
        // violation, and it must be the "no line invokes" one rather than the exit-check one.
        var violations = HT5Violations(planted);
        Assert.Single(violations);
        Assert.Contains("no line invokes", violations[0]);
    }

    static List<string> HT5Violations(IReadOnlyList<string> lines)
    {
        const string sharded = "python scripts/test_sharded.py --project tests/FusionRpg.Data.Tests/FusionRpg.Data.Tests.csproj";
        var violations = new List<string>();
        var found = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() != sharded) continue;
            found = true;
            var next = i + 1 < lines.Count ? lines[i + 1] : string.Empty;
            if (!Regex.IsMatch(next, @"^\s*if \(\$LASTEXITCODE -ne 0\) \{ throw "))
                violations.Add($"line {i + 1}: the sharded-runner line is not followed by its exit check");
        }
        if (!found) violations.Add("no line invokes test_sharded.py with the Data.Tests csproj path");
        return violations;
    }
}
