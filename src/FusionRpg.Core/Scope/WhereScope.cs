namespace FusionRpg.Core.Scope;

/// <summary>
/// Execution context for a buff/debuff scope (buff-debuff-scope-ideal.md §3). Not one execution
/// host under <see cref="Battlefield"/> — two, sharing one grant-issuing front end but with
/// materially different readers (spec-battlefield-scope.md's own audit correction). See
/// <c>ScopeHost</c> for the sub-dimension that distinguishes them.
/// </summary>
public enum WhereScope
{
    Battlefield = 0,
    WorldMap,
}

public static class WhereScopes
{
    public static string Name(WhereScope scope) => scope switch
    {
        WhereScope.Battlefield => "battlefield",
        WhereScope.WorldMap => "worldMap",
        _ => "",
    };

    public static bool TryParse(string? text, out WhereScope scope)
    {
        switch (text)
        {
            case "battlefield": scope = WhereScope.Battlefield; return true;
            case "worldMap": scope = WhereScope.WorldMap; return true;
            default: scope = default; return false;
        }
    }
}

/// <summary>
/// Which reader executes a <see cref="WhereScope.Battlefield"/> scope. Only meaningful there —
/// <see cref="WhereScope.WorldMap"/> has exactly one host and never carries this. G8 (a `match`-scope
/// `stat.modify` on `defense` reads one side-wide cached value on the live path) is what makes this a
/// real, load-bearing distinction rather than a formality: the same kind can resolve differently on
/// each host (spec-scope-model.md Assumption 2).
/// </summary>
public enum ScopeHost
{
    Sim = 0,
    Live,
}

public static class ScopeHosts
{
    public static string Name(ScopeHost host) => host switch
    {
        ScopeHost.Sim => "sim",
        ScopeHost.Live => "live",
        _ => "",
    };

    public static bool TryParse(string? text, out ScopeHost host)
    {
        switch (text)
        {
            case "sim": host = ScopeHost.Sim; return true;
            case "live": host = ScopeHost.Live; return true;
            default: host = default; return false;
        }
    }
}
