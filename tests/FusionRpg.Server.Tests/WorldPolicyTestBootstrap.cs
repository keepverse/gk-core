using FusionRpg.Core.Workspace;
namespace FusionRpg.Server.Tests;

/// <summary>
/// Shared, thread-safe, run-exactly-once configuration for the World/Loam policies several
/// in-process-HTTP test classes in this assembly need (<see cref="WorldCedeForecastTests"/>,
/// <see cref="DistrictAssaultCommandWireTests"/>, and any future one). Extracted here because xUnit
/// runs test CLASSES in parallel by default: two classes each guarding their own private
/// `_tuningConfigured` bool would race to call `Policy.Configure(...)` on the SAME shared statics
/// from two threads at once, which is exactly the kind of transient corruption that broke unrelated,
/// concurrently-running tests elsewhere in this assembly the first time this was tried as two
/// separate per-class copies. <see cref="Lazy{T}"/>'s default thread-safety mode guarantees the
/// factory below runs exactly once, however many callers race to touch <see cref="Value"/>
/// concurrently.
/// </summary>
static class WorldPolicyTestBootstrap
{
    static readonly Lazy<bool> _once = new(() =>
    {
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));

        FusionRpg.Core.World.Loam.LoamPolicy.Configure(
            FusionRpg.Core.World.Loam.LoamTuningLoader.Parse(Read("loam.v5.json")));
        var worldTuning = FusionRpg.Core.World.WorldTuningLoader.Parse(Read("world.v6.json"));
        FusionRpg.Core.World.WorldTuningHub.Configure(worldTuning);
        // world-map W42/W50: committing a turn always runs `Growth`, which reads `RecruitPolicy` —
        // omitting this throws "RecruitPolicy.Configure(...) has not run" the moment any commit
        // through these tests' own `/commit` route resolves.
        FusionRpg.Core.World.Growth.RecruitPolicy.Configure(worldTuning.Growth);
        // Committing "dave" alone can still run an AI faction's own policy (auto-fill), which reads
        // this the same way AptitudeChannelModsTests.cs's own bootstrap already does.
        FusionRpg.Core.World.Ai.WorldAiPolicy.Configure(
            FusionRpg.Core.World.Ai.WorldAiTuningLoader.Parse(Read("ai.v3.json")));
        return true;
    });

    public static void EnsureConfigured() => _ = _once.Value;

    public static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
