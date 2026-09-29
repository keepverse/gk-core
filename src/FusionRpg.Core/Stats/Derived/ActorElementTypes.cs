namespace FusionRpg.Core.Stats.Derived;

public enum ElementTypeId
{
    Fire,
    Ice,
    Air,
    Earth,
    Light,
    Dark
}

/// <summary>
/// The one legal way to enumerate elements (element-hub-ssot.md §roster).
/// Ring: fire → ice → earth → air → fire; light ⇄ dark mutual counter, neutral vs the ring.
/// </summary>
public static class ElementRoster
{
    public const string OmniId = "omni";

    public static readonly IReadOnlyList<ElementTypeId> Concrete = new[]
    {
        ElementTypeId.Fire,
        ElementTypeId.Ice,
        ElementTypeId.Air,
        ElementTypeId.Earth,
        ElementTypeId.Light,
        ElementTypeId.Dark
    };

    /// <summary>
    /// Strict name → id parse. Names only — numeric strings are rejected
    /// (Enum.TryParse would accept "42" and, post-extension, silently remap "4"/"5").
    /// </summary>
    public static bool TryParse(string? value, out ElementTypeId id)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "fire": id = ElementTypeId.Fire; return true;
            case "ice": id = ElementTypeId.Ice; return true;
            case "air": id = ElementTypeId.Air; return true;
            case "earth": id = ElementTypeId.Earth; return true;
            case "light": id = ElementTypeId.Light; return true;
            case "dark": id = ElementTypeId.Dark; return true;
            default: id = default; return false;
        }
    }
}

public sealed record ActorElementTypes
{
    public static ActorElementTypes Neutral { get; } = new();

    public ElementTypeId? Primary { get; init; }
    public ElementTypeId? Secondary { get; init; }

    public bool IsNeutral => Primary is null && Secondary is null;

    public static ActorElementTypes Create(ElementTypeId? primary = null, ElementTypeId? secondary = null)
    {
        if (primary is null && secondary is not null)
            throw new ArgumentException("Secondary type requires a primary type.");
        if (primary is not null && primary == secondary)
            throw new ArgumentException("Primary and secondary types must differ.");
        return new ActorElementTypes
        {
            Primary = primary,
            Secondary = secondary
        };
    }

    public static ActorElementTypes Parse(string? primary, string? secondary)
    {
        var p = ParseSlot(primary, "primary");
        var s = ParseSlot(secondary, "secondary");
        return Create(p, s);
    }

    static ElementTypeId? ParseSlot(string? value, string slotName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (string.Equals(value, ElementRoster.OmniId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Element slot '{slotName}' cannot use omni.");
        if (ElementRoster.TryParse(value, out var parsed))
            return parsed;
        throw new ArgumentException($"Unknown element type '{value}' for slot '{slotName}'.");
    }
}

public static class ElementTypeIdExtensions
{
    public static string ToElementId(this ElementTypeId id) => id switch
    {
        ElementTypeId.Fire => "fire",
        ElementTypeId.Ice => "ice",
        ElementTypeId.Air => "air",
        ElementTypeId.Earth => "earth",
        ElementTypeId.Light => "light",
        ElementTypeId.Dark => "dark",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown element type.")
    };
}
