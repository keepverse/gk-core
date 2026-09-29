using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;

namespace FusionRpg.Core.Items.Power;

/// <summary>
/// R1/R2's shared shape: a per-mille share of the item's rarity ceiling, or unpriced with a reason.
/// Unpriced is a third outcome, never a zero (spec-item-power-reads.md's rule 1 — a read that coerces
/// it to 0 is a bug, and a whole family becomes free the same way <c>CoefficientTable.cs:71-74</c>
/// warns about at the table-lookup layer).
/// </summary>
public readonly record struct PowerShareRead(long? ShareMilli, bool Over, string? UnpricedReason, bool CoefficientSensitive)
{
    public bool Unpriced => ShareMilli is null;

    public static PowerShareRead AsUnpriced(string reason, bool coefficientSensitive) =>
        new(null, Over: false, reason, coefficientSensitive);
}

/// <summary>
/// The four item-power reads (spec-item-power-reads.md, item module 9) — **D13 is VOID**: this class
/// builds no vector, coefficient or cost function. It calls E9/E10 (`CostFunction`, `PowerVector`,
/// `PowerScalar`, `MarginalRead` — all shipped 2026-08-22) and states the honesty rules that keep an
/// approximate number from reading as an exact one.
/// </summary>
public static class ItemPowerReads
{
    /// <summary>The same reference vector `RungMonotonicity` prices a rung's `qPowerMilli` against —
    /// not a new one. R2 must equal that path's own number.</summary>
    static readonly PowerVector GrantedActionReference = PowerVector.FromCategory(PowerCategory.Offense, 1000);

    /// <summary>
    /// R1 — implicit budget share. Coefficient-INSENSITIVE: it is a ratio of two prices computed by
    /// the same function (I3's shipped tier-equality guard is the real check; this cap is a second
    /// check on the ratio), so a uniform coefficient rescale cancels out.
    /// </summary>
    public static PowerShareRead ImplicitShare(AtomRow implicitAtom, int? rarityCeiling, ItemPowerTuning tuning, PowerTables? tables = null)
    {
        var priced = CostFunction.Price(implicitAtom, tables);
        if (!priced.Ok) return PowerShareRead.AsUnpriced(priced.Verdict.Reason, coefficientSensitive: false);
        if (rarityCeiling is not { } ceiling || ceiling <= 0)
            return PowerShareRead.AsUnpriced("rarity has no seeded budget ceiling", coefficientSensitive: false);

        // Widen before multiplying, divide by 1000 last, exactly once (AGENTS.md numeric rules).
        var shareMilli = checked((long)priced.Power.Total * 1000L) / ceiling;
        return new PowerShareRead(shareMilli, Over: shareMilli > tuning.ImplicitShareCapMilli, null, CoefficientSensitive: false);
    }

    /// <summary>
    /// R2 — granted-action price, via the SAME path `RungMonotonicity` already uses to verify a
    /// rung's power climbs. Coefficient-sensitive (cross-shape: an action's price and an affix
    /// bundle's price come from different pricing shapes, so a uniform error does not cancel) —
    /// report as a share with a band, never as a threshold. `qPowerMilli: null` (no resolvable rung)
    /// is refused, never priced at zero — G4's own dominance fear.
    ///
    /// <para>⭐ <paramref name="tuning"/> is what turns this read from REPORTABLE into GATING, which is
    /// this method's own documented lifecycle ("reportable today and gating only when module 19
    /// `granted-actions` lands"). With no tuning the <c>Over</c> flag stays <c>false</c> and the caller
    /// gets a bare share — the behaviour every pre-module-19 caller already has. With one, the share is
    /// measured against <c>grantedActionShareCapMilli</c> when a balance pass has set one, else against
    /// the whole ceiling (1000‰ — the per-mille identity, a bounded ratio, not an invented number).
    /// Module 19's <c>ItemGrantValidator</c> is the first caller to pass it.</para>
    /// </summary>
    public static PowerShareRead GrantedActionPrice(int? qPowerMilli, int? rarityCeiling, ItemPowerTuning? tuning = null)
    {
        if (qPowerMilli is not { } q)
            return PowerShareRead.AsUnpriced("action has no resolvable rung", coefficientSensitive: true);
        if (rarityCeiling is not { } ceiling || ceiling <= 0)
            return PowerShareRead.AsUnpriced("rarity has no seeded budget ceiling", coefficientSensitive: true);

        var priced = GrantedActionReference.ScaleMilli(q).Total;
        // Widen before multiplying, divide by 1000 last, exactly once (AGENTS.md numeric rules).
        var shareMilli = checked((long)priced * 1000L) / ceiling;

        var over = tuning is { } t
            && shareMilli > (t.GrantedActionShareCapMilli ?? Grants.ItemGrantLimits.WholeCeilingShareMilli);
        return new PowerShareRead(shareMilli, over, null, CoefficientSensitive: true);
    }

    /// <summary>R3 — the card's power number, Rule P: two significant figures with its band, never
    /// four digits of confidence. <paramref name="tuning"/>'s `ShowPowerOnCard` is the reversible
    /// suppression alternative (G3 §10 Q7) — a file save, not a code change.</summary>
    public static CardPowerDisplay CardPower(PowerVector v, ItemPowerTuning tuning)
    {
        if (!tuning.ShowPowerOnCard) return CardPowerDisplay.Suppressed;

        var scalar = PowerScalar.Of(v);
        var rounded = RoundToSigFigs(scalar, tuning.PowerDisplaySigFigs);
        return new CardPowerDisplay(Shown: true, rounded, tuning.PowerDisplayBandPercent);
    }

    internal static int RoundToSigFigs(int value, int sigFigs)
    {
        if (value == 0) return 0;
        var magnitude = (int)Math.Floor(Math.Log10(Math.Abs((double)value))) + 1;
        var dropDigits = magnitude - sigFigs;
        if (dropDigits <= 0) return value;
        var factor = (long)Math.Pow(10, dropDigits);
        return (int)(((value + factor / 2) / factor) * factor);
    }
}

/// <summary>R3's rendered outcome — `Shown: false` when the card power row is suppressed
/// (`showPowerOnCard: false`), nothing else about the card changes.</summary>
public readonly record struct CardPowerDisplay(bool Shown, int RoundedValue, int BandPercent)
{
    public static readonly CardPowerDisplay Suppressed = new(false, 0, 0);

    public string Render() => Shown ? $"≈ {RoundedValue:N0} (±{BandPercent}%)" : "";
}
