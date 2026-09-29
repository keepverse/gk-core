using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Tests.Dungeon;

/// <summary>
/// Configures the three Dungeon hubs once for the whole assembly, in Program.cs's own boot order
/// (registries load first, pure; `DungeonTuningHub`/`EncounterTuningHub` next, cross-checked against
/// those registries at parse time; `DungeonRegistryHub` last) — so any test under
/// `Delve/Difficulty` that reaches <see cref="RungTable"/>/<see cref="Delve.Difficulty.PermadeathGate"/>
/// or a catalog (<see cref="DifficultyRungCatalog"/>, <see cref="BandCatalog"/>) finds it configured
/// regardless of which test class xunit happens to run first — matches <c>ContractTuningTestBootstrap</c>'s
/// module-initializer shape, but reads the real, shipped files (`DungeonTestFiles`) rather than a
/// hand-built object graph, since that is this registry's own established convention
/// (`DungeonTuningTests`' doc comment: "a fixture copy could drift from what ships").
/// </summary>
internal static class DungeonHubTestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        var registries = DungeonRegistryLoader.LoadAll(DungeonTestFiles.RegistryDir());
        DungeonTuningHub.Configure(
            DungeonTuningLoader.Parse(File.ReadAllText(DungeonTestFiles.DungeonTuningPath()), registries));
        EncounterTuningHub.Configure(
            EncounterTuningLoader.Parse(File.ReadAllText(DungeonTestFiles.EncounterTuningPath()), registries, DungeonTestFiles.ThreatRungIds()));
        DungeonRegistryHub.Configure(registries);
    }
}
