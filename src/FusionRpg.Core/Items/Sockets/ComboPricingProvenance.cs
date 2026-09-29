namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// What a combination-pricing measurement was taken AGAINST (spec-combo-budget §4), as published in the
/// sockets tuning's optional <c>comboPricing.measuredAgainst</c>.
///
/// <para>⛔ <b>Every version here is the FILENAME revision <c>n</c> of <c>&lt;domain&gt;.v{n}.json</c> —
/// what <c>gk-core/tools/tuning/publish.py</c>'s own <c>latest_version</c> reads — and never the file's internal
/// <c>"version"</c> field.</b> The two disagree for `sockets` today (<c>sockets.v1.json</c> carries 3,
/// bumped in place by a later publish, while <c>v2</c> carries 2), so only the filename is monotonic;
/// a measurement recorded against the internal field would refuse every future revision that is in fact
/// newer.</para>
/// </summary>
/// <param name="CircuitSize"><see cref="SocketLimits.SocketCircuitSize"/> at the measurement, recorded so
/// a future change to the structural constant cannot reuse the number.</param>
/// <param name="CombinationCorpusDigest">SHA-256 over the accepted Strain/Splice set (ids, ingredient
/// families, grants, host pins, sorted) — the corpus moves without any tuning file moving (an R11 re-run,
/// an R13 per-id ruling), and a new word is new power nobody priced.</param>
public sealed record ComboPricingMeasuredAgainst(
    int SocketsVersion,
    int StrainSpliceVersion,
    int MaterialsVersion,
    int CircuitSize,
    string CombinationCorpusDigest);

/// <summary>What the host actually LOADED, in the same shape as
/// <see cref="ComboPricingMeasuredAgainst"/> — the filename revisions it read, its
/// <see cref="SocketLimits.SocketCircuitSize"/>, and the digest it computed over the corpus it accepted.
/// A host builds this once, after every input is loaded, and hands it to
/// <see cref="ComboPricingProvenance.Check"/>.</summary>
public sealed record ComboPricingLoadedRevisions(
    int SocketsVersion,
    int StrainSpliceVersion,
    int MaterialsVersion,
    int CircuitSize,
    string CombinationCorpusDigest);

/// <summary>A named refusal from the provenance check, in the repo's rule-id shape (never a bare bool:
/// the boot prints WHICH field moved and in which direction).</summary>
public sealed record ComboPricingProvenanceRefusal(string Rule, string Detail)
{
    /// <summary>The ladder binds at tiers the published measurement never priced.</summary>
    public const string UnmeasuredRule = "socket.combo-pricing-unmeasured";

    /// <summary>A revision or the corpus moved after the measurement was taken.</summary>
    public const string StaleRule = "socket.combo-pricing-stale";
}

/// <summary>
/// strain-splice-host SSH6.6 (`combo-budget` §5): the ONE pure check that stops a combination ladder
/// binding against prices nobody measured.
///
/// <para><b>Why it is pure and lives here.</b> The parsers cannot enforce this alone —
/// <c>StrainSpliceTuning.Parse</c> is handed file TEXT and never learns which revision it read — so the
/// host calls this once every input is loaded (the server at boot, the Python report at start), passing
/// the filename revisions it read and the digest it computed. Nothing here reads a file, a clock or a
/// random source: same inputs, same verdict.</para>
///
/// <para><b>Today's single flat floor with no <c>comboPricing</c> at all is unaffected</b> — a one-rung
/// ladder has nothing to bind against, which is exactly the state the shipped revision is in until
/// `tier-ladder` publishes rungs 2..n.</para>
/// </summary>
public static class ComboPricingProvenance
{
    /// <summary>
    /// `null` when the loaded revisions may bind, else the refusal that names the field and the two
    /// values. Refuses, never clamps and never defaults: an absent measurement on a multi-rung ladder is
    /// a ladder that prices nothing, and a mismatched measurement is a ladder priced against content that
    /// no longer exists.
    /// </summary>
    public static ComboPricingProvenanceRefusal? Check(
        ComboPricingMeasuredAgainst? measuredAgainst,
        ComboPricingLoadedRevisions loaded,
        int ladderRungCount)
    {
        if (loaded is null) throw new ArgumentNullException(nameof(loaded));
        if (ladderRungCount < 1)
            throw new SocketTuningRejection(
                $"a combination ladder with {ladderRungCount} rungs has no rung to bind at");

        if (measuredAgainst is null)
        {
            return ladderRungCount > 1
                ? new ComboPricingProvenanceRefusal(
                    ComboPricingProvenanceRefusal.UnmeasuredRule,
                    $"the strain-splice ladder carries {ladderRungCount} rungs, so combinations bind at " +
                    "tiers nobody measured: run `python -m seedsmith items combo-budget --report`, then " +
                    "publish its derived coefficients and its `measuredAgainst` with the tuning tool")
                : null;
        }

        var moved = new List<string>();
        if (measuredAgainst.SocketsVersion != loaded.SocketsVersion)
            moved.Add($"socketsVersion measured {measuredAgainst.SocketsVersion}, loaded {loaded.SocketsVersion}");
        if (measuredAgainst.StrainSpliceVersion != loaded.StrainSpliceVersion)
            moved.Add($"strainSpliceVersion measured {measuredAgainst.StrainSpliceVersion}, " +
                      $"loaded {loaded.StrainSpliceVersion}");
        if (measuredAgainst.MaterialsVersion != loaded.MaterialsVersion)
            moved.Add($"materialsVersion measured {measuredAgainst.MaterialsVersion}, " +
                      $"loaded {loaded.MaterialsVersion}");
        if (measuredAgainst.CircuitSize != loaded.CircuitSize)
            moved.Add($"circuitSize measured {measuredAgainst.CircuitSize}, loaded {loaded.CircuitSize}");
        if (!string.Equals(measuredAgainst.CombinationCorpusDigest, loaded.CombinationCorpusDigest,
                           StringComparison.Ordinal))
            moved.Add($"combinationCorpusDigest measured '{measuredAgainst.CombinationCorpusDigest}', " +
                      $"loaded '{loaded.CombinationCorpusDigest}'");

        return moved.Count == 0
            ? null
            : new ComboPricingProvenanceRefusal(
                ComboPricingProvenanceRefusal.StaleRule,
                "the combination pricing was measured against different content (" +
                string.Join("; ", moved) +
                "): re-run the report and republish `comboPricing.measuredAgainst` in the same commit " +
                "as the change that moved it");
    }
}
