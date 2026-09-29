using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `zomboss-commander-clock` SP7.3 — "the level is readable through one seam." Zomboss's commander
/// level (`rpg_actor_progression`, `kind='player'`, `type_id=0`, `empire_id='zomboss'`) has exactly
/// one reader today, <c>RpgStore.CommanderLevelOfUnlocked</c>/<c>CommanderLevelOf</c>
/// (`RpgStore.Progression.cs`). The spec's own R23 consumer contract: "the same seam is the only
/// level `ai-empire-species`' Zomboss commander pool reads — a guard test asserts no second Zomboss
/// commander-level reader." `ai-empire-species` (`empire-progression`) is not built yet, so this
/// guard's job today is to keep it that way until that module lands and to catch a future second
/// reader the instant one is added — the same allowlist shape
/// `LegacyEquipTableRetirementGuardTests` already establishes for a retired table.
/// </summary>
public class ZombossCommanderLevelSingleReaderGuardTests
{
    /// <summary>`SELECT level FROM rpg_actor_progression` (single column, no other columns) is the
    /// distinctive shape only `CommanderLevelOfUnlocked` uses — every other reader in this file
    /// (`ReadActorStateUnlocked`, `ReadEmpireActorUnlocked`, …) selects a wider column list. A
    /// second reader copying the same narrow shape would be exactly the kind of quiet duplicate this
    /// guard exists to catch, whether or not it also names `zomboss` — a general-purpose second
    /// reader that merely CAN be pointed at Zomboss is just as much a second seam.</summary>
    const string ReadPattern = "SELECT level FROM rpg_actor_progression";

    static readonly string AllowedFile = Path.Combine("src", "FusionRpg.Data", "Sqlite", "RpgStore.Progression.cs");

    [Fact]
    public void No_production_file_outside_RpgStore_Progression_reads_the_narrow_commander_level_shape()
    {
        var offenders = new List<string>();
        foreach (var file in ProductionSources())
        {
            var relative = Path.GetRelativePath(FindRepoRoot(), file);
            if (string.Equals(relative, AllowedFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (StripComments(File.ReadAllText(file)).Contains(ReadPattern, StringComparison.Ordinal))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "Zomboss's (and every empire's) commander level is readable through ONE seam, " +
            "RpgStore.CommanderLevelOf. These files also read the narrow `" + ReadPattern +
            "` shape: " + string.Join(", ", offenders) +
            ". A second reader belongs in ai-empire-species reading through CommanderLevelOf, never a new query.");
    }

    /// <summary>Inside the one allowed file, the pattern appears in exactly one statement — proving
    /// there is no second, copy-pasted instance of the same query even within
    /// `RpgStore.Progression.cs` itself.</summary>
    [Fact]
    public void The_pattern_appears_exactly_once_inside_the_one_allowed_file()
    {
        var text = StripComments(File.ReadAllText(Path.Combine(FindRepoRoot(), AllowedFile)));
        var mentions = Regex.Matches(text, Regex.Escape(ReadPattern)).Count;
        Assert.Equal(1, mentions);
    }

    static string StripComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx < 0 ? line : line[..idx];
        }));

    static IEnumerable<string> ProductionSources()
    {
        var root = FindRepoRoot();
        foreach (var project in new[] { "FusionRpg.Core", "FusionRpg.Data", "FusionRpg.Server", "FusionRpg.Contracts", "FusionRpg.Injector" })
        {
            var dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                if (rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                yield return file;
            }
        }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }
}
