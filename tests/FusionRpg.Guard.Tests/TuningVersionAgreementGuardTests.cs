using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// ST5.1 (`spec-rung-table-activation.md` contract 3, spec test 3): the mechanism behind one Guard
/// theory, <c>TuningVersionAgreement(domain)</c> — a scan that finds every <c>&lt;domain&gt;.v&lt;n&gt;.json</c>
/// token inside a <b>string literal</b> under the roots it is given and reports the versions it saw.
///
/// <para><b>Agreement, never "latest".</b> Pointing every reader back at an older version to revert a
/// balance pass stays legal; what this refuses is two readers disagreeing, which is the drift that makes
/// a retune reach one path and not another.</para>
///
/// <para><b>The roots are parameters</b> (the acceptance's first clause), which is what lets this file
/// prove the mechanism against a fixture tree and a planted violation. The real-tree rows are added by
/// the tasks that make each domain agree — <c>action-rungs</c> by ST5.2, <c>action-base</c> by ST5.4 —
/// because asserting agreement before the readers agree would just be a red test standing in for
/// unfinished work.</para>
///
/// <para><b>What counts.</b> A token is read only when a quote follows the filename, so the many prose
/// mentions in comments and doc-comments are invisible by construction — and comment lines are skipped
/// first, whichever marker the language uses. A string literal is matched whatever precedes the filename
/// inside it (`"action-rungs.v1.json"`, a path, `pool.py`'s provenance string, a message), which is the
/// point: a message that names a stale file is exactly the drift this guard exists for.</para>
/// </summary>
public class TuningVersionAgreementGuardTests
{
    /// <summary>`<domain>.v<n>.json` immediately followed by a quote — i.e. the end of a string literal
    /// in C# or Python. The lookahead is what keeps this to literals rather than prose.</summary>
    static readonly Regex VersionedFile = new(
        @"(?<domain>[A-Za-z0-9_\-]+)\.v(?<version>\d+)\.json(?=[""'])",
        RegexOptions.Compiled);

    /// <summary>The roots a domain's readers live in: production C# and the seedsmith package. Tests,
    /// `bin`/`obj` and the wider `tools/` tree are not readers of the server's tuning.</summary>
    public static IReadOnlyList<string> DefaultRoots(string repoRoot) => new[]
    {
        Path.Combine(repoRoot, "src"),
        Path.Combine(KeepverseRoots.Forge(), "tools", "seedsmith", "seedsmith"),
    };

    /// <summary>Every version each domain is named at, over the given roots. Roots are parameters so a
    /// test can point the same scan at a fixture tree.</summary>
    public static Dictionary<string, SortedSet<int>> ScanVersions(IReadOnlyList<string> roots)
    {
        var found = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(path) is not (".cs" or ".py")) continue;
                // Never a build artifact: a copied tuning file under bin/ is not a reader.
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;

                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)
                        || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

                    foreach (Match match in VersionedFile.Matches(line))
                    {
                        var domain = match.Groups["domain"].Value;
                        var version = int.Parse(match.Groups["version"].Value);
                        if (!found.TryGetValue(domain, out var versions))
                            found[domain] = versions = new SortedSet<int>();
                        versions.Add(version);
                    }
                }
            }
        }

        return found;
    }

    /// <summary>One domain's versions over the real roots — the assertion the per-domain rows make.</summary>
    public static SortedSet<int> VersionsOf(string repoRoot, string domain) =>
        ScanVersions(DefaultRoots(repoRoot)).TryGetValue(domain, out var versions)
            ? versions
            : new SortedSet<int>();

    public static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string Fixture(params (string File, string Text)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "tuning-agreement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (file, text) in files)
            File.WriteAllText(Path.Combine(root, file), text);
        return root;
    }

    [Fact]
    public void The_scan_reads_a_string_literal_and_ignores_the_same_words_in_a_comment()
    {
        var root = Fixture(
            ("Reader.cs", "var path = \"data/tuning/action-rungs.v4.json\";\n"),
            ("Prose.cs", "// action-rungs.v1.json is mentioned here and is not a reader\n"),
            ("Doc.cs", "/// <see cref=\"x\"/> reads action-rungs.v2.json in prose only\n"));
        try
        {
            Assert.Equal(new[] { 4 }, ScanVersions(new[] { root })["action-rungs"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_reader_naming_a_second_version_is_the_disagreement_this_refuses()
    {
        var root = Fixture(
            ("Server.cs", "var a = \"action-rungs.v4.json\";\n"),
            ("Injector.py", "PROVENANCE = 'action-rungs.v1.json'\n"));
        try
        {
            var versions = ScanVersions(new[] { root })["action-rungs"];

            Assert.Equal(new[] { 1, 4 }, versions);
            Assert.True(versions.Count > 1, "two readers on two versions is what the guard must see");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Spec test 3's planted violation, and its own rule about the probe: the probe is written
    /// and removed INSIDE the test, and a failed delete fails the test — so the delete is asserted, never
    /// swallowed by a catch.</summary>
    [Fact]
    public void A_probe_holding_a_second_version_turns_a_green_scan_red_then_goes_away()
    {
        var root = Fixture(("Agreed.cs", "var a = \"action-rungs.v4.json\";\n"));
        var probe = Path.Combine(root, "Probe.cs");
        try
        {
            Assert.Single(ScanVersions(new[] { root })["action-rungs"]);

            File.WriteAllText(probe, "var b = \"action-rungs.v1.json\";\n");
            Assert.Equal(2, ScanVersions(new[] { root })["action-rungs"].Count);

            File.Delete(probe);
            Assert.False(File.Exists(probe), "the probe must really be gone");
            Assert.Equal(new[] { 4 }, ScanVersions(new[] { root })["action-rungs"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_build_artifact_is_not_a_reader()
    {
        var root = Fixture(("Reader.cs", "var a = \"action-rungs.v4.json\";\n"));
        var bin = Path.Combine(root, "bin", "Debug");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "Copied.cs"), "var b = \"action-rungs.v1.json\";\n");
        try
        {
            Assert.Equal(new[] { 4 }, ScanVersions(new[] { root })["action-rungs"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>One domain's versions over the given roots must be exactly one.</summary>
    public static void AssertAgrees(IReadOnlyList<string> roots, string domain)
    {
        var versions = ScanVersions(roots).TryGetValue(domain, out var seen) ? seen : new SortedSet<int>();

        Assert.True(versions.Count == 1,
                    $"{domain} is named at "
                    + (versions.Count == 0 ? "no version" : string.Join(", ", versions))
                    + " — one version per domain, or a retune reaches some readers and not others");
    }

    /// <summary>The per-domain real-tree rows. <c>action-base</c> is action-enrich's (ST5.4) and agrees
    /// today; <c>action-rungs</c> is ST5.2's, added when every reader moves to one version.</summary>
    public static IEnumerable<object[]> AgreedDomains() => new[]
    {
        new object[] { "action-base" },
        // ST5.2: added in the commit that switched every reader to v4, because the row can only be
        // green once they agree — and it is what fails if a future publish leaves one reader behind.
        new object[] { "action-rungs" },
        // element-catalog: added after the actor-HUD branch published element-catalog.v2 with the
        // hudGlyph column and moved ONLY the Injector reader to it (RpgHost.cs). The Server kept
        // reading v1, so the two readers disagreed: the HUD's glyphs were null from the served
        // catalog while the Injector saw them, and nothing in this suite noticed. The failure was
        // invisible from both ends — the branch's own review found it by diffing the two readers,
        // after the feature had already been built. The versions in use are [1, 1] today; the row
        // is green now and red for any future publish that leaves a reader behind.
        new object[] { "element-catalog" },
    };

    [Theory]
    [MemberData(nameof(AgreedDomains))]
    public void TuningVersionAgreement(string domain)
    {
        AssertAgrees(DefaultRoots(RepoRoot()), domain);
    }

    /// <summary>ST5.4's second clause: the row above must be able to FAIL. The same assertion is pointed
    /// at a fixture that starts agreed and then gains a second version, and it must go red naming the
    /// domain — otherwise a green row would prove nothing about the tree it reads.</summary>
    [Fact]
    public void The_action_base_row_fails_on_a_planted_mismatch()
    {
        var root = Fixture(("Server.cs", "var a = \"action-base.v2.json\";\n"));
        try
        {
            AssertAgrees(new[] { root }, "action-base");

            File.WriteAllText(Path.Combine(root, "Injector.cs"), "var b = \"action-base.v1.json\";\n");

            var thrown = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertAgrees(new[] { root }, "action-base"));
            Assert.Contains("action-base", thrown.Message);
            Assert.Contains("1, 2", thrown.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>ST5.2's second clause, clause-for-clause the same shape as the action-base row above:
    /// the <c>action-rungs</c> row must be able to FAIL too, or a green row would prove nothing about the
    /// tree it reads. The plant is <c>v1</c> because that is the version both hosts actually carried
    /// before ST5.2 — the real disagreement this row exists to catch, not a synthetic one.</summary>
    [Fact]
    public void The_action_rungs_row_fails_on_a_planted_mismatch()
    {
        var root = Fixture(("Server.cs", "var a = \"action-rungs.v4.json\";\n"));
        try
        {
            AssertAgrees(new[] { root }, "action-rungs");

            File.WriteAllText(Path.Combine(root, "Injector.cs"), "var b = \"action-rungs.v1.json\";\n");

            var thrown = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertAgrees(new[] { root }, "action-rungs"));
            Assert.Contains("action-rungs", thrown.Message);
            Assert.Contains("1, 4", thrown.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_scan_reaches_the_real_roots()
    {
        var found = ScanVersions(DefaultRoots(RepoRoot()));

        Assert.Contains("action-rungs", found.Keys);
        Assert.Contains("action-base", found.Keys);
    }
}
