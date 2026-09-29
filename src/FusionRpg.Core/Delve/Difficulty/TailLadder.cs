using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Difficulty;

/// <summary>One tail step's outcome — either a composed band/Θ, or a named refusal
/// (spec-difficulty-ladder.md §3: "the picker rejects an n before composing when the would-be Θ …
/// exceeds MaxIndex").</summary>
public sealed record TailStepResult(bool Offered, int Band, int Theta, long? MaxIndexAtRefusal);

/// <summary>
/// "Impossible" is a name, not a ceiling (§3). Past rung 10 (`impossible`), only the band moves —
/// the rule row (encounter/economy/loot columns) is rung 10's, verbatim. The ONLY absolute bound is
/// <see cref="PowerLadder.MaxIndex"/>, a computed property of the loaded curve, never a literal
/// (ideal §11.10 R... / ssot-power-scale.md PS-8: "no hard progression ceilings" — this is the one
/// bound that is derived and throws, never an authored cap).
/// </summary>
public static class TailLadder
{
    /// <summary>`n` is 1-based — the first step past rung 10. Composes through the same
    /// <see cref="RoomThetaComposer"/> every rung uses; the tail carries no separate rule row.</summary>
    public static TailStepResult TryBand(
        PowerTuning power, DungeonTuning dungeon, DomainThetaInputs domain, int n, bool isBoss, ParentWorldTerms world)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "tail step n must be >= 1.");
        if (!dungeon.DifficultyTail.Enabled)
            return new TailStepResult(false, 0, 0, null);

        var rung10 = RungTable.Get(dungeon.DifficultyTail.RulesFrozenAtRung); // rung 10's rule row, frozen
        var maxIndex = new PowerLadder(power).MaxIndex;

        // Pre-check with the SAME weighted-sum shape the composer uses, so the refusal is named
        // before any exception would fire deeper in the stack (ContentExplain's checked cast, or
        // a downstream PowerLadder.Value call on an oversized Θ).
        RoomTheta composed;
        try
        {
            composed = RoomThetaComposer.Compose(power, dungeon, domain, rung10, row: 0, tailPlus: n, isBoss, world);
        }
        catch (OverflowException)
        {
            return new TailStepResult(false, 0, 0, maxIndex);
        }

        if (composed.Theta > maxIndex)
            return new TailStepResult(false, composed.Band, composed.Theta, maxIndex);

        return new TailStepResult(true, composed.Band, composed.Theta, null);
    }

    /// <summary>The player-facing label — `difficulty.tail.labelFormat` (starting shape
    /// `"abyss +{n}"`), never the raw band or Θ (§3, §6: the picker gets a name, never a delta).
    /// A single-token substitution, not a composite format string: the validator's own rule is
    /// just "contains `{n}`".</summary>
    public static string Label(DungeonTuning dungeon, int n) =>
        dungeon.DifficultyTail.LabelFormat.Replace("{n}", n.ToString(), StringComparison.Ordinal);
}
