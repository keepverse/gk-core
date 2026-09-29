using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Uniques;

/// <summary>
/// What a unique's fixed core is worth, in AE × 100, broken out so a refusal can say which line paid
/// for what rather than only that a total was too big.
/// </summary>
/// <param name="IdentityAeHundredths">Every core atom above <c>seq 0</c>, drawbacks signed negative.</param>
/// <param name="RawStatAeHundredths">The <see cref="UniqueValidator.RawStatKinds"/> subset, unsigned —
/// what `narrow` is measured against.</param>
/// <param name="VarianceAeHundredths">One AE per roll, at the rung's reference tier.</param>
public readonly record struct UniquePricing(
    long IdentityAeHundredths, long RawStatAeHundredths, long VarianceAeHundredths)
{
    public long TotalAeHundredths => IdentityAeHundredths + VarianceAeHundredths;
}

/// <summary>
/// The per-row import checks — spec-uniques.md's eight rule ids, every one raised as the single
/// <see cref="AtomRejectionReason.ContentRuleViolated"/> code with a <c>unique.*</c> payload
/// (item-ideal.md §2b.1 / README #3). <b>No member is added to the closed enum.</b>
///
/// <para><b>Every check runs and every failure is returned</b>, unlike <see cref="ContainerValidator"/>'s
/// first-fail. A unique is authored content validated in bulk at import — 144 rows reported one problem
/// at a time is 144 round trips for the author, and the cross-row checks in
/// <see cref="UniqueCorpusValidator"/> already need the whole list to exist before they can run.</para>
///
/// <para><b>AE is priced from the atom's TIER, never from its raw parameter.</b> A core may hold hp,
/// per-mille and millisecond params at once and SC4 forbids summing across those units; the AE unit is
/// defined tier-relatively (one rolled affix at the middle of the rung's window), so tier is the only
/// unit-safe basis. The raw value is read for exactly two things that are unit-free: the SIGN of a
/// drawback, and the ±15% identity spread, which is a ratio.</para>
///
/// <para><b><see cref="ContainerValidator"/> is not re-implemented here.</b> G1's premise — an
/// out-of-band fixed-core magnitude loads clean because the tier window is checked only inside the pool
/// loop — is a property of that validator, and this one neither repeats nor contradicts it.</para>
/// </summary>
public static class UniqueValidator
{
    /// <summary>
    /// Which kinds count as a <b>raw stat</b> for `narrow`. The two stat kinds and nothing else: the
    /// unique exemplar's own reasoning is that a <c>resource.delta</c> rider "is not a raw stat, which
    /// is exactly why it can be generous", and the same is true of every board, status, shield and
    /// spawn kind. Structural, not a balance number — it is a classification of the closed kind
    /// vocabulary, and a balance pass changes the ceiling in `uniques.v1.json`, not this list.
    /// </summary>
    public static readonly IReadOnlyCollection<string> RawStatKinds =
        new[] { "stat.modify", "stat.derived" };

    /// <summary>Kinds whose value sign carries "this costs you something" (definitions §2).</summary>
    static readonly IReadOnlyCollection<string> SignedMagnitudeKinds =
        new[] { "stat.modify", "stat.derived", "resource.delta" };

    /// <summary>
    /// Validate one unique against its container and the rung it carries.
    /// </summary>
    /// <param name="rungOrdinal">The rung's own ordinal, for the floor and the ≥ 90 reachability rule.</param>
    /// <param name="roleId">The role the container's base type occupies. A unique carries no role of its
    /// own — it occupies the role of the base type it is built on.</param>
    /// <param name="isSetMember">
    /// Whether an <c>item_set_member</c> row references this container. Supplied by the caller because
    /// it is a cross-table fact and Core does no I/O; <see cref="UniqueRules.SetMembership"/> fires on it.
    /// </param>
    public static IReadOnlyList<AtomRejection> Validate(
        UniqueRow u, ContainerRow c, RarityRungWindow rung, int rungOrdinal, string roleId,
        Func<string, AtomRow?> lookupAtom, UniqueTuning tuning, bool isSetMember = false)
    {
        if (u is null) throw new ArgumentNullException(nameof(u));
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        UniqueRules.EnsureRegistered();

        var fails = new List<AtomRejection>();

        // ---- the class stays an ordinary item container (§3.4, "Never" boundary) -----------------
        if (c.Kind != ContainerKind.Item)
            fails.Add(Rule(UniqueRules.Shape, u,
                $"container kind is {c.Kind}; a unique is an ordinary '{ContainerKind.Item}' container and " +
                "this module asks for no new container_kind"));

        if (!string.Equals(c.ContainerId, u.ContainerId, StringComparison.Ordinal))
            fails.Add(Rule(UniqueRules.Shape, u,
                $"item_unique row is keyed on '{u.ContainerId}' but the container is '{c.ContainerId}'"));

        // ---- shape (§3.6, corrected: the two roll columns, never `pool_rolls`) --------------------
        var totalRolls = c.PrefixRolls + c.SuffixRolls;
        if (totalRolls > UniqueLimits.MaxTotalRolls)
            fails.Add(Rule(UniqueRules.Shape, u,
                $"prefix_rolls {c.PrefixRolls} + suffix_rolls {c.SuffixRolls} = {totalRolls} exceeds " +
                $"{UniqueLimits.MaxTotalRolls}; two rolls reintroduce the rare's grind on the one item whose " +
                "promise was that finding it was the event"));

        // Only meaningful when there IS a variance pool: a unique with no pool authors no tier at all
        // and its NULL window is not a violation.
        if (c.Pool.Count > 0 && c.MinTier != c.MaxTier)
            fails.Add(Rule(UniqueRules.Shape, u,
                $"variance pool spans tiers [{Show(c.MinTier)}, {Show(c.MaxTier)}]; a unique authors ONE tier " +
                "so ilvl narrowing is a no-op inside the item and a clean structural refusal outside it"));

        var core = new List<(ContainerAtomRow Entry, AtomRow Atom)>();
        foreach (var entry in c.Atoms)
        {
            var atom = lookupAtom(entry.AtomId);
            if (atom is null)
            {
                // UnknownAtom is ContainerValidator's refusal and it is a real code; do not shadow it
                // with a content rule. Skip the row here so the rest of the checks still report.
                continue;
            }

            core.Add((entry, atom));
        }

        // seq 0 is the base type's inherited base stat (I3 §5.2's convention, and §7.4's table excludes
        // it by name). Identity is everything above it.
        var identity = core.Where(x => x.Entry.Seq > 0).ToList();
        if (identity.Count > tuning.MaxIdentityAtoms)
            fails.Add(Rule(UniqueRules.Shape, u,
                $"{identity.Count} identity atoms above seq 0 exceeds the readability cap of " +
                $"{tuning.MaxIdentityAtoms} (uniques.v1.json maxIdentityAtoms)"));

        foreach (var (entry, atom) in identity)
            CheckIdentitySpread(u, entry, atom, tuning, fails);

        // ---- rung eligibility (§4.1) and reachability (§4.5 rule 1, UNCHANGED by D7) --------------
        // D4.23: u.Enabled (already modeled, spec-unique-pipeline.md §5 point 5) exempts a retired
        // row from this check the same way UniqueCorpusValidator's own seed-level check is exempted —
        // one rule, checked consistently at both the seed and the row layer.
        if (u.Enabled && !tuning.IsRungEligible(rungOrdinal))
            fails.Add(Rule(UniqueRules.RungIneligible, u,
                $"rung '{rung.RarityId}' is ordinal {rungOrdinal}, below the floor of {tuning.RungFloorOrdinal} " +
                "(unique_eligible = 0) — the two rungs below it are rungs whose whole meaning is the absence " +
                "of design"));

        if (u.Acquisition == UniqueAcquisition.Drop && rungOrdinal >= 90)
            fails.Add(Rule(UniqueRules.Unreachable, u,
                $"acquisition 'drop' at ordinal {rungOrdinal}: every unique at ordinal ≥ 90 must be " +
                "source-locked or deterministic. D7 lifted the rung ceiling; it did not lift this one — an " +
                "item you cannot find is a different problem from a rung you cannot reach"));

        // ---- role (§3.7 device 4, the per-row half) -----------------------------------------------
        if (tuning.ForbiddenRoles.Contains(roleId, StringComparer.Ordinal))
            fails.Add(Rule(UniqueRules.RoleForbidden, u,
                $"role '{roleId}' is barred from carrying a unique in v1 — a duplicated role with the " +
                "smallest budget is doubled by construction, which is the fastest path to convergence"));

        // ---- sets (§3.8) ---------------------------------------------------------------------------
        if (isSetMember)
            fails.Add(Rule(UniqueRules.SetMembership, u,
                "an item_set_member row references this container; a unique may not be a set member — the " +
                "1.5 AE premium would be paid twice and none of I5's anti-jail rules can reach a unique's core"));

        // ---- counter-pressure (§3.7 device 1) — CHECKED against the content, never trusted ---------
        var pricing = Price(identity, totalRolls, rung);
        var baseline = UniqueBudget.RungBaselineAeHundredths(rung);
        if (!SatisfiesCounterPressure(u, identity, pricing, baseline, tuning, out var why))
            fails.Add(Rule(UniqueRules.CounterPressure, u, why));

        // ---- budget (§3.7 device 2) -----------------------------------------------------------------
        var allowance = UniqueBudget.AllowanceAeHundredths(rung, tuning);
        if (pricing.TotalAeHundredths > allowance)
            fails.Add(Rule(UniqueRules.Budget, u,
                $"summed content is {pricing.TotalAeHundredths} AE×100 against an allowance of {allowance} " +
                $"(rung baseline {baseline} + premium {tuning.BudgetPremiumAeHundredths})"));

        var drift = Math.Abs(u.BudgetAeHundredths - pricing.TotalAeHundredths);
        // Widen before multiplying, divide never: both sides are compared as products, not ratios.
        if (checked(drift * 100L) > pricing.TotalAeHundredths * tuning.BudgetDriftTolerancePercent)
            fails.Add(Rule(UniqueRules.Budget, u,
                $"declared budget_ae {u.BudgetAeHundredths} differs from the summed content " +
                $"{pricing.TotalAeHundredths} by more than ±{tuning.BudgetDriftTolerancePercent}% " +
                "(definitions §7's shared drift tolerance)"));

        return fails;
    }

    /// <summary>
    /// Price a unique's identity and variance in AE × 100 at its rung. Public because the corpus
    /// report and the parity metric price the same way, and two reckoners would disagree the first
    /// time either changed.
    /// </summary>
    public static UniquePricing Price(
        IReadOnlyList<(ContainerAtomRow Entry, AtomRow Atom)> identity, int totalRolls, RarityRungWindow rung)
    {
        long signed = 0, raw = 0;
        foreach (var (entry, atom) in identity)
        {
            var ae = UniqueBudget.AeHundredthsOf(RarityOverlapSimulator.TierMidpoint(ClampTier(atom.Tier)), rung);
            var isCost = HasNegativeMagnitude(atom, entry.OverridesJson);
            signed += isCost ? -ae : ae;
            if (!isCost && RawStatKinds.Contains(atom.KindId, StringComparer.Ordinal)) raw += ae;
        }

        // One roll is one AE by definition -- that is what the unit means. The variance pool sits at
        // the container's single authored tier, which for a well-formed unique is inside the rung's
        // window; pricing it at the reference tier is the same statement as "1 AE".
        var variance = (long)totalRolls * UniqueBudget.AeScale;
        return new UniquePricing(signed, raw, variance);
    }

    static int ClampTier(int tier) =>
        tier < 1 ? 1 : tier > RarityOverlapSimulator.TierCount ? RarityOverlapSimulator.TierCount : tier;

    static bool SatisfiesCounterPressure(
        UniqueRow u, IReadOnlyList<(ContainerAtomRow Entry, AtomRow Atom)> identity,
        UniquePricing pricing, long baseline, UniqueTuning tuning, out string why)
    {
        switch (u.CounterPressure)
        {
            case UniqueCounterPressure.Drawback:
                if (identity.Any(x => HasNegativeMagnitude(x.Atom, x.Entry.OverridesJson)))
                {
                    why = "";
                    return true;
                }

                why = "declares counter_pressure 'drawback' but no identity atom carries a negative magnitude " +
                      "on a signed kind; the declaration is checked against the content, never trusted";
                return false;

            case UniqueCounterPressure.Conditional:
                if (identity.Any(x => HasPredicate(x.Atom.WhenJson)))
                {
                    why = "";
                    return true;
                }

                why = "declares counter_pressure 'conditional' but no identity atom carries a non-empty " +
                      "when_json predicate, so the capability fires unconditionally";
                return false;

            case UniqueCounterPressure.Narrow:
                // long, not int: AE x 100 against a rung baseline that contentScale can reach. Widen
                // BEFORE multiplying, and compare as a product so nothing divides at all.
                if (pricing.RawStatAeHundredths * 1000L <= baseline * (long)tuning.NarrowCeilingPerMille)
                {
                    why = "";
                    return true;
                }

                why = $"declares counter_pressure 'narrow' but its raw-stat total is " +
                      $"{pricing.RawStatAeHundredths} AE×100, above " +
                      $"{tuning.NarrowCeilingPerMille}‰ of the rung baseline {baseline}";
                return false;

            default:
                why = $"counter_pressure '{u.CounterPressure}' is not one of the three checked kinds";
                return false;
        }
    }

    /// <summary>
    /// True when any value spec on the atom (after the container's overrides) is negative on a kind
    /// whose sign means "this costs you". <b>The check asks the kind</b>; it does not assume a negative
    /// number is a cost, because sign carries meaning per kind (definitions §2) and a negative
    /// <c>box.set</c> parameter is a malformed row, not a drawback.
    /// </summary>
    public static bool HasNegativeMagnitude(AtomRow atom, string? overridesJson)
    {
        if (!SignedMagnitudeKinds.Contains(atom.KindId, StringComparer.Ordinal)) return false;

        foreach (var spec in ValueSpecs(atom, overridesJson))
            if (spec.Min < 0 || spec.Max < 0) return true;

        return false;
    }

    /// <summary>True when <c>when_json</c> carries a non-empty <c>predicate</c> object.</summary>
    public static bool HasPredicate(string? whenJson)
    {
        if (string.IsNullOrWhiteSpace(whenJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(whenJson!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty("predicate", out var pred) &&
                   pred.ValueKind == JsonValueKind.Object &&
                   pred.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            // A malformed when_json is AtomRowValidator's rejection, not a content rule of ours.
            return false;
        }
    }

    static void CheckIdentitySpread(
        UniqueRow u, ContainerAtomRow entry, AtomRow atom, UniqueTuning tuning, List<AtomRejection> fails)
    {
        foreach (var spec in ValueSpecs(atom, entry.OverridesJson))
        {
            if (spec.Roll != RollPolicy.OnInstantiate) continue;
            if (spec.Min == spec.Max) continue;

            long min = spec.Min, max = spec.Max;
            var midpoint = Math.Abs(min + max) / 2;
            var halfWidth = (max - min) / 2;

            if (midpoint == 0)
            {
                fails.Add(Rule(UniqueRules.Shape, u,
                    $"identity atom '{entry.AtomId}' rolls [{min}, {max}] around a midpoint of zero, so its " +
                    "spread is unbounded as a fraction of what it is a spread of"));
                continue;
            }

            // Widen before multiplying, divide never: compare the two products.
            if (checked(halfWidth * 1000L) > midpoint * (long)tuning.IdentitySpreadPerMille)
                fails.Add(Rule(UniqueRules.Shape, u,
                    $"identity atom '{entry.AtomId}' rolls [{min}, {max}], a spread of ±{halfWidth} around " +
                    $"{midpoint} — wider than ±{tuning.IdentitySpreadPerMille}‰ of the midpoint. Wide enough " +
                    "that a bad copy stops being the item"));
        }
    }

    /// <summary>
    /// Every value-kind param on the atom, with the container's own overrides applied. Reads the kind
    /// registry for which params ARE values, so a `box.set` boxType or a `status.apply` status id is
    /// never mistaken for a magnitude.
    /// </summary>
    static IEnumerable<ValueSpec> ValueSpecs(AtomRow atom, string? overridesJson)
    {
        var kind = AtomKindRegistry.Get(atom.KindId);
        if (kind is null) yield break;

        var overrides = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(overridesJson))
        {
            JsonDocument? ovDoc = null;
            try { ovDoc = JsonDocument.Parse(overridesJson!); }
            catch (JsonException) { /* BadValueSpec is ContainerValidator's; ignore here */ }

            if (ovDoc is not null)
            {
                using (ovDoc)
                {
                    if (ovDoc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in ovDoc.RootElement.EnumerateObject())
                            overrides[p.Name] = p.Value.Clone();
                }
            }
        }

        Dictionary<string, JsonElement> baseParams = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(atom.ParamsJson))
        {
            JsonDocument? pDoc = null;
            try { pDoc = JsonDocument.Parse(atom.ParamsJson); }
            catch (JsonException) { }

            if (pDoc is not null)
            {
                using (pDoc)
                {
                    if (pDoc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in pDoc.RootElement.EnumerateObject())
                            baseParams[p.Name] = p.Value.Clone();
                }
            }
        }

        foreach (var def in kind.Params.Defs)
        {
            if (def.Kind != ParamKind.Value) continue;

            if (!overrides.TryGetValue(def.Name, out var raw) && !baseParams.TryGetValue(def.Name, out raw))
                continue;

            if (AtomJson.TryReadValueSpec(raw, out var spec).IsOk) yield return spec;
        }
    }

    static AtomRejection Rule(string ruleId, UniqueRow u, string detail) =>
        AtomRejection.ContentRule(ruleId, $"{u.ContainerId}: {detail}");

    static string Show(int? v) => v?.ToString() ?? "null";
}
