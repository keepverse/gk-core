using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Actions.Grants;

/// <summary>The one recognized grant role this program answers item 9 about (spec-grant-seam.md
/// §6): a grant carrying this role is a candidate to REPLACE the actor's default attack, never a
/// magnitude or an envelope override.</summary>
public static class ActionGrantRoles
{
    public const string DefaultAttack = "default-attack";
}

/// <summary>One assembled entry, with EVERY source that grants it — provenance is kept, never
/// collapsed (spec §2: "removing one source leaves the action, because the other row is still
/// live"). <c>"intrinsic"</c> is the reserved source name for the species basics/innate.</summary>
public readonly record struct AssembledAction(string ActionId, IReadOnlyList<string> Sources);

/// <summary>A grant whose action_id was ALREADY present from the actor's own intrinsic set — "an
/// item granting what the species already has" (spec §2). Reported, never silently swallowed and
/// never rejected: the same item on a different species may be a real upgrade.</summary>
public readonly record struct RedundantGrantReport(string ActionId, string Source);

public sealed record AssemblyResult(
    IReadOnlyList<AssembledAction> Actions,
    string DefaultAttackActionId,
    IReadOnlyList<RedundantGrantReport> RedundantGrants);

/// <summary>
/// T23 (action-todo.md, spec-grant-seam.md §2, item 4): the entry point the item lane was told NOT
/// to implement for itself. Pure — no persistence, no cap enforcement (item 8 / T24's own job, per
/// the todo's own split), no run-phase check (the snapshot-moment / freeze concern is also T24's).
/// </summary>
public static class ActionSetAssembler
{
    /// <param name="basics">The species' three basics plus its (optional) innate — always present,
    /// regardless of grants.</param>
    /// <param name="liveGrants">Live `rpg_action_grant` rows only — a withdrawn row must never reach
    /// here (spec §4: "the next assembly omits it").</param>
    /// <param name="isDefaultAttackEligible">`ActionRow.DefaultAttackEligible` for the named id
    /// (A1 §2.1) — a grant declaring <see cref="ActionGrantRoles.DefaultAttack"/> for an ineligible
    /// action is a content error, not a silent fallback.</param>
    public static AssemblyResult Assemble(
        SpeciesBasicsRow basics,
        IReadOnlyList<ActionGrantRow> liveGrants,
        Func<string, bool> isDefaultAttackEligible)
    {
        if (basics is null) throw new ArgumentNullException(nameof(basics));
        if (liveGrants is null) throw new ArgumentNullException(nameof(liveGrants));
        if (isDefaultAttackEligible is null) throw new ArgumentNullException(nameof(isDefaultAttackEligible));

        const string intrinsicSource = "intrinsic";
        var sourcesByAction = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var redundant = new List<RedundantGrantReport>();

        void AddIntrinsic(string actionId) => sourcesByAction[actionId] = new List<string> { intrinsicSource };

        AddIntrinsic(basics.AttackActionId);
        AddIntrinsic(basics.GuardActionId);
        AddIntrinsic(basics.MoveActionId);
        if (!string.IsNullOrEmpty(basics.InnateActionId))
            AddIntrinsic(basics.InnateActionId!);

        string? defaultAttackOverride = null;

        foreach (var grant in liveGrants)
        {
            if (sourcesByAction.TryGetValue(grant.ActionId, out var existingSources))
            {
                // Reported ONLY when the grant duplicates an INTRINSIC action — two different paid
                // grants overlapping is the ordinary "one entry, two rows" case and reports nothing.
                if (existingSources.Contains(intrinsicSource))
                    redundant.Add(new RedundantGrantReport(grant.ActionId, grant.Source));
                existingSources.Add(grant.Source);
            }
            else
            {
                sourcesByAction[grant.ActionId] = new List<string> { grant.Source };
            }

            if (grant.GrantRole == ActionGrantRoles.DefaultAttack)
            {
                if (!isDefaultAttackEligible(grant.ActionId))
                    throw new ArgumentException(
                        $"grant '{grant.ActionId}' declares role '{ActionGrantRoles.DefaultAttack}' but the action is not default_attack_eligible",
                        nameof(liveGrants));
                defaultAttackOverride = grant.ActionId;
            }
        }

        // "An unarmed actor keeps the species attack" (spec §2) — the override is optional, the
        // fallback is not.
        var defaultAttack = defaultAttackOverride ?? basics.AttackActionId;

        // Built via kvp enumeration rather than `.Keys` — the purity scan bans `.Keys`/`.Values`
        // outright, since either can hide non-deterministic dictionary-iteration-order bugs.
        var actionIds = new List<string>(sourcesByAction.Count);
        foreach (var kvp in sourcesByAction)
            actionIds.Add(kvp.Key);
        actionIds.Sort(StringComparer.Ordinal); // ordinal order, never a generated id or insertion order

        var assembled = new List<AssembledAction>(actionIds.Count);
        foreach (var id in actionIds)
            assembled.Add(new AssembledAction(id, sourcesByAction[id]));

        return new AssemblyResult(assembled, defaultAttack, redundant);
    }
}
