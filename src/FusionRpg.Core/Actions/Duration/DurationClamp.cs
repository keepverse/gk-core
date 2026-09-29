namespace FusionRpg.Core.Actions.Duration;

/// <param name="ClampedVictimTurns">The turn count to hand <see cref="IDurationResolver.ToTicks"/> —
/// never above <see cref="DurationTuning.MaxVictimTurns"/>.</param>
/// <param name="IntensityBonusMilli">Per-mille of <c>status.intensity.*</c> the excess converted
/// into — zero when the resolved duration never reached the bound.</param>
public readonly record struct DurationClampResult(int ClampedVictimTurns, long IntensityBonusMilli);

/// <summary>
/// T28 (spec-duration-resolver.md §3, §3.1): clamp-and-convert — the LAST step after
/// <c>durationNetFactor</c> has already scaled the authored victim-turn count, never a validation on
/// the authored row. "A clamp applied at authoring time is one a duration-stacking build walks
/// straight through" — this class only ever sees the fully-resolved, already-scaled value.
///
/// <para><b><see cref="DurationClampResult.ClampedVictimTurns"/> is a BOUNDED RATIO, PS-8 EXEMPT —
/// this comment is that exemption</b> (spec §1: "you lose at most N of your actions… the declaration
/// must say so in a comment"). It bounds how many of the VICTIM's OWN turns one control effect may
/// steal, never total effect: the excess is never discarded, it is REDIRECTED into
/// <see cref="DurationClampResult.IntensityBonusMilli"/> — itself uncapped — which is what makes this
/// a SOFT cap rather than a ceiling (spec §3: "nothing is lost, it is redirected").</para>
///
/// <para>Fixed-point throughout — <c>resolvedVictimTurnsMilli</c> is the already-scaled turn count in
/// per-mille units (1000 = one whole turn), matching this program's established milli-`long`
/// convention. (Not driven by a floating-point ban — the purity scan's former <c>double</c>/<c>float</c>
/// ban was removed by the 2026-09-15 owner ruling.)</para>
/// </summary>
public static class DurationClamp
{
    public static DurationClampResult ClampAndConvert(long resolvedVictimTurnsMilli, DurationTuning tuning)
    {
        if (resolvedVictimTurnsMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(resolvedVictimTurnsMilli), resolvedVictimTurnsMilli, "a resolved duration is never negative");

        var maxMilli = checked((long)tuning.MaxVictimTurns * 1000);
        if (resolvedVictimTurnsMilli <= maxMilli)
            return new DurationClampResult((int)((resolvedVictimTurnsMilli + 500) / 1000), IntensityBonusMilli: 0);

        var excessMilli = resolvedVictimTurnsMilli - maxMilli;
        var intensityBonusMilli = checked(excessMilli * tuning.IntensityPerExcessTurnMilli / 1000);
        return new DurationClampResult(tuning.MaxVictimTurns, intensityBonusMilli);
    }
}
