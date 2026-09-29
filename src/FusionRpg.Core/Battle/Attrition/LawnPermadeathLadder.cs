namespace FusionRpg.Core.Battle.Attrition;

/// <summary>
/// `solid-remediation` T4.10 — the lawn's own <see cref="IPermadeathLadder"/>, keyed to damage taken.
///
/// <para><b>The design is the direction, not the constants.</b> The task says so outright: *"the
/// curve's direction is the design, its constants are not"*. The direction is that a member which
/// took more damage risks more — nothing below a floor, rising linearly to the tuned chance at a full
/// health bar's worth taken — and that permanent death sits behind injury on a higher floor, so a
/// member risks a limb before it risks a life. Every number lives in
/// `gk-core/data/tuning/lawn-attrition.v2.json` and is marked unmeasured there.</para>
///
/// <para><b>Why the lawn needed a ladder at all.</b> Before T4.9 the settlement decision took a
/// `bool`, and a mode that had not thought about permanent death passed `false`. The lawn was that
/// mode: injury and death never triggered, and nothing anywhere said whether that was a decision or an
/// oversight. This class is the decision, written down.</para>
///
/// <para><b>Deterministic by construction.</b> The roll is drawn by the caller and handed in, the same
/// shape <c>CaptureAction.Resolve</c> uses — this type performs no I/O and owns no RNG, so a replay
/// with the same seed settles identically.</para>
/// </summary>
public sealed class LawnPermadeathLadder : IPermadeathLadder
{
    readonly LawnAttritionTuning _tuning;
    readonly int _damageTakenMilli;
    readonly int _rolledMilli;

    /// <param name="damageTakenMilli">Damage taken over the run as per-mille of max HP. Values above
    /// 1000 are legitimate — an actor can be overkilled — and are treated as a full bar rather than
    /// extrapolated past the tuned endpoint, because the curve was never designed beyond it.</param>
    /// <param name="rolledMilli">A 0..999 draw the caller already made off its own seeded stream.</param>
    public LawnPermadeathLadder(LawnAttritionTuning tuning, int damageTakenMilli, int rolledMilli)
    {
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        if (damageTakenMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(damageTakenMilli), damageTakenMilli, "damage taken is never negative");
        if (rolledMilli is < 0 or > 999)
            throw new ArgumentOutOfRangeException(nameof(rolledMilli), rolledMilli, "a per-mille roll is 0..999");

        _damageTakenMilli = damageTakenMilli;
        _rolledMilli = rolledMilli;
    }

    public bool PermadeathApplies() =>
        _rolledMilli < ChanceMilli(
            _damageTakenMilli, _tuning.PermadeathFloorMilli, _tuning.PermadeathChanceAtFullMilli);

    /// <summary>The injury half of the same curve, on its own floor. Exposed because injury is the
    /// common case and a caller reads it directly rather than through the settlement outcome.</summary>
    public int InjuryChanceMilli() =>
        ChanceMilli(_damageTakenMilli, _tuning.InjuryFloorMilli, _tuning.InjuryChanceAtFullMilli);

    /// <summary>
    /// Zero below the floor; from the floor to a full bar it rises linearly to
    /// <paramref name="chanceAtFullMilli"/>.
    /// </summary>
    /// <remarks>
    /// Integer per-mille arithmetic: widen before multiplying and divide by 1000 last, so the single
    /// truncation happens at the end (docs/architecture/numeric-types.md's numeric rule). `long` because the intermediate
    /// product of two per-mille values is up to 10^6 — comfortably inside `int`, but the rule is about
    /// the shape of the arithmetic, not about whether today's constants happen to fit.
    /// </remarks>
    internal static int ChanceMilli(int damageTakenMilli, int floorMilli, int chanceAtFullMilli)
    {
        var taken = Math.Min(damageTakenMilli, 1000);
        if (taken <= floorMilli) return 0;

        var span = 1000 - floorMilli;
        if (span <= 0) return chanceAtFullMilli; // a floor at a full bar: all-or-nothing at 1000‰

        var over = (long)(taken - floorMilli);
        return checked((int)(over * chanceAtFullMilli / span));
    }
}
