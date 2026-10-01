using Xunit;
using FusionRpg.Core.Workspace;

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

    /// <summary>Reads a file from gk-FUSION, which is what carries <c>src/FusionRpg.Injector</c>.</summary>
    ///
    /// <para>This walked up from the test host's own directory looking for a
    /// <c>src/FusionRpg.Injector</c> directory and reported
    /// <c>DirectoryNotFoundException : repo root</c> when it ran out of parents. It could never succeed:
    /// the only ancestors of a gk-core test's bin output are gk-core and the workspace root, and
    /// gk-fusion is a SIBLING. A walk reaches ancestors; this needed a sibling. The marker directory it
    /// probed for is itself a gk-fusion path, so the probe was looking for the thing that moved.</para>
    ///
    /// <para>Two files carried this helper verbatim. The refusal also named nothing — not the marker, not
    /// the owner, not the directory it had walked to — so the message could not be acted on even by
    /// someone who had the split in front of them.</para>
    static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { KeepverseRoots.Fusion() }.Concat(parts).ToArray());
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"not found: {path} - gk-fusion carries src/FusionRpg.Injector, and this is a gk-core test, "
                + "so the walk up this used to do could never have reached it",
                path);
        }
        return File.ReadAllText(path);
    }
}
