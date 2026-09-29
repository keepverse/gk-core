using System.Runtime.CompilerServices;
using FusionRpg.Core.Tests.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Scope;

/// <summary>
/// T4 (buff-debuff-scope-todo.md Phase 1): the purity scan extended to <c>Core/Scope/</c>. No
/// tick-path exemption — unlike <c>Actions/</c>, nothing here needs LINQ, so the default kernel-wide
/// ban stays on, unweakened. Same shape as <c>ActionsPurityGuardTests</c> (P0.1's own precedent):
/// point the same scanner at a second directory.
/// </summary>
public class ScopePurityGuardTests
{
    static string ScopeDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(repo, "src", "FusionRpg.Core", "Scope");
    }

    [Fact]
    public void Scope_sources_contain_no_wall_clock_ambient_rng_or_dictionary_enumeration()
    {
        var dir = ScopeDir();
        Assert.True(Directory.Exists(dir), $"scope source dir not found: {dir}");
        Assert.True(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Length > 0,
            "purity scan found no scope files to check");

        var offences = KernelPurityScan.Scan(dir);
        Assert.True(offences.Count == 0,
            "scope-layer purity violated (no wall clock, no ambient RNG, " +
            "no dictionary enumeration):\n" + string.Join("\n", offences));
    }

    [Theory]
    [InlineData("var t = DateTime.UtcNow;", "DateTime")]
    [InlineData("readonly Random _rng = new();", "Random")]
    [InlineData("var g = Guid.NewGuid();", "Guid.NewGuid")]
    [InlineData("var h = key.GetHashCode();", ".GetHashCode(")]
    public void A_planted_violation_still_fails_inside_Scope(string badLine, string expectedToken)
    {
        var tmp = NewTempRoot();
        try
        {
            var scopeSub = Path.Combine(tmp, "Scope");
            Directory.CreateDirectory(scopeSub);
            File.WriteAllLines(Path.Combine(scopeSub, "Offender.cs"),
                new[] { "namespace X;", "public sealed class Offender", "{", "    " + badLine, "}" });

            var offences = KernelPurityScan.Scan(tmp);
            Assert.Contains(offences, o => o.Contains(expectedToken, StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void A_planted_dictionary_enumeration_still_fails_inside_Scope()
    {
        var tmp = NewTempRoot();
        try
        {
            var scopeSub = Path.Combine(tmp, "Scope");
            Directory.CreateDirectory(scopeSub);
            File.WriteAllLines(Path.Combine(scopeSub, "Offender.cs"), new[]
            {
                "namespace X;",
                "using System.Collections.Generic;",
                "public sealed class Offender",
                "{",
                "    void A(Dictionary<string, int> d) { foreach (var k in d.Keys) { } }",
                "}"
            });

            Assert.Contains(KernelPurityScan.Scan(tmp), o => o.Contains(".Keys", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void A_planted_LINQ_call_DOES_fail_inside_Scope_unlike_Actions()
    {
        // No tick-path exemption for this directory (spec-scope-model.md Project structure) —
        // nothing under Scope/ needs LINQ, so the default kernel-wide ban stays on, unweakened.
        var tmp = NewTempRoot();
        try
        {
            var scopeSub = Path.Combine(tmp, "Scope");
            Directory.CreateDirectory(scopeSub);
            File.WriteAllLines(Path.Combine(scopeSub, "Offender.cs"), new[]
            {
                "using System.Collections.Generic;",
                "using System.Linq;",
                "public sealed class Offender",
                "{",
                "    void A(List<int> items) { var x = items.Where(i => i > 0).ToList(); }",
                "}"
            });

            Assert.Contains(KernelPurityScan.Scan(tmp), o => o.Contains(".Where(", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    static string NewTempRoot()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-scope-purity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    static void Cleanup(string dir)
    {
        Directory.Delete(dir, true);
    }
}
