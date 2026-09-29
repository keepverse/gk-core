using FusionRpg.Core.Items.Sockets;

namespace FusionRpg.Server;

/// <summary>
/// strain-splice-host SSH6.7 (`combo-budget` §5): the boot's own half of the provenance check —
/// what it LOADED, and the one call that refuses when the published pricing was measured against
/// something else.
///
/// <para>⛔ <b>Why this is a class and not three lines in `Program.cs`.</b> The check is a pure Core
/// function, but the two things it needs are the boot's own facts: which FILENAME revision each tuning
/// reader actually read, and the digest of the combination set the store accepted. Those live here so
/// the boot passes them explicitly and a test can drive the same computation against the real shipped
/// files without starting a server.</para>
/// </summary>
public static class ComboPricingBoot
{
    /// <summary>The tuning filenames this boot read — the names, never their contents: the provenance
    /// compares FILENAME revisions, and passing the name it read is the only way the browser cannot
    /// silently substitute a different file's revision.</summary>
    public sealed record LoadedTuningFiles(string Sockets, string StrainSplice, string Materials);

    /// <summary>
    /// The loaded revisions, the structural circuit size and the corpus digest, in the shape
    /// <see cref="ComboPricingProvenance.Check"/> compares.
    /// </summary>
    public static ComboPricingLoadedRevisions LoadedRevisions(
        LoadedTuningFiles files,
        IReadOnlyList<ComboRecipe> acceptedRecipes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> grantsById)
    {
        if (files is null) throw new ArgumentNullException(nameof(files));
        return new ComboPricingLoadedRevisions(
            SocketsVersion: SocketTuningFiles.RevisionOf(files.Sockets),
            StrainSpliceVersion: SocketTuningFiles.RevisionOf(files.StrainSplice),
            MaterialsVersion: SocketTuningFiles.RevisionOf(files.Materials),
            CircuitSize: SocketLimits.SocketCircuitSize,
            CombinationCorpusDigest: CombinationCorpus.Digest(acceptedRecipes, grantsById));
    }

    /// <summary>
    /// Refuses the boot, BY NAME, when the published pricing was measured against different content.
    /// Called once, after sockets, strain-splice, materials and the accepted combination set are all
    /// loaded and BEFORE anything binds — a refusal is a start failure, never a warning.
    ///
    /// <para>The ladder's rung count is read from the LOADED tuning's own
    /// <see cref="StrainSpliceTuning.TierLadder"/>. It used to be a `const int TierLadderRungCount = 1`
    /// here, which made a code edit the way to publish a ladder — the opposite of a tuning-owned value, and
    /// exactly what the magic-number guard's M2 flags ("const with balance vocabulary - belongs in config",
    /// tunables-ssot.md T1). The tuning already derives that ladder from `minTierPlan`, so nothing is
    /// restated and a published ladder moves the count with it.</para>
    /// </summary>
    public static void RequireVerified(
        ComboPricingMeasuredAgainst? measuredAgainst, ComboPricingLoadedRevisions loaded,
        StrainSpliceTuning strainSpliceTuning)
    {
        ArgumentNullException.ThrowIfNull(strainSpliceTuning);
        var refusal = ComboPricingProvenance.Check(
            measuredAgainst, loaded, strainSpliceTuning.TierLadder.Count);
        if (refusal is not null) throw new ComboPricingProvenanceException(refusal);
    }
}

/// <summary>The boot refusal, carrying the rule id and the detail the boot prints. A type rather than a
/// bare <c>InvalidOperationException</c> so a caller (and its test) can read WHICH rule fired.</summary>
public sealed class ComboPricingProvenanceException : Exception
{
    public ComboPricingProvenanceException(ComboPricingProvenanceRefusal refusal)
        : base($"ContentRuleViolated{{{refusal.Rule}}}: {refusal.Detail}")
    {
        Refusal = refusal;
    }

    public ComboPricingProvenanceRefusal Refusal { get; }
}
