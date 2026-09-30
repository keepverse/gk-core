using System.Diagnostics;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `solid-enforcement` `commander-identity` SE4.4 — falsifiers for `guard-open-identity.py`'s two
/// invariants (spec-commander-identity.md "The regression guard"):
/// I1 (no `enum *Id`/`*Ids` under `Commanders/`/`World/`), I2 (no `switch` over an `EmpireId`/
/// `CommanderRef` value anywhere in `src/`). Same fixture-directory + external-process shape
/// `LawnRepositionSingleWriterGuardTests` already establishes for a PowerShell guard with no C#
/// entry point of its own.
/// </summary>
public class OpenIdentityGuardTests
{
    [Fact]
    public void PlantedViolation_I1_enum_named_Id_under_Commanders_fails_and_names_the_file()
    {
        var script = FindScript();
        var fixture = NewFixture("i1-id");
        try
        {
            var commandersDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Commanders");
            Directory.CreateDirectory(commandersDir);
            File.WriteAllText(
                Path.Combine(commandersDir, "BadEnum.cs"),
                "namespace X { public enum CommanderId { Dave, Zomboss } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0, $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
                            // STDERR, not stdout: the port routes findings to stderr and a clean verdict to
                // stdout, so a caller branches on the stream. The PowerShell original emitted both
                // with `Write-Host`, which writes the INFORMATION stream and is invisible to a
                // `2>&1` capture - the trap this migration exists to remove.
Assert.Contains("BadEnum.cs", stderr, StringComparison.Ordinal);
            Assert.Contains("CommanderId", stderr, StringComparison.Ordinal);
            Assert.Contains("I1", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void PlantedViolation_I1_enum_named_Ids_under_World_fails()
    {
        var script = FindScript();
        var fixture = NewFixture("i1-ids");
        try
        {
            var worldDir = Path.Combine(fixture, "src", "FusionRpg.Core", "World");
            Directory.CreateDirectory(worldDir);
            File.WriteAllText(
                Path.Combine(worldDir, "BadEnumPlural.cs"),
                "namespace X { public enum RegionIds { North, South } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0, $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("BadEnumPlural.cs", stderr, StringComparison.Ordinal);
            Assert.Contains("RegionIds", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void PlantedViolation_I2_switch_expression_over_EmpireId_fails_and_names_the_file()
    {
        var script = FindScript();
        var fixture = NewFixture("i2-expr");
        try
        {
            var badDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Somewhere");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(
                Path.Combine(badDir, "BadSwitchExpr.cs"),
                "namespace X { class Bad { string Name(EmpireId empire) => empire switch " +
                "{ EmpireId e when e.Value == \"dave\" => \"Dave\", _ => \"?\" }; } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0, $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("BadSwitchExpr.cs", stderr, StringComparison.Ordinal);
            Assert.Contains("I2", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void PlantedViolation_I2_switch_statement_over_CommanderRef_fails()
    {
        var script = FindScript();
        var fixture = NewFixture("i2-stmt");
        try
        {
            var badDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Somewhere");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(
                Path.Combine(badDir, "BadSwitchStmt.cs"),
                "namespace X { class Bad { void Do(CommanderRef commander) { switch (commander) " +
                "{ default: break; } } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit != 0, $"expected fail exit, got 0\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("BadSwitchStmt.cs", stderr, StringComparison.Ordinal);
            Assert.Contains("I2", stderr, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    /// <summary>The inverse test: a fixture shaped like the REAL, compliant tree (a directory
    /// lookup, an EmpireId parameter used without a switch, an *Id enum OUTSIDE Commanders/World)
    /// exits 0 — proving the guard exempts what it should, not merely that an empty tree is silent.</summary>
    [Fact]
    public void InverseTest_guard_exits_0_on_a_compliant_tree_shaped_like_the_real_one()
    {
        var script = FindScript();
        var fixture = NewFixture("inverse-compliant");
        try
        {
            var commandersDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Commanders");
            Directory.CreateDirectory(commandersDir);
            File.WriteAllText(
                Path.Combine(commandersDir, "ICommanderDirectory.cs"),
                "namespace X { public interface ICommanderDirectory { bool TryResolve(EmpireId empire, out string name); } " +
                "public readonly record struct EmpireId(string Value); public readonly record struct CommanderRef(string Value); }\n");

            // An *Id enum OUTSIDE Commanders/World (e.g. a closed atom-kind vocabulary) must NOT trip I1.
            var itemsDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Items");
            Directory.CreateDirectory(itemsDir);
            File.WriteAllText(
                Path.Combine(itemsDir, "AtomKind.cs"),
                "namespace X { public enum AtomKindId { Combat, Resource } }\n");

            // An EmpireId parameter read via if/else and equality, never switched — must not trip I2.
            var siteDir = Path.Combine(fixture, "src", "FusionRpg.Core", "Somewhere");
            Directory.CreateDirectory(siteDir);
            File.WriteAllText(
                Path.Combine(siteDir, "GoodSite.cs"),
                "namespace X { class Good { string Name(EmpireId empire, ICommanderDirectory dir) { " +
                "if (dir.TryResolve(empire, out var n)) return n; return \"?\"; } } }\n");

            var (exit, stdout, stderr) = RunScript(script, fixture);
            Assert.True(exit == 0, $"expected OK exit, got {exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("OPEN-IDENTITY GUARD OK", stdout, StringComparison.Ordinal);
        }
        finally { Cleanup(fixture); }
    }

    /// <summary>Proves the real, current tree is itself compliant — the acceptance's own "exits 0 on
    /// the real tree" clause, run against the actual repository root rather than a fixture.</summary>
    [Fact]
    public void The_real_tree_is_compliant()
    {
        var script = FindScript();
        var root = FindRepoRoot();
        var (exit, stdout, stderr) = RunScript(script, root);
        Assert.True(exit == 0, $"expected OK exit, got {exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("OPEN-IDENTITY GUARD OK", stdout, StringComparison.Ordinal);
    }

    static string NewFixture(string tag) =>
        Path.Combine(Path.GetTempPath(), "fusionrpg-open-identity-" + tag + "-" + Guid.NewGuid().ToString("N"));

    // testing-standard.md R3: a failed cleanup is a failure, never a swallowed catch. No SQLite
    // connection pooling is involved here (these fixtures are plain directories of .cs text files
    // for the guard script to scan, never an RpgStore) so the delete is expected to
    // always succeed; if it does not, the test must fail and say why, not hide it.
    static void Cleanup(string fixture)
    {
        Directory.Delete(fixture, recursive: true);
    }

    static (int Exit, string Stdout, string Stderr) RunScript(string script, string root)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{script}\" --root \"{root}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return ExternalProcess.Run(psi, 60_000, "guard script timed out");
    }

    static string FindScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var script = Path.Combine(dir.FullName, "scripts", "guard-open-identity.py");
            if (File.Exists(script)) return script;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the guard-open-identity.py script");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
