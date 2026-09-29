using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Items.Power;

public readonly record struct AptitudeAffixPriceResult(bool Available, string Reason, PowerVector? Marginal)
{
    public static AptitudeAffixPriceResult Refused(string reason) => new(false, reason, null);
}

/// <summary>
/// R4 — aptitude-affix pricing (D8, amended 2026-09-03: a **share delta**, not points). Specified now,
/// inert until its vocabulary lands elsewhere (item-ideal.md §2g row 2 — a 13th atom kind or an
/// `aptitude.*` channel family, and a fifth <see cref="AllocationScope"/>): neither is this module's
/// to add. When they land, this reads the MARGINAL price, never the stored context-free one — the
/// exact reason <see cref="MarginalRead"/>'s own doc gives: a stored price cannot see what an
/// aptitude affix multiplies against, and D8's named failure mode IS that multiplicative dominance.
/// </summary>
public static class AptitudeAffixPrice
{
    /// <summary>
    /// Flip to true the day an item `AllocationScope` and an `aptitude.*` channel family both ship
    /// (item-ideal.md §2g row 2) — matching the same pattern <c>RungMonotonicity.PredicatePricingLanded</c>
    /// already uses for a different landed-elsewhere vocabulary. <see cref="VocabularyReady"/> also
    /// checks <see cref="AllocationScope"/>'s own member count as a redundant, self-updating guard: a
    /// fifth scope value landing without this flag being flipped is caught by a test rather than
    /// silently believed.
    /// </summary>
    const bool AptitudeVocabularyLanded = false;

    public static bool VocabularyReady => AptitudeVocabularyLanded && Enum.GetValues<AllocationScope>().Length > 4;

    /// <summary>
    /// <paramref name="shareDeltaMilli"/> is carried for the caller's own reporting; the actual price
    /// is always the marginal read of the candidate atom against the actor's current atoms, per D8's
    /// amendment — a share delta is not itself a price.
    /// </summary>
    public static AptitudeAffixPriceResult Read(
        IReadOnlyList<AtomRow> actorAtoms, AtomRow aptitudeAtom, long shareDeltaMilli, PowerTables? tables = null)
    {
        if (!VocabularyReady)
            return AptitudeAffixPriceResult.Refused(
                "no item AllocationScope and no aptitude.* channel family exist yet (item-ideal.md §2g row 2) "
                + "— effect-atom and class-system own both, not this module");

        var marginal = MarginalRead.Of(actorAtoms, aptitudeAtom, tables);
        return new AptitudeAffixPriceResult(Available: true, Reason: "", marginal);
    }
}
