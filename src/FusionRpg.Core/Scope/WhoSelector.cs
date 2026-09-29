using FusionRpg.Contracts;

namespace FusionRpg.Core.Scope;

/// <summary>
/// Which population a <see cref="WhoSelector"/> names (buff-debuff-scope-ideal.md §3). Four values,
/// closed — matching the atom layer's own closed-kind discipline. Adding a fifth is a reviewed
/// change (spec-scope-model.md Boundaries).
/// </summary>
public enum WhoKind
{
    Target = 0,
    Type,
    UniqueCreature,
    Relation,
}

public static class WhoKinds
{
    public static string Name(WhoKind kind) => kind switch
    {
        WhoKind.Target => "target",
        WhoKind.Type => "type",
        WhoKind.UniqueCreature => "uniqueCreature",
        WhoKind.Relation => "relation",
        _ => "",
    };

    public static bool TryParse(string? text, out WhoKind kind)
    {
        switch (text)
        {
            case "target": kind = WhoKind.Target; return true;
            case "type": kind = WhoKind.Type; return true;
            case "uniqueCreature": kind = WhoKind.UniqueCreature; return true;
            case "relation": kind = WhoKind.Relation; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>
/// The typed authoring contract for "who a scope reaches" — orthogonal `Kind` + payload fields,
/// matching <c>ActionTargetSpec</c>'s own established shape (one enum, separate typed fields, never a
/// fat discriminated union). References <see cref="RelationKind"/> directly from
/// <c>FusionRpg.Contracts</c> rather than <c>FusionRpg.Core.Actions</c>'s own <c>ActionRelation</c>
/// alias — the whole point of the T1 extraction was so `Scope/` depends on the shared type without
/// depending on `Actions/` (spec-scope-model.md Assumption 1).
/// </summary>
public sealed record WhoSelector
{
    public WhoKind Kind { get; init; } = WhoKind.Relation;

    /// <summary>For <see cref="WhoKind.Target"/> — a live entity pointer, `entity:` owner-key shaped.</summary>
    public string? TargetPtr { get; init; }

    /// <summary>For <see cref="WhoKind.Type"/> — matches <c>ActionTargetFilters.TypeIds</c>'s shape.</summary>
    public IReadOnlyList<int>? TypeIds { get; init; }

    /// <summary>
    /// For <see cref="WhoKind.UniqueCreature"/> — a durable specimen instance id, resolved through
    /// <c>MatchUniqueBindingsFacet</c> (battlefield) or <c>WorldState.Entities[].Members[]</c>
    /// (world map) depending on <see cref="WhereScope"/>.
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>For <see cref="WhoKind.Relation"/> — own/enemy/any/self, resolved against the granter.</summary>
    public RelationKind? Relation { get; init; }
}
