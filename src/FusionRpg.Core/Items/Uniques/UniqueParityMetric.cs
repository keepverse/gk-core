namespace FusionRpg.Core.Items.Uniques;

/// <summary>Where one reading sits against ssot-uniques.md §3.7 device 3's band.</summary>
public enum UniqueParityVerdict
{
    /// <summary><c>W ∈ [lower, upper]</c> — the rare wins the stat sheet often enough, and not too often.</summary>
    InBand = 0,

    /// <summary><c>W &lt; lower</c> — the unique is strictly better on the stat sheet, so rolled loot in
    /// that role is filler.</summary>
    StrictlyBetter,

    /// <summary><c>W &gt; upper</c> — the unique loses to most rares before its capability is counted.</summary>
    Trophy,
}

/// <summary>
/// One measurement: one unique's magnitude in one channel family, against the rolled distribution at
/// its own rung.
/// </summary>
public readonly record struct UniqueParityReading(
    string SeedId, string ContainerId, string RarityId, string Family, string PowerBand,
    long Magnitude, int WPerMille, UniqueParityVerdict Verdict);

/// <summary>
/// The corpus-wide parity report. <see cref="HasThreshold"/> is the honesty flag spec-uniques.md asked
/// for: it was <c>false</c> while no harness existed, and it is <c>true</c> now that one does.
/// </summary>
public sealed record UniqueParityReport(
    IReadOnlyList<UniqueParityReading> Readings,
    int LowerBoundPerMille,
    int UpperBoundPerMille,
    bool HasThreshold,
    string Basis)
{
    public int InBand => Readings.Count(r => r.Verdict == UniqueParityVerdict.InBand);
    public int StrictlyBetter => Readings.Count(r => r.Verdict == UniqueParityVerdict.StrictlyBetter);
    public int Trophy => Readings.Count(r => r.Verdict == UniqueParityVerdict.Trophy);
}

/// <summary>
/// ssot-uniques.md §3.7 <b>device 3</b>, measured — <i>"for any unique U at rung n, let W be the
/// probability that a randomly rolled rare at rung n beats U on total magnitude within one channel
/// family."</i>
///
/// <para>⛔ <b>There is no second simulator here, and that is the point.</b> §9.2 asked module 7 for the
/// harness rather than a re-implementation, spec-uniques.md forbids a second one by name, and module 7
/// built <see cref="RarityOverlapSimulator"/> saying explicitly that it claimed the invariant *"because
/// the only would-be consumer (spec-uniques.md) declined to build a second simulator."* This type calls
/// that harness: the same <see cref="RarityOverlapSimulator.Seed"/>, the same
/// <see cref="RarityOverlapSimulator.RollMagnitudes"/>, the same
/// <see cref="RarityOverlapSimulator.UpsetRate"/> paired comparison, and the same tier band table
/// exposed as <see cref="RarityOverlapSimulator.TierMidpoint"/>. §9.2's exact ask — <i>"the same
/// measurement with a fixed-value item on one side, run on the same code with the same seed"</i> —
/// is what the fixed side literally is: an array of the unique's own magnitude.</para>
///
/// <para><b>The rolled side draws ONE affix, not the rung's whole count band.</b> Parity is measured
/// <i>within one channel family</i> (SC4 forbids cross-family totals), and the one-atom-per-group rule
/// means a rolled rare's total inside a single family is exactly one affix however many it draws
/// overall. This is the one parameter that differs from §3.5's overlap measurement, and it differs
/// because the two invariants are about different things: overlap is about a rung beating the rung
/// below it, parity is about one line beating one line.</para>
///
/// <para><b>The threshold is live.</b> spec-uniques.md instructed: <i>"register parity as a reported
/// metric with no threshold until the harness exists, and say in the report that it is unbounded."</i>
/// The harness exists (module 7, 2026-09-04), so <see cref="UniqueParityReport.HasThreshold"/> is true
/// and the band comes from <c>uniques.v1.json</c>. It bounds a <b>report</b>, not an import refusal —
/// the three hard devices are counter-pressure, budget and anti-convergence; device 3 was never one of
/// them, and making it hard on the day it first became measurable would refuse authored content
/// against a number nobody has yet had a chance to author against.</para>
/// </summary>
public static class UniqueParityMetric
{
    /// <summary>
    /// The rolled side of the comparison: the rung's own tier window, drawing <b>one</b> affix. The
    /// rung's stream name is unchanged, so the same seed reproduces the same draws as every other
    /// consumer of the harness.
    /// </summary>
    public static RarityRungWindow SingleFamilyWindow(RarityRungWindow rung) =>
        rung with { AffixCount = 1 };

    /// <summary>
    /// <c>W</c> in per-mille: how often one rolled affix at this rung beats <paramref name="magnitude"/>.
    /// </summary>
    public static int MeasurePerMille(RarityRungWindow rung, long magnitude, int[]? rolledCache = null)
    {
        var rolled = rolledCache ?? RarityOverlapSimulator.RollMagnitudes(SingleFamilyWindow(rung));
        if (rolled.Length == 0)
            throw new InvalidOperationException($"rung '{rung.RarityId}' produced no rolls to measure against");

        // The fixed-value side, exactly as §9.2 asked for it -- and then the SHIPPED paired comparison,
        // not a second loop that could quietly disagree about strictness.
        var fixedSide = new int[rolled.Length];
        var m = magnitude > int.MaxValue ? int.MaxValue : magnitude < int.MinValue ? int.MinValue : (int)magnitude;
        Array.Fill(fixedSide, m);

        var w = RarityOverlapSimulator.UpsetRate(rolled, fixedSide);
        return (int)Math.Round(w * 1000.0, MidpointRounding.AwayFromZero);
    }

    public static UniqueParityVerdict VerdictOf(int wPerMille, UniqueTuning tuning) =>
        wPerMille < tuning.ParityLowerBoundPerMille ? UniqueParityVerdict.StrictlyBetter
        : wPerMille > tuning.ParityUpperBoundPerMille ? UniqueParityVerdict.Trophy
        : UniqueParityVerdict.InBand;

    /// <summary>
    /// Measure every identity line of every unique in the corpus. One reading per
    /// <c>(unique, channel family)</c> — never a summed cross-family number, which SC4 forbids and
    /// which is the reason the invariant is stated per family in the first place.
    /// </summary>
    public static UniqueParityReport Measure(
        IReadOnlyList<UniqueSeed> corpus, Func<string, RarityRungWindow?> rungWindow, UniqueTuning tuning)
    {
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (rungWindow is null) throw new ArgumentNullException(nameof(rungWindow));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        // Rolled once per rung and reused, matching the harness's own "2x10^5 rolls per rung, not per
        // pair" contract.
        var cache = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var readings = new List<UniqueParityReading>();

        foreach (var s in corpus)
        {
            if (rungWindow(s.RarityId) is not { } rung) continue;

            if (!cache.TryGetValue(rung.RarityId, out var rolled))
                cache[rung.RarityId] = rolled = RarityOverlapSimulator.RollMagnitudes(SingleFamilyWindow(rung));

            foreach (var a in s.FixedAtoms)
            {
                var magnitude = RarityOverlapSimulator.TierMidpoint(UniqueBudget.TierOfPowerBand(a.PowerBand));
                var w = MeasurePerMille(rung, magnitude, rolled);
                readings.Add(new UniqueParityReading(
                    s.SeedId, s.ContainerId, s.RarityId, a.Family, a.PowerBand, magnitude, w, VerdictOf(w, tuning)));
            }
        }

        return new UniqueParityReport(
            readings, tuning.ParityLowerBoundPerMille, tuning.ParityUpperBoundPerMille,
            HasThreshold: true,
            Basis:
            $"module 7's RarityOverlapSimulator, seed {RarityOverlapSimulator.Seed}, " +
            $"{RarityOverlapSimulator.RollsPerRung} rolls per rung, ONE affix per draw (parity is measured " +
            "within one channel family). No second simulator: the fixed side is an array of the unique's own " +
            "magnitude, compared with the harness's own UpsetRate.");
    }
}
