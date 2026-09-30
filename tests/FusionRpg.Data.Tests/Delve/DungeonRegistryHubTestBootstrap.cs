using System.Runtime.CompilerServices;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Delve;

/// <summary>
/// Configures all three Dungeon hubs once for the whole assembly from the real, shipped files, in
/// `Program.cs`'s own boot order — registries load first, pure; `DungeonTuningHub`/`EncounterTuningHub`
/// next, cross-checked against those registries at parse time; `DungeonRegistryHub` last — mirroring
/// `FusionRpg.Core.Tests.Dungeon.DungeonHubTestBootstrap`'s own identical shape exactly (originally
/// this file configured only `DungeonRegistryHub`, all `DelveGraphRoll.Roll` under this assembly ever
/// needed; extended 2026-09-07, party-dungeon D4.30, for the first Data.Tests class that also needs
/// `DungeonTuningHub`/`EncounterTuningHub` configured — a real, growing need, not a speculative one).
/// </summary>
internal static class DungeonRegistryHubTestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);

        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        DungeonTuningHub.Configure(
            DungeonTuningLoader.Parse(File.ReadAllText(Path.Combine(dir.FullName, "data", "tuning", "dungeon.v3.json")), registries));
        EncounterTuningHub.Configure(
            EncounterTuningLoader.Parse(File.ReadAllText(Path.Combine(dir.FullName, "data", "tuning", "encounter.v1.json")), registries,
                // tier-propagation-contract T-2: the threat ladder is read from its own tuning file, never restated.
                FusionRpg.Core.Creatures.Generation.CreatureThreatTuningLoader.Parse(
                    File.ReadAllText(Path.Combine(dir.FullName, "data", "tuning", "creature-threat.v2.json"))).RungIds));
        DungeonRegistryHub.Configure(registries);
    }
}
