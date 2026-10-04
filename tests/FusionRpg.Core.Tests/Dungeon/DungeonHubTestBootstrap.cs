using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Tests.Dungeon;

internal static class DungeonHubTestBootstrap
{
    /// <summary>Why the three Dungeon hubs were left unconfigured, or null when they were configured.
    /// Exposed so an absent content pack is REPORTABLE rather than silent.</summary>
    internal static string? RootResolutionRefusal { get; private set; }

    [ModuleInitializer]
    public static void Init()
    {
        try
        {
            var registries = DungeonRegistryLoader.LoadAll(DungeonTestFiles.RegistryDir());
            DungeonTuningHub.Configure(
                DungeonTuningLoader.Parse(File.ReadAllText(DungeonTestFiles.DungeonTuningPath()), registries));
            EncounterTuningHub.Configure(
                EncounterTuningLoader.Parse(File.ReadAllText(DungeonTestFiles.EncounterTuningPath()), registries,
                    DungeonTestFiles.ThreatRungIds()));
            DungeonRegistryHub.Configure(registries);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            RootResolutionRefusal =
                "CONTENT-ROOT-UNREACHABLE: the three Dungeon hubs were NOT configured because "
                + "data/seed/dungeon/_registry does not resolve from this checkout. " + ex.Message;
        }
    }
}
