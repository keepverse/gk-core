namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// `eligibility-tags` (T5.2, `spec-eligibility-tags.md`): tag-based affix eligibility plus a
/// per-container allow/deny override — what keeps the affix library SHARED (Q6) rather than forked
/// per feature. Tags live on the affix (owned by whichever module writes them — `affix-library`,
/// `affix-authoring`; not this module's concern); eligibility lives on the container/feature that
/// draws from the shared library. This module owns only the MATCH rule, deliberately decoupled from
/// where an affix's own tags are stored (its own "Project structure" names exactly one file — this
/// one — not a schema change to `AffixRow` or `ContainerRow`).
/// </summary>
/// <param name="RequireTags">Bare tag KEYS that must all be present on the affix, any value —
/// `"element"` matches `{element: fire}` and `{element: ice}` alike.</param>
/// <param name="AnyOfTags">`"key:value"` pairs; at least one must match. Empty means no constraint on
/// this axis (every affix passes it).</param>
/// <param name="Allow">Affix ids admitted regardless of whether the tag rule would select them — the
/// escape hatch, positive direction.</param>
/// <param name="Deny">Affix ids excluded regardless of the tag rule OR <see cref="Allow"/> — the
/// escape hatch, negative direction, and it always wins.</param>
public sealed record EligibilityRule(
    IReadOnlyList<string> RequireTags,
    IReadOnlyList<string> AnyOfTags,
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Deny)
{
    public static readonly EligibilityRule Empty =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

public static class EligibilityResolver
{
    /// <summary>
    /// deny always wins — an explicit exclusion is a stronger authoring signal than a tag match, the
    /// same shape PoE's own item-specific mod exclusions take.
    /// </summary>
    public static bool IsEligible(string affixId, IReadOnlyDictionary<string, string> affixTags, EligibilityRule rule) =>
        rule.Deny.Contains(affixId) ? false
        : rule.Allow.Contains(affixId) ? true
        : TagsMatch(affixTags, rule);

    static bool TagsMatch(IReadOnlyDictionary<string, string> tags, EligibilityRule rule)
    {
        foreach (var key in rule.RequireTags)
            if (!tags.ContainsKey(key)) return false;

        if (rule.AnyOfTags.Count == 0) return true;

        foreach (var pair in rule.AnyOfTags)
        {
            var colon = pair.IndexOf(':');
            if (colon < 0) continue;
            var key = pair[..colon];
            var value = pair[(colon + 1)..];
            if (tags.TryGetValue(key, out var actual) && string.Equals(actual, value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Every affix id in <paramref name="catalog"/> the rule admits, tag-eligible affixes
    /// unioned with <see cref="EligibilityRule.Allow"/> and then minus <see cref="EligibilityRule.Deny"/>
    /// — module 1's own "distinct drawable groups" validation runs on top of whatever this returns.</summary>
    public static IReadOnlyList<string> DrawablePool(
        IReadOnlyList<AffixRow> catalog, Func<string, IReadOnlyDictionary<string, string>> tagsOf, EligibilityRule rule) =>
        catalog.Where(a => IsEligible(a.AffixId, tagsOf(a.AffixId), rule)).Select(a => a.AffixId).ToList();

    /// <summary>
    /// A rule is unsatisfiable when a class the container has a non-zero roll budget for has ZERO
    /// eligible affixes of that class — same failure module 1 already names for an empty drawable
    /// set (<see cref="AtomRejectionReason.UnsatisfiablePool"/>), rejected at load, never discovered
    /// as a silent under-fill at roll time.
    /// </summary>
    public static AtomRejection Validate(
        EligibilityRule rule, IReadOnlyList<AffixRow> catalog,
        Func<string, IReadOnlyDictionary<string, string>> tagsOf, int prefixRolls, int suffixRolls)
    {
        foreach (var id in rule.Allow)
            if (catalog.All(a => a.AffixId != id))
                return AtomRejection.Fail(AtomRejectionReason.UnknownAtom, $"eligibility allow: unknown affix '{id}'");
        foreach (var id in rule.Deny)
            if (catalog.All(a => a.AffixId != id))
                return AtomRejection.Fail(AtomRejectionReason.UnknownAtom, $"eligibility deny: unknown affix '{id}'");

        var eligibleIds = DrawablePool(catalog, tagsOf, rule).ToHashSet(StringComparer.Ordinal);
        var eligible = catalog.Where(a => eligibleIds.Contains(a.AffixId)).ToList();

        if (prefixRolls > 0 && !eligible.Any(a => a.Class is AffixClass.Prefix or AffixClass.Mixed))
            return AtomRejection.Fail(AtomRejectionReason.UnsatisfiablePool,
                $"eligibility rule selects zero Prefix-eligible affixes, but prefix_rolls is {prefixRolls}");

        if (suffixRolls > 0 && !eligible.Any(a => a.Class is AffixClass.Suffix or AffixClass.Mixed))
            return AtomRejection.Fail(AtomRejectionReason.UnsatisfiablePool,
                $"eligibility rule selects zero Suffix-eligible affixes, but suffix_rolls is {suffixRolls}");

        return AtomRejection.Ok;
    }
}
