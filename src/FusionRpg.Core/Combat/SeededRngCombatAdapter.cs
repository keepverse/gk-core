using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Combat;

/// <summary>
/// ICombatRng over the owned deterministic PRNG (combat-unification: spec-combat-resolver-core).
/// Replayable hosts (battle, future sim resolution) roll through this; System.Random-backed
/// SeededCombatRng never backs goldens. Note: CombatProbability.RollSuccess draws
/// Next(1_000_000) — a different rejection-sampling threshold than NextPerMille's 1000, covered
/// by each host's version stamp.
/// </summary>
public sealed class SeededRngCombatAdapter : ICombatRng
{
    readonly SeededRng _rng;

    public SeededRngCombatAdapter(SeededRng rng) =>
        _rng = rng ?? throw new ArgumentNullException(nameof(rng));

    /// <summary>Mirrors the ICombatRng contract (SeededCombatRng): non-positive max → 0.</summary>
    public int Next(int exclusiveMax) => exclusiveMax <= 0 ? 0 : _rng.NextInt(exclusiveMax);
}
