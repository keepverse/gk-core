using FusionRpg.Core.ActorSurface;

namespace FusionRpg.Core.Hud;

/// <summary>
/// Shared display tokens for Unity TextMesh and Phaser/Inspector.
/// Status glyphs resolve from injected <see cref="StatusSurfaceCatalogHub"/> (ideal §4.1) —
/// never from id-slice initials or hashed RGB.
/// </summary>
public static class ActorHudDisplayTokens
{
    /// <summary>Designed placeholder when catalog is unset or id is unknown (GG-62).</summary>
    public static readonly StatusHudTokenResolve UnknownStatus = new("·", "#a89880", "Unknown status");

    /// <summary>
    /// Resolve player-visible status glyph fields from the injected status catalog.
    /// No file I/O. When the hub is not configured or the id is missing, returns
    /// <see cref="UnknownStatus"/> — never id-slice initials.
    /// </summary>
    public static StatusHudTokenResolve ResolveStatus(string? id)
    {
        if (StatusSurfaceCatalogHub.TryGet(id, out var entry))
            return new StatusHudTokenResolve(entry.HudToken, entry.Color, entry.DisplayName);
        return UnknownStatus;
    }

    public static string TierLetter(ActorHudTier tier) => tier switch
    {
        ActorHudTier.Unique => "U",
        ActorHudTier.Elite => "E",
        ActorHudTier.Boss => "B",
        _ => ""
    };

    public static string TierLetter(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return "";
        return tier.Trim().ToLowerInvariant() switch
        {
            "unique" => "U",
            "elite" => "E",
            "boss" => "B",
            _ => ""
        };
    }
}

/// <summary>Player-facing status strip fields from status-catalog (or designed placeholder).</summary>
public sealed record StatusHudTokenResolve(string HudToken, string Color, string DisplayName);
