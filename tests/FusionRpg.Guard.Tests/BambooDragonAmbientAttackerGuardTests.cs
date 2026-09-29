using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N39 (live 2026-09-16): BambooDragon's breath reaches <c>Zombie.TakeDamage</c> with <c>damageFrom</c>
/// null, so its hits had no dealt record and no rider (a Bound BambooDragon: 0 riders over 150 s while its grant was bound).
/// Its <c>FixedUpdate</c> must bracket it as the ambient attacker, and the observer must read that bracket for a null
/// <c>damageFrom</c>. Source scan: the Injector has no CI-runnable unit tests.
/// </summary>
public class BambooDragonAmbientAttackerGuardTests
{
    [Fact]
    public void Bamboo_dragon_fixed_update_brackets_itself_as_the_ambient_attacker()
    {
        var text = Read("src", "FusionRpg.Injector", "GameHooks.cs");
        var patch = text.IndexOf("[HarmonyPatch(typeof(PlantBambooDragon), nameof(PlantBambooDragon.FixedUpdate))]", StringComparison.Ordinal);
        Assert.True(patch >= 0, "BambooDragon.FixedUpdate must be patched");
        var body = text.Substring(patch, 400);
        Assert.Contains("Prefix(PlantBambooDragon __instance) => BeginPlantAmbientAttack(__instance);", body, StringComparison.Ordinal);
        Assert.Contains("Postfix() => EndMultiMeleeAttack();", body, StringComparison.Ordinal);

        var begin = text.IndexOf("static void BeginPlantAmbientAttack(Plant attacker)", StringComparison.Ordinal);
        Assert.Contains("Effects.EventDrainHost.BeginAmbientMeleeAttacker(attacker.Pointer, typeId);", text.Substring(begin, 600), StringComparison.Ordinal);
    }

    [Fact]
    public void Observer_takes_the_ambient_attacker_when_the_damage_source_names_nobody()
    {
        var text = Read("src", "FusionRpg.Injector", "Effects", "LawnCombatObserverBridge.cs");
        var record = text.IndexOf("public static void RecordVanillaHit(", StringComparison.Ordinal);
        var body = text.Substring(record, text.IndexOf("public static void RecordRpgDelta(", record, StringComparison.Ordinal) - record);
        var resolve = body.IndexOf("ResolveAttackerAndSwingId(damageFrom", StringComparison.Ordinal);
        var unnamed = body.IndexOf("(attackerPtr.Length == 0 || CombatPtr.EqualsPtr(attackerPtr, victimPtr))", StringComparison.Ordinal);
        var ambient = body.IndexOf("EventDrainHost.TryPeekAmbientMeleeAttacker(out var ambient)", StringComparison.Ordinal);
        var record2 = body.IndexOf("LawnCombatObserver.RecordVanillaHit(", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && unnamed > resolve && ambient > unnamed, "an unnamed or self-named source must take the ambient attacker");
        Assert.True(record2 > ambient, "the ambient attacker must be chosen before the hit is recorded");
    }

    static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("repo root");
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }
}
