using System.IO;

namespace FusionRpg.Core.Tests.Dungeon;

/// <summary>Locates the repo root from the test binary's output directory — the
/// <c>EligibilityAxisTests.FindRepoRoot</c> shape, reused so every dungeon test reads the real,
/// shipped registry and tuning files rather than a hand-transcribed copy (tunables-ssot.md §7.2:
/// "the balance surface is the file", not a fixture that can drift from it).</summary>
public static class DungeonTestFiles
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    public static string RegistryDir() => Path.Combine(RepoRoot(), "data", "seed", "dungeon", "_registry");
    public static string DungeonTuningPath() => Path.Combine(RepoRoot(), "data", "tuning", "dungeon.v3.json");
    public static string EncounterTuningPath() => Path.Combine(RepoRoot(), "data", "tuning", "encounter.v1.json");
    public static string ThreatTuningPath() => Path.Combine(RepoRoot(), "data", "tuning", "creature-threat.v2.json");

    /// <summary>The threat ladder's declaring read (tier-propagation-contract T-2) — every
    /// <c>EncounterTuningLoader.Parse</c> caller in tests validates against this, never a tuple.</summary>
    public static IReadOnlyList<string> ThreatRungIds() =>
        FusionRpg.Core.Creatures.Generation.CreatureThreatTuningLoader.Parse(
            File.ReadAllText(ThreatTuningPath())).RungIds;
    public static string NerveContainerPath() => Path.Combine(RepoRoot(), "data", "seed", "dungeon", "_containers", "nerve.v1.json");
    public static string LayoutsDir() => Path.Combine(RepoRoot(), "data", "seed", "dungeon", "layouts");
    public static string QuestsDir() => Path.Combine(RepoRoot(), "data", "seed", "dungeon", "quests");
    public static string EventsDir() => Path.Combine(RepoRoot(), "data", "seed", "dungeon", "events");
    public static string EncountersDir() => Path.Combine(RepoRoot(), "data", "seed", "dungeon", "encounters");
    public static string RoomsDir() => Path.Combine(RepoRoot(), "data", "seed", "dungeon", "rooms");
    public static string DomainsDir() => Path.Combine(RepoRoot(), "data", "seed", "dungeon", "domains");
    public static string SpeciesDir() => Path.Combine(RepoRoot(), "data", "seed", "creatures", "species");
}
