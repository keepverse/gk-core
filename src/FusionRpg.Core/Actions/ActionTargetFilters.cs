namespace FusionRpg.Core.Actions;

/// <summary>
/// Typed authoring surface over the shipped resolver's filter keys (spec-targeting.md §3). Covers
/// exactly what `TargetResolver.FilterPool` reads today — `side` is deliberately absent, replaced by
/// <see cref="ActionTargetSpec.Relation"/>. Anything not on this list is rejected at authoring, not
/// ignored; growing it is a reviewed change, matching the atom program's closed-leaf discipline.
/// </summary>
public sealed record ActionTargetFilters
{
    /// <summary>Subsumes the shipped resolver's `typeId` and `typeIdIn` — one is a list of one.</summary>
    public IReadOnlyList<int>? TypeIds { get; init; }

    /// <summary>Null keeps the shipped default (true unless the relation resolves to the plant side).</summary>
    public bool? ExcludeMindControlled { get; init; }

    /// <summary>Absolute board filters, kept because content may legitimately want "the front column".</summary>
    public int? Row { get; init; }
    public int? ColMin { get; init; }
    public int? ColMax { get; init; }
}
