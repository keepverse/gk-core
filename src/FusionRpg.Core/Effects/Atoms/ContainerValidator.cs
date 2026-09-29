using System.Text.Json;
using System.Text.RegularExpressions;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// Judges one <see cref="ContainerRow"/> before it reaches the tables.
///
/// <para>Same law as E4 — a bad container is rejected <b>whole</b>, with its id and reason. The rule
/// carrying the most weight is <c>prefix_rolls/suffix_rolls ≤ distinct <b>drawable</b> groups</c> in
/// that same budget (T3.2 — the two budgets are counted separately, a Mixed-class affix's group
/// counting toward both): a pool that cannot satisfy the one-per-group rule under-fills the instance
/// silently, which is exactly the failure mode this program exists to remove.</para>
/// </summary>
public static class ContainerValidator
{
    // item-ideal.md §2b.1: rarity-bands (module 7) raises ContentRuleViolated{rarity.*} rather than
    // growing the closed 33-code list a third time (RarityLadderMutated/UnknownRarity/RarityBandViolated
    // all fold into this one namespace).
    static ContainerValidator() => ContentRuleNamespaces.Register("rarity");

    // container-kind-expansion (X7, 2026-09-07): gem/charm/combo/consumable added. This pattern is
    // independently hardcoded, NOT derived from ContainerKind/PrefixOf -- found missing its own four
    // new prefixes only by running the real, already-existing "every kind has a prefix and a valid id
    // passes" test, which the original 16-file switch-site grep did not catch (this file has no
    // `ContainerKind.` literal for that grep to find).
    // achievement-title T3 extends the same pattern: empire-title|actor-title.
    // relic-item-kind (empire-development Task 1.3a, 2026-09-15): `relic` joins the same
    // independently-hardcoded prefix list X7's comment above describes — owned by relic-item-kind's
    // own generator under `relic.*`, never authored outside a relic anchor.
    // ⚠️ Merged 2026-09-16: both programs extended this alternation on their own branch, so the
    // union is the resolution — and this file is exactly the "independently-hardcoded prefix list"
    // X7 warns about, which is why two branches could widen it without either noticing the other.
    // species-progression SP3.2: `species-progression` joins the same independently-hardcoded list.
    static readonly Regex ContainerIdRe =
        new(@"^(item|trait|skill|species-passive|patron|world-buff|enemy|gem|charm|combo|consumable|empire-title|actor-title|relic|species-progression)\.[a-z0-9-]+$",
            RegexOptions.Compiled);

    /// <summary>
    /// Validate. <paramref name="lookupAtom"/> resolves an atom id against the loaded catalog —
    /// supplied by the store, so this stays free of I/O. <paramref name="lookupAffix"/> resolves a
    /// pool row's affix id (T3.1 — the pool references affixes, never bare atoms).
    /// </summary>
    /// <param name="rarityExists">
    /// item-ideal.md, `rarity-bands` — the FK `effect_container.rarity` never had. Opt-in: omitting it
    /// registers no check at all, matching every other optional delegate in this file, so the hundreds
    /// of existing callers with no rarity ladder loaded are unaffected. Pass it once module 7 seeds
    /// the ladder.
    /// </param>
    public static AtomRejection Validate(
        ContainerRow c, Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix,
        Func<string, bool>? rarityExists = null)
    {
        if (c is null) return AtomRejection.Fail(AtomRejectionReason.BadParamValue, "null container");

        // ---- identity --------------------------------------------------------------------------
        if (!ContainerIdRe.IsMatch(c.ContainerId ?? ""))
            return Fail(AtomRejectionReason.BadParamValue,
                $"container_id '{c.ContainerId}' does not match the grammar");

        var prefix = ContainerRow.PrefixOf(c.Kind);
        if (!c.ContainerId!.StartsWith(prefix + ".", StringComparison.Ordinal))
            return Fail(AtomRejectionReason.BadParamValue,
                $"container_id '{c.ContainerId}' does not carry the '{prefix}.' prefix its kind requires");

        if (c.MinTier is { } lo && c.MaxTier is { } hi && lo > hi)
            return Fail(AtomRejectionReason.BadParamValue, $"tier window [{lo}, {hi}] is inverted");

        // ---- the fixed core ---------------------------------------------------------------------
        var seqs = new HashSet<int>();
        var coreAtoms = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in c.Atoms)
        {
            if (!seqs.Add(entry.Seq))
                return Fail(AtomRejectionReason.DuplicateSeq, $"seq {entry.Seq} appears twice");

            var atom = lookupAtom(entry.AtomId);
            if (atom is null)
                return Fail(AtomRejectionReason.UnknownAtom, entry.AtomId);

            coreAtoms.Add(entry.AtomId);

            var ov = ValidateOverrides(atom, entry.OverridesJson);
            if (!ov.IsOk) return Fail(ov.Reason, $"{entry.AtomId}: {ov.Detail}");
        }

        // ---- the pool ----------------------------------------------------------------------------
        if (c.PrefixRolls < 0)
            return Fail(AtomRejectionReason.BadParamValue, $"prefix_rolls {c.PrefixRolls} is negative");
        if (c.SuffixRolls < 0)
            return Fail(AtomRejectionReason.BadParamValue, $"suffix_rolls {c.SuffixRolls} is negative");

        if ((c.PrefixRolls > 0 || c.SuffixRolls > 0) && c.Pool.Count == 0)
            return Fail(AtomRejectionReason.UnsatisfiablePool,
                $"prefix_rolls/suffix_rolls is {c.PrefixRolls}/{c.SuffixRolls} but the pool is empty");

        // Group -> whether ANY row in it can actually be drawn. A group whose every row is weight 0
        // exists in the table and is unreachable in a draw; counting it is how a container passes
        // validation and then hands back fewer atoms than it promised. Tracked separately per budget
        // (T3.2) because a Mixed-class affix's group counts toward BOTH the prefix and suffix budgets
        // at once (A1) — a single combined count could not express that.
        var drawablePrefix = new Dictionary<string, bool>(StringComparer.Ordinal);
        var drawableSuffix = new Dictionary<string, bool>(StringComparer.Ordinal);
        var anyDrawable = false;

        foreach (var row in c.Pool)
        {
            if (row.Weight < 0)
                return Fail(AtomRejectionReason.BadParamValue,
                    $"{row.AffixId}: weight {row.Weight} is negative — rejected, never clamped");

            var affix = lookupAffix(row.AffixId);
            if (affix is null)
                return Fail(AtomRejectionReason.UnknownAtom, row.AffixId);

            // Every CONCRETE ref in the bundle is checked against the window and the fixed core; a
            // slot ref's concrete atom is not known until module 2 resolves it, so it is exempt here
            // — the same reason AffixValidator defers a slot's tier check.
            string? soleConcreteAtomId = null;
            AtomRow? soleConcreteAtom = null;
            var concreteRefCount = 0;
            foreach (var r in affix.Refs)
            {
                if (r.AtomId is null) continue;
                concreteRefCount++;
                var atom = lookupAtom(r.AtomId);
                if (atom is null)
                    return Fail(AtomRejectionReason.UnknownAtom, r.AtomId); // AffixValidator already
                                                                              // proved this once, but a
                                                                              // container may be
                                                                              // validated against a
                                                                              // catalog snapshot the
                                                                              // affix was not

                if (coreAtoms.Contains(r.AtomId))
                    return Fail(AtomRejectionReason.DuplicateAtomInContainer,
                        $"{r.AtomId} (via affix {row.AffixId}) is in both the fixed core and the pool");

                if (c.MinTier is { } min && atom.Tier < min)
                    return Fail(AtomRejectionReason.TierOutOfWindow,
                        $"{r.AtomId} (via affix {row.AffixId}) is tier {atom.Tier}, below the window minimum {min}");
                if (c.MaxTier is { } max && atom.Tier > max)
                    return Fail(AtomRejectionReason.TierOutOfWindow,
                        $"{r.AtomId} (via affix {row.AffixId}) is tier {atom.Tier}, above the window maximum {max}");

                soleConcreteAtomId = r.AtomId;
                soleConcreteAtom = atom;
            }

            // The default group is derived from a single concrete ref's own family+variant, exactly
            // like a bare atom did before affixes existed. A multi-ref bundle or a slot-bearing affix
            // cannot compute that default — it must declare `Group` explicitly, or the container is
            // rejected rather than silently grouped with nothing.
            string group;
            if (!string.IsNullOrWhiteSpace(row.Group))
                group = row.Group!;
            else if (affix.Refs.Count == 1 && concreteRefCount == 1)
                group = soleConcreteAtom!.FamilyId + "|" + soleConcreteAtom.Variant;
            else
                return Fail(AtomRejectionReason.BadParamValue,
                    $"affix {row.AffixId} is a multi-ref or slot-bearing bundle and must declare an " +
                    "explicit pool `Group` — it has no single atom to derive a default from");

            var isDrawable = row.Weight > 0;
            if (affix.Class is AffixClass.Prefix or AffixClass.Mixed)
                drawablePrefix[group] = drawablePrefix.TryGetValue(group, out var hadP) && hadP || isDrawable;
            if (affix.Class is AffixClass.Suffix or AffixClass.Mixed)
                drawableSuffix[group] = drawableSuffix.TryGetValue(group, out var hadS) && hadS || isDrawable;
            anyDrawable |= isDrawable;
        }

        if (c.Pool.Count > 0 && !anyDrawable)
            return Fail(AtomRejectionReason.UnsatisfiablePool,
                "every pool row has weight 0 — the draw would return nothing");

        var drawablePrefixGroups = drawablePrefix.Values.Count(v => v);
        if (c.PrefixRolls > drawablePrefixGroups)
            return Fail(AtomRejectionReason.PoolRollsExceedGroups,
                $"prefix_rolls {c.PrefixRolls} exceeds {drawablePrefixGroups} drawable prefix group(s) — " +
                "one atom per group per draw cannot be satisfied");

        var drawableSuffixGroups = drawableSuffix.Values.Count(v => v);
        if (c.SuffixRolls > drawableSuffixGroups)
            return Fail(AtomRejectionReason.PoolRollsExceedGroups,
                $"suffix_rolls {c.SuffixRolls} exceeds {drawableSuffixGroups} drawable suffix group(s) — " +
                "one atom per group per draw cannot be satisfied");

        if (!string.IsNullOrEmpty(c.Rarity) && rarityExists is not null && !rarityExists(c.Rarity))
            return AtomRejection.ContentRule("rarity.unknown",
                $"{c.ContainerId}: rarity '{c.Rarity}' is not in the seeded ladder");

        return AtomRejection.Ok;

        AtomRejection Fail(AtomRejectionReason reason, string detail) =>
            AtomRejection.Fail(reason, $"{c.ContainerId}: {detail}");
    }

    /// <summary>
    /// An override replaces a value spec on the referenced atom, so it obeys the same schema and the
    /// same E2 validation as the original. It may tighten a range; it may not invent a param the kind
    /// does not declare, and it may not change what the atom <i>is</i>.
    /// </summary>
    static AtomRejection ValidateOverrides(AtomRow atom, string? overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson)) return AtomRejection.Ok;

        var kind = AtomKindRegistry.Get(atom.KindId);
        if (kind is null) return AtomRejection.Fail(AtomRejectionReason.UnknownKind, atom.KindId);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(overridesJson!); }
        catch (JsonException ex)
        {
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"overrides_json: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    "overrides_json must be an object");

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "kind_id", StringComparison.OrdinalIgnoreCase))
                    return AtomRejection.Fail(AtomRejectionReason.OverrideChangesKind,
                        "an override tunes a value; it does not rewrite what the atom is");

                var def = kind.Params.Defs.FirstOrDefault(d =>
                    string.Equals(d.Name, prop.Name, StringComparison.OrdinalIgnoreCase));

                if (def is null)
                    return AtomRejection.Fail(AtomRejectionReason.UnknownParam,
                        $"'{prop.Name}' is not declared by {atom.KindId}");

                if (def.Kind != ParamKind.Value) continue;

                var spec = AtomJson.TryReadValueSpec(prop.Value, out _);
                if (!spec.IsOk)
                    return AtomRejection.Fail(spec.Reason, $"{prop.Name}: {spec.Detail}");
            }
        }

        return AtomRejection.Ok;
    }
}
