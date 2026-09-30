using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N27: both take-damage prefixes report a hit on an entity that still has health to
/// <c>EntityLiveness.NoteHitOnLivingEntity</c>, and the drain stats window publishes the count, so a stale dead mark on a
/// pooled, reactivated entity shows up in every perf window. Source scan: the Injector has no CI-runnable unit tests.
/// </summary>
public class StaleDeadMarkObservationGuardTests
{
    [Fact]
    public void Both_take_damage_prefixes_report_living_hits_and_the_stats_window_publishes_them()
    {
        var root = RepoRoot();
        var hooks = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "GameHooks.cs"));
        Assert.Contains("if (__instance.thePlantHealth > 0) Effects.EventDrainHost.Liveness.NoteHitOnLivingEntity(__instance.Pointer);", hooks, StringComparison.Ordinal);
        Assert.Contains("if (Bridges.ZombieCombatFields.GetHp(__instance) > 0) Effects.EventDrainHost.Liveness.NoteHitOnLivingEntity(__instance.Pointer);", hooks, StringComparison.Ordinal);

        var host = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "Effects", "EventDrainHost.cs"));
        Assert.Contains("var (staleHits, stalePtr) = Liveness.TakeStaleMarkHits();", host, StringComparison.Ordinal);
        Assert.Contains("d[\"staleDeadMarkHits\"] = staleHits;", host, StringComparison.Ordinal);
        Assert.Contains("d[\"livenessDeadMarks\"] = Liveness.DeadCount;", host, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
