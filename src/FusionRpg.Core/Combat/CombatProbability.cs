using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.Combat;

public static class CombatProbability
{
    public static double Sigmoid(double delta, double scale) =>
        ResistanceEvaluator.Sigmoid(delta / scale, CombatProbabilityPolicy.Steepness);

    public static bool RollSuccess(ICombatRng rng, double probability)
    {
        if (probability <= 0) return false;
        if (probability >= 1) return true;
        return rng.Next(1_000_000) / 1_000_000.0 < probability;
    }
}
