namespace FusionRpg.Core.World;

/// <summary>Stable kebab-case id checks for world catalog entries.</summary>
public static class WorldIds
{
    public static void RequireKebab(string? id, string label)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(label + " is empty.");

        // Reject padding rather than trimming it away: catalog lookups compare ordinally, so a
        // stored " seat " would validate here and then match nothing.
        var s = id!;
        if (s != s.Trim())
            throw new InvalidOperationException($"{label} '{id}' must not have leading or trailing whitespace.");

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= 'a' and <= 'z') continue;
            if (c is >= '0' and <= '9') continue;
            if (c == '-') continue;
            throw new InvalidOperationException(
                $"{label} '{id}' must be kebab-case (lowercase letters, digits, hyphens).");
        }

        if (s[0] == '-' || s[^1] == '-')
            throw new InvalidOperationException($"{label} '{id}' must not start or end with '-'.");
    }
}
