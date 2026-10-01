using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N3 finding (live 2026-09-16): a zombie bite's vanilla observer row named the bitten plant as its own
/// attacker (<c>damageFrom</c> does not identify the biter), so no bite row ever joined its RPG record (37 bites, 0 joined).
/// The drain already knows the biter — <c>TryRecordMeleeDealt</c> runs from the attack hook before <c>Plant.TakeDamage</c> —
/// so the bridge must ask it first for a plant victim. Source scan: the Injector has no CI-runnable unit tests.
/// </summary>
public class LawnObserverMeleeAttackerGuardTests
{
    [Fact]
    public void Drain_remembers_the_same_frame_melee_attacker_per_target()
    {
        var text = Read("src", "FusionRpg.Injector", "Effects", "EventDrainHost.cs");
        var dealt = text.IndexOf("public static bool TryRecordMeleeDealt(", StringComparison.Ordinal);
        var store = text.IndexOf("_meleeAttackerByTarget[targetPtr] = attackerPtr;", dealt, StringComparison.Ordinal);
        Assert.True(dealt >= 0 && store > dealt, "TryRecordMeleeDealt must remember the attacker for its target");

        var peek = text.IndexOf("public static bool TryPeekMeleeAttacker(IntPtr targetPtr, out IntPtr attackerPtr)", StringComparison.Ordinal);
        Assert.True(peek >= 0, "the drain must expose a same-frame melee attacker lookup");
        Assert.Contains("_meleePairsFrame == SafeFrame()", text.Substring(peek, 400), StringComparison.Ordinal);
    }

    [Fact]
    public void Observer_bridge_asks_the_drain_for_a_plant_victims_biter_before_reading_damageFrom()
    {
        var text = Read("src", "FusionRpg.Injector", "Effects", "LawnCombatObserverBridge.cs");
        var record = text.IndexOf("public static void RecordVanillaHit(", StringComparison.Ordinal);
        var body = text.Substring(record, text.IndexOf("public static void RecordRpgDelta(", record, StringComparison.Ordinal) - record);
        var melee = body.IndexOf("EventDrainHost.TryPeekMeleeAttacker(", StringComparison.Ordinal);
        var fallback = body.IndexOf("ResolveAttackerAndSwingId(damageFrom", StringComparison.Ordinal);
        Assert.True(melee >= 0, "a plant victim's attacker must come from the drain's melee record");
        Assert.True(fallback > melee, "the damageFrom fallback runs only after the melee lookup");
    }

    /// <summary>Reads a file from gk-FUSION, which is what carries <c>src/FusionRpg.Injector</c>.</summary>
    ///
    /// <para>The twin of the helper in <c>BambooDragonAmbientAttackerGuardTests</c>, with the same walk and
    /// the same impossible marker: it probed ancestors for a <c>src/FusionRpg.Injector</c> directory, which
    /// gk-fusion carries and gk-core does not, so it reported <c>DirectoryNotFoundException : repo root</c>
    /// rather than ever finding one. See that file for the full argument; the walk reaches ancestors and
    /// this needed a sibling.</para>
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
