using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items.Power;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>The before/after figure a workbench shows. Both halves are module 9's R3 render.</summary>
public readonly record struct MutationPreviewDisplay(CardPowerDisplay Before, CardPowerDisplay After)
{
    /// <summary>Suppressed on both halves when <c>showPowerOnCard</c> is off (G3 §10 Q7).</summary>
    public bool Shown => Before.Shown || After.Shown;

    public string Render() => Shown ? $"{Before.Render()} → {After.Render()}" : "";
}

/// <summary>
/// spec-enhance-reroll.md §10 — <b>the one read this module takes from module 9, and the only one.</b>
/// The dependency on <c>item-power-reads</c> was declared in the spec header and never used in the
/// body; §4b needs exactly one thing from it, and inventing a second pricer here is the failure
/// <c>spec-item-power-reads.md</c> was written to prevent.
///
/// <para><b>R3 — <c>PowerScalar</c> with its ±25% band</b> is used for the before/after figure on a
/// mutation preview and for the item-versus-item half of §4b (a perfected item of one rung against a
/// fresh drop of another is a cross-family comparison, and E9's vector is the only unit that can
/// express it). R1, R2 and R4 are <b>not read here</b>: R1 and R4 are content lints and R2 is module
/// 19's.</para>
///
/// <para>Three rules ride along, all module 9's:</para>
/// <list type="bullet">
/// <item><b><c>unpriced</c> is never <c>0</c></b> — a preview that cannot price the result says so.</item>
/// <item><b>Two significant figures with the band</b> — <c>≈ 1,300 (±25%)</c>, never <c>1,284</c>.</item>
/// <item>⛔ <b>Never a gate.</b> A power read may not refuse a mutation, price one, or decide an
/// outcome. It is display and reporting only.</item>
/// </list>
///
/// <para>⚠ <c>showPowerOnCard = false</c> suppresses the preview figure too, or G3 §10 Q7's reversal
/// is only half a reversal. One tunable, two surfaces — which is why this calls
/// <see cref="ItemPowerReads.CardPower"/> rather than reading the scalar itself.</para>
/// </summary>
public static class MutationPreview
{
    public static MutationPreviewDisplay Preview(PowerVector before, PowerVector after, ItemPowerTuning tuning) =>
        new(ItemPowerReads.CardPower(before, tuning), ItemPowerReads.CardPower(after, tuning));
}
