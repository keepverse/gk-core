using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Shield;

public readonly record struct ShieldLayerResult(long Spent, long Remainder, long DamageToShield);

/// <summary>Thrown by <see cref="ShieldMath.AbsorbLayer"/> when <c>input</c> exceeds
/// <see cref="ShieldMath.MaxInput"/> — refuses to silently clamp (spec-caps-reconcile.md §2.1).</summary>
public sealed class ShieldInputOverflow : Exception
{
    public long Input { get; }
    public long MaxInput { get; }

    public ShieldInputOverflow(long input, long maxInput)
        : base($"shield math: input={input} exceeds MaxInput={maxInput} for the loaded ShieldPolicy — refuses to clamp")
    {
        Input = input;
        MaxInput = maxInput;
    }
}

/// <summary>
/// Pure single-layer absorb math — shield-system-spec.md §2.4. All arithmetic is 64-bit
/// integer at permille scale; rounding is half away from zero via (num + d/2) / d on
/// non-negative operands. No rolls, no floats in any game-affecting branch.
/// </summary>
public static class ShieldMath
{
    /// <summary>
    /// Gate-entry bound so every product <see cref="AbsorbLayer"/> forms against <c>input</c> stays
    /// inside <c>long</c> — DERIVED from <see cref="ShieldPolicy"/>'s own loaded coefficients (F13:
    /// reads <c>MatchupShareKPm</c>, <c>ChipFloorKPm</c>, <c>PenCapKPm</c>), never a literal
    /// (spec-caps-reconcile.md §2.1). Recomputed on every read, not cached — it must track whatever
    /// <see cref="ShieldPolicy"/> is currently configured with, same as every other Policy read.
    ///
    /// <para>Three products in <see cref="AbsorbLayer"/> scale with <c>input</c>:
    /// <c>weightedRelationUnitPm × MatchupShareKPm × input</c> (elemMod's numerator),
    /// <c>ChipFloorKPm × input</c> (floor's numerator), <c>PenCapKPm × input</c> (cap's numerator).
    /// <c>MaxInput</c> is <c>long.MaxValue</c> divided by the LARGEST of the three coefficients — the
    /// tightest of the three safe ceilings, i.e. the one that binds first.</para>
    ///
    /// <para><c>weightedRelationUnitPm</c>'s documented [-1000,1000] range on <see cref="AbsorbLayer"/>
    /// is not assumed here, it is provable: <see cref="ShieldElementMatrix.RelationUnit"/> only ever
    /// returns {-1,0,1}, and <c>ElementPayload</c> requires its component weights to be positive and
    /// sum to 1.0 (± <c>WeightSumEpsilon</c>) before <see cref="WeightedRelationUnitPm"/> converts them
    /// to per-mille — so Σ weightPm_i ≈ 1000, and the worst case (every component agreeing in sign)
    /// sums to at most 1000 in magnitude.</para>
    /// </summary>
    public static long MaxInput
    {
        get
        {
            checked
            {
                var elemCoefficient = 1000L * Math.Max(1L, Math.Abs(ShieldPolicy.MatchupShareKPm));
                var floorCoefficient = Math.Max(1L, Math.Abs(ShieldPolicy.ChipFloorKPm));
                var capCoefficient = Math.Max(1L, Math.Abs(ShieldPolicy.PenCapKPm));
                var widest = Math.Max(elemCoefficient, Math.Max(floorCoefficient, capCoefficient));
                return long.MaxValue / widest;
            }
        }
    }

    /// <param name="input">Damage reaching this layer, HP units (≥ 0).</param>
    /// <param name="shieldHp">This shield's current pool, HP units (≥ 0).</param>
    /// <param name="weightedRelationUnitPm">Σ(componentWeightPm × relUnit) in [−1000, 1000].</param>
    /// <param name="breakerDelta">pen − toughness in HP units (may be negative).</param>
    /// <param name="hitCount">Coalesced hit count (≥ 1); scales the flat breaker term so
    /// coalesced ≡ n× uncoalesced.</param>
    public static ShieldLayerResult AbsorbLayer(
        long input, long shieldHp, long weightedRelationUnitPm, long breakerDelta, long hitCount)
    {
        if (input <= 0 || shieldHp <= 0)
            return new ShieldLayerResult(0, input < 0 ? 0 : input, 0);
        if (input > MaxInput) throw new ShieldInputOverflow(input, MaxInput);
        if (hitCount < 1) hitCount = 1;

        // elemMod = relUnitPm × KPm × input / 1e6 (permille × permille), half away from zero.
        var elemMod = RoundDivSigned(weightedRelationUnitPm * ShieldPolicy.MatchupShareKPm * input, 1_000_000);
        var baseValue = input + elemMod;

        // spec-evasion-chain.md §2 (T5.2): the clamp+delta shape extracted to ClampedContest, reused
        // unmodified by block/parry (T5.3) rather than a second saturation curve (Q6). Exactly the
        // same constants as before extraction — refactor and behaviour change stay separate. Floor
        // and cap bound against RAW input, not baseValue (input + elemMod) — the shipped math always
        // has, even though spec-evasion-chain.md §2's own pseudocode describes one shared "base" for
        // both; see ClampedContest.Apply's boundsBase doc for the discrepancy and why shipped code
        // wins over the spec's prose here.
        var damageToShield = ClampedContest.Apply(
            baseValue, breakerDelta, hitCount, input, ShieldPolicy.ChipFloorKPm, ShieldPolicy.PenCapKPm);

        var spent = Math.Min(shieldHp, damageToShield);
        // Proportional remainder, half away from zero; operands non-negative here.
        var remainder = damageToShield == 0
            ? input
            : (input * (damageToShield - spent) + damageToShield / 2) / damageToShield;
        return new ShieldLayerResult(spent, remainder, damageToShield);
    }

    /// <summary>
    /// Σ(componentWeightPm × relUnit) against one shield's element. Untyped shield → 0.
    /// Weights convert to permille once (half away from zero) — the only float boundary.
    /// </summary>
    public static long WeightedRelationUnitPm(
        IReadOnlyList<ElementPayloadComponent> components, ElementTypeId? shieldElement)
    {
        if (shieldElement is not { } shieldEl || components.Count == 0)
            return 0;
        long sum = 0;
        for (var i = 0; i < components.Count; i++)
        {
            var weightPm = (long)Math.Round(components[i].Weight * 1000.0, MidpointRounding.AwayFromZero);
            sum += weightPm * ShieldElementMatrix.RelationUnit(components[i].Element, shieldEl);
        }

        return sum;
    }

    static long RoundDivSigned(long num, long div)
    {
        // div > 0; num may be negative — round half away from zero.
        return num >= 0 ? (num + div / 2) / div : -((-num + div / 2) / div);
    }
}
