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
        // ⚠ THIS LOOP WAS THE DEFEAT, AND IT IS GONE ON PURPOSE. It read
        //   while (dir is not null && !Directory.Exists(Path.Combine(KeepverseRoots.Content(), ...)))
        // whose condition never mentions `dir`. KeepverseRoots.Content() is already absolute, so the
        // condition was loop-invariant: true on entry, false immediately, and the walk never moved.
        // `dir` stayed at AppContext.BaseDirectory, so the three tuning files below were read from
        // tests/FusionRpg.Data.Tests/bin/Debug/net8.0/data/tuning/ - a directory that does not exist.
        // A [ModuleInitializer] runs at ASSEMBLY LOAD, so the TypeInitializationException killed every
        // test in the assembly: 1924 of 1924, and the assembly is not in FusionRpg.slnx, so no
        // solution-wide run had ever reported it. A walk-up may only decide a path by looking at the
        // directory it is walking through; naming an already-absolute root in the condition makes the
        // walk a no-op that silently pins the result to the test output folder.
        var tuning = Path.Combine(KeepverseRoots.Core(), "data", "tuning");

        var registries = DungeonRegistryLoader.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "_registry"));
        DungeonTuningHub.Configure(
            DungeonTuningLoader.Parse(File.ReadAllText(Path.Combine(tuning, "dungeon.v3.json")), registries));
        EncounterTuningHub.Configure(
            EncounterTuningLoader.Parse(File.ReadAllText(Path.Combine(tuning, "encounter.v1.json")), registries,
                // tier-propagation-contract T-2: the threat ladder is read from its own tuning file, never restated.
                FusionRpg.Core.Creatures.Generation.CreatureThreatTuningLoader.Parse(
                    File.ReadAllText(Path.Combine(tuning, "creature-threat.v2.json"))).RungIds));
        DungeonRegistryHub.Configure(registries);
    }
}
