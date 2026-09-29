namespace FusionRpg.Core.Combat.Element;

/// <summary>Weighted element component list — weights must be positive and sum to 1.</summary>
public sealed class ElementPayload
{
    // structural: floating-point comparison tolerance, not a balance number (tunables-ssot.md T2).
    public const double WeightSumEpsilon = 1e-6;

    readonly IReadOnlyList<ElementPayloadComponent> _components;

    ElementPayload(IReadOnlyList<ElementPayloadComponent> components) =>
        _components = components;

    public IReadOnlyList<ElementPayloadComponent> Components => _components;

    public static ElementPayload From(IReadOnlyList<ElementPayloadComponent> components)
    {
        Validate(components);
        return new ElementPayload(components);
    }

    public static void Validate(IReadOnlyList<ElementPayloadComponent>? components)
    {
        if (components == null || components.Count == 0)
            throw new ArgumentException("Element payload must contain at least one component.");

        var sum = 0.0;
        foreach (var c in components)
        {
            if (c.Weight <= 0)
                throw new ArgumentException("Element payload weights must be positive.");
            sum += c.Weight;
        }

        if (Math.Abs(sum - 1.0) > WeightSumEpsilon)
            throw new ArgumentException($"Element payload weights must sum to 1.0 (got {sum}).");
    }
}
