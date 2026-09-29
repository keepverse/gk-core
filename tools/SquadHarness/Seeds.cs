using System.Text;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §7: "Every trial's seed is a pure function of its coordinates and nothing
/// else: <c>seed(a, d, k) = mix64(runSeed, attackerIndex a, defenderIndex d, trialIndex k)</c>." No
/// <c>Random()</c> without a seed, no clock, no <c>Guid.NewGuid</c> anywhere on this path.
///
/// <para><b>Resolved ambiguity, stated as a default.</b> The spec writes "attackerIndex"/"defenderIndex"
/// as if they were fixed integer positions in a canonical roster, but never says whose roster or in
/// what order. A position that depended on which OTHER builds happened to be in a given sweep call
/// would break the exact property §7 wants: rerunning a SUBSET of a roster (§9.2's refine pass reruns
/// only the cells inside their own half-width) would silently reseed a surviving cell, because its
/// position in the id-sorted list shifts when neighbours are removed. So "index" here is not a position
/// at all -- it is a stable 64-bit hash of the build's own <c>Id</c> string, which depends on nothing
/// but that string. Two calls that both include the same Id always seed it identically, regardless of
/// what else is in the roster or what order it was constructed in -- exactly
/// <c>Reordering_the_roster_does_not_move_a_cell</c>'s own claim, extended to cover a narrowed
/// re-run as well as a shuffled one.</para>
///
/// <para><b>Not <see cref="string.GetHashCode()"/></b> -- .NET randomises string hash codes per process
/// (Marvin, seeded at startup) for hash-flooding resistance, which would make
/// <c>A_second_process_reproduces_the_hash</c> fail by construction. <see cref="HashId"/> is FNV-1a over
/// the UTF-8 bytes: a fixed, unseeded, well-known avalanche function.</para>
/// </summary>
public static class Seeds
{
    public static ulong Mix(ulong runSeed, string attackerId, string defenderId, long k)
    {
        var h = runSeed;
        h = Round(h, HashId(attackerId));
        h = Round(h, HashId(defenderId));
        h = Round(h, unchecked((ulong)k));
        return h;
    }

    /// <summary>FNV-1a 64-bit over UTF-8 bytes -- a fixed, well-known hash with no per-process
    /// randomisation, unlike <see cref="string.GetHashCode()"/>.</summary>
    public static ulong HashId(string id)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var h = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(id))
        {
            h ^= b;
            h *= prime;
        }
        return h;
    }

    static ulong Round(ulong h, ulong x)
    {
        h ^= x;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }
}
