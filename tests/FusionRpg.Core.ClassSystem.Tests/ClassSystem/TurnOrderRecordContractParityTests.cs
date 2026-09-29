using System.Text.RegularExpressions;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>`battle-tempo` `forecast-rail` FR2 — the web contract's `TurnOrderEntry` type must name
/// exactly the same fields as the C# record, camelCase vs PascalCase. Mirrors
/// `UnitClassContractParityTests`' own established pattern exactly: parses the TS file's text
/// directly, no npm/vitest toolchain needed — "not read side by side" (that test's own acceptance
/// wording), so a field added to one side with the other forgotten fails here immediately.</summary>
public class TurnOrderRecordContractParityTests
{
    [Fact]
    public void TsTypeFieldsMatchTheCSharpRecordFields()
    {
        var repoRoot = FindRepoRoot();
        var tsText = File.ReadAllText(Path.Combine(repoRoot, "web", "fusion-rpg-web", "src", "contract", "types.ts"));
        var tsTextNoComments = Regex.Replace(tsText, @"//[^\n]*", "");

        var typeMatch = Regex.Match(tsTextNoComments, @"export type TurnOrderEntry = \{([^}]*)\};");
        Assert.True(typeMatch.Success, "could not find `export type TurnOrderEntry = { ... }` in types.ts");

        var tsFields = Regex.Matches(typeMatch.Groups[1].Value, @"(\w+)\s*:")
            .Select(m => m.Groups[1].Value)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.True(tsFields.Count > 0, "parsed zero TurnOrderEntry fields from types.ts");

        var csharpFields = typeof(TurnOrderEntry).GetProperties().Select(p => p.Name).ToList();
        string ToCamel(string pascal) => char.ToLowerInvariant(pascal[0]) + pascal[1..];
        var csharpAsCamel = csharpFields.Select(ToCamel).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(csharpAsCamel, tsFields);
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
