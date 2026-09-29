namespace FusionRpg.Core.Match;

/// <summary>
/// The one admission rule for the concurrency axis of a unique lawn deploy — owner ruling D6:
/// *"Lawn deploy cap: at most 5 unique creatures per side, 10 on the board."*
/// (`docs/architecture/creature-lawn-deploy/spec-unique-deploy-cap.md`.)
///
/// <para><b>Pure, and it owns no count.</b> Two numbers come in (this empire's live uniques, the
/// board's live uniques) and a <see cref="GateResult"/> goes out, using <c>CapPolicy</c>'s own result
/// type and reason-code convention rather than re-declaring either. The count query and the call site
/// live inside the store's existing deploy transaction, which is a different file and a different
/// concern.</para>
///
/// <para><b>Structural, not a progression ceiling.</b> This bounds how many smart-tier actors run at
/// once; it caps no magnitude and no roster size. The two limits and their reason live in
/// <c>gk-core/data/tuning/lawn-deploy.v1.json</c> with the class stated in the file's own <c>_meta</c>, and
/// their register row is <c>ssot-power-scale.md</c> §11.3.</para>
///
/// <para><b>Precedence is stated, not incidental:</b> the per-empire reason wins when both limits are
/// reached, because "this empire already holds its five" is the actionable statement for the caller and
/// the board number is the frame-budget backstop behind it.</para>
/// </summary>
public static class LawnUniqueDeployCap
{
    /// <summary>
    /// Admit or refuse one unique deploy. <paramref name="limits"/> comes from
    /// <c>LawnDeployLimitsTuningHub.Tuning</c> in production; a caller may pass any instance, which is
    /// what makes every branch testable without a file.
    ///
    /// <para>A non-positive limit reads as "no limit" on the same convention <c>CapPolicy.Check</c>
    /// already uses (<c>max &lt; 0</c> admits) — the loader refuses such a file at load, so this is the
    /// belt-and-braces branch, never the normal path.</para>
    /// </summary>
    public static GateResult TryAdmit(int liveUniquesForEmpire, int liveUniquesOnBoard, LawnDeployLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (limits.MaxConcurrentUniquesPerEmpire > 0 && liveUniquesForEmpire >= limits.MaxConcurrentUniquesPerEmpire)
            return GateResult.Reject(GateReasons.CapUniquePerEmpire);

        if (limits.MaxConcurrentUniquesOnBoard > 0 && liveUniquesOnBoard >= limits.MaxConcurrentUniquesOnBoard)
            return GateResult.Reject(GateReasons.CapUniqueBoard);

        return GateResult.Allowed();
    }
}
