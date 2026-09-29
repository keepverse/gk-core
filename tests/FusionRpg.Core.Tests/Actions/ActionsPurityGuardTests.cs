using System.Runtime.CompilerServices;
using FusionRpg.Core.Tests.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// P0.1 (action-todo.md): the purity scan extended to <c>Core/Actions/</c> before the first line of
/// action code lands. Purity rules (wall clock, ambient RNG, dictionary enumeration) are ON with no
/// exceptions — floating point is not a purity rule since the 2026-09-15 owner ruling; tick-path rules (LINQ, scene scans) are OFF for the whole directory —
/// <c>TargetResolver</c> and the runtime generator (A13) need LINQ, and their allocation is asserted
/// directly by their own tests rather than by this blunt static ban.
///
/// Reuses <see cref="KernelPurityScan"/> — the shape is "point the same scanner at a second
/// directory and add one exemption entry", not new machinery.
/// </summary>
public class ActionsPurityGuardTests
{
    static string ActionsDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                                  // tests/.../Actions
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));        // repo root
        return Path.Combine(repo, "src", "FusionRpg.Core", "Actions");
    }

    [Fact]
    public void Action_sources_contain_no_wall_clock_ambient_rng_or_dictionary_enumeration()
    {
        var dir = ActionsDir();
        Assert.True(Directory.Exists(dir), $"action source dir not found: {dir}");
        Assert.True(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Length > 0,
            "purity scan found no action files to check");

        var offences = KernelPurityScan.Scan(dir);
        Assert.True(offences.Count == 0,
            "action-layer purity violated (no wall clock, no ambient RNG, " +
            "no dictionary enumeration):\n" + string.Join("\n", offences));
    }

    [Theory]
    [InlineData("var t = DateTime.UtcNow;", "DateTime")]
    [InlineData("readonly Random _rng = new();", "Random")]
    [InlineData("var g = Guid.NewGuid();", "Guid.NewGuid")]
    [InlineData("var h = key.GetHashCode();", ".GetHashCode(")]
    public void A_planted_violation_still_fails_inside_Actions(string badLine, string expectedToken)
    {
        // A guard that cannot fail is decoration (P0.1 acceptance). Proves purity is still ON for
        // the new directory even though tick-path rules below are OFF for it.
        var tmp = NewTempRoot();
        try
        {
            var actionsSub = Path.Combine(tmp, "Actions");
            Directory.CreateDirectory(actionsSub);
            File.WriteAllLines(Path.Combine(actionsSub, "Offender.cs"),
                new[] { "namespace X;", "public sealed class Offender", "{", "    " + badLine, "}" });

            var offences = KernelPurityScan.Scan(tmp);
            Assert.Contains(offences, o => o.Contains(expectedToken, StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void A_planted_dictionary_enumeration_still_fails_inside_Actions()
    {
        var tmp = NewTempRoot();
        try
        {
            var actionsSub = Path.Combine(tmp, "Actions");
            Directory.CreateDirectory(actionsSub);
            File.WriteAllLines(Path.Combine(actionsSub, "Offender.cs"), new[]
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
    public void A_planted_LINQ_call_does_NOT_fail_inside_Actions()
    {
        // Tick-path rules are off for the whole directory — TargetResolver needs LINQ. This is the
        // acceptance line from action-todo.md P0.1 read literally: "a planted .Where( does not."
        var tmp = NewTempRoot();
        try
        {
            var actionsSub = Path.Combine(tmp, "Actions", "Targeting");
            Directory.CreateDirectory(actionsSub);
            File.WriteAllLines(Path.Combine(actionsSub, "Resolver.cs"), new[]
            {
                "using System.Collections.Generic;",
                "using System.Linq;",
                "public sealed class Resolver",
                "{",
                "    void A(List<int> items) { var x = items.Where(i => i > 0).ToList(); }",
                "}"
            });

            var offences = KernelPurityScan.Scan(tmp);
            Assert.DoesNotContain(offences, o => o.Contains(".Where(", StringComparison.Ordinal));
            Assert.DoesNotContain(offences, o => o.Contains(".ToList(", StringComparison.Ordinal));
            Assert.DoesNotContain(offences, o => o.Contains("using System.Linq", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void The_tick_path_exemption_does_not_leak_outside_Actions()
    {
        // The exemption is a named directory prefix, not a global relaxation. Anything outside
        // Actions/ — including the battle kernel this scan was built for — keeps the tick-path ban.
        var tmp = NewTempRoot();
        try
        {
            var other = Path.Combine(tmp, "Battle", "Timeline");
            Directory.CreateDirectory(other);
            File.WriteAllLines(Path.Combine(other, "Sneaky.cs"), new[]
            {
                "using System.Collections.Generic;",
                "using System.Linq;",
                "public sealed class Sneaky",
                "{",
                "    void A(List<int> items) { var x = items.Where(i => i > 0); }",
                "}"
            });

            Assert.Contains(KernelPurityScan.Scan(tmp), o => o.Contains(".Where(", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    [Fact]
    public void A_directory_merely_named_similarly_gets_no_relief()
    {
        // "Actions/" is a prefix match on the relative path, not a substring match on the name —
        // ActionsHelper/ must not inherit the exemption meant for Actions/.
        var tmp = NewTempRoot();
        try
        {
            var lookalike = Path.Combine(tmp, "ActionsHelper");
            Directory.CreateDirectory(lookalike);
            File.WriteAllLines(Path.Combine(lookalike, "Sneaky.cs"), new[]
            {
                "using System.Collections.Generic;",
                "using System.Linq;",
                "public sealed class Sneaky",
                "{",
                "    void A(List<int> items) { var x = items.Where(i => i > 0); }",
                "}"
            });

            Assert.Contains(KernelPurityScan.Scan(tmp), o => o.Contains(".Where(", StringComparison.Ordinal));
        }
        finally { Cleanup(tmp); }
    }

    static string NewTempRoot()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-actions-purity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    static void Cleanup(string dir)
    {
        Directory.Delete(dir, true);
    }
}
