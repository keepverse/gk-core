using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-ai` (spec-siege-ai.md, R1): a real, reusable hit-chance estimate for
/// `AiCandidate.HitChanceMilli` — the FIRST of 17.4's own four named blockers, closed by reading
/// `OverlayCombatCalculator.Compute`'s own already-shipped Omni fallback path
/// (`Combat/OverlayCombatCalculator.cs:117-119`) rather than inventing a second formula. **A new,
/// additive function, never a modification to that resolver** — `OverlayCombatCalculator` stays
/// untouched, so every existing combat golden is provably unaffected by this file's own existence.
///
/// <para><b>Omni-only, deliberately</b> — not per-element. `OverlayCombatCalculator`'s own per-element
/// path additionally weighs each `ElementPayloadComponent`, which needs to know the SPECIFIC action
/// about to be used; a targeting decision (which enemy to attack, not yet which attack) does not have
/// one yet. The Omni channels (`CombatAccuracyOmni`/`CombatDodgeOmni`) are exactly the values that
/// path ALSO falls back to when a caller supplies no components (`OverlayCombatCalculator.cs`'s own
/// "empty component list is a resolver-level fallback" comment) — the same default every other
/// omni-scoped caller in this codebase already accepts, not a new approximation invented here.</para>
///
/// <para><b>The double stays inside this function.</b> `CombatProbability.Sigmoid` returns a
/// `double` (0..1) — converted to an `int` per-mille and rounded ONCE, immediately, before crossing
/// back into `AiScoring`'s own integer per-mille arithmetic (spec R5's integer scoring design for
/// `SiegeAi.cs`, which stays integer because the boundary is exactly here, not smeared across both
/// files). The former source scan that banned float/double in `SiegeAi.cs` was removed by the
/// 2026-09-15 owner ruling; this is a design boundary, not an enforced ban.</para>
/// </summary>
public static class SiegeHitChance
{
    public static int EstimateMilli(ActorDerivedSnapshot attacker, ActorDerivedSnapshot defender)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(defender);

        var accuracyDelta = attacker.Get(DerivedStatChannels.CombatAccuracyOmni)
                             - defender.Get(DerivedStatChannels.CombatDodgeOmni);
        var pHit = CombatProbability.Sigmoid(accuracyDelta, CombatProbabilityPolicy.AccuracyScale);
        return (int)Math.Clamp(Math.Round(pHit * 1000.0), 0.0, 1000.0);
    }
}
