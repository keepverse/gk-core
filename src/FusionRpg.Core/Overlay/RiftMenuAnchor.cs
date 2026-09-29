namespace FusionRpg.Core.Overlay;

/// <summary>
/// Which reviewed PVZ menu screen exists right now. Written by the menu screen's own **lifecycle**
/// callbacks and read O(1) by the tombstone's placement path — never sent to the server, never on the
/// hit path, never a combat/domain input.
///
/// Decision 17: presence, not navigation. <see cref="Set"/> runs on the screen's own <c>Start</c>;
/// <see cref="Clear"/> on its own hide/exit. No navigation method is ever called to produce this, and
/// the injector never drives the engine's menu state.
///
/// Pure and Unity-free: the Harmony patches that write it live in the injector, so the state machine
/// and its type-guard semantics are testable from nowhere (the <c>OverlaySwitchState</c> precedent).
/// </summary>
public sealed class RiftMenuAnchor
{
    /// <summary>The reviewed surface that is present, or <see cref="RiftMenuSurface.None"/>.</summary>
    public RiftMenuSurface Current { get; private set; } = RiftMenuSurface.None;

    /// <summary>True only when the present surface is a human-reviewed, non-gameplay, non-pause one.</summary>
    public bool IsReviewed => RiftMenuAnchorPolicy.IsReviewed(Current);

    /// <summary>The screen's own <c>Start</c> ran — the menu object now exists.</summary>
    public void Set(RiftMenuSurface surface) => Current = surface;

    /// <summary>
    /// A screen went away. Clears **only** the surface that actually went away, so a submenu hiding
    /// cannot clear a main menu that is still up: that type guard is the load-bearing half of this
    /// class (spec-menu-anchor.md). Clearing an already-cleared surface is a no-op.
    /// </summary>
    public void Clear(RiftMenuSurface surface)
    {
        if (Current == surface) Current = RiftMenuSurface.None;
    }
}
