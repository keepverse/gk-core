using FusionRpg.Core.PassiveTree.Catalog;

namespace FusionRpg.Core.PassiveTree.Binding;

/// <summary>
/// One node's binder input (task D2) — the bridge between `tree-plan`'s emitted per-node fields
/// (§3.1's IN list: `treeShareMilli`, `treeBudgetMilli`, `budgetShareMilli`, all read as given, R4)
/// and `tree-language`'s chosen `affixIds[]` (1..3, R6), neither of which this module invents.
///
/// <para><b><see cref="ExclusionForm"/> is carried through UNUSED by the pricing path.</b> §7.3's
/// rule is that an excluded node — nullification included — binds exactly like an unexcluded one:
/// same affixes, same `kMicro`, same budget. The field exists only so one caller can carry a node's
/// full identity through the pipeline without a second, near-duplicate input shape; nothing in
/// <see cref="TreeBinderRun"/> reads it.</para>
///
/// <para><b><see cref="DeliberateHole"/> is `tree-plan`'s own suppression flag</b> (§7.2's
/// "consequence for tree-plan": "stage 1 owes a flag that suppresses conversion nodes at plan time,
/// so the refusal is a zero-count assertion in a healthy run rather than a per-run event"). Read
/// here, never invented by this module: when the plan already knows a slot cannot bind today (the
/// canonical case named when this was written was a conversion placeholder reserved ahead of the
/// then-unbuilt 18th atom kind, D16 — **`element.convert` shipped 2026-09-07** and the binder no
/// longer refuses it by kind, but `tree-plan`/`tree-language` still allocate it ZERO nodes upstream,
/// so this mechanism's illustrative case remains hypothetical: no real plan has ever set this flag
/// for a real reason yet), it flags that node. The binder still refuses it and still reports the
/// unspent budget
/// (§7.2 item 2 — nothing is ever silently absorbed), but a flagged refusal does not by itself fail
/// the run the way a surprise refusal does (§7.2 item 3's `FAIL, not NOT_MEASURED` is for the
/// unflagged case — see <see cref="BinderRunReport.From"/>).</para>
///
/// <para><b><see cref="Branch"/>, <see cref="Tier"/>, <see cref="NodeKey"/>, <see cref="NodeClass"/>
/// and <see cref="ExcludeProps"/> are carried through UNUSED by the pricing path too</b> (2026-09-07,
/// found the moment a real live-boot proof first tried to import a real committed
/// `gk-data/packs/fusion/data/generated/passive-tree/&lt;treeId&gt;.json` and discovered nothing had ever written the
/// `tree-catalog` `TreeRecord`/`NodeRecord` shape `spec-tree-binder.md`'s own Project structure table
/// names ("THIS is what ships") and "`tree-catalog` owns the on-disk record shape; this module writes
/// it and never redefines it" both state unambiguously). Same reasoning as
/// <see cref="ExclusionForm"/> above: one caller carries a node's full identity through the pipeline
/// rather than a second, near-duplicate input shape; <see cref="TreeBinderRun"/> reads none of these
/// five.</para>
/// </summary>
public sealed record BindInputNode(
    string NodeId,
    long TreeShareMilli,
    long TreeBudgetMilli,
    long BudgetShareMilli,
    long Branches,
    IReadOnlyList<string> AffixIds,
    ExclusionForm ExclusionForm,
    bool DeliberateHole,
    TreeBranch Branch = TreeBranch.Off,
    int Tier = 0,
    string NodeKey = "",
    NodeClass NodeClass = NodeClass.Magnitude,
    IReadOnlyList<string>? ExcludeProps = null,
    // seedsmith-content-standard, content-completeness-passive-tree (2026-09-08): carried through
    // UNUSED by the pricing path, same reasoning as Branch/Tier/NodeKey/NodeClass/ExcludeProps
    // above — one caller carries a node's full identity rather than a second, near-duplicate shape.
    string? Name = null,
    string? Flavor = null)
{
    public IReadOnlyList<string> ExcludeProps { get; init; } = ExcludeProps ?? Array.Empty<string>();
}
