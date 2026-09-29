namespace FusionRpg.Core.Hud;

/// <summary>Maps status effective magnitude to lawn display band — not raw numbers (GG-60).
///
/// <para>Thresholds read from <see cref="ActorHudTuningHub"/> (`gk-core/data/tuning/actor-hud.v1.json`
/// `magnitudeMidThreshold`/`magnitudeHighThreshold`), matching <see cref="PowerBandDisplay"/>'s own
/// sibling pattern in this file group — a balance/UX pass tuning where "Mid"/"High" kicks in is
/// exactly the kind of number `tunables-ssot.md` T1 puts in config, not a `const`.</para></summary>
public static class MagnitudeBandDisplay
{
    public static MagnitudeBand FromEffectiveMagnitude(double magnitude)
    {
        var tuning = ActorHudTuningHub.Tuning;
        if (double.IsNaN(magnitude) || magnitude < tuning.MagnitudeMidThreshold)
            return MagnitudeBand.Low;
        if (magnitude < tuning.MagnitudeHighThreshold)
            return MagnitudeBand.Mid;
        return MagnitudeBand.High;
    }
}
