// battle-tempo forecast-rail FR2, executed standalone (Core.Tests blocked). Mirrors
// gk-core/tests/FusionRpg.Core.ClassSystem.Tests/ClassSystem/TurnOrderRecordContractParityTests.cs case-for-case.

using System.Text.RegularExpressions;
using FusionRpg.Core.Battle.Timeline;

var failures = 0;
void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS  {name}"); return; }
    Console.WriteLine($"FAIL  {name}");
    failures++;
}

string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
        dir = dir.Parent!;
    }
    throw new DirectoryNotFoundException("repo root");
}

var repoRoot = FindRepoRoot();
var tsText = File.ReadAllText(Path.Combine(repoRoot, "web", "fusion-rpg-web", "src", "contract", "types.ts"));
var tsTextNoComments = Regex.Replace(tsText, @"//[^\n]*", "");

var typeMatch = Regex.Match(tsTextNoComments, @"export type TurnOrderEntry = \{([^}]*)\};");
Check("FoundTurnOrderEntryInTypesTs", typeMatch.Success);

var tsFields = Regex.Matches(typeMatch.Groups[1].Value, @"(\w+)\s*:")
    .Select(m => m.Groups[1].Value)
    .OrderBy(x => x, StringComparer.Ordinal)
    .ToList();
Console.WriteLine($"  TS fields: {string.Join(", ", tsFields)}");
Check("ParsedNonZeroFields", tsFields.Count > 0);

var csharpFields = typeof(TurnOrderEntry).GetProperties().Select(p => p.Name).ToList();
string ToCamel(string pascal) => char.ToLowerInvariant(pascal[0]) + pascal[1..];
var csharpAsCamel = csharpFields.Select(ToCamel).OrderBy(x => x, StringComparer.Ordinal).ToList();
Console.WriteLine($"  C# fields (camel): {string.Join(", ", csharpAsCamel)}");

Check("TsTypeFieldsMatchTheCSharpRecordFields", csharpAsCamel.SequenceEqual(tsFields));

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PROBES PASSED" : $"{failures} PROBE(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
