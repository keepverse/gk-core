using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N36 (live 2026-09-15): FumeShroom damages zombies with the plant itself as <c>damageFrom</c>, so no
/// dealt record was made and its hits carried no rider. The zombie take-damage prefix must try the direct-plant record in
/// its non-bullet branch, and that record must skip a victim already recorded as melee this frame (Shulkflower/WaterShulk
/// record their victims in <c>AttackEffect</c>). Source scan: the Injector has no CI-runnable unit tests.
/// </summary>
public class DirectPlantDealtGuardTests
{
    [Fact]
    public void Zombie_hits_from_a_plant_record_a_dealt_hit_once()
    {
        var root = KeepverseRoots.Fusion();
        var host = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "Effects", "EventDrainHost.cs"));
        var method = host.IndexOf("public static bool TryRecordDirectPlantDealt(", StringComparison.Ordinal);
        Assert.True(method >= 0, "missing TryRecordDirectPlantDealt");
        var end = host.IndexOf("\n    }", method, StringComparison.Ordinal);
        var body = host.Substring(method, end - method);
        Assert.Contains("damageFrom.TryCast<Plant>()", body, StringComparison.Ordinal);
        var dedupe = body.IndexOf("if (_meleePairsFrame == frame && _meleePairsByTarget.ContainsKey(targetPtr))", StringComparison.Ordinal);
        var skip = body.IndexOf("return false;", dedupe, StringComparison.Ordinal);
        var record = body.IndexOf("TryRecordMeleeDealt(targetSide, plant.Pointer,", StringComparison.Ordinal);
        Assert.True(dedupe >= 0 && skip > dedupe && record > skip, "a victim already recorded this frame must be skipped before recording");

        var hooks = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "GameHooks.cs"));
        var zombiePrefix = hooks.IndexOf("public static class ZombieTakeDamage", StringComparison.Ordinal);
        var bullet = hooks.IndexOf("Effects.EventDrainHost.TryRecordDealtFromBullet(", zombiePrefix, StringComparison.Ordinal);
        var direct = hooks.IndexOf("Effects.EventDrainHost.TryRecordDirectPlantDealt(", zombiePrefix, StringComparison.Ordinal);
        var ambient = hooks.IndexOf("Effects.EventDrainHost.TryRecordAmbientMeleeDealt(", zombiePrefix, StringComparison.Ordinal);
        Assert.True(bullet > zombiePrefix && direct > bullet, "the direct-plant record belongs after the bullet record in the zombie prefix");
        Assert.True(ambient > direct, "the ambient fallback runs only when the direct-plant record did not");
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
