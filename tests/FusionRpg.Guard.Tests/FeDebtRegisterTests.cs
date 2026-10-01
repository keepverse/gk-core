using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The FE debt register's contract (`docs/architecture/fe-debt-register.md`, solid-remediation X4).
///
/// <para>⚠️ These assert the <b>schema and the closure</b> and never the row count — and never the
/// violation counts the rows quote either. Those counts are <b>readings taken on a date</b>; the FE is
/// a surface the owner has said will be rebuilt, so they move in both directions and pinning one would
/// turn an ordinary FE edit into a chore to bump a literal here.</para>
///
/// <para>What must hold is that a row is a <b>finding</b>: it names a defect, a place, what it blocks,
/// and whether anything back-end depends on it. That last verdict is the one
/// <c>solid-remediation</c> acts on — a row with a back-end dependency is not deferrable just because
/// it lives under <c>web/</c>.</para>
/// </summary>
[Trait("VerificationId", "guard.fe-debt-register")]
public sealed class FeDebtRegisterTests
{
    const string RegisterPath = "docs/architecture/fe-debt-register.md";

    /// <summary>The register is the WORKSPACE ROOT's document, and its `where` cells cite files in any
    /// repository - so both the register and each citation are resolved by asking which repository carries
    /// the path, gk-core first.</summary>
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    /// <summary>The repository carrying a cited path. A register whose citations are resolved against ONE
    /// repository rots the moment it cites another, and `docs/**` alone already did.</summary>
    static string? CarryingRoot(string repoRelativePath)
    {
        foreach (var root in new[] { KeepverseRoots.Core(), KeepverseRoots.Workspace(),
                                     KeepverseRoots.Fusion(), KeepverseRoots.Web(),
                                     KeepverseRoots.Content(), KeepverseRoots.Forge() })
        {
            if (File.Exists(Path.Combine(root, repoRelativePath.Replace('/', Path.DirectorySeparatorChar))))
                return root;
        }
        return null;
    }

    /// <summary>Every pipe line of the `## Rows` table whose first cell looks like `FE-nn`.</summary>
    static IReadOnlyList<string[]> Rows()
    {
        // The register lives at `docs/architecture/fe-debt-register.md` and the workspace root is the only
        // repository that carries it - gk-core has a `docs/` of its own, holding
        // `docs/research/class-system/real-runs`, so a directory-level check concludes gk-core owns `docs`
        // and this read misses. Measured, and it is why the failure read "the FE debt register is missing"
        // rather than anything about a malformed table.
        var path = Path.Combine(KeepverseRoots.Workspace(), RegisterPath);
        Assert.True(File.Exists(path),
                    $"the FE debt register is missing: {RegisterPath} (looked in {path})");

        var rows = new List<string[]>();
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal)) continue;
            var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length == 0) continue;
            if (!Regex.IsMatch(cells[0], @"^`FE-\d+`$")) continue;
            rows.Add(cells);
        }
        return rows;
    }

    [Fact]
    public void The_register_exists_and_holds_at_least_one_row()
    {
        // Liveness, not size: an empty table satisfies every per-row assertion below vacuously.
        Assert.NotEmpty(Rows());
    }

    [Fact]
    public void Every_row_carries_all_five_schema_fields()
    {
        foreach (var cells in Rows())
        {
            Assert.True(cells.Length == 5,
                $"{cells[0]}: expected id + what/where/blocks/be-depends, got {cells.Length} cells");
            for (var i = 0; i < cells.Length; i++)
                Assert.False(string.IsNullOrWhiteSpace(cells[i]),
                    $"{cells[0]}: field {i} is empty");
        }
    }

    [Fact]
    public void Every_row_states_what_it_blocks()
    {
        // The register's own rule: "the FE is ugly" is not a row, because it names nothing that
        // cannot proceed. A row whose `blocks` is a shrug is that row wearing a table cell.
        var shrugs = new[] { "nothing", "n/a", "none", "tbd", "todo", "unknown", "-", "—", "?" };
        foreach (var cells in Rows())
        {
            var blocks = cells[3];
            Assert.False(shrugs.Contains(blocks, StringComparer.OrdinalIgnoreCase),
                $"{cells[0]}: blocks '{blocks}' names nothing — that is a taste, not debt");
        }
    }

    [Fact]
    public void Every_row_returns_a_back_end_dependency_verdict()
    {
        // This is the column solid-remediation acts on, so it is a verdict, not prose: the cell must
        // OPEN with yes or no. Everything after is the reason, and the reason is welcome.
        foreach (var cells in Rows())
        {
            var verdict = cells[4].TrimStart('*', ' ').ToLowerInvariant();
            Assert.True(verdict.StartsWith("yes", StringComparison.Ordinal)
                     || verdict.StartsWith("no", StringComparison.Ordinal),
                $"{cells[0]}: be-depends '{cells[4]}' does not open with yes or no");
        }
    }

    [Fact]
    public void Every_row_points_at_something_that_still_exists()
    {
        var root = RepoRoot();
        foreach (var cells in Rows())
        {
            // A `where` cell may cite two sides of a divergence, or a guard plus a count, so the rule
            // is that at least one cited path resolves — a register whose every citation has rotted
            // is stale, and that is what this catches.
            var cited = Regex.Matches(cells[2], @"[A-Za-z0-9_./-]+\.(?:cs|tsx|ts|json|md|ps1)\b")
                .Select(m => m.Value)
                .ToArray();

            Assert.True(cited.Length > 0, $"{cells[0]}: where '{cells[2]}' cites no file at all");
            Assert.True(cited.Any(c => CarryingRoot(c) is not null),
                $"{cells[0]}: none of its cited paths exist any more: {string.Join(", ", cited)}");
        }
    }
}
