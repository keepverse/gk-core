using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P1.4 — the web contract's `UnitClass` union must name exactly the
/// same classes as the C# enum, camelCase vs PascalCase. Parses both files and asserts equality —
/// "not read side by side" (P1.4's own acceptance wording): a class added to one side with the other
/// forgotten fails here immediately instead of silently rendering wrong on the day it is read.</summary>
public class UnitClassContractParityTests
{
    [Fact]
    public void TsUnionMatchesCSharpEnum()
    {
        var repoRoot = FindRepoRoot();
        var tsText = File.ReadAllText(Path.Combine(repoRoot, "web", "fusion-rpg-web", "src", "contract", "types.ts"));
        // Strip `//` line comments first -- the union spans several lines with an explanatory comment
        // between the original ten members and the two class-system additions.
        var tsTextNoComments = Regex.Replace(tsText, @"//[^\n]*", "");

        var unionMatch = Regex.Match(tsTextNoComments, @"export type UnitClass =\s*((?:\s*\|\s*""[a-zA-Z]+""\s*)+);");
        Assert.True(unionMatch.Success, "could not find `export type UnitClass = ...` in types.ts");
        var tsMembers = Regex.Matches(unionMatch.Groups[1].Value, @"""([a-zA-Z]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(tsMembers.Count > 0, "parsed zero UnitClass members from types.ts");

        var csharpMembers = Enum.GetNames<UnitClass>();

        // camelCase (TS) <-> PascalCase (C#): lower the first letter and compare as sets.
        string ToCamel(string pascal) => char.ToLowerInvariant(pascal[0]) + pascal[1..];
        var csharpAsCamel = csharpMembers.Select(ToCamel).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var tsSorted = tsMembers.OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(csharpAsCamel, tsSorted);
    }

    [Fact]
    public void LedgerDocNamesEveryClass()
    {
        // spec-magnitude-and-units.md §3 is the authored ledger both the C# enum and the TS union
        // trace back to -- every enum member must appear there as a backtick-wrapped class name.
        var repoRoot = FindRepoRoot();
        var docText = File.ReadAllText(Path.Combine(repoRoot, "docs", "design", "spec-magnitude-and-units.md"));
        // The ledger's own count line. It read "twelve classes" until the world-numbers program
        // added `LoamUnits` on 2026-09-04 and updated the doc to "thirteen"; the C# enum was the
        // side that lagged. Asserting the doc's CURRENT count keeps this honest -- but the real
        // coverage is the per-member loop below, which does not depend on the prose at all.
        Assert.Contains("thirteen classes", docText, StringComparison.Ordinal);

        foreach (var name in Enum.GetNames<UnitClass>())
            Assert.Contains($"`{name}`", docText, StringComparison.Ordinal);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
