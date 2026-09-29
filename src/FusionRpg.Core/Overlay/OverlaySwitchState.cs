namespace FusionRpg.Core.Overlay;

/// <summary>
/// Decision logic for the in-game overlay button: when a click actually sends, when to probe for a
/// host, and whether the button is drawn at all. Pure and clock-injected — the caller passes the
/// time, so the injector can drive this from the Unity thread and tests can drive it from nowhere.
/// </summary>
public sealed class OverlaySwitchState
{
    /// <summary>A held or double click inside this window sends once.</summary>
    public static int DebounceMs => OverlayTuningHub.Tuning.SwitchState.DebounceMs;

    /// <summary>How often to ask whether an overlay host is listening.</summary>
    public static int ProbeIntervalMs => OverlayTuningHub.Tuning.SwitchState.ProbeIntervalMs;

    /// <summary>
    /// After this long an in-flight send is treated as abandoned. The pipe write has no timeout of
    /// its own, so a stalled reader on the other end would otherwise leave the gate closed for the
    /// rest of the session and the button dead. Comfortably longer than a healthy connect + write.
    /// </summary>
    public static int SendTimeoutMs => OverlayTuningHub.Tuning.SwitchState.SendTimeoutMs;

    long _lastSendMs;
    bool _hasSent;
    long _lastProbeMs;
    bool _hasProbed;

    /// <summary>Player preference (persisted with the other presentation toggles).</summary>
    public bool SettingsEnabled { get; set; } = true;

    /// <summary>Last probe verdict. Starts false so a launcher-less session never flashes a dead button.</summary>
    public bool HostReachable { get; private set; }

    /// <summary>
    /// A toggle is on the wire. The connect alone may take 250 ms, so a send can outlive the
    /// debounce window — the gate lives here rather than in the caller so a click that never
    /// reaches the pipe is not recorded as if it had.
    /// </summary>
    public bool SendInFlight { get; private set; }

    /// <summary>True once an in-flight send has outlived <see cref="SendTimeoutMs"/>.</summary>
    bool SendAbandoned(long nowMs) => SendInFlight && nowMs - _lastSendMs >= SendTimeoutMs;

    /// <summary>
    /// A board is live. The button is in-match chrome: its corner was chosen against the seed bank
    /// and shovel, not against menu screens, so drawing it outside a match risks stealing a menu
    /// click. The global hotkey still works everywhere, so nothing is lost by hiding it here.
    /// </summary>
    public bool MatchActive { get; private set; }

    public bool ButtonVisible => SettingsEnabled && HostReachable && MatchActive;

    /// <summary>True when this click should produce a send. Measured from the last send, not the last attempt.</summary>
    public bool TryClick(long nowMs)
    {
        // An abandoned send must not hold the gate shut forever — the player would be left
        // clicking a dead button with no way to recover short of restarting the game.
        if (SendAbandoned(nowMs)) SendInFlight = false;

        if (SendInFlight) return false; // refuse before recording: nothing is going out
        if (_hasSent && nowMs - _lastSendMs < DebounceMs) return false;
        _hasSent = true;
        _lastSendMs = nowMs;
        SendInFlight = true;
        return true;
    }

    /// <summary>The send finished (either way) — the next click may go out once the window allows.</summary>
    public void MarkSendComplete() => SendInFlight = false;

    /// <summary>True when a probe is due — immediately on a fresh state, then once per interval.</summary>
    public bool ShouldProbe(long nowMs) => !_hasProbed || nowMs - _lastProbeMs >= ProbeIntervalMs;

    public void MarkProbeSent(long nowMs)
    {
        _hasProbed = true;
        _lastProbeMs = nowMs;
    }

    /// <summary>Shows the button and forces the next <see cref="ShouldProbe"/> — the launcher may have come or gone between matches.</summary>
    public void OnMatchStart()
    {
        MatchActive = true;
        _hasProbed = false;
    }

    /// <summary>Board is over: hide the button until the next one starts.</summary>
    public void OnMatchEnd() => MatchActive = false;

    /// <summary>
    /// Records a probe verdict. Returns true only when reachability actually flipped, so the caller
    /// logs one line per transition instead of one per probe.
    /// </summary>
    public bool ApplyProbeResult(bool reachable, long nowMs)
    {
        MarkProbeSent(nowMs);
        if (HostReachable == reachable) return false;
        HostReachable = reachable;
        return true;
    }
}
