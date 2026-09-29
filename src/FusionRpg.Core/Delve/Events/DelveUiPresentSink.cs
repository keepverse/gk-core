using FusionRpg.Core.Effects;

namespace FusionRpg.Core.Delve.Events;

/// <summary>One banner queued by a room's own event resolution, in the order shown. `DurationMs` is
/// always resolved (never null) — <see cref="DelveUiPresentSink"/>'s own default fill-in already ran.</summary>
public sealed record DelveBanner(string BannerId, int DurationMs);

/// <summary>
/// `event-deck` D3.6 (spec-event-deck.md §5's own dispatch table row: "`ui.present` (`op:banner`) |
/// the host's `IUiPresentSink.ShowBanner(bannerId, durationMs)` ... a `DelveUiPresentSink` appending to
/// the room's presentation list for `delve-stage`"). Implements the existing, shipped
/// <see cref="IUiPresentSink"/> (`Effects/UiPresentSink.cs`) for the delve context.
///
/// <para><b>PARTIALLY BUILT — the default duration is a plain constructor parameter, never loaded
/// here:</b> `gk-core/data/tuning/delve-ui.v1.json` is `delve-stage`'s OWN tuning file
/// (`spec-delve-stage.md` §12: `banner.durationMs | ms | this module` — "this module" being
/// `delve-stage`, not `event-deck`), and it does not exist yet (`delve-stage` is Phase 5, unbuilt).
/// Reading it from here would reach into a module this task has no authority over — the caller supplies
/// the default the same way every other "read model owned elsewhere" value in this program is
/// supplied.</para>
///
/// <para><see cref="SetMeter"/> is a no-op: event-deck's own dispatch table (§5) names exactly five
/// atom kinds it ever emits (`resource.delta`, `status.apply`, `shield.grant`, `stat.derived`,
/// `ui.present` with `op:banner`) — `op:meter` is a different `ui.present` sub-op this module never
/// authors, so there is nothing for this sink to ever route there.</para>
/// </summary>
public sealed class DelveUiPresentSink : IUiPresentSink
{
    readonly List<DelveBanner> _banners = new();
    readonly int _defaultDurationMs;

    public DelveUiPresentSink(int defaultDurationMs)
    {
        if (defaultDurationMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(defaultDurationMs), defaultDurationMs, "a default banner duration must be positive");
        _defaultDurationMs = defaultDurationMs;
    }

    /// <summary>Every banner shown this room, in call order — `delve-stage`'s own future read model
    /// (D5.x, unbuilt) reads this the same way `RecordingUiPresentSink` already lets a test read it.</summary>
    public IReadOnlyList<DelveBanner> Banners => _banners;

    public void ShowBanner(string bannerId, int? durationMs)
    {
        if (string.IsNullOrWhiteSpace(bannerId)) throw new ArgumentException("bannerId required", nameof(bannerId));
        _banners.Add(new DelveBanner(bannerId, durationMs ?? _defaultDurationMs));
    }

    public void SetMeter(string targetPtr, string meterId, double ratio) { }
}
