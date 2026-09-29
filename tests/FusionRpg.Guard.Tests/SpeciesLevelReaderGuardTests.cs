using System;
using System.IO;
using System.Linq;
using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `ai-empire-species` EP4.13 (spec test 7) - **one species-level reader.** Two storages must never become
/// two readers, so this scans the Data layer's own sources and asserts that a direct species-level read
/// appears only where it is allowed: the reader itself (`RpgStore.EmpireSpecies.cs`), the writer
/// (`RpgStore.Progression.cs`, which reads the row it just wrote), and the two pre-existing aptitude sites
/// in `RpgStore.Aptitudes.cs` that `EP4.15` converts to the reader. That last entry is DEBT with an owner
/// and a task id, not a loophole: it shrinks to nothing when EP4.15 lands, and a NEW file making such a
/// read fails here.
/// </summary>
public class SpeciesLevelReaderGuardTests
{
    static readonly string[] AllowedFiles =
    {
        "RpgStore.EmpireSpecies.cs",   // the reader (EP4.13)
        "RpgStore.Progression.cs",     // the writer
        "RpgStore.Aptitudes.cs",       // EP4.15's debt: the two Empty guards
    };

    [Fact]
    public void Only_the_reader_and_the_writer_make_a_direct_species_level_read()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Data", "Sqlite");
        var offenders = Directory.EnumerateFiles(dir, "*.cs")
            .Where(f => ReadsSpeciesLevel(File.ReadAllText(f)))
            .Select(f => Path.GetFileName(f)!)
            .Where(name => !AllowedFiles.Contains(name, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_guard_rejects_planted_drift()
    {
        // A guard never proven to fail is not evidence: the same predicate must reject a synthetic line.
        Assert.True(ReadsSpeciesLevel(
            "var level = GetRpgActor(playerId, RpgActorKinds.Species, typeId)?.Level ?? 1;"));
        Assert.True(ReadsSpeciesLevel(
            "var level = ReadActorDtoUnlocked(db, playerId, RpgActorKinds.Species, typeId)?.Level ?? 1;"));
        // ... and must NOT fire on a write, a kind comparison, or an id binding.
        Assert.False(ReadsSpeciesLevel(
            "if (kind == RpgActorKinds.Species) TryApplyXpUnlocked(db, owner, kind, typeId, ...);"));
        Assert.False(ReadsSpeciesLevel("cmd.Parameters.AddWithValue(\"$k\", RpgActorKinds.Species);"));
    }

    [Fact]
    public void The_reader_file_exists_where_the_guard_expects_it()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Data", "Sqlite");
        Assert.Contains("RpgStore.EmpireSpecies.cs",
            Directory.EnumerateFiles(dir, "*.cs").Select(f => Path.GetFileName(f)!).ToArray());
    }

    /// <summary>A direct species-level read: a level field or reader call applied to the species kind on
    /// the same line. Deliberately narrow, so it cannot fire on the writer's own calls.</summary>
    static bool ReadsSpeciesLevel(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("RpgActorKinds.Species", StringComparison.Ordinal)) continue;
            if (line.Contains("GetRpgActor(", StringComparison.Ordinal)
                || line.Contains("ReadActorDtoUnlocked(", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
