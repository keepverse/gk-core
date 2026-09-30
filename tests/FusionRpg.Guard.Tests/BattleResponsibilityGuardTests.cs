using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The ownership-shaped battle responsibility guard (`gk-core/scripts/guard-battle-responsibility.py`,
/// solid-remediation G2; ported from `guard-battle-responsibility.ps1`).
///
/// <para><b>The defect it replaces.</b> `guard-class-system.ps1:129-150` is a <b>positive-presence</b>
/// check: one reference to a symbol anywhere satisfies it. That answers "does this symbol appear?" when
/// the rule is "is this mechanism decided in exactly one place?", which is why a hand-copied reflect
/// formula passes it today. These tests hold the new guard to the second question.</para>
///
/// <para>Every negative case uses a <b>synthetic fixture</b> rather than a real file, so that fixing a
/// real defect never turns one of these green by accident — and so they keep testing the guard after
/// `retaliation-shared` and `estimator-parity` remove the allowlist entries the shipped register
/// carries today.</para>
/// </summary>
[Trait("VerificationId", "guard.battle-responsibility")]
public sealed class BattleResponsibilityGuardTests
{
    /// <summary>A formula no real file contains, so a fixture's findings can only come from the fixture.</summary>
    const string FixtureFormula = "var zz = ZzRateDelta / policy.ZzGuardFixtureScale;";
    const string FixturePattern = @"/\s*[A-Za-z0-9_.]*ZzGuardFixtureScale";

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static (int Exit, string Output) RunGuard(string? root = null, string? registryPath = null)
    {
        var repo = RepoRoot();
        var script = Path.Combine(repo, "scripts", "guard-battle-responsibility.py");
        var args = $"\"{script}\"";
        if (root is not null) args += $" --root \"{root}\"";
        if (registryPath is not null) args += $" --registry-path \"{registryPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = args,
            WorkingDirectory = repo,
            CreateNoWindow = true
        };
        var (exit, stdout, stderr) = ExternalProcess.Run(psi, 300_000, "battle responsibility guard timed out");
        return (exit, stdout + stderr);
    }

    /// <summary>A throwaway tree with one mechanism whose owner is `src/Fake/Owner.cs`.</summary>
    sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string RegistryPath { get; }

        public Fixture(object registry)
        {
            Root = Path.Combine(Path.GetTempPath(), "battle-responsibility-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "src", "Fake"));
            Directory.CreateDirectory(Path.Combine(Root, "tools", "FakeTool"));
            Write("src/Fake/Owner.cs", $"namespace Fake;\npublic static class Owner {{ public static void M() {{ {FixtureFormula} }} }}\n");

            RegistryPath = Path.Combine(Root, "registry.json");
            File.WriteAllText(RegistryPath, JsonSerializer.Serialize(registry,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        public void Write(string relative, string content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            // A failed temp-delete is a failure, never a swallowed catch — testing-standard.md, after
            // an empty catch around Directory.Delete leaked 65.5 GB in one local run.
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    static object OneMechanism(object[]? allow = null) => new
    {
        schemaVersion = 1,
        scan = new[] { "src", "tools" },
        mechanisms = new object[]
        {
            new
            {
                id = 99,
                name = "Fixture mechanism",
                owner = "src/Fake/Owner.cs",
                decisions = new object[]
                {
                    new { id = "zz", what = "the fixture formula", pattern = FixturePattern, allow = allow ?? Array.Empty<object>() }
                }
            }
        }
    };

    [Fact]
    public void Refuses_a_second_implementation_of_a_registered_mechanism()
    {
        using var fixture = new Fixture(OneMechanism());
        fixture.Write("src/Fake/Copy.cs", $"namespace Fake;\npublic static class Copy {{ public static void M() {{ {FixtureFormula} }} }}\n");

        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit != 0, $"guard accepted a second owner\n{output}");
        Assert.Contains("src/Fake/Copy.cs", output, StringComparison.Ordinal);
        Assert.Contains("second owner", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Passes_when_the_second_occurrence_is_only_a_comment_or_a_string()
    {
        // A guard that reads source as text cannot tell a formula from a sentence about a formula.
        // Both directions of that mistake have shipped here: EntityFields12PlusGuardTests passed on a
        // commented-out write, and keymapGuard failed on a comment explaining its own rule.
        using var fixture = new Fixture(OneMechanism());
        fixture.Write("src/Fake/Prose.cs",
            $"namespace Fake;\n" +
            $"// This mechanism computes {FixtureFormula}\n" +
            $"/* and the block form: {FixtureFormula} */\n" +
            $"public static class Prose {{ public const string Doc = \"{FixtureFormula}\"; }}\n");

        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit == 0, $"guard failed on a comment and a string literal\n{output}");
    }

    [Fact]
    public void Scans_tools_as_well_as_src()
    {
        // G2's own finding: the guard it replaces scans two filenames and never tools/. The third
        // copy of the shipped reflect formula lives in gk-core/tools/CombatSim/Analytic.cs, and nothing had
        // ever looked there.
        using var fixture = new Fixture(OneMechanism());
        fixture.Write("tools/FakeTool/Tool.cs", $"namespace FakeTool;\npublic static class Tool {{ public static void M() {{ {FixtureFormula} }} }}\n");

        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit != 0, $"guard did not scan tools/\n{output}");
        Assert.Contains("tools/FakeTool/Tool.cs", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_allowlisted_second_owner_passes_and_names_the_module_that_removes_it()
    {
        using var fixture = new Fixture(OneMechanism(new object[]
        {
            new { path = "src/Fake/Copy.cs", module = "fixture-module", why = "the module that deletes this copy" }
        }));
        fixture.Write("src/Fake/Copy.cs", $"namespace Fake;\npublic static class Copy {{ public static void M() {{ {FixtureFormula} }} }}\n");

        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit == 0, $"guard refused a properly allowlisted entry\n{output}");
    }

    [Fact]
    public void Refuses_an_allowlist_entry_that_names_no_module()
    {
        // An exception with no module that removes it is how grandfathered debt becomes a template.
        using var fixture = new Fixture(OneMechanism(new object[]
        {
            new { path = "src/Fake/Copy.cs", why = "no module named" }
        }));
        fixture.Write("src/Fake/Copy.cs", $"namespace Fake;\npublic static class Copy {{ public static void M() {{ {FixtureFormula} }} }}\n");

        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit != 0, $"guard accepted an allowlist entry with no owning module\n{output}");
        Assert.Contains("names no owning module", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_registered_owner_that_no_longer_matches_its_own_pattern()
    {
        // A register naming a file that no longer decides the mechanism is worse than no register:
        // it reports one owner while the real one has moved somewhere nothing checks.
        using var fixture = new Fixture(OneMechanism());
        File.WriteAllText(Path.Combine(fixture.Root, "src", "Fake", "Owner.cs"),
            "namespace Fake;\npublic static class Owner { }\n");

        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit != 0, $"guard accepted an owner that owns nothing\n{output}");
        Assert.Contains("does not match its own decision pattern", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_mechanism_with_neither_a_pattern_nor_a_note()
    {
        // A row that says nothing looks exactly like a row that is enforced. The note is not an
        // exemption — it is the admission that this row is not yet mechanical.
        using var fixture = new Fixture(new
        {
            schemaVersion = 1,
            scan = new[] { "src", "tools" },
            mechanisms = new object[] { new { id = 99, name = "Silent row", owner = "src/Fake/Owner.cs", decisions = Array.Empty<object>() } }
        });

        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit != 0, $"guard accepted a row with no pattern and no note\n{output}");
        Assert.Contains("no decision patterns and no note", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Survives_a_directory_it_cannot_enumerate()
    {
        // LadderRestatementGuardTests was vetoed for a week by a `.tmp-*` directory whose ACL denied
        // enumeration: the guard threw, CI went red, and nothing was wrong with the code it guards.
        // An unreadable directory is not a finding.
        using var fixture = new Fixture(OneMechanism());
        var denied = Path.Combine(fixture.Root, "src", "Denied");
        Directory.CreateDirectory(denied);

        var acl = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = $"\"{denied}\" /deny \"*S-1-1-0:(OI)(CI)(RX)\"",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        acl.WaitForExit(60_000);

        try
        {
            // Prove the deny took effect. Without this the test would pass on any machine where the
            // ACL silently failed to apply — green for the wrong reason, which is the failure mode
            // this whole program exists to stop.
            Assert.Throws<UnauthorizedAccessException>(() => Directory.EnumerateDirectories(denied).ToArray());

            var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);
            Assert.True(exit == 0, $"guard threw or failed on an unreadable directory\n{output}");
        }
        finally
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "icacls",
                Arguments = $"\"{denied}\" /remove:d \"*S-1-1-0\"",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!.WaitForExit(60_000);
        }
    }

    /// <summary>
    /// A <c>shape: "banned"</c> decision is one that should exist NOWHERE — not even in the file
    /// registered as the mechanism's owner.
    ///
    /// <para>It exists because of what happened when T2.3 extracted the reflect formula into
    /// <c>ElementalResolver.RateFromZero</c>. The shape stopped appearing in the dispatcher, which is
    /// still the mechanism's owner, and the default rule — "the owner must match its own pattern" —
    /// then demanded the owner keep writing the very formula it had just stopped writing. A shape can
    /// outlive its owner, and the register has to be able to say so.</para>
    /// </summary>
    [Fact]
    public void A_banned_shape_fails_even_in_the_registered_owner_file()
    {
        using var fixture = new Fixture(new
        {
            schemaVersion = 1,
            scan = new[] { "src", "tools" },
            mechanisms = new object[]
            {
                new
                {
                    id = 99,
                    name = "Fixture mechanism",
                    owner = "src/Fake/Owner.cs",
                    decisions = new object[]
                    {
                        new { id = "zz", shape = "banned", what = "nobody writes this", pattern = FixturePattern, allow = Array.Empty<object>() }
                    }
                }
            }
        });

        // The fixture's Owner.cs carries the formula, and for a banned shape that is itself a finding.
        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit != 0, $"a banned shape was allowed in the owner file\n{output}");
        Assert.Contains("src/Fake/Owner.cs", output, StringComparison.Ordinal);
        Assert.DoesNotContain("does not match its own decision pattern", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_banned_shape_passes_when_nobody_writes_it()
    {
        using var fixture = new Fixture(new
        {
            schemaVersion = 1,
            scan = new[] { "src", "tools" },
            mechanisms = new object[]
            {
                new
                {
                    id = 99,
                    name = "Fixture mechanism",
                    owner = "src/Fake/Owner.cs",
                    decisions = new object[]
                    {
                        new { id = "zz", shape = "banned", what = "nobody writes this", pattern = "ZzNobodyWritesThisAnywhere", allow = Array.Empty<object>() }
                    }
                }
            }
        });

        var (exit, output) = RunGuard(fixture.Root, fixture.RegistryPath);

        Assert.True(exit == 0, $"a banned shape nobody writes was reported as a finding\n{output}");
    }

    [Fact]
    public void The_shipped_register_is_green_on_the_current_tree()
    {
        var (exit, output) = RunGuard();

        Assert.True(exit == 0, $"shipped register red\n{output}");
        Assert.Contains("BATTLE RESPONSIBILITY GUARD OK", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The register's size is a <b>closed vocabulary</b>, so pinning it is correct — the mirror image
    /// of the population rule, not an exception to it. The list is the owner's eleven plus eight
    /// additions accepted 2026-09-16, and it changes only when a human changes the law. A twentieth
    /// mechanism is a reviewed amendment to `battle-engine-ssot.md`, never something that ships
    /// because content grew, so this literal fails exactly when it should.
    ///
    /// <para>It skips <b>13</b> on purpose. That entry claimed forked intent declaration and was
    /// retracted the same day it was written: five `IIntentSource` implementations are five policies
    /// plugged into one seam, which is the shape the law wants. The gap keeps the retraction visible
    /// rather than renumbering it out of history.</para>
    /// </summary>
    [Fact]
    public void The_register_is_a_closed_vocabulary_of_nineteen_mechanisms_skipping_the_retracted_thirteen()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "battle-responsibility.v1.json")));

        var ids = doc.RootElement.GetProperty("mechanisms")
            .EnumerateArray()
            .Select(m => m.GetProperty("id").GetInt32())
            .ToArray();

        // The count is fully implied by the element-wise equality below -- never pinned separately
        // (population-pin SE3.4, 2026-09-20).
        Assert.Equal(Enumerable.Range(1, 12).Concat(Enumerable.Range(14, 7)).ToArray(), ids);
        Assert.DoesNotContain(13, ids);
    }

    [Fact]
    public void Every_shipped_row_is_either_mechanical_or_names_what_would_close_it()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "battle-responsibility.v1.json")));

        foreach (var mechanism in doc.RootElement.GetProperty("mechanisms").EnumerateArray())
        {
            var id = mechanism.GetProperty("id").GetInt32();
            var hasDecisions = mechanism.TryGetProperty("decisions", out var decisions)
                               && decisions.GetArrayLength() > 0;
            if (hasDecisions)
            {
                foreach (var decision in decisions.EnumerateArray())
                    foreach (var allow in decision.GetProperty("allow").EnumerateArray())
                        Assert.True(allow.TryGetProperty("module", out var module)
                                    && !string.IsNullOrWhiteSpace(module.GetString()),
                            $"#{id}: an allowlist entry names no module that removes it");
                continue;
            }

            Assert.True(mechanism.TryGetProperty("note", out var note)
                        && !string.IsNullOrWhiteSpace(note.GetString()),
                $"#{id}: no decision pattern and no note saying why");
            Assert.True(mechanism.TryGetProperty("waits-on", out var waits)
                        && !string.IsNullOrWhiteSpace(waits.GetString()),
                $"#{id}: a note with no waits-on is a shrug, not an admission");
        }
    }
}
