using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// ⛔ <b>Per-budget targeting — <c>pool_rolls</c> does not exist.</b> I7 is built end to end on a
/// single <c>pool_rolls</c> column with <c>K = pool_rolls − T</c> and <c>ANCHOR_MULT = 2^K</c>;
/// verified 2026-09-04, both tables split into <c>PrefixRolls</c>/<c>SuffixRolls</c> and
/// <c>Instantiator.Draw</c> runs its budget draw twice, each with its own RNG stream. The
/// algebra restates per budget and the hazard I7 handed to module 1 (two sources of truth for
/// <c>pool_rolls</c>) no longer exists.
///
/// <para>⚠ The two passes are <b>separately streamed, not independent</b> (A1, wired 2026-09-05): a
/// <see cref="AffixClass.Mixed"/> affix spends one roll of each budget simultaneously, so it is one
/// target in <see cref="TargetPrefix"/> <i>and</i> one in <see cref="TargetSuffix"/>. Derive both
/// with <see cref="RerollPolicy.TargetsFor"/> rather than counting targets by hand.</para>
/// </summary>
public readonly record struct BudgetTargets(int PrefixRolls, int SuffixRolls, int TargetPrefix, int TargetSuffix)
{
    /// <summary>Anchors kept in the prefix budget.</summary>
    public int AnchorsPrefix => PrefixRolls - TargetPrefix;

    /// <summary>Anchors kept in the suffix budget.</summary>
    public int AnchorsSuffix => SuffixRolls - TargetSuffix;

    public int Targets => TargetPrefix + TargetSuffix;
}

/// <summary>What a reroll would redraw, and what it keeps.</summary>
/// <param name="Seq">The drawn <c>effect_instance_atom.seq</c> this affix occupies.</param>
public sealed record DrawnAffix(int Seq, string AffixId, string Group, AffixClass Class, int Tier);

/// <summary>
/// spec-enhance-reroll.md §2/§7 — temper, reforge and imprint, all expressed <b>per budget</b>. Pure:
/// no store, no file I/O, no RNG owned here (the caller passes the draw in), and every balance number
/// arrives in <see cref="EnhancementTuning"/>.
/// </summary>
public static class RerollPolicy
{
    static RerollPolicy() => MutationRules.EnsureRegistered();

    /// <summary>
    /// <c>ANCHOR_MULT = 2 ^ (K_prefix + K_suffix)</c> — superlinear, unchanged in shape from I7, but
    /// summed over the two budgets rather than read off one column. Returns a <c>long</c> and
    /// <b>throws</b> rather than saturating if the exponent could not be represented: an absolute
    /// bound derived from the arithmetic, per AGENTS.md, never a silent clamp.
    /// </summary>
    // Structural (tunables-ssot.md T2), never a balance dial: `long` carries 63 usable bits, so
    // 2^63 is not representable and 62 is the largest exponent that is. It is the TYPE's width, and
    // a balance pass has nothing to say about it.
    const int MaxAnchorExponent = 62;

    public static long AnchorMultiplier(BudgetTargets t)
    {
        var k = t.AnchorsPrefix + t.AnchorsSuffix;
        if (k < 0) throw new ArgumentOutOfRangeException(nameof(t), k, "anchors cannot be negative — targets exceed the budget");
        if (k > MaxAnchorExponent)
            throw new OverflowException(
                $"anchor multiplier 2^{k} exceeds long — refuses to wrap. A container with {k} untargeted rolls " +
                "is a content defect, not a price");
        return 1L << k;
    }

    /// <summary>
    /// §2's targeting rules, refused BY NAME. At least one target, and never more targets than the
    /// budget holds.
    /// </summary>
    public static AtomRejection ValidateTargets(BudgetTargets t)
    {
        if (t.PrefixRolls < 0 || t.SuffixRolls < 0)
            return MutationRules.Violated("reroll.budget-negative", "prefix/suffix rolls cannot be negative");
        if (t.TargetPrefix < 0 || t.TargetSuffix < 0)
            return MutationRules.Violated("reroll.target-negative", "target counts cannot be negative");
        if (t.Targets < 1)
            return MutationRules.Violated("reroll.no-target",
                "a reroll with no target is a paid no-op — T_prefix + T_suffix must be at least 1");
        if (t.TargetPrefix > t.PrefixRolls)
            return MutationRules.Violated("reroll.target-exceeds-budget",
                $"T_prefix={t.TargetPrefix} exceeds prefix_rolls={t.PrefixRolls}");
        if (t.TargetSuffix > t.SuffixRolls)
            return MutationRules.Violated("reroll.target-exceeds-budget",
                $"T_suffix={t.TargetSuffix} exceeds suffix_rolls={t.SuffixRolls}");
        return AtomRejection.Ok;
    }

    /// <summary>
    /// ⭐ §2's <c>Mixed</c> hazard is <b>BUILT, not refused</b> (2026-09-05). A <c>Mixed</c> affix
    /// consumes one prefix roll AND one suffix roll simultaneously, so rerolling one frees a slot in
    /// both budgets and must redraw into both. That is now exactly what
    /// <c>Instantiator.DrawBudget</c> does: the prefix pass carries the suffix budget, a <c>Mixed</c>
    /// pick spends one of each, and the suffix pass excludes it — <c>Resolver</c>'s own A1 semantics,
    /// threaded into the atom-id draw the same day. <c>reroll.mixed-affix-undefined</c> is gone with
    /// its reason: it named module 2 (`resolution-order`), which landed 2026-09-02.
    ///
    /// <para><b>What is still refused, narrowed to the case that genuinely remains:</b> a target whose
    /// affix carries a <b>slot</b> ref. <c>Instantiator.DrawBudget</c> returns bare atom ids and rolls
    /// no domain member, no tier and no value, so it cannot redraw into a slot-bearing pool;
    /// <see cref="Resolver.Resolve"/> can, but has no <c>count</c>/<c>excludeGroups</c> seam for a
    /// partial redraw. ⚠ <b>This is class-agnostic on purpose</b> — a slot-bearing <c>Prefix</c> affix
    /// is exactly as un-redrawable as a slot-bearing <c>Mixed</c> one, so refusing only <c>Mixed</c>
    /// would name the wrong thing and let a real failure through.</para>
    /// </summary>
    /// <param name="lookupAffix">Reads the target's own refs. <see cref="DrawnAffix"/> carries the
    /// class, not the bundle, and the residual case is about the refs — so the catalog is asked rather
    /// than the class guessed from.</param>
    public static AtomRejection ValidateRerollable(
        IEnumerable<DrawnAffix> targets, Func<string, AffixRow?> lookupAffix)
    {
        foreach (var target in targets)
        {
            var affix = lookupAffix(target.AffixId);
            // Distinct from `reroll.affix-outside-pool` (ValidatePostOp's, about the CONTAINER's pool):
            // this is a target the affix catalog no longer knows at all, which a catalog revision can
            // produce for an already-owned item.
            if (affix is null)
                return MutationRules.Violated("reroll.affix-unknown",
                    $"target affix '{target.AffixId}' at seq {target.Seq} is not in the affix catalog — " +
                    "a reroll cannot redraw a bundle it cannot read");

            var slot = affix.Refs.FirstOrDefault(r => r.IsSlot);
            if (slot is not null)
                return MutationRules.Violated("reroll.slot-affix-undefined",
                    $"affix '{target.AffixId}' at seq {target.Seq} has a slot ref ('{slot.SlotName}' over domain " +
                    $"'{slot.SlotDomain}') — a redraw runs through Instantiator.DrawBudget, which returns bare atom " +
                    "ids and rolls no domain member or tier. Resolver.Resolve does, but has no partial-redraw seam " +
                    "yet. Refused rather than guessed at");
        }

        return AtomRejection.Ok;
    }

    /// <summary>
    /// §2's target counts, derived rather than trusted: <b>a <c>Mixed</c> target counts against BOTH
    /// budgets</b>, because rerolling it frees a slot in each. Getting this wrong is silent — the op
    /// would validate, and <see cref="AnchorMultiplier"/> would price a freed suffix roll as an anchor
    /// — so it is computed here from the drawn list rather than left to a caller to remember.
    /// </summary>
    public static BudgetTargets TargetsFor(
        ContainerRow container, IEnumerable<DrawnAffix> drawn, IEnumerable<int> targetSeqs)
    {
        var targets = new HashSet<int>(targetSeqs);
        var picked = drawn.Where(a => targets.Contains(a.Seq)).ToList();
        return new BudgetTargets(
            container.PrefixRolls, container.SuffixRolls,
            picked.Count(a => Eligible(a.Class, AffixClass.Prefix)),
            picked.Count(a => Eligible(a.Class, AffixClass.Suffix)));
    }

    /// <summary>
    /// A partial redraw seeds each budget's exclusion set with the <b>groups of that budget's
    /// retained affixes</b> before drawing — the one behavioural change this module needs from the
    /// instantiator, and it is what makes one-per-group survive a partial reroll.
    /// </summary>
    public static IReadOnlySet<string> RetainedGroups(IEnumerable<DrawnAffix> drawn, IEnumerable<int> targetSeqs, AffixClass budget)
    {
        var targets = new HashSet<int>(targetSeqs);
        return new HashSet<string>(
            drawn.Where(a => !targets.Contains(a.Seq) && Eligible(a.Class, budget)).Select(a => a.Group),
            StringComparer.Ordinal);
    }

    /// <summary>Which budget an affix class draws against — <c>Mixed</c> is eligible in both.</summary>
    public static bool Eligible(AffixClass affix, AffixClass budget) => budget switch
    {
        AffixClass.Prefix => affix is AffixClass.Prefix or AffixClass.Mixed,
        AffixClass.Suffix => affix is AffixClass.Suffix or AffixClass.Mixed,
        _ => true,
    };

    /// <summary>
    /// §2's post-op invariant, restated per budget: the counts are unchanged, one-per-group holds,
    /// every affix came from the container's own pool, and every tier is inside the container's
    /// window. <b>This is what makes "an item the generator could never have dropped" structurally
    /// impossible</b> — a rerolled item validates exactly as a freshly instantiated one.
    /// </summary>
    public static AtomRejection ValidatePostOp(ContainerRow container, IReadOnlyList<DrawnAffix> drawn)
    {
        var prefixes = drawn.Count(a => Eligible(a.Class, AffixClass.Prefix));
        var suffixes = drawn.Count(a => Eligible(a.Class, AffixClass.Suffix));

        if (prefixes != container.PrefixRolls)
            return MutationRules.Violated("reroll.prefix-count-changed",
                $"{prefixes} prefix affixes after the reroll, container '{container.ContainerId}' authors {container.PrefixRolls}");
        if (suffixes != container.SuffixRolls)
            return MutationRules.Violated("reroll.suffix-count-changed",
                $"{suffixes} suffix affixes after the reroll, container '{container.ContainerId}' authors {container.SuffixRolls}");

        var groups = drawn.Select(a => a.Group).ToList();
        if (groups.Distinct(StringComparer.Ordinal).Count() != groups.Count)
            return MutationRules.Violated("reroll.group-collision",
                "two drawn affixes share a group — one-per-group is the rule that stops '+10 atk / +12 atk'");

        var pool = new HashSet<string>(container.Pool.Select(p => p.AffixId), StringComparer.Ordinal);
        foreach (var affix in drawn)
        {
            if (!pool.Contains(affix.AffixId))
                return MutationRules.Violated("reroll.affix-outside-pool",
                    $"affix '{affix.AffixId}' is not in container '{container.ContainerId}'s pool — a reroll may only " +
                    "draw what the generator could have drawn");
            if (container.MinTier is { } min && affix.Tier < min)
                return MutationRules.Violated("reroll.tier-outside-window",
                    $"affix '{affix.AffixId}' at t{affix.Tier} is below the container's min tier t{min}");
            if (container.MaxTier is { } max && affix.Tier > max)
                return MutationRules.Violated("reroll.tier-outside-window",
                    $"affix '{affix.AffixId}' at t{affix.Tier} is above the container's max tier t{max}");
        }

        return AtomRejection.Ok;
    }

    // ---- price ------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>reroll_cost_mult</c> <b>rung leg</b> — the integer module 7's <c>rarity_budget</c> table
    /// stores, one per rung. Derived from one tunable slope over the ladder index, never ten authored
    /// numbers (module 7's own house style for this table).
    /// </summary>
    public static int RungLegMilli(int rungIndex, EnhancementTuning t)
    {
        if (rungIndex < 0 || rungIndex >= RarityLadder.RungIds.Count)
            throw new ArgumentOutOfRangeException(nameof(rungIndex), rungIndex,
                $"rung index is 0..{RarityLadder.RungIds.Count - 1} — never rarity.ordinal");
        return EnhancementTuning.RerollRungLegMilli(rungIndex, t);
    }

    /// <summary>
    /// ⭐ <b><c>reroll_cost_mult</c>'s decided shape</b> (ssot-rarity.md §4.4 / §9.7, which requires it
    /// scale with <b>affix count</b>, not rung alone): the seeded per-rung leg multiplied by an
    /// affix-count leg. The affix leg dominates on purpose — §8.1's "low rungs are the best crafting
    /// bases" mechanism only works when a low-affix item is cheap to USE, and
    /// <see cref="EnhancementTuning.Parse"/> refuses a tuning where it does not.
    ///
    /// <para><c>long</c>, widened before multiplying, divided by 1000 exactly once at the end.</para>
    /// </summary>
    public static long CostMultMilli(int rungIndex, int affixCount, EnhancementTuning t)
    {
        if (affixCount < 0) throw new ArgumentOutOfRangeException(nameof(affixCount), affixCount, "affix count cannot be negative");
        var rung = (long)RungLegMilli(rungIndex, t);
        var affix = (long)EnhancementTuning.AffixLegMilli(affixCount, t);
        return checked(rung * affix) / 1000L;
    }
}
