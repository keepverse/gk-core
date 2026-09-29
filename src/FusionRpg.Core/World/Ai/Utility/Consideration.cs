namespace FusionRpg.Core.World.Ai.Utility;

/// <summary>
/// One reason to care, scored (spec-ai-commander.md §The consideration arithmetic).
///
/// A consideration maps a single normalised input through a curve. "How hurt am I", "how far is it",
/// "how badly do I want that" — each is one of these, and a behaviour's score is the product of all
/// of them.
/// </summary>
public readonly record struct Consideration(string Name, ResponseCurve Curve, int Input, int Threshold = 500)
{
    public int Score() => ResponseCurves.Evaluate(Curve, Input, Threshold);
}

/// <summary>
/// Scoring a behaviour from its considerations, after the
/// <see href="https://www.gameai.com/iaus.php">Infinite Axis Utility System</see>.
///
/// The score is the **product**, and that is the whole design: one zero kills a behaviour outright,
/// which replaces a tier of guard clauses with arithmetic. "Do not charge if you are dying" stops
/// being an `if` somebody has to remember and becomes a health consideration that reaches zero.
///
/// Nothing calls this yet. `frontier-rules` is a rule list because scoring wants an economy to score
/// against and there is not one until <c>sector-development</c>. What ships here is the arithmetic,
/// tested in isolation, so the wave that needs it inherits a scorer rather than writing one.
/// </summary>
public static class Considerations
{
    /// <summary>
    /// The product of every consideration, compensated for how many there are.
    ///
    /// Multiplying N scores drags everything toward zero — three considerations at 800‰ each give
    /// 512‰, so a behaviour that is *good at everything* scores worse than one with a single
    /// mediocre axis. Compensation pulls that back proportionally to how much was lost to arity
    /// alone, so behaviours with different numbers of considerations remain comparable.
    /// </summary>
    public static int Score(IReadOnlyList<Consideration> considerations)
    {
        if (considerations.Count == 0) return 0;

        long product = ResponseCurves.Max;
        foreach (var consideration in considerations)
        {
            var score = Math.Clamp(consideration.Score(), 0, ResponseCurves.Max);
            // A short-circuit, not a rule: the product would reach zero anyway and compensation
            // cannot lift it off zero. Removing this changes no answer, only how much arithmetic
            // runs — worth knowing, because a mutant that deletes it survives and should.
            if (score == 0) return 0;
            product = product * score / ResponseCurves.Max;
        }

        return Compensate((int)product, considerations.Count);
    }

    /// <summary>
    /// `modifier = 1000 − 1000/n`, then add back that fraction of what is missing.
    ///
    /// One consideration is uncompensated by construction (the modifier is zero), which is correct:
    /// with nothing multiplied together there is nothing to compensate for.
    /// </summary>
    public static int Compensate(int score, int count)
    {
        if (count <= 0) return score;

        // No special case for a single consideration: `1000 - 1000/1` is already zero, so the
        // arithmetic leaves it untouched on its own. The guard that used to sit here was redundant,
        // which a surviving mutant is how we found out — removing it means the doc comment above and
        // the code now say the same thing.
        var modifier = ResponseCurves.Max - ResponseCurves.Max / count;
        var makeUp = (long)(ResponseCurves.Max - score) * modifier / ResponseCurves.Max;

        // Scaled by the score itself, so compensation lifts a decent behaviour and cannot rescue a
        // hopeless one — and can never carry anything past the ceiling.
        return (int)Math.Min(ResponseCurves.Max, score + makeUp * score / ResponseCurves.Max);
    }

    /// <summary>
    /// Which consideration is holding a behaviour back — the one a turn report should name.
    ///
    /// Under fog an AI's mistake and a bug look identical from outside; "wanted to attack, but
    /// health scored 40" is the sentence that separates them.
    /// </summary>
    public static Consideration? Weakest(IReadOnlyList<Consideration> considerations)
    {
        Consideration? weakest = null;
        var lowest = int.MaxValue;

        // Ordinal by name on ties, so the explanation does not change between runs.
        foreach (var consideration in considerations.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var score = consideration.Score();
            if (score >= lowest) continue;
            lowest = score;
            weakest = consideration;
        }

        return weakest;
    }
}
