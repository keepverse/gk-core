using FusionRpg.Core.Combat;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.Balance.Analytic;

/// <summary>
/// class-system-todo.md P4.4 — status uptime under refresh-not-stack semantics, and the "rides the
/// action-multiplied hit" rule (spec-deterministic-core.md §2.1 correction 4, §2.2).
///
/// <para><b>Uptime, not an expected count.</b> <c>StatusRuntime.cs:248</c> (<c>UpsertInstance</c>,
/// <c>StatusStacking.Refresh</c>): a re-application of the same (StatusId, GrantId) REPLACES the
/// existing instance outright rather than adding a second, independently-ticking one. A naive
/// <c>p × magnitude × duration</c> per-swing sum over-counts whenever the status is likely to be
/// refreshed before it expires — that form has no ceiling and grows without bound as more apply
/// attempts pile up. §2.2 records the actual incident this fixes: a coefficient search ran against,
/// and converged beautifully on, exactly that wrong form (a DoT over-count sitting undetected in a doc
/// comment). <see cref="Uptime"/> — <c>1 − (1 − p)^duration</c> — is the probability that at least one
/// of <c>duration</c> independent per-round apply attempts (probability <paramref name="p"/> each)
/// lands inside a trailing <c>duration</c>-round window, which is exactly the steady-state condition
/// "the status is active this round" reduces to under refresh semantics. It saturates toward 1 as
/// <c>p</c> or <c>duration</c> grow, instead of growing without bound.</para>
///
/// <para><b>Rides the action-multiplied hit.</b> <c>ResistanceEvaluator.cs:238</c>:
/// <c>effectiveMagnitude = request.BaseMagnitude * intensityNetFactor</c> — the runtime already reads
/// whatever <c>BaseMagnitude</c> its caller supplied, and a skill packet at, say, ×1.8 is responsible
/// for supplying an already-multiplied value before it ever reaches here. The POC's own prediction
/// model instead read the status DEFINITION's raw authored magnitude directly, skipping that multiply
/// — invisible until both actions and status were live at once
/// (spec-residual-fit.md §6: 15.4% → 4.1% once fixed). <see cref="EffectiveMagnitude"/> is this rule
/// made explicit and tested, specifically so it cannot quietly go missing from the model again.</para>
/// </summary>
public static class StatusUptime
{
    /// <param name="p">Per-round apply probability, in <c>[0, 1]</c>.</param>
    /// <param name="duration">Status duration, in rounds (fractional allowed — a millisecond duration
    /// divided by round length is a caller concern, not this function's). Non-negative; 0 means the
    /// status has no window to be active in, so uptime is 0 regardless of <paramref name="p"/>.</param>
    public static double Uptime(double p, double duration)
    {
        if (double.IsNaN(p) || p < 0.0 || p > 1.0)
            throw new ArgumentOutOfRangeException(nameof(p), p, "must be a probability in [0, 1]");
        if (double.IsNaN(duration) || duration < 0.0)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "must be non-negative");

        return 1.0 - Math.Pow(1.0 - p, duration);
    }

    /// <summary>The status magnitude an action actually delivers — <c>baseMagnitude × actionMultiplier</c>,
    /// never the authored base alone (§2.1 correction 4).</summary>
    /// <param name="baseMagnitude">The status definition's authored magnitude. Any finite value —
    /// this module does not assume a sign convention belonging to the status catalog.</param>
    /// <param name="actionMultiplier">The triggering action's own damage multiplier (1.0 for an
    /// unmultiplied hit). Non-negative — a multiplier does not flip the magnitude's sign.</param>
    public static double EffectiveMagnitude(double baseMagnitude, double actionMultiplier)
    {
        if (double.IsNaN(baseMagnitude))
            throw new ArgumentOutOfRangeException(nameof(baseMagnitude), baseMagnitude, "must not be NaN");
        if (double.IsNaN(actionMultiplier) || actionMultiplier < 0.0)
            throw new ArgumentOutOfRangeException(nameof(actionMultiplier), actionMultiplier, "must be non-negative");

        return baseMagnitude * actionMultiplier;
    }

    // ---- The deterministic status read -- calls the SHIPPED ResistanceEvaluator, P4.6 -------------

    public readonly record struct StatusOutcome(double PFinal, double Magnitude, double DurationRounds);

    /// <summary>
    /// The deterministic read of one status application: forces the apply roll to succeed
    /// (<see cref="FixedStatusRng"/> with value 0, which is always less than any nonzero <c>PFinal</c>)
    /// then reads <see cref="StatusApplyResult.PFinal"/> back out separately from the payload it would
    /// have delivered -- the two are needed apart for the closed form (an uptime weighted by
    /// probability, not a single trial's outcome), and <see cref="ResistanceEvaluator.Evaluate"/>
    /// computes both in one pass, so this calls it once rather than twice. This is the SAME shipped
    /// object <c>StatusRuntime.Apply</c> drives (spec-deterministic-core.md §2's own standard) — no
    /// resist math is re-derived here.
    /// </summary>
    public static StatusOutcome Expected(
        string statusId, double magnitudeShareOfBase, double baseDurationRounds, double grantChance,
        CombatActorSnapshot attacker, CombatActorSnapshot defender, double baseDamage)
    {
        if (string.IsNullOrWhiteSpace(statusId)) throw new ArgumentException("must not be empty", nameof(statusId));
        if (attacker is null) throw new ArgumentNullException(nameof(attacker));
        if (defender is null) throw new ArgumentNullException(nameof(defender));
        if (double.IsNaN(baseDamage) || baseDamage < 0)
            throw new ArgumentOutOfRangeException(nameof(baseDamage), baseDamage, "must be non-negative");

        var request = new StatusApplyRequest(
            StatusId: statusId, HostPtr: "defender", AttackerPtr: "attacker",
            BaseMagnitude: baseDamage * magnitudeShareOfBase, BaseDuration: baseDurationRounds,
            GrantChance: grantChance);
        var evaluator = new ResistanceEvaluator();
        var r = evaluator.Evaluate(request, attacker.Derived, defender.Derived, new FixedStatusRng(0.0));
        return r.Applied ? new StatusOutcome(r.PFinal, r.EffectiveMagnitude, r.EffectiveDuration) : new StatusOutcome(0.0, 0.0, 0.0);
    }

    /// <summary>Expected DoT damage per round, from steady-state uptime: rides BOTH the status's own
    /// apply chance AND the swing's own hit chance (a round spent missing cannot land a DoT) -- the two
    /// are independent probabilities, so they multiply BEFORE going into <see cref="Uptime"/>, not
    /// after (contrast <see cref="CcDisabledShare"/>, which applies its own pHit factor outside
    /// Uptime — a real, deliberate asymmetry in the reference this ports, not a simplification).</summary>
    public static double ExpectedDotPerRound(StatusOutcome outcome, double pHit)
    {
        if (double.IsNaN(pHit) || pHit < 0 || pHit > 1)
            throw new ArgumentOutOfRangeException(nameof(pHit), pHit, "must be a probability in [0, 1]");
        return Uptime(outcome.PFinal * pHit, outcome.DurationRounds) * outcome.Magnitude;
    }

    /// <summary>Steady-state share of rounds a CC status keeps its target disabled, from ITS OWN apply
    /// chance alone (the caller multiplies by pHit separately -- see <see cref="ExpectedDotPerRound"/>'s
    /// doc for why the two compositions are not the same shape).</summary>
    public static double CcDisabledShare(StatusOutcome outcome) => Uptime(outcome.PFinal, outcome.DurationRounds);
}
