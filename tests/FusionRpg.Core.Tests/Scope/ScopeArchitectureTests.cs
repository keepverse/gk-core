using System.Runtime.CompilerServices;
using Xunit;

namespace FusionRpg.Core.Tests.Scope;

/// <summary>
/// T4 (buff-debuff-scope-todo.md Phase 1): dependency direction stays outward. `Scope/` defines
/// vocabulary that `Battle/` and `World/` executors consume — never the reverse. Referencing
/// `FusionRpg.Contracts` is expected (T1's whole point) and deliberately not checked for here; only
/// the three Core-internal subsystems named in spec-scope-model.md's own Boundaries are banned.
/// Same source-scan technique T33 used for <c>StubIntentSource.cs</c> in the action program.
/// </summary>
public class ScopeArchitectureTests
{
    static readonly string[] BannedNamespaces =
    {
        "FusionRpg.Core.Battle",
        "FusionRpg.Core.World",
        "FusionRpg.Core.Effects",
    };

    static string ScopeDir([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(repo, "src", "FusionRpg.Core", "Scope");
    }

    /// <summary>The one scan both the real check and the planted-violation test exercise.</summary>
    static List<string> ScanForBannedReferences(string dir)
    {
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var banned in BannedNamespaces)
            {
                if (text.Contains(banned, StringComparison.Ordinal))
                    violations.Add($"{Path.GetFileName(file)} references {banned}");
            }
        }
        return violations;
    }

    [Fact]
    public void Nothing_under_Core_Scope_references_Battle_World_or_Effects()
    {
        var dir = ScopeDir();
        Assert.True(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Length > 0,
            "no scope files found to scan");

        var violations = ScanForBannedReferences(dir);
        Assert.True(violations.Count == 0,
            "Scope/ must never reference Battle/World/Effects runtime types:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("FusionRpg.Core.Battle")]
    [InlineData("FusionRpg.Core.World")]
    [InlineData("FusionRpg.Core.Effects")]
    public void A_planted_reference_to_each_banned_namespace_fails_the_same_scan(string bannedNamespace)
    {
        // A guard that cannot fail is decoration (P0.1's own acceptance line, reused here) — this
        // exercises the SAME ScanForBannedReferences the real test above calls, not a separate
        // re-assertion, matching ActionsPurityGuardTests' own planted-violation discipline.
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-scope-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Sneaky.cs"),
                $"using {bannedNamespace};\nnamespace X;\npublic class Sneaky {{}}\n");

            var violations = ScanForBannedReferences(tmp);
            Assert.Contains(violations, v => v.Contains(bannedNamespace, StringComparison.Ordinal));
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void A_reference_to_FusionRpg_Contracts_does_NOT_fail_the_scan()
    {
        // The expected, deliberate dependency (T1) must not be mistaken for a violation.
        var tmp = Path.Combine(Path.GetTempPath(), "fusionrpg-scope-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Fine.cs"),
                "using FusionRpg.Contracts;\nnamespace X;\npublic class Fine {}\n");

            Assert.Empty(ScanForBannedReferences(tmp));
        }
        finally { Directory.Delete(tmp, true); }
    }
}
