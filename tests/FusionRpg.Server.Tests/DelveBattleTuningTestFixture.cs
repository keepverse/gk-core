using FusionRpg.Core.Workspace;
namespace FusionRpg.Server.Tests;

/// <summary>
/// D2.16 — <c>DelveBattleSessionTests</c>/<c>DelveBattleSessionManagerTests</c> resolve REAL battles
/// under the `delve` profile end to end (real <c>OverlayCombatCalculator.Compute</c> included), which
/// needs several process-global tuning hubs configured. <c>PowerAndAptitudeTuningTestBootstrap</c>'s
/// own <c>[ModuleInitializer]</c> sets sane minimal defaults for the whole assembly once, but several
/// SIBLING test classes (<c>AptitudeChannelModsTests</c>, <c>BuildSquadEquippedActionsTests</c>,
/// <c>RolledItemEquipRuntimeTests</c>) reconfigure the SAME hubs with their OWN, mutually different
/// fixtures in their own constructors — an already-established pattern in this assembly, not something
/// this task invented (`AssemblyParallelism.cs` disables cross-class parallelism, but that only removes
/// RACES; it does not stop one class's constructor from overwriting a shared static that a class
/// running later in the same sequential run then reads). `AptitudeChannelModsTests`, for one real
/// example, configures `BattleTuningHub` from the STALE `battle.v2.json` (no `delve` row at all).
///
/// <para>The established fix for this hazard, matching every one of those sibling files exactly: each
/// class that needs a specific shape re-asserts it in ITS OWN constructor, from the REAL shipped
/// `gk-core/data/tuning/*.json` (never an inline fixture divorced from what ships) — since xUnit constructs a
/// fresh instance of the test class per test method, this reconfigures the shared statics back to what
/// THIS class needs immediately before every one of its own tests runs, regardless of what any other
/// class most recently left them as.</para>
/// </summary>
static class DelveBattleTuningTestFixture
{
    public static void ConfigureRealBattleTuning()
    {
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));

        // battle.v5.json is the real, current shipped file and the one that carries
        // timeline.profiles.delve (party-dungeon D2.9) -- v2/v3/v4 either lack it or are stale in
        // other ways sibling test files already accept for their own, unrelated purposes.
        FusionRpg.Core.Battle.BattleTuningHub.Configure(
            FusionRpg.Core.Battle.BattleTuningLoader.Parse(Read("battle.v5.json")));
        FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(
            FusionRpg.Core.Battle.BattleResourceTuningLoader.Parse(Read("battle-resources.v1.json")));
        FusionRpg.Core.Actions.ActionTimingPolicy.Configure(
            FusionRpg.Core.Actions.ActionTimingTuningLoader.Parse(Read("action-timing.v1.json")));
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
            FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(Read("stats.v1.json")));
        FusionRpg.Core.Combat.CombatPolicy.Configure(
            FusionRpg.Core.Combat.CombatTuningLoader.Parse(Read("combat.v1.json")));
        FusionRpg.Core.Status.StatusPolicy.Configure(
            FusionRpg.Core.Status.StatusTuningLoader.Parse(Read("status.v1.json")));
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
