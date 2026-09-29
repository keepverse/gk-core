using FusionRpg.Core.Items.Requirements;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// One base type's place in the upgrade tree. <b>The edge is authored, never derived</b> — owner
/// ruling 2026-09-21 (T37/T38): a class ladder is a style/weight axis, so a successor read off it
/// would silently turn an upgrade into a different kind of item (a `blade` into a `launcher`, or a
/// `cloth` piece into whatever the next rung happens to be wearing). <see cref="SuccessorOf"/> is the
/// explicit per-base-type edge; it is null for every base type nobody has authored an edge for yet,
/// which is the correct state for all non-armour types today (T38's content pass).
/// </summary>
public sealed record ItemUpgradeNode(
    string BaseTypeId,
    string Ladder,
    string Frame,
    int Rung,
    int LadderRungCount,
    string ImplicitFamily,
    string? SuccessorOf,
    IReadOnlySet<string> LegalAffixPoolTags,
    /// <summary>
    /// The base type's own role — Rule 1's production input
    /// (<see cref="RoleAllowListLegality"/>). Runtime-readable: `RpgStore.GetBaseType` returns frame and
    /// role. Defaulted so every corpus-time caller and test written before the ruling still compiles.
    /// </summary>
    string Role = "");

/// <summary>One affix of the input, exactly as the input owns it. Upgrade carries these
/// <b>unchanged</b> — identity, tier and value — and never re-rolls, re-tiers or drops one
/// (spec-item-upgrade-tree.md § 2, § 2a).</summary>
public sealed record ItemUpgradeAffix(string PoolTag, string FamilyId, int Tier, long Value);

/// <summary>
/// The input, the successor's own requirement and the owner's standing. There is deliberately
/// <b>no RNG</b>: an upgrade has no failure chance of its own (spec § 5) — risk belongs to
/// `craft-risk-ladder`, and a private die here would be a second failure mechanism.
/// </summary>
public sealed record ItemUpgradeRequest(
    ItemUpgradeNode Input,
    IReadOnlyList<ItemUpgradeAffix> Affixes,
    RequirementProfile? SuccessorRequirement,
    int OwnerLevel,
    AptitudeAllocation? OwnerAllocation = null);

/// <summary>What an upgrade will produce. Both implicits are named because the card must show the
/// outgoing and incoming implicit <b>before</b> the input is consumed (spec § 2a Rule 2) — the change
/// is a real consequence of a different base type, never a hidden one.</summary>
public sealed record ItemUpgradePlan(
    ItemUpgradeNode Successor,
    IReadOnlyList<ItemUpgradeAffix> CarriedAffixes,
    string OutgoingImplicitFamily,
    string IncomingImplicitFamily);

/// <summary>`Ok` with a plan, or a named refusal and no plan at all — never a partial plan, because
/// a caller must not be able to consume the input against half a decision.</summary>
public readonly record struct ItemUpgradeDecision(
    bool Ok, ItemUpgradePlan? Plan, string? RefusalCode, string? RefusalDetail);

/// <summary>
/// Rule 1's one injected seam (spec-item-upgrade-tree.md § 2a, resolved 2026-09-21): whether a carried
/// affix is legal on the successor. Two implementations ship — <see cref="PoolTagLegality"/> for a
/// caller holding authored pool tags (this policy's own tests, and the `affix_pool_tag` column if it is
/// ever emitted) and <see cref="RoleAllowListLegality"/> for the shipped corpus, whose affix families
/// carry a per-role allow-list instead. One predicate, so switching the source is wiring, not a
/// rewrite of the rule.
/// </summary>
public interface IAffixLegality
{
    bool Allows(ItemUpgradeAffix affix, ItemUpgradeNode successor);

    /// <summary>Named in the refusal, so a log says which rule refused.</summary>
    string Rule { get; }
}

/// <summary>The historical rule: the successor's own legal pool tags must contain the affix's tag.</summary>
public sealed class PoolTagLegality : IAffixLegality
{
    public string Rule => "affix-pool-tag";

    public bool Allows(ItemUpgradeAffix affix, ItemUpgradeNode successor) =>
        successor.LegalAffixPoolTags.Contains(affix.PoolTag);
}

/// <summary>
/// species-gear-chain T37 (`item-upgrade-tree`) — the one successor lookup and the three mandatory
/// rules, pure: no I/O, no tuning read, no RNG. Every input arrives as an argument, so the caller
/// owns what this file does not decide.
///
/// <para><b>Refuse, never warn-and-proceed.</b> The input is consumed on success and there is no
/// undo, so an illegal affix set, an unmet requirement or an absent edge is a refusal that consumes
/// nothing (spec § 2a, § 4, § 5).</para>
/// </summary>
public static class ItemUpgradePolicy
{
    /// <summary>Commander gear: "single-band in v1 — it is not item-level tiered". It is not a
    /// progression ladder at all, so it is refused by name rather than left to the absent-edge path —
    /// an authored edge on a `standard` row would be a content mistake worth failing loudly.</summary>
    public const string StandardLadder = "standard";

    /// <summary>
    /// Resolve the successor of <paramref name="request"/>'s input, or refuse by name.
    ///
    /// <para>Order is the spec's own: the authored edge first (so a base type with none refuses as
    /// such), then the edge's <b>shape</b> — same ladder, same frame, exactly one rung up — then affix
    /// legality on the successor's own pool, then the requirement check, which is the last thing
    /// before a caller may consume anything.</para>
    /// </summary>
    public static ItemUpgradeDecision Decide(
        ItemUpgradeRequest request,
        IReadOnlyDictionary<string, ItemUpgradeNode> catalog,
        IAffixLegality? legality = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request.Input);
        ArgumentNullException.ThrowIfNull(request.Affixes);
        if (request.SuccessorRequirement is not null) ArgumentNullException.ThrowIfNull(request.OwnerAllocation);

        var input = request.Input;

        if (string.Equals(input.Ladder, StandardLadder, StringComparison.Ordinal))
            return Refuse("upgrade.standard-refused",
                $"'{input.BaseTypeId}' is {StandardLadder} gear — a single-band ladder, not a progression one");

        if (input.SuccessorOf is not { Length: > 0 } successorId)
        {
            // Two honest readings of "no edge", named separately so the difference is visible in a log:
            // nothing left to climb (only when the caller declares rung data), versus a base type whose
            // edge nobody has authored yet.
            return input.LadderRungCount > 0 && input.Rung >= input.LadderRungCount
                ? Refuse("upgrade.top-rung",
                    $"'{input.BaseTypeId}' is rung {input.Rung} of {input.LadderRungCount} on '{input.Ladder}' — the ladder's own top, refused here")
                : Refuse("upgrade.no-successor",
                    $"'{input.BaseTypeId}' has no authored successorOf edge — author one (a ladder is a style axis, never a successor spine)");
        }

        if (!catalog.TryGetValue(successorId, out var successor))
            return Refuse("upgrade.successor-unknown",
                $"'{input.BaseTypeId}' declares successorOf '{successorId}', which is not in the catalog");

        if (!string.Equals(successor.Frame, input.Frame, StringComparison.Ordinal))
            return Refuse("upgrade.successor-not-same-frame",
                $"'{successor.BaseTypeId}' is frame '{successor.Frame}' against '{input.Frame}' — an edge never changes frame");

        // ⛔ The ladder/rung shape checks run ONLY for callers that declare rung data (this policy's own
        // tests, and any future corpus-time caller). The runtime cannot read a base type's class or band
        // (`RpgStore.GetBaseType` returns frame and role only), and under the 2026-09-21 ruling the
        // authored edge IS the authority — so production passes no rung data and must not be made to
        // invent one. The content-time invariant is T38's closure check plus `ItemSeedValidator`.
        if (input.LadderRungCount > 0)
        {
            if (!string.Equals(successor.Ladder, input.Ladder, StringComparison.Ordinal))
                return Refuse("upgrade.successor-not-same-ladder-and-frame",
                    $"'{successor.BaseTypeId}' is ({successor.Ladder}, {successor.Frame}) against ({input.Ladder}, {input.Frame}) — an edge never changes ladder or frame");

            // `checked`: the rung is a small identity integer, and a successor edge landing anywhere but
            // exactly one rung up is a content defect, never a silent multi-rung jump.
            if (successor.Rung != checked(input.Rung + 1))
                return Refuse("upgrade.successor-not-next-rung",
                    $"'{successor.BaseTypeId}' is rung {successor.Rung}, not {input.Rung} + 1");
        }

        var rule = legality ?? DefaultLegality;
        foreach (var affix in request.Affixes)
        {
            if (!rule.Allows(affix, successor))
                return Refuse("upgrade.affix-illegal-on-successor",
                    $"affix '{affix.FamilyId}' is not legal on the successor '{successor.BaseTypeId}' ({rule.Rule}) — refuse, never relabel");
        }

        if (request.SuccessorRequirement is { } requirement)
        {
            var trial = RequirementTrialEvaluator.Evaluate(requirement, request.OwnerLevel, request.OwnerAllocation!);
            if (!trial.Ready)
                return Refuse("upgrade.requirement-unmet",
                    $"the successor '{successor.BaseTypeId}' requires {trial.Unmet}, which this owner does not meet — the input is not consumed");
        }

        // Carried, not re-created: one new list so a caller cannot mutate the input's own affixes
        // through the plan, and the affix records themselves are the input's own values.
        var carried = new List<ItemUpgradeAffix>(request.Affixes);

        return new ItemUpgradeDecision(true,
            new ItemUpgradePlan(successor, carried, input.ImplicitFamily, successor.ImplicitFamily),
            null, null);
    }

    static ItemUpgradeDecision Refuse(string code, string detail) =>
        new(false, null, code, detail);

    /// <summary>The historical tag rule, used when a caller passes no predicate of its own.</summary>
    static readonly IAffixLegality DefaultLegality = new PoolTagLegality();
}
