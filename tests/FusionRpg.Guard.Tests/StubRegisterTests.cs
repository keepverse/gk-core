using System.Linq;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The stub register's contract (`docs/architecture/stub-register.md`, solid-remediation G1).
///
/// <para>⚠️ These assert the <b>schema and the closure</b> — every row names a finding and an owner —
/// and never the row count. The number of rows is a <b>reading</b> that grows whenever work uncovers
/// debt; pinning it would fail the day someone registers a stub honestly, and the "fix" would be to
/// bump a literal. That is the validation-ssot rule this repo states outright.</para>
/// </summary>
[Trait("VerificationId", "guard.stub-register")]
public sealed class StubRegisterTests
{
    const string RegisterPath = "docs/architecture/stub-register.md";

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>The rows of the `## Rows` table: every pipe line whose first cell looks like `SR-nn`.</summary>
    static IReadOnlyList<string[]> Rows()
    {
        var path = Path.Combine(RepoRoot(), RegisterPath);
        Assert.True(File.Exists(path), $"the stub register is missing: {RegisterPath}");

        var rows = new List<string[]>();
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal)) continue;
            var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length == 0) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(cells[0], @"^`SR-\d+`$")) continue;
            rows.Add(cells);
        }
        return rows;
    }

    [Fact]
    public void The_register_exists_and_holds_at_least_one_row()
    {
        // Liveness, not size: a register that parses to nothing would satisfy every per-row assertion
        // below vacuously.
        Assert.NotEmpty(Rows());
    }

    [Fact]
    public void Every_row_carries_all_six_schema_fields()
    {
        foreach (var cells in Rows())
        {
            Assert.True(cells.Length == 7,
                $"{cells[0]}: expected id + 6 fields, got {cells.Length} cells");
            for (var i = 0; i < cells.Length; i++)
                Assert.False(string.IsNullOrWhiteSpace(cells[i]),
                    $"{cells[0]}: field {i} is empty — a row missing a field is a to-do, not debt");
        }
    }

    [Fact]
    public void Every_row_declares_a_kind_from_the_closed_vocabulary()
    {
        // The kind IS the distinction the register exists to record, so this vocabulary is closed and
        // pinning it is correct — a new kind is a reviewed change, not content growth. `unowned` was
        // added 2026-09-17 when `battle-responsibility-guard` produced a finding neither of the first
        // two described — a responsibility the law names with nothing implementing it. `solid` was
        // added 2026-09-19 by SE0.8: a SOLID-violating SHAPE that is known, measured, unfixed and
        // working, which is neither unreached (`dark`) nor unimplemented (`unowned`). `red` was added
        // 2026-09-19 by `python-test-lane` D6: a committed test that fails on a clean HEAD because the
        // test is right and the tree is wrong — neither refusing by design (`stub`), unreached
        // (`dark`), unimplemented (`unowned`) nor a working-but-wrong shape (`solid`).
        var allowed = new[] { "stub", "dark", "unowned", "solid", "red" };
        foreach (var cells in Rows())
            Assert.True(allowed.Contains(cells[1]),
                $"{cells[0]}: kind '{cells[1]}' is not one of " + string.Join("/", allowed));
    }

    [Fact]
    public void Every_row_names_a_finding_and_an_owner()
    {
        foreach (var cells in Rows())
        {
            var waitsOn = cells[4];
            var owner = cells[5];
            Assert.False(string.IsNullOrWhiteSpace(waitsOn), $"{cells[0]}: no waits-on");
            Assert.False(string.IsNullOrWhiteSpace(owner), $"{cells[0]}: no owner");

            // "someday", "TBD" and friends are how a register turns back into a wish list.
            foreach (var weasel in new[] { "someday", "tbd", "todo", "unknown", "?" })
                Assert.False(string.Equals(waitsOn, weasel, StringComparison.OrdinalIgnoreCase),
                    $"{cells[0]}: waits-on '{waitsOn}' names no finding");
        }
    }

    /// <summary>An open (not struck) `solid` row must wait on a real solid-enforcement module id.
    /// "waits on someday" is how a known SOLID shape becomes permanent, and a `solid` row is closed by
    /// the module named here, in that module's last commit.</summary>
    static IReadOnlyList<string> OpenSolidRowsWithoutAModule(IEnumerable<string[]> rows, IReadOnlySet<string> modules) =>
        rows.Where(c => c.Length > 4 && c[1] == "solid" && !c[0].StartsWith("~~", StringComparison.Ordinal))
            .Where(c => !modules.Contains(c[4].Trim('`')))
            .Select(c => $"{c[0]}: waits-on '{c[4]}' is not a solid-enforcement module id")
            .ToList();

    [Fact]
    public void Every_open_solid_row_waits_on_a_module_of_the_enforcement_program()
    {
        var violations = OpenSolidRowsWithoutAModule(Rows(), EnforcementMap.ModuleIds(RepoRoot()));
        Assert.True(violations.Count == 0, string.Join("\n  ", violations));
    }

    [Fact]
    public void Every_open_solid_row_falsifier_a_row_waiting_on_no_module_is_refused()
    {
        string[][] planted =
        {
            new[] { "`SR-99`", "solid", "a shape", "`src/FusionRpg.Data/Sqlite/RpgStore.cs`", "someday-module", "solid-enforcement", "no" },
        };

        Assert.Single(OpenSolidRowsWithoutAModule(planted, new HashSet<string>(new[] { "srp-file-budget" }, StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_row_points_at_a_file_that_exists()
    {
        var root = RepoRoot();
        foreach (var cells in Rows())
        {
            var where = cells[3].Trim('`');
            var filePart = where.Split(':')[0];
            Assert.True(File.Exists(Path.Combine(root, filePart)),
                $"{cells[0]}: where '{where}' names a file that does not exist — the register is stale");
        }
    }
}
