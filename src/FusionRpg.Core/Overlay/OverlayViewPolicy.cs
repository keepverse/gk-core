namespace FusionRpg.Core.Overlay;

/// <summary>
/// Rules for the in-game view (overlayHost=injector). Kept pure and here, rather than buried in
/// the Win32 file, because the first two decide whether a player can get back to their game.
/// </summary>
public static class OverlayViewPolicy
{
    public const int VirtualKeyEscape = 0x1B;
    public const int VirtualKeyF10 = 0x79;

    /// <summary>Responsive enough for the browser to render without stuttering.</summary>
    public const int VisiblePumpMs = 5;

    /// <summary>Hidden means nothing renders, so idle far cheaper inside a game process.</summary>
    public const int HiddenPumpMs = 50;

    /// <summary>
    /// Keys that dismiss the view. The view covers the game, so the in-game button that opened it
    /// is underneath — without one of these the only exit would be killing the game.
    /// </summary>
    public static bool IsCloseKey(int virtualKey) =>
        virtualKey == VirtualKeyEscape || virtualKey == VirtualKeyF10;

    /// <summary>
    /// The window is topmost and excluded from alt-tab, so if the player switches to another
    /// application it would sit over that application with no way to alt-tab back to it.
    /// Losing foreground therefore means hiding.
    /// </summary>
    public static bool ShouldAutoHide(bool visible, bool foregroundIsOurProcess) =>
        visible && !foregroundIsOurProcess;

    /// <summary>
    /// Init runs at game start, which can be before the server is listening. Navigating only once
    /// would leave a permanent error page; re-navigating a page that loaded would throw away SPA
    /// state, which is exactly what hide-not-destroy exists to preserve.
    /// </summary>
    public static bool ShouldNavigateOnShow(bool lastNavigationSucceeded) => !lastNavigationSucceeded;

    public static int PumpIntervalMs(bool visible) => visible ? VisiblePumpMs : HiddenPumpMs;

    /// <summary>
    /// Whether a navigation target belongs to our own server. The view runs inside the game
    /// process, so an off-origin page — an external link in the SPA, or anything a compromised
    /// page tries — must not load here; it belongs in the player's real browser. Fails closed:
    /// anything we cannot parse as http(s) on our own origin is refused.
    /// </summary>
    public static bool IsSameOrigin(string? serverUrl, string? targetUrl)
    {
        if (!TryHttpUri(serverUrl, out var ours)) return false;
        if (!TryHttpUri(targetUrl, out var theirs)) return false;

        return string.Equals(ours!.Scheme, theirs!.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(ours.Host, theirs.Host, StringComparison.OrdinalIgnoreCase)
               && ours.Port == theirs.Port;
    }

    /// <summary>
    /// Whether a URI may be handed to the OS shell. <c>Process.Start</c> with
    /// <c>UseShellExecute</c> invokes whatever protocol handler is registered, so only http(s)
    /// links — never <c>file:</c>, <c>javascript:</c> or an arbitrary app scheme — may reach it.
    /// </summary>
    public static bool IsExternallyOpenable(string? uri) => TryHttpUri(uri, out _);

    static bool TryHttpUri(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return true;
    }
}
