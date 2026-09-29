namespace FusionRpg.Core.Settings;

/// <summary>
/// The kind of value a user setting holds. Closed on purpose — the store persists JSON, and without a
/// declared kind every reader would have to guess how to parse it.
/// </summary>
public enum UserSettingKind
{
    Bool,
}

/// <summary>One user-facing preference: its id, what it holds, and what it is when nobody has chosen.</summary>
/// <param name="Key">Stable id. Never renamed — it is the persisted primary key.</param>
/// <param name="Kind">How <paramref name="DefaultJson"/> and any stored value are parsed.</param>
/// <param name="DefaultJson">The value when the player has never set one. JSON, so the shape matches
/// what is stored rather than needing a second encoding.</param>
/// <param name="Summary">One line, for whoever reads the registry rather than the UI.</param>
public sealed record UserSettingDef(string Key, UserSettingKind Kind, string DefaultJson, string Summary);

/// <summary>
/// <b>The centralized user-settings registry</b> (`solid-remediation`, 2026-09-17, owner:
/// *"Make new user setting module if we dont have centralize user settings"*).
///
/// <para><b>Why this exists rather than another one-off.</b> The repo had no user-settings concept at
/// all. The <c>settings</c> table in <c>RpgStore</c> is internal singleton app state — <c>stats</c>,
/// <c>cheats</c>, <c>current_player_id</c> — with no per-player scoping and no public API. The one
/// real user preference that had shipped, <c>lawnViewMode</c>, lived in browser <c>localStorage</c>,
/// which cannot reach the injector and does not follow the player to another browser. The next
/// preference would have invented a third mechanism.</para>
///
/// <para><b>The key vocabulary is CLOSED and its count is pinned by a test.</b> This is a registry the
/// code owns and a human edits, which is exactly the case
/// <c>validation-ssot.md</c> says to pin — unlike a population such as species or items, where the size
/// is a reading. Adding a setting is a reviewed change, and an unknown key is refused at the API rather
/// than silently stored.</para>
/// </summary>
public static class UserSettingKeys
{
    /// <summary>Whether the in-world actor HUD (bars, status tokens above each entity) is drawn.
    ///
    /// <para>Defaults to <b>false</b>. Measured 2026-09-17 on a live board: the world HUD walk made
    /// <c>vfx.tick</c> <b>2.513 ms/frame at 80 entities</b> — on its own more than the <b>2 ms/frame</b>
    /// whole-injector stress budget <c>perf-probe-plan.md</c> §0 locks for 200+ entities. The owner's
    /// ruling was to disable it by default and let the player turn it back on.</para></summary>
    public const string WorldHud = "lawn.worldHud";

    /// <summary>Whether cosmetic combat effects are rendered. Gameplay and the actor HUD are independent.</summary>
    public const string VisualEffects = "lawn.visualEffects";

    /// <summary>Every declared setting. The array IS the vocabulary.</summary>
    public static readonly IReadOnlyList<UserSettingDef> All = new[]
    {
        new UserSettingDef(
            WorldHud, UserSettingKind.Bool, "false",
            "Draw the in-world actor HUD. Off by default: it dominated vfx.tick at 2.513 ms/frame."),
        new UserSettingDef(
            VisualEffects, UserSettingKind.Bool, "true",
            "Render cosmetic combat effects. Turn this off to reduce visual-effects work without changing gameplay."),
    };

    public static bool IsKnown(string? key) =>
        !string.IsNullOrWhiteSpace(key) && All.Any(d => string.Equals(d.Key, key, StringComparison.Ordinal));

    public static UserSettingDef? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));

    /// <summary>Parses a stored/incoming JSON value for <paramref name="key"/>, or null when it does not
    /// fit the declared kind. A malformed value is refused rather than coerced — a setting that silently
    /// becomes <c>false</c> because it failed to parse is indistinguishable from one the player chose.</summary>
    public static bool TryParseBool(string key, string? json, out bool value)
    {
        value = false;
        var def = Find(key);
        if (def is null || def.Kind != UserSettingKind.Bool) return false;
        var raw = (json ?? "").Trim();
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
        return false;
    }

    /// <summary>The declared default, already parsed. Used when the player has never chosen.</summary>
    public static bool DefaultBool(string key) =>
        TryParseBool(key, Find(key)?.DefaultJson, out var v) && v;
}
