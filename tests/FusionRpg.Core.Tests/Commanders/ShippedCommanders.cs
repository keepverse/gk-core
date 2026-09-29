using System;
using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Commanders;

namespace FusionRpg.Core.Tests.Commanders;

/// <summary>
/// The authored <c>default-commanders.v1.json</c> as a directory, for Core tests that need the shipped
/// commander rows (stable ids, empires, display names, defaults) rather than a hardcoded pair. The
/// registry is a closed authored vocabulary, so a test may look its rows up by well-known empire.
/// </summary>
public static class ShippedCommanders
{
    public static DataCommanderDirectory Directory { get; } = Load();

    public static CommanderRef Dave => Directory.DefaultFor(EmpireId.Dave);
    public static CommanderRef Zomboss => Directory.DefaultFor(EmpireId.Zomboss);

    static DataCommanderDirectory Load([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var root = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        var path = Path.Combine(root, "data", "seed", "commanders", "_registry", "default-commanders.v1.json");
        return DataCommanderDirectory.Parse(File.ReadAllText(path));
    }
}
