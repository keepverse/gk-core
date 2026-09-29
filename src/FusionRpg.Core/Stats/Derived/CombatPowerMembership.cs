namespace FusionRpg.Core.Stats.Derived;

/// <summary>
/// `combat-membership` (Q5, closed — combat-power-number-ideal.md "Combat-affecting membership (D2
/// filter)") — the ONE closed predicate for "does this derived channel's total raise the combat power
/// matrix (Standing)." <see cref="DerivedStatChannels.IsCombatChannel"/> alone is NOT this predicate:
/// using it alone would drop <c>skill.cooldown.*</c>/<c>skill.effectiveness.*</c> and break the
/// product rule that a build trading cooldown/effectiveness for raw stats should still be priced.
///
/// <para><b>Include (locked):</b></para>
/// <list type="bullet">
/// <item>Every id <see cref="DerivedStatChannels.IsCombatChannel"/> already covers (~196 element-typed
/// combat channels, via <see cref="DerivedStatChannels.AllCombatChannelIds"/> — survives an
/// <c>ElementTable</c> roster swap since it reads the same live generation).</item>
/// <item>Every <see cref="DerivedStatChannels.SkillCooldownPrefix"/> / <see cref="DerivedStatChannels.SkillEffectivenessPrefix"/>
/// channel (open-prefix families; combat-support, not element-typed).</item>
/// <item><c>status.power.*</c> / <c>status.resist.*</c> — <see cref="DerivedStatRegistry.TryResolveChannel"/>'s
/// own open-prefix rule accepts ANY suffix on these two families unconditionally (the same rule combat
/// Apply/potency reads through), so a prefix match here is exactly "registered or accepted by registry
/// open-prefix rules," not a narrower hand-curated subset.</item>
/// </list>
///
/// <para><b>Exclude (locked):</b></para>
/// <list type="bullet">
/// <item>All <c>progression.*</c> — especially <c>progression.power</c> (Θ). Pricing the naive Hub
/// Derived snapshot would fold the level ladder into Standing, which is the one wrong shape this
/// predicate exists to prevent.</item>
/// <item><c>resource.max.*</c> / <c>resource.regen.*</c> — pools are not combat-power Standing.</item>
/// <item>Every other <c>status.*</c> family (<c>status.duration.*</c>, <c>status.durationReduction.*</c>,
/// <c>status.intensity.*</c>, <c>status.intensityReduction.*</c>) — these describe how long/strong an
/// APPLIED status persists, a different axis from power/resist potency, and are not in the locked
/// include list.</item>
/// <item>Future loot / magic-find / XP-bonus derived families. <b>Do not invent those families here</b>
/// — this predicate only needs to keep excluding them by default (anything not explicitly included
/// above falls through to <c>false</c>); adding a new family that should count as combat-power without
/// updating this type is the defect, not a gap this predicate can pre-empt.</item>
/// </list>
/// </summary>
public static class CombatPowerMembership
{
    const string StatusPowerPrefix = "status.power.";
    const string StatusResistPrefix = "status.resist.";

    /// <summary>True if <paramref name="channelId"/>'s total should raise the combat power matrix.
    /// O(1): <see cref="DerivedStatChannels.IsCombatChannel"/> is an already-cached HashSet lookup;
    /// every other check is a cheap ordinal prefix compare — this method allocates nothing.</summary>
    public static bool Includes(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return false;

        if (DerivedStatChannels.IsCombatChannel(channelId)) return true;

        if (channelId.StartsWith(DerivedStatChannels.SkillCooldownPrefix, StringComparison.Ordinal)
            || channelId.StartsWith(DerivedStatChannels.SkillEffectivenessPrefix, StringComparison.Ordinal))
            return true;

        if (channelId.StartsWith(StatusPowerPrefix, StringComparison.Ordinal)
            || channelId.StartsWith(StatusResistPrefix, StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>Filters any sequence down to the members whose channel id
    /// <see cref="Includes"/> — the one place a Standing-adjacent caller (ProjectStanding,
    /// copy/chip surfaces) narrows a channel set, so no caller ever hand-rolls a second filter.</summary>
    public static IEnumerable<T> Filter<T>(IEnumerable<T> items, Func<T, string> channelIdOf)
    {
        foreach (var item in items)
            if (Includes(channelIdOf(item)))
                yield return item;
    }
}
