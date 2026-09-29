namespace FusionRpg.Core.Items.Uniques;

/// <summary>One unique's authored content, priced in AE × 100 at its own rung.</summary>
public readonly record struct UniqueBudgetReading(
    string SeedId,
    string ContainerId,
    string RarityId,
    long IdentityAeHundredths,
    long VarianceAeHundredths,
    long RawStatAeHundredths,
    long BaselineAeHundredths,
    long AllowanceAeHundredths,
    bool NarrowSatisfied)
{
    public long TotalAeHundredths => IdentityAeHundredths + VarianceAeHundredths;
    public bool OverAllowance => TotalAeHundredths > AllowanceAeHundredths;
}

public sealed record UniqueCorpusReport(IReadOnlyList<UniqueBudgetReading> Readings, string Basis)
{
    public int OverAllowance => Readings.Count(r => r.OverAllowance);
    public int NarrowDeclaredAndUnsatisfied => Readings.Count(r => !r.NarrowSatisfied);
}

/// <summary>
/// The budget device (§3.7 device 2) measured over the <b>seed</b> corpus, and deliberately reported
/// rather than refused.
///
/// <para>⛔ <b>Why this is a report and <see cref="UniqueValidator"/>'s budget check is a refusal.</b>
/// A seed authors bands, never numbers — seed-contract.md §3 forbids a magnitude in a seed, and
/// `item_unique.budget_ae` is not a seed field at all. So there is nothing here for a declared-versus-
/// summed check to compare, and the summed side is priced by <i>this module's own</i> band → tier → AE
/// reckoning rather than by anything an author wrote. Refusing 144 authored rows against a price they
/// were never given a way to see is not a validator working; it is a validator invented after the fact.
/// The hard check runs where a declared <c>budget_ae</c> exists, which is the concrete container.</para>
///
/// <para>⚠ <b>The baseline is the count-band FLOOR</b> (<see cref="UniqueBudget"/>'s own remark), which
/// makes the allowance smaller than the published half-range implies, so an "over allowance" count from
/// this report is an <b>upper bound</b> on the real one.</para>
/// </summary>
public static class UniqueCorpusReporter
{
    /// <summary>
    /// <paramref name="familyKind"/> resolves an affix family id to its <c>kindId</c>
    /// (`gk-data/packs/fusion/data/seed/items/affix-families/*.json`), which is how a raw stat is told from a capability
    /// rider. A family that does not resolve contributes to the total and <b>not</b> to the raw-stat
    /// subtotal, because guessing it into `narrow`'s ceiling would make an unresolved reference look
    /// like a balance failure.
    /// </summary>
    public static UniqueCorpusReport Measure(
        IReadOnlyList<UniqueSeed> corpus,
        Func<string, RarityRungWindow?> rungWindow,
        Func<string, string?> familyKind,
        UniqueTuning tuning)
    {
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (rungWindow is null) throw new ArgumentNullException(nameof(rungWindow));
        if (familyKind is null) throw new ArgumentNullException(nameof(familyKind));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var readings = new List<UniqueBudgetReading>();
        foreach (var s in corpus)
        {
            if (rungWindow(s.RarityId) is not { } rung) continue;

            long identity = 0, raw = 0;
            foreach (var a in s.FixedAtoms)
            {
                var ae = UniqueBudget.AeHundredthsOfBand(a.PowerBand, rung);
                identity += ae;
                var kind = familyKind(a.Family);
                if (kind is not null && UniqueValidator.RawStatKinds.Contains(kind, StringComparer.Ordinal))
                    raw += ae;
            }

            var variance = (long)s.TotalRolls * UniqueBudget.AeScale;
            var baseline = UniqueBudget.RungBaselineAeHundredths(rung);
            var allowance = UniqueBudget.AllowanceAeHundredths(rung, tuning);

            // Widen before multiplying, compare as products so nothing divides.
            var narrowOk = s.CounterPressure.Kind != UniqueCounterPressure.Narrow ||
                           raw * 1000L <= baseline * (long)tuning.NarrowCeilingPerMille;

            readings.Add(new UniqueBudgetReading(
                s.SeedId, s.ContainerId, s.RarityId, identity, variance, raw, baseline, allowance, narrowOk));
        }

        return new UniqueCorpusReport(readings,
            "band → tier → RarityOverlapSimulator.TierMidpoint, priced as AE × 100 against the rung's " +
            "reference tier. Baseline is the seeded count-band FLOOR (prefixRolls + suffixRolls), so the " +
            "allowance understates and the over-allowance count is an upper bound.");
    }
}
