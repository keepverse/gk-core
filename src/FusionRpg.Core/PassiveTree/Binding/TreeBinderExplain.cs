using System.Text;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.PassiveTree.Binding;

/// <summary>
/// `gk-forge/tools/TreeBinder --explain &lt;nodeId&gt;` (task D2, spec-tree-binder.md §Commands): "prints the
/// whole chain for one node — plan input, anchor pin, formula, rounding, stored `kMicro`, the
/// `UnitClass` check and its verdict." Mirrors <see cref="TreeBinderRun.BindNode"/> step for step so
/// the explanation can never drift from what actually binds — it calls the SAME
/// <see cref="AffixComposer"/>/<see cref="ChannelUnits"/>/<see cref="ChannelLegality"/>/
/// <see cref="ChannelAnchor"/>/<see cref="CoefficientBinder"/> primitives rather than a second,
/// parallel implementation of the formula.
/// </summary>
public static class TreeBinderExplain
{
    public static string Explain(
        BindInputNode input,
        IReadOnlyDictionary<string, AffixRow> affixesById,
        IReadOnlyDictionary<string, AtomRow> atomsById,
        PowerTuning powerTuning)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"tree-binder: EXPLAIN {input.NodeId}");
        sb.AppendLine("  plan input");
        sb.AppendLine($"    treeShareMilli      {input.TreeShareMilli}");
        sb.AppendLine($"    treeBudgetMilli     {input.TreeBudgetMilli}");
        sb.AppendLine($"    budgetShareMilli    {input.BudgetShareMilli}   (tree-plan's own number, read as given -- R4)");
        sb.AppendLine($"    branches            {input.Branches}");
        sb.AppendLine($"  affixIds              {string.Join(", ", input.AffixIds)}");

        IReadOnlyList<ResolvedAtom> resolved;
        try
        {
            resolved = AffixComposer.Resolve(input.AffixIds, affixesById, atomsById);
        }
        catch (BindRefusal ex)
        {
            sb.AppendLine($"  REFUSED               {ex.Message}");
            return sb.ToString();
        }

        foreach (var r in resolved)
        {
            sb.AppendLine($"  atom  kind={r.KindId} channel='{r.ChannelId}' op='{r.Op}' trigger={r.Trigger ?? "(none)"}");

            if (string.IsNullOrEmpty(r.ChannelId) || string.IsNullOrEmpty(r.Op))
            {
                sb.AppendLine("    -> mechanism-class atom (no channel/op): resolved, not priced by this formula (B4)");
                continue;
            }

            var unitClass = ChannelUnits.For(r.ChannelId);
            if (unitClass is null)
            {
                sb.AppendLine("    -> composed, not priced: no registered UnitClass for this channel");
                continue;
            }

            var verdict = ChannelLegality.VerdictFor(unitClass.Value);
            sb.AppendLine($"    unitClass check       '{unitClass}' -> verdict {verdict}");

            if (verdict == ChannelLegality.Verdict.Refused)
            {
                sb.AppendLine("    -> REFUSED: outright-refuse UnitClass (§4.1's six-class refuse set)");
                continue;
            }
            if (verdict != ChannelLegality.Verdict.LadderScaled)
            {
                sb.AppendLine("    -> composed, not priced: no bake-time coefficient formula for this verdict yet (§3.6)");
                continue;
            }

            long anchor;
            try
            {
                anchor = ChannelAnchor.ForChannel(r.ChannelId, powerTuning);
            }
            catch (ChannelAnchor.UnknownChannelPin ex)
            {
                sb.AppendLine($"    -> composed, not priced: {ex.Message}");
                continue;
            }

            sb.AppendLine($"    channelAnchorMilli    {anchor}   (channel's own pin at Theta=20 over hp's pin, derived at bake time -- §3.3)");

            var num = input.TreeShareMilli * input.TreeBudgetMilli * input.BudgetShareMilli * anchor;
            var denom = input.Branches * 1_000_000L;
            sb.AppendLine($"    formula               num = treeShareMilli*treeBudgetMilli*budgetShareMilli*channelAnchorMilli");
            sb.AppendLine($"                          num = {input.TreeShareMilli} * {input.TreeBudgetMilli} * {input.BudgetShareMilli} * {anchor} = {num}");
            sb.AppendLine($"                          denom = branches * 1,000,000 = {input.Branches} * 1,000,000 = {denom}");

            var kMicro = CoefficientBinder.Bind(input.TreeShareMilli, input.TreeBudgetMilli,
                input.BudgetShareMilli, anchor, input.Branches);
            sb.AppendLine($"    rounding              round_half_away_from_zero({num} / {denom})");
            sb.AppendLine($"    stored kMicro         {kMicro}");

            var op = Enum.Parse<NodeAtomOp>(r.Op, ignoreCase: true);
            var atom = new NodeAtom(r.KindId, AttachPoint.Stat, r.ChannelId, op, r.Trigger, r.WhenJson,
                kMicro, ScaleAxis.PTheta, unitClass.Value);
            try
            {
                ChannelLegality.CheckBind(atom);
                sb.AppendLine("    UnitClass check       PASS");
            }
            catch (BindRefusal ex)
            {
                sb.AppendLine($"    UnitClass check       REFUSED: {ex.Message}");
            }
        }

        return sb.ToString();
    }
}
