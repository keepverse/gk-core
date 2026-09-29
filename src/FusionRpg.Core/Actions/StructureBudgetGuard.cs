using System.Linq;
using FusionRpg.Core.Actions.Rungs;

namespace FusionRpg.Core.Actions;

/// <summary>
/// T30, R1 (spec-action-catalog.md, spec-rung-table.md §4, action-ideal.md §8.2/§8.3): "a rung buys
/// structure, not only numbers." An authored or seeded action whose structure exceeds its rung's
/// <see cref="RungRow.StructureBudget"/> is rejected at load, naming the rung and the axis — never
/// silently accepted, which is what would make the ladder "advisory" (a rung-2 action carrying a
/// reaction would price above its rung while the content lied).
///
/// <para><b>Only the four axes computable from the three tables T30 itself reads</b> (spec §2's own
/// "Read" stage names exactly <c>rpg_action</c> + <c>rpg_action_cost</c> + <c>rpg_action_effect_scope</c>,
/// nothing from the atom program) are detected here:</para>
/// <list type="bullet">
/// <item><b>condition</b> — <c>ConditionsJson</c> is authored (non-empty).</item>
/// <item><b>sequence</b> — <c>ActionEnvelope.ResolveOffsets</c> has more than one offset (multi-hit).</item>
/// <item><b>consumption</b> — any cost row pays <see cref="ActionCostTiming.PerTick"/> (an ongoing
/// drain, distinct from a single up-front <c>onCommit</c> cost).</item>
/// <item><b>scopeSplit</b> — the action's scope rows span more than one distinct
/// <see cref="ActionEffectScope"/> (action-ideal.md §8.2's own example: "strike-and-heal-self").</item>
/// <item><b>riderStatus</b> — more than one atom is bound to the SAME scope (a base effect plus a
/// second atom riding the same target, the shape action-ideal.md §8.2's PoE example describes,
/// distinct from splitting across scopes).</item>
/// </list>
/// <para><b>Two axes are an honest, documented gap, never guessed at</b>: <c>reaction</c> cannot be
/// spent by anything authored today — <see cref="ActionKind"/> has exactly three members
/// (<c>Basic</c>/<c>Innate</c>/<c>Skill</c>), none reaction-shaped, verified by reading the enum
/// rather than assumed — so it is correctly never flagged, not merely unchecked. <c>restriction</c>
/// (action-ideal.md §8.7: "a self-debuff — <c>status.apply</c> scoped to <c>caster</c>") needs the
/// effect-atom program's own per-atom payload/target data, which is OUTSIDE the three tables this
/// module reads; detecting it belongs to whichever module first needs to read atom internals from
/// the action layer, not to a guess planted here.</para>
/// </summary>
public static class StructureBudgetGuard
{
    public static ActionRejection Check(
        ActionRow row, IReadOnlyList<ActionCostRow> costs, IReadOnlyList<ActionScopeRow> scopes, RungTable rungTable)
    {
        if (!rungTable.TryGet(row.Rung, out var rungRow))
            return Fail(row.ActionId, ActionRejectionReason.UnknownRung, $"rung {row.Rung} does not index a loaded rung row");

        var budget = new HashSet<string>(rungRow.StructureBudget, StringComparer.Ordinal);
        foreach (var axis in SpentAxes(row, costs, scopes))
        {
            if (!budget.Contains(axis))
                return Fail(row.ActionId, ActionRejectionReason.StructureExceedsBudget,
                    $"rung {row.Rung} does not budget for axis '{axis}'");
        }

        return ActionRejection.Ok;
    }

    /// <summary>Exposed for direct testing of the detection logic in isolation from the rung lookup.</summary>
    public static IReadOnlyList<string> SpentAxes(
        ActionRow row, IReadOnlyList<ActionCostRow> costs, IReadOnlyList<ActionScopeRow> scopes)
    {
        var spent = new List<string>();

        if (!string.IsNullOrWhiteSpace(row.ConditionsJson))
            spent.Add(StructureAxes.Condition);

        if (row.Envelope.ResolveOffsets.Count > 1)
            spent.Add(StructureAxes.Sequence);

        if (costs.Any(c => c.When == ActionCostTiming.PerTick))
            spent.Add(StructureAxes.Consumption);

        if (scopes.Select(s => s.Scope).Distinct().Count() > 1)
            spent.Add(StructureAxes.ScopeSplit);

        if (scopes.GroupBy(s => s.Scope).Any(g => g.Count() > 1))
            spent.Add(StructureAxes.RiderStatus);

        return spent;
    }

    /// <summary>
    /// A-G1 (spec-tier-access-gate.md §3.3, §5 test 5): axes this guard can never detect, distinct
    /// from an axis <see cref="SpentAxes"/> checked and found not spent. Reporting <c>0</c>/absent for
    /// one would be indistinguishable from the other — <c>undetectable</c> is its own state.
    ///
    /// <para>Today that is <see cref="StructureAxes.Restriction"/> alone. Its own docstring above
    /// says why: it needs the effect-atom program's own per-atom payload/target data, which is
    /// OUTSIDE the three tables this module reads (`rpg_action` + `rpg_action_cost` +
    /// `rpg_action_effect_scope`) — a real cross-program dependency, not a gap this method can close
    /// by itself.</para>
    ///
    /// <para><b><see cref="StructureAxes.Reaction"/> is deliberately NOT here.</b> It is
    /// UNSPENDABLE, not undetectable: <see cref="ActionKind"/> has exactly three
    /// members, none reaction-shaped, so nothing authored today can spend it and <see cref="Check"/>
    /// is already correct to never flag it. Folding it into this list would claim a detection gap
    /// that does not exist — a brief naming <c>reaction</c> is refused at authoring instead (A-S1's
    /// distribution planner), never something this guard needs to detect.</para>
    ///
    /// <para>This method takes no arguments and never will for the same reason
    /// <see cref="SpentAxes"/> takes a row: which axes CAN be checked is a fixed property of what
    /// this guard reads, not of any one action.</para>
    /// </summary>
    public static IReadOnlyList<string> UndetectableAxes() => new[] { StructureAxes.Restriction };

    static ActionRejection Fail(string actionId, ActionRejectionReason reason, string detail) =>
        ActionRejection.Fail(reason, $"{actionId}: {detail}");
}
