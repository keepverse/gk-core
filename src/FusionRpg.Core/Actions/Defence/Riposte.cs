namespace FusionRpg.Core.Actions.Defence;

/// <summary>
/// T27 (spec-defence-actions.md §4): the riposte — spent `poise` converts to damage on release. "A
/// guard that costs nothing when it stops nothing would also produce nothing, and BASTION would
/// still have no way to win" — FORCE spends `stamina` to attack, FINESSE spends `qi` to cast,
/// BASTION spends `poise` to block; this is what gives that spend an offence side.
///
/// <para><b><c>shareMilli</c> is a BOUNDED RATIO over an UNCAPPED POOL — PS-8 exempt, and this
/// comment is that exemption</b> (spec §4: "the declaration must say so in a comment"). `[0, 1000]`
/// per-mille is a bounded ratio like any evasion-chain contest share, never a progression ceiling;
/// it is not a cap on damage. Output scales with `Θ` because the POOL it multiplies — `poise` itself,
/// already `Θ`-scaled through the derived-stat channels `ResourceMax`/`ResourceRegen` read (T15) —
/// does. This module authors no `Θ` curve of its own.</para>
/// </summary>
public static class Riposte
{
    /// <summary><c>spentPoise × shareMilli / 1000</c> — widened before multiplying, divided by 1000
    /// exactly once (docs/architecture/numeric-types.md "Numeric overflow").</summary>
    public static long DamageFromSpentPoise(long spentPoise, int shareMilli)
    {
        if (spentPoise < 0) throw new ArgumentOutOfRangeException(nameof(spentPoise), spentPoise, "spent poise is never negative");
        if (shareMilli < 0 || shareMilli > 1000) throw new ArgumentOutOfRangeException(nameof(shareMilli), shareMilli, "share is a per-mille ratio in [0, 1000] — bounded, PS-8 exempt (see this class's own doc comment)");

        return checked(spentPoise * shareMilli / 1000);
    }
}
