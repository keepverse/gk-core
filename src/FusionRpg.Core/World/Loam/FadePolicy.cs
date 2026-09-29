namespace FusionRpg.Core.World.Loam;

/// <summary>
/// Shortfall lowers <c>StabilityMilli</c>, scaled to how deep it runs; surplus raises it, always
/// more slowly than a shortfall could ever lower it (spec-loam-calc.md #5). Symmetric rates would
/// make a sector oscillate on the boundary and turn a dramatic mechanic into a flickering number,
/// so recovery is fixed and slow while decay grows — capped — with the deficit.
/// </summary>
public static class FadePolicy
{
    /// <summary>
    /// The new <c>StabilityMilli</c>, given the current value and a component's balance.
    /// <paramref name="pressureMilli"/> (spec-loam-texture.md's fade contagion) and
    /// <paramref name="surge"/> (a Fracture surge) both only ever affect the decay side — recovery
    /// is untouched, matching this program's own "surge makes holding harder, never makes giving up
    /// easier" resolution.
    /// </summary>
    public static int Apply(int currentStabilityMilli, long balance, int pressureMilli = 0, bool surge = false) =>
        balance < 0
            ? Math.Max(0, currentStabilityMilli - DecayFor(-balance, pressureMilli, surge))
            : Math.Min(1000, currentStabilityMilli + LoamPolicy.RecoveryMilli);

    /// <summary>
    /// How much a deficit of this size decays stability this turn — floored at
    /// <see cref="LoamPolicy.BaseDecayMilli"/> so even a one-unit shortfall costs something, ceilinged
    /// at <see cref="LoamPolicy.MaxDecayMilli"/> so no single turn can zero a sector outright.
    ///
    /// <paramref name="pressureMilli"/> (fade contagion) adds directly to the pre-clamp sum, and
    /// <paramref name="surge"/> (a Fracture surge) scales that whole sum — never the clamped
    /// return value, which is exactly the audit-caught bug this shape exists to avoid: scaling an
    /// already-clamped decay could push a sector already at the ceiling past it.
    /// </summary>
    public static int DecayFor(long deficitMagnitude, int pressureMilli = 0, bool surge = false)
    {
        // Everything stays `long` until the final clamp so a huge deficit cannot overflow its way
        // past the ceiling instead of hitting it — the same overflow-by-construction discipline as
        // `LoamUpkeep`.
        long scaled = LoamPolicy.BaseDecayMilli
                      + deficitMagnitude / LoamPolicy.DecayScaleDivisor * LoamPolicy.DecayPerDeficitUnitMilli
                      + pressureMilli;

        if (surge) scaled = scaled * LoamPolicy.SurgeDecayMultiplierMilli / 1000;

        return (int)Math.Max(0, Math.Min(LoamPolicy.MaxDecayMilli, scaled));
    }
}
