using FusionRpg.Core.Overlay;
using Xunit;

namespace FusionRpg.Core.Tests.Overlay;

/// <summary>
/// The in-game overlay button's decision logic. No Unity, no pipe, no clock —
/// every time is passed in so the whole thing is deterministic.
/// </summary>
public class OverlaySwitchStateTests
{
    static OverlaySwitchState Reachable(long nowMs = 0)
    {
        var s = new OverlaySwitchState();
        s.ApplyProbeResult(true, nowMs);
        s.OnMatchStart();
        return s;
    }

    // ---- debounce ----

    [Fact]
    public void First_click_sends()
    {
        var s = Reachable();
        Assert.True(s.TryClick(1_000));
    }

    [Fact]
    public void Second_click_inside_the_debounce_window_is_swallowed()
    {
        var s = Reachable();
        Assert.True(s.TryClick(1_000));
        Assert.False(s.TryClick(1_000 + OverlaySwitchState.DebounceMs - 1));
    }

    [Fact]
    public void Click_at_the_debounce_boundary_sends()
    {
        var s = Reachable();
        Assert.True(s.TryClick(1_000));
        s.MarkSendComplete();
        Assert.True(s.TryClick(1_000 + OverlaySwitchState.DebounceMs));
    }

    [Fact]
    public void A_held_click_produces_exactly_one_send()
    {
        var s = Reachable();
        var sends = 0;
        for (var t = 0; t < OverlaySwitchState.DebounceMs; t += 16) // ~60fps of held mouse
        {
            if (s.TryClick(t)) sends++;
            s.MarkSendComplete(); // even with instant sends, the window alone must hold the line
        }
        Assert.Equal(1, sends);
    }

    [Fact]
    public void A_swallowed_click_does_not_extend_the_window()
    {
        var s = Reachable();
        Assert.True(s.TryClick(0));
        s.MarkSendComplete();
        Assert.False(s.TryClick(200));
        // The window is measured from the last *send*, not the last attempt.
        Assert.True(s.TryClick(OverlaySwitchState.DebounceMs));
    }

    // ---- in-flight sends (Prove-It: a click during a slow send used to vanish) ----

    [Fact]
    public void A_click_while_a_send_is_in_flight_is_refused()
    {
        var s = Reachable();
        Assert.True(s.TryClick(0));
        Assert.True(s.SendInFlight);
        Assert.False(s.TryClick(350));
    }

    [Fact]
    public void A_click_during_a_slow_send_does_not_consume_the_next_window()
    {
        // The pipe connect alone can take 250 ms, so a send outliving the 300 ms debounce
        // window is normal. The click that lands during it must not be counted as a send:
        // it never reached the pipe, and treating it as one locks the player out again.
        var s = Reachable();
        Assert.True(s.TryClick(0));       // send starts, and runs long
        Assert.False(s.TryClick(350));    // past the window, but nothing was sent
        s.MarkSendComplete();
        Assert.True(s.TryClick(400));     // window is measured from t=0, not from the refused click
    }

    [Fact]
    public void Completing_a_send_does_not_bypass_the_debounce_window()
    {
        var s = Reachable();
        Assert.True(s.TryClick(0));
        s.MarkSendComplete();             // fast send
        Assert.False(s.TryClick(100));    // still inside the window
        Assert.True(s.TryClick(OverlaySwitchState.DebounceMs));
    }

    [Fact]
    public void A_fresh_state_has_no_send_in_flight()
    {
        Assert.False(new OverlaySwitchState().SendInFlight);
    }

    // ---- Prove-It: a send that never completes used to disable the button for the session ----

    [Fact]
    public void A_send_that_never_completes_does_not_disable_the_button_forever()
    {
        // The pipe write has no timeout of its own: if the launcher's reader stalls mid-connection
        // the completion callback never runs, so the in-flight gate would never clear and the
        // player would be left clicking a dead button until they restart the game.
        var s = Reachable();
        Assert.True(s.TryClick(0));
        Assert.False(s.TryClick(1_000)); // still legitimately in flight

        Assert.True(
            s.TryClick(OverlaySwitchState.SendTimeoutMs),
            "an abandoned send must stop blocking clicks once it is clearly never coming back");
    }

    [Fact]
    public void An_abandoned_send_is_not_declared_dead_early()
    {
        var s = Reachable();
        Assert.True(s.TryClick(0));
        Assert.False(s.TryClick(OverlaySwitchState.SendTimeoutMs - 1));
    }

    [Fact]
    public void A_late_completion_after_a_timeout_does_not_corrupt_the_gate()
    {
        // The abandoned send can still finish later; that must not cancel a newer one.
        var s = Reachable();
        Assert.True(s.TryClick(0));
        Assert.True(s.TryClick(OverlaySwitchState.SendTimeoutMs)); // second send starts
        s.MarkSendComplete();                                     // the *first* one finally lands
        Assert.False(s.SendInFlight);
        Assert.True(s.TryClick(OverlaySwitchState.SendTimeoutMs + OverlaySwitchState.DebounceMs));
    }

    [Fact]
    public void The_send_timeout_is_longer_than_the_pipe_can_legitimately_take()
    {
        // 250 ms connect + write; anything under that would abandon healthy sends.
        Assert.True(OverlaySwitchState.SendTimeoutMs >= 2_000);
    }

    // ---- visibility gate ----

    [Fact]
    public void Button_is_hidden_until_a_probe_finds_a_host()
    {
        var s = new OverlaySwitchState();
        Assert.False(s.HostReachable);
        Assert.False(s.ButtonVisible);
    }

    [Fact]
    public void Button_is_visible_when_enabled_and_reachable()
    {
        var s = Reachable();
        Assert.True(s.ButtonVisible);
    }

    [Fact]
    public void Settings_toggle_hides_the_button_even_when_reachable()
    {
        var s = Reachable();
        s.SettingsEnabled = false;
        Assert.False(s.ButtonVisible);
        Assert.True(s.HostReachable); // reachability is unaffected by the player's preference
    }

    [Fact]
    public void Losing_the_host_hides_the_button()
    {
        var s = Reachable();
        s.ApplyProbeResult(false, 5_000);
        Assert.False(s.ButtonVisible);
    }

    // ---- match gate: the button is in-match chrome, not menu chrome ----

    [Fact]
    public void Button_is_hidden_outside_a_match()
    {
        var s = new OverlaySwitchState();
        s.ApplyProbeResult(true, 0);
        Assert.False(s.MatchActive);
        Assert.False(s.ButtonVisible); // placement was chosen against the seed bank, not menu UI
    }

    [Fact]
    public void Starting_a_match_shows_the_button()
    {
        var s = new OverlaySwitchState();
        s.ApplyProbeResult(true, 0);
        s.OnMatchStart();
        Assert.True(s.ButtonVisible);
    }

    [Fact]
    public void Ending_a_match_hides_the_button()
    {
        var s = Reachable();
        Assert.True(s.ButtonVisible);
        s.OnMatchEnd();
        Assert.False(s.MatchActive);
        Assert.False(s.ButtonVisible);
    }

    [Fact]
    public void Visibility_needs_all_three_of_match_host_and_preference()
    {
        var s = Reachable();
        Assert.True(s.ButtonVisible);

        s.SettingsEnabled = false;
        Assert.False(s.ButtonVisible);
        s.SettingsEnabled = true;

        s.ApplyProbeResult(false, 100);
        Assert.False(s.ButtonVisible);
        s.ApplyProbeResult(true, 200);

        s.OnMatchEnd();
        Assert.False(s.ButtonVisible);
    }

    // ---- probe scheduling ----

    [Fact]
    public void Probes_immediately_on_a_fresh_state()
    {
        var s = new OverlaySwitchState();
        Assert.True(s.ShouldProbe(0));
    }

    [Fact]
    public void Does_not_probe_again_inside_the_interval()
    {
        var s = new OverlaySwitchState();
        s.MarkProbeSent(1_000);
        Assert.False(s.ShouldProbe(1_000 + OverlaySwitchState.ProbeIntervalMs - 1));
    }

    [Fact]
    public void Probes_again_at_the_interval()
    {
        var s = new OverlaySwitchState();
        s.MarkProbeSent(1_000);
        Assert.True(s.ShouldProbe(1_000 + OverlaySwitchState.ProbeIntervalMs));
    }

    [Fact]
    public void Match_start_forces_the_next_probe()
    {
        var s = new OverlaySwitchState();
        s.MarkProbeSent(1_000);
        Assert.False(s.ShouldProbe(1_100));
        s.OnMatchStart();
        Assert.True(s.ShouldProbe(1_100));
    }

    // ---- log on transition only ----

    [Fact]
    public void Probe_result_reports_a_transition_only_when_reachability_changes()
    {
        var s = new OverlaySwitchState();
        Assert.True(s.ApplyProbeResult(true, 0));    // unknown -> reachable
        Assert.False(s.ApplyProbeResult(true, 100)); // steady
        Assert.False(s.ApplyProbeResult(true, 200)); // steady
        Assert.True(s.ApplyProbeResult(false, 300)); // reachable -> gone
        Assert.False(s.ApplyProbeResult(false, 400));// steady
    }

    [Fact]
    public void A_failed_first_probe_is_not_a_transition()
    {
        // Starting unreachable and staying unreachable must not write a log line
        // on every player who never opens the launcher.
        var s = new OverlaySwitchState();
        Assert.False(s.ApplyProbeResult(false, 0));
    }

    [Fact]
    public void Repeated_probe_failures_never_log_more_than_once()
    {
        var s = Reachable();
        var transitions = 0;
        for (var t = 0; t < 10; t++)
            if (s.ApplyProbeResult(false, 1_000 + t * OverlaySwitchState.ProbeIntervalMs)) transitions++;
        Assert.Equal(1, transitions);
    }
}
