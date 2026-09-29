namespace FusionRpg.Core.Actions.Seeding;

/// <summary>One candidate in a weighted pool — reused for target shapes, categories, and any other
/// closed vocabulary T31 rolls over. Weight is per-mille-of-nothing-in-particular — just a relative
/// weight, the same running-total-selection shape <c>Instantiator.Draw</c> already uses for atoms.</summary>
public readonly record struct WeightedOption<T>(T Value, int Weight);

public sealed class NoDrawableWeightedOptionException : Exception
{
    public NoDrawableWeightedOptionException(string what)
        : base($"no drawable option for '{what}' — every candidate had weight <= 0, or the pool was empty") { }
}

/// <summary>
/// T31 (spec-action-seeding.md §4): the generic weighted draw the runtime generator rolls target
/// shapes (and, later, category/element bias) over — the SAME running-total selection
/// <c>Instantiator.Draw</c> already uses for atoms, generalized to any candidate type rather than
/// duplicated. Deterministic: same seed, same stream name, same pick, always.
/// </summary>
public static class WeightedChoice
{
    /// <param name="streamName">Namespaces the RNG draw the same way <c>Instantiator.Draw</c>
    /// namespaces its own pool roll per container — two different rolls off one seed must not share
    /// a sequence.</param>
    public static T Pick<T>(IReadOnlyList<WeightedOption<T>> options, long rollSeed, string streamName)
    {
        var drawable = new List<WeightedOption<T>>(options.Count);
        var total = 0;
        foreach (var o in options)
        {
            if (o.Weight <= 0) continue;
            drawable.Add(o);
            total += o.Weight;
        }

        if (drawable.Count == 0)
            throw new NoDrawableWeightedOptionException(streamName);

        var rng = new AtomRngImpl(unchecked((ulong)rollSeed), "action.seed." + streamName);
        var target = rng.NextInclusive(1, total);

        var running = 0;
        foreach (var candidate in drawable)
        {
            running += candidate.Weight;
            if (running >= target) return candidate.Value;
        }

        return drawable[^1].Value; // unreachable while total/target stay in sync -- last resort, not a silent wrong answer
    }
}
