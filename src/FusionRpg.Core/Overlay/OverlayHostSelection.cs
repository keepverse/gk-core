namespace FusionRpg.Core.Overlay;

/// <summary>Which process owns the web view window.</summary>
public enum OverlayHostMode
{
    /// <summary>FusionRpg.Launcher hosts it; the injector signals over the named pipe. Default.</summary>
    Launcher = 0,

    /// <summary>The injector owns a borderless window in the game process. No Launcher required.</summary>
    Injector
}

/// <summary>
/// Resolves <c>overlayHost</c>. Env beats host config, matching how <c>FUSIONRPG_SERVER_URL</c>
/// already overrides the injector's <c>ServerUrl</c> setting.
/// </summary>
public static class OverlayHostSelection
{
    public const string EnvVar = "FUSIONRPG_OVERLAY_HOST";

    /// <summary>Env first, then host config, then <see cref="OverlayHostMode.Launcher"/>.</summary>
    public static OverlayHostMode Resolve(string? envValue, string? configValue)
    {
        // An unusable env value falls through rather than overriding: a typo in an env var
        // should not silently discard a deliberate config choice.
        if (TryParse(envValue, out var fromEnv)) return fromEnv;
        if (TryParse(configValue, out var fromConfig)) return fromConfig;
        return OverlayHostMode.Launcher;
    }

    public static bool TryParse(string? value, out OverlayHostMode mode)
    {
        mode = OverlayHostMode.Launcher;
        if (string.IsNullOrWhiteSpace(value)) return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "launcher":
                mode = OverlayHostMode.Launcher;
                return true;
            case "injector":
                mode = OverlayHostMode.Injector;
                return true;
            default:
                return false;
        }
    }
}
