namespace FusionRpg.Core.Combat;

/// <summary>
/// Canonical entity ptr for snapshots, Funnel keys, and FA10 lookup.
/// Strips <c>entity:</c> and <c>0x</c>; comparison is case-insensitive.
/// </summary>
public static class CombatPtr
{
    public static string Normalize(string? ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return "";

        // ⚡ Fast path: the overwhelming majority of calls pass a string that is ALREADY canonical —
        // it came out of a previous Normalize, or out of a cache keyed by one. The general path below
        // allocates unconditionally (`ToLowerInvariant` always returns a fresh string, even when every
        // character is already lower case), and this runs on the per-hit and per-frame HUD paths, so
        // that allocation showed up as tens of thousands of dead strings per second at the 300z tier.
        // Returning the SAME instance when nothing would change is semantically identical — the general
        // path's answer for a canonical input is a copy of that input.
        if (IsCanonical(ptr)) return ptr;

        var s = ptr.Trim();
        if (s.StartsWith("entity:", StringComparison.OrdinalIgnoreCase))
            s = s[7..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="s"/> is already exactly what <see cref="Normalize"/> would return:
    /// a non-empty run of lower-case hex digits. Every transformation the general path performs is
    /// ruled out by that test — whitespace, <c>entity:</c> (the <c>:</c>), <c>0x</c> (the <c>x</c>)
    /// and upper-case <c>A</c>–<c>F</c> are all non-members of the set.
    /// </summary>
    static bool IsCanonical(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= '0' and <= '9') continue;
            if (c is >= 'a' and <= 'f') continue;
            return false;
        }

        return true;
    }

    public static bool EqualsPtr(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
}
