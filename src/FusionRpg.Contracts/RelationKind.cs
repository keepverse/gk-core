namespace FusionRpg.Contracts;

/// <summary>
/// Who a target is relative to whoever is asking — resolved against the asker rather than an
/// absolute side, so one authored rule serves both factions without duplicate rows. Lives in
/// Contracts (not `FusionRpg.Core.Actions`, where this concept was first authored as
/// `ActionRelation`) so both `Actions/ActionTargetSpec.cs` and `Core/Scope/WhoSelector.cs` can
/// depend on one shared type instead of each defining their own copy — buff-debuff-scope program,
/// spec-scope-model.md, Assumption 1.
/// </summary>
public enum RelationKind
{
    Self = 0,
    Ally,
    Enemy,
    Any,
}

public static class RelationKinds
{
    public static string Name(RelationKind relation) => relation switch
    {
        RelationKind.Self => "self",
        RelationKind.Ally => "ally",
        RelationKind.Enemy => "enemy",
        RelationKind.Any => "any",
        _ => "",
    };

    public static bool TryParse(string? text, out RelationKind relation)
    {
        switch (text)
        {
            case "self": relation = RelationKind.Self; return true;
            case "ally": relation = RelationKind.Ally; return true;
            case "enemy": relation = RelationKind.Enemy; return true;
            case "any": relation = RelationKind.Any; return true;
            default: relation = default; return false;
        }
    }
}
