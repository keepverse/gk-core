using FusionRpg.Core.Overlay;
using Xunit;

namespace FusionRpg.Core.Tests.Overlay;

/// <summary>
/// Rules for the in-game view that decide whether a player can get back to their game.
/// Pure, because the alternative is discovering them by being trapped in a covered lawn.
/// </summary>
public class OverlayViewPolicyTests
{
    // ---- there must always be a way out ----

    [Fact]
    public void Escape_closes_the_view()
    {
        // The view covers the game, so the in-game button that opened it is underneath it.
        // Without a key that closes it, the only exit is killing the game.
        Assert.True(OverlayViewPolicy.IsCloseKey(OverlayViewPolicy.VirtualKeyEscape));
    }

    [Fact]
    public void The_configured_toggle_key_also_closes_the_view()
    {
        Assert.True(OverlayViewPolicy.IsCloseKey(OverlayViewPolicy.VirtualKeyF10));
    }

    [Theory]
    [InlineData(0x41)] // A
    [InlineData(0x20)] // space
    [InlineData(0x0D)] // enter — the SPA needs it
    public void Ordinary_keys_are_left_to_the_page(int vk)
    {
        Assert.False(OverlayViewPolicy.IsCloseKey(vk));
    }

    // ---- never strand a topmost window over someone else's desktop ----

    [Fact]
    public void A_visible_view_hides_when_the_game_stops_being_foreground()
    {
        // Topmost + excluded from alt-tab means that if the player switches away, our window
        // would sit over whatever they switched to, unreachable by alt-tab.
        Assert.True(OverlayViewPolicy.ShouldAutoHide(visible: true, foregroundIsOurProcess: false));
    }

    [Fact]
    public void A_visible_view_stays_up_while_the_game_is_foreground()
    {
        Assert.False(OverlayViewPolicy.ShouldAutoHide(visible: true, foregroundIsOurProcess: true));
    }

    [Fact]
    public void A_hidden_view_is_never_asked_to_hide_again()
    {
        Assert.False(OverlayViewPolicy.ShouldAutoHide(visible: false, foregroundIsOurProcess: false));
        Assert.False(OverlayViewPolicy.ShouldAutoHide(visible: false, foregroundIsOurProcess: true));
    }

    // ---- the server may not be up when the game starts ----

    [Fact]
    public void A_view_that_never_loaded_navigates_again_when_shown()
    {
        // Init happens at game start, which can be before the server is listening; navigating
        // once would leave a permanent error page behind the button.
        Assert.True(OverlayViewPolicy.ShouldNavigateOnShow(lastNavigationSucceeded: false));
    }

    [Fact]
    public void A_loaded_view_is_not_reloaded_on_every_show()
    {
        // Reloading would throw away SPA state, which is the whole point of hide-not-destroy.
        Assert.False(OverlayViewPolicy.ShouldNavigateOnShow(lastNavigationSucceeded: true));
    }

    // ---- the view runs inside the game process, so keep it on our own origin ----

    [Theory]
    [InlineData("http://127.0.0.1:5088", "http://127.0.0.1:5088/")]
    [InlineData("http://127.0.0.1:5088", "http://127.0.0.1:5088/#/roster")]
    [InlineData("http://127.0.0.1:5088/", "http://127.0.0.1:5088/api/stats")]
    [InlineData("http://127.0.0.1:5088", "http://127.0.0.1:5088/deep/path?q=1")]
    public void Our_own_pages_are_allowed(string server, string target)
    {
        Assert.True(OverlayViewPolicy.IsSameOrigin(server, target));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5088", "https://github.com/letuhao1994/plant-vs-zombie-rise-of-summoner")]
    [InlineData("http://127.0.0.1:5088", "http://127.0.0.1:9999/")]
    [InlineData("http://127.0.0.1:5088", "https://127.0.0.1:5088/")]
    [InlineData("http://127.0.0.1:5088", "http://evil.example/")]
    public void Anything_off_origin_is_refused(string server, string target)
    {
        // An external link must not load inside the game process; it belongs in the real browser.
        Assert.False(OverlayViewPolicy.IsSameOrigin(server, target));
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("edge://settings")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_http_and_junk_targets_are_refused(string? target)
    {
        Assert.False(OverlayViewPolicy.IsSameOrigin("http://127.0.0.1:5088", target));
    }

    [Fact]
    public void Host_comparison_ignores_case()
    {
        Assert.True(OverlayViewPolicy.IsSameOrigin("http://LocalHost:5088", "http://localhost:5088/x"));
    }

    [Fact]
    public void An_unusable_server_url_refuses_everything()
    {
        // Fail closed: if we cannot tell what our origin is, nothing is same-origin.
        Assert.False(OverlayViewPolicy.IsSameOrigin("", "http://127.0.0.1:5088/"));
        Assert.False(OverlayViewPolicy.IsSameOrigin("nonsense", "http://127.0.0.1:5088/"));
    }

    // ---- handing a link to the OS shell ----

    [Theory]
    [InlineData("https://github.com/letuhao1994/plant-vs-zombie-rise-of-summoner")]
    [InlineData("http://example.com/docs")]
    public void Web_links_may_be_opened_in_the_real_browser(string uri)
    {
        Assert.True(OverlayViewPolicy.IsExternallyOpenable(uri));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("steam://run/12345")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_web_link_is_never_handed_to_the_shell(string? uri)
    {
        // Process.Start with UseShellExecute runs whatever protocol handler is registered,
        // so only http(s) may ever reach it.
        Assert.False(OverlayViewPolicy.IsExternallyOpenable(uri));
    }

    // ---- idle cost inside a game process ----

    [Fact]
    public void The_pump_idles_slower_when_nothing_is_on_screen()
    {
        var hidden = OverlayViewPolicy.PumpIntervalMs(visible: false);
        var shown = OverlayViewPolicy.PumpIntervalMs(visible: true);

        Assert.True(shown < hidden, "a visible view needs a responsive pump");
        Assert.True(shown > 0, "never spin at zero delay inside a game process");
        Assert.True(hidden >= 25, "hidden costs nothing to render, so do not wake 200 times a second");
    }
}
