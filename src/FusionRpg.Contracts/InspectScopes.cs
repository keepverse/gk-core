namespace FusionRpg.Contracts;

/// <summary>
/// The debug inspect-scope vocabulary — the three places a control probe may read, plus the default.
///
/// <para>Declared once here because the two assemblies that need it cannot see each other:
/// <c>FusionRpg.Server</c>'s <c>/inspect</c> route and <c>FusionRpg.Injector</c>'s
/// <c>debug.inspect</c> command carried a byte-identical line —
/// <c>if (scope is not ("menu" or "lawn" or "all")) scope = "all";</c> — with no declaring type
/// anywhere (combat-math-dedup T14, D18). Both reach Contracts (Server → Contracts; Injector → Core →
/// Contracts), so Contracts is the one home the assembly graph allows.</para>
/// </summary>
public static class InspectScopes
{
    /// <summary>The UI control tree.</summary>
    public const string Menu = "menu";

    /// <summary>The lawn board.</summary>
    public const string Lawn = "lawn";

    /// <summary>Both — and the default when the caller names nothing, or something unknown.</summary>
    public const string All = "all";

    /// <summary>The closed set, so a surface that lists the choices reads the same declaration the
    /// validator does.</summary>
    public static readonly IReadOnlyList<string> Known = new[] { Menu, Lawn, All };

    /// <summary>Trim + lowercase the caller's value, then fall back to <see cref="All"/> when it names
    /// none of the three — the one normalization both hosts call, so a renamed or added scope cannot
    /// reach one and miss the other.</summary>
    public static string Normalize(string? scope)
    {
        var text = (scope ?? All).Trim().ToLowerInvariant();
        return text is Menu or Lawn or All ? text : All;
    }
}
