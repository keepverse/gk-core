using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Power;

namespace FusionRpg.Core.Items.Grants;

/// <summary>
/// What the validator needs to know about the base type a grant sits on. Supplied by the caller —
/// module 6 shipped the 740-row corpus and the Core readers but <b>no <c>item_base_type</c> table</b>
/// (recorded by module 17 as a wiring gap), so there is nothing to join against yet. This mirrors
/// <see cref="EquipItemFacts"/>, which takes the same shape for the same reason.
/// </summary>
/// <param name="ContainerKind">The container's declared kind. Must be <c>Item</c>.</param>
/// <param name="RoleId">The base type's equip role — <c>effect_container.slot</c>'s frame-neutral
/// role id (§5.2: "the primary only check is one string compare against a column that exists").</param>
public readonly record struct ItemGrantBaseTypeFacts(ContainerKind? ContainerKind, string? RoleId);

/// <summary>
/// R2's inputs for one action, resolved by the caller from the shipped rung table
/// (<c>RungTable.TryResolve(row.Rung, out var m)</c> → <c>m.QPowerMilli</c>).
/// </summary>
/// <param name="QPowerMilli"><c>null</c> when the action names no resolvable rung — refused as
/// <c>unpriced</c>, never read as zero.</param>
/// <param name="RarityCeilingMilli">The seeded <c>rarity_budget.power_ceiling</c> for the item's
/// rung. <c>null</c> when the caller has no ceiling to price against — reported, not refused, because
/// that is a caller gap and not a defect in the authored row.</param>
public readonly record struct ItemGrantPriceInputs(int? QPowerMilli, int? RarityCeilingMilli);

/// <summary>
/// The item side's import-phase checks — ssot-granted-actions.md §6.1 plus
/// spec-granted-actions.md's R2 budget call.
///
/// <para><b>Returns every failure rather than first-fail</b> (modules 17 and 18's rule, kept): a
/// catalogue reported one problem at a time is one round trip per problem.</para>
///
/// <para><b>Import, never bind and never drop.</b> R2 "fails a lint; it does not silently shrink an
/// item at drop time", and the two flag checks run at exactly the moment
/// <c>ActionNotGrantable</c> and <c>ActionNotDefaultAttackEligible</c> are checked on the write
/// path — so the seam's headline failure is caught before a row is stored, not discovered at
/// runtime.</para>
/// </summary>
public static class ItemGrantValidator
{
    /// <summary>
    /// One row, against its base type, its action, and R2.
    /// </summary>
    /// <param name="action">The resolved <c>rpg_action</c> row, or <c>null</c> when the id names
    /// nothing. ⛔ With X3 unresolved this is <c>null</c> for every id, which is why gate GA2 ships
    /// with zero content rows rather than rows that point at an empty table.</param>
    /// <param name="powerTuning">Module 9's tuning. When supplied, R2 becomes GATING — that is the
    /// literal sense of its own note that the read "is reportable today and gating only when module 19
    /// lands." When <c>null</c>, the budget arm does not run at all.</param>
    public static IReadOnlyList<AtomRejection> ValidateRow(
        ItemGrantedActionRow row,
        ItemGrantBaseTypeFacts baseType,
        ActionRow? action,
        ItemGrantPriceInputs price,
        ItemPowerTuning? powerTuning = null)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        var fails = new List<AtomRejection>();
        fails.AddRange(ValidateShape(row, baseType));
        fails.AddRange(ValidateAction(row, baseType, action));
        if (powerTuning is { } tuning) fails.AddRange(ValidateBudget(row, price, tuning));
        return fails;
    }

    /// <summary>The rules that need neither an action nor a price — the ones a corpus lint can run
    /// with the action table empty.</summary>
    public static IReadOnlyList<AtomRejection> ValidateShape(
        ItemGrantedActionRow row, ItemGrantBaseTypeFacts baseType)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        var fails = new List<AtomRejection>();

        if (!ItemGrantContainerIds.IsWellFormed(row.ContainerId))
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.UnknownContainer,
                $"'{row.ContainerId}' is not a legal item container id — a grant keys on the BASE TYPE's " +
                $"container id (§4.4), whose namespace is '{ItemGrantContainerIds.Prefix}'"));

        if (baseType.ContainerKind is { } kind && kind != Effects.Atoms.ContainerKind.Item)
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.UnknownContainer,
                $"'{row.ContainerId}' is a container of kind '{kind}'; §5.2 requires container_kind = " +
                "'item'. The ACTION's own atoms live in a separate 'skill' container referenced by " +
                "rpg_action.container_id, and the two must never be merged (§3.3)"));

        if (row.Seq < 0)
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.BadValue,
                $"'{row.ContainerId}' declares seq {row.Seq}; seq is stable authoring and display order " +
                "and is part of the primary key"));

        if (string.IsNullOrWhiteSpace(row.ActionId))
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.UnknownAction,
                $"'{row.ContainerId}' seq {row.Seq} names no action; the action id is the entire seam"));

        if (row.Role == ItemGrantRole.DefaultAttack
            && baseType.RoleId is { } roleId
            && !string.Equals(roleId, ItemGrantLimits.DefaultAttackRoleId, StringComparison.Ordinal))
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.DefaultAttackNotAllowed,
                $"'{row.ContainerId}' declares role '{row.RoleWire}' on equip role '{roleId}'; §4.3 " +
                $"option (C) makes it legal on '{ItemGrantLimits.DefaultAttackRoleId}' only, so the " +
                "1H + off-hand conflict is unrepresentable rather than arbitrated. An off-hand may " +
                $"still grant an EXTRA action ('{ItemGrantRoles.Granted}')"));

        return fails;
    }

    /// <summary>Handshake items 1, 2 and 3 — the three flags without which the seam's headline failure
    /// modes are unpreventable (failure mode 2).</summary>
    public static IReadOnlyList<AtomRejection> ValidateAction(
        ItemGrantedActionRow row, ItemGrantBaseTypeFacts baseType, ActionRow? action)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        var fails = new List<AtomRejection>();

        if (string.IsNullOrWhiteSpace(row.ActionId)) return fails;

        if (action is null)
        {
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.UnknownAction,
                $"'{row.ContainerId}' seq {row.Seq} names action '{row.ActionId}', which is not in " +
                "rpg_action — either a genuinely misnamed id, or this server has never imported the " +
                "real action-corpus briefs (Program.cs's ActionCorpusImporter.Import call, default on)"));
            return fails;
        }

        if (!action.Enabled)
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.UnknownAction,
                $"'{row.ContainerId}' names action '{row.ActionId}', which is disabled. A disabled " +
                "action is not referenceable, exactly as a disabled atom is not bindable (§6.1)"));

        if (action.Kind == ActionKind.Basic)
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.BasicCollision,
                $"'{row.ContainerId}' names basic action '{row.ActionId}'; every actor holds its three " +
                "basics intrinsically, so granting one double-counts it. The shipped " +
                "ActionValidator.ValidateGrant refuses this at the write — this refuses it at import"));

        if (!action.Grantable)
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.NotGrantable,
                $"'{row.ContainerId}' names action '{row.ActionId}', which is not flagged grantable " +
                "(handshake item 2). 'move', 'pass' and the defence actions are actor-intrinsic"));

        if (row.Role == ItemGrantRole.DefaultAttack && !action.DefaultAttackEligible)
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.DefaultAttackNotAllowed,
                $"'{row.ContainerId}' proposes '{row.ActionId}' as a default-attack replacement, but the " +
                "action is not flagged default_attack_eligible (handshake item 3). The two flags are " +
                "separate on purpose: collapsing them would make every grantable action a legal " +
                "default attack"));

        _ = baseType;
        return fails;
    }

    /// <summary>
    /// ⭐ <b>R2, picked up.</b> spec-item-power-reads.md built the read and named this module as its
    /// consumer; nothing had ever called it. One call at import — never at drop, never at bind.
    ///
    /// <para>Two outcomes refuse. <b>Unpriced-for-lack-of-a-rung is a content defect</b> and is refused:
    /// G4's stated fear is that "pricing it at zero would make every action-granting item strictly
    /// dominant", and this is the enforcement half of module 9's answer.
    /// <b>Unpriced-for-lack-of-a-ceiling is a CALLER gap</b> — the rarity budget simply was not seeded
    /// for this rung — and is reported, not refused: refusing an authored row because the caller has no
    /// ceiling would blame the content for the harness.</para>
    /// </summary>
    public static IReadOnlyList<AtomRejection> ValidateBudget(
        ItemGrantedActionRow row, ItemGrantPriceInputs price, ItemPowerTuning tuning)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        var fails = new List<AtomRejection>();

        var read = ItemPowerReads.GrantedActionPrice(price.QPowerMilli, price.RarityCeilingMilli, tuning);

        if (read.Unpriced)
        {
            if (price.QPowerMilli is null)
                fails.Add(ItemGrantRules.Fail(ItemGrantRules.Unpriced,
                    $"'{row.ContainerId}' grants '{row.ActionId}', which has no resolvable rung " +
                    $"({read.UnpricedReason}). An unpriced action is REFUSED, never read as 0 — a free " +
                    "action would make every action-granting item strictly dominant in any budget"));
            return fails;
        }

        if (read.Over)
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.OverBudget,
                $"'{row.ContainerId}' grants '{row.ActionId}' priced at {read.ShareMilli}‰ of its rarity " +
                $"ceiling, over the {EffectiveCapMilli(tuning)}‰ allowance. Reported as a share with a " +
                $"±{tuning.PowerDisplayBandPercent}% band, never as an exact threshold: an action's price " +
                "and an affix bundle's price come from different shapes, so the error does not cancel"));

        return fails;
    }

    /// <summary>The soft cap R2 measures against: module 9's <c>grantedActionShareCapMilli</c> when a
    /// balance pass has set one, else the whole ceiling — see
    /// <see cref="ItemGrantLimits.WholeCeilingShareMilli"/> for why that bound is the unit's identity
    /// rather than an invented number.</summary>
    public static int EffectiveCapMilli(ItemPowerTuning tuning) =>
        tuning.GrantedActionShareCapMilli ?? ItemGrantLimits.WholeCeilingShareMilli;

    /// <summary>
    /// §6.4's cross-row checks: they are properties of the CATALOGUE, not of a row, so they run once
    /// over a whole base type's grant rows rather than per row.
    /// </summary>
    public static IReadOnlyList<AtomRejection> ValidateContainer(
        string containerId, IReadOnlyList<ItemGrantedActionRow> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        var fails = new List<AtomRejection>();

        var seenSeq = new HashSet<int>();
        var seenAction = new HashSet<string>(StringComparer.Ordinal);
        var defaultAttacks = 0;

        foreach (var row in rows)
        {
            if (!seenSeq.Add(row.Seq))
                fails.Add(ItemGrantRules.Fail(ItemGrantRules.DuplicateSeq,
                    $"'{containerId}' declares seq {row.Seq} twice; (container_id, seq) is the primary key"));

            if (!seenAction.Add(row.ActionId))
                fails.Add(ItemGrantRules.Fail(ItemGrantRules.DuplicateAction,
                    $"'{containerId}' declares action '{row.ActionId}' twice. Two ITEMS granting one " +
                    "action is legal and dedups to one set entry (§3.7a); one item declaring it twice " +
                    "is a row error"));

            if (row.Role == ItemGrantRole.DefaultAttack) defaultAttacks++;
        }

        if (defaultAttacks > ItemGrantLimits.MaxDefaultAttacksPerContainer)
            fails.Add(ItemGrantRules.Fail(ItemGrantRules.DefaultAttackNotAllowed,
                $"'{containerId}' declares {defaultAttacks} default-attack rows; §3.7(c)'s precedence has " +
                "exactly two rungs and a third would need an arbitration rule"));

        return fails;
    }

    /// <summary>
    /// Display order — §3.7's "(equip role ordinal, seq, action_id), compared ORDINAL". Never a
    /// generated id: definitions §5 is explicit that sorting on one produces different bytes from
    /// identical inputs, and under <c>(setup, seed, decision-trace)</c> that is a broken replay.
    /// </summary>
    public static IReadOnlyList<ItemGrantedActionRow> InDisplayOrder(
        IReadOnlyList<ItemGrantedActionRow> rows, Func<string, int> roleOrdinalOf)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        if (roleOrdinalOf is null) throw new ArgumentNullException(nameof(roleOrdinalOf));

        var ordered = rows.ToList();
        ordered.Sort((a, b) =>
        {
            var byRole = roleOrdinalOf(a.ContainerId).CompareTo(roleOrdinalOf(b.ContainerId));
            if (byRole != 0) return byRole;
            var bySeq = a.Seq.CompareTo(b.Seq);
            return bySeq != 0 ? bySeq : string.CompareOrdinal(a.ActionId, b.ActionId);
        });
        return ordered;
    }
}
