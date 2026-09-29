namespace FusionRpg.Core.Overlay;

/// <summary>
/// Which reviewed PVZ menu screen is open right now. A **closed vocabulary** the code owns and a human
/// reviews — not a derived population. v1 ships one reviewed member; widening it is an owner review
/// (the second reviewed surface is an open question, see spec-menu-anchor.md).
///
/// Decision 17: this is a *presence* signal, produced by the menu screen's own lifecycle, never by a
/// navigation call. The injector may read menu presence and attach a UI element; it never drives the
/// engine's menu state.
/// </summary>
public enum RiftMenuSurface
{
    /// <summary>No reviewed menu screen is present.</summary>
    None = 0,

    /// <summary>The PVZ main menu (<c>MainMenu</c>). The only reviewed, non-gameplay, non-pause surface.</summary>
    MainMenu = 1,
}

/// <summary>
/// The reviewed-surface policy, split from the carrier so the "which surfaces may host the affordance"
/// rule is one readable place. Pure and static — no state, so tests can assert it directly.
/// </summary>
public static class RiftMenuAnchorPolicy
{
    /// <summary>True only for a surface a human reviewed as non-gameplay and non-pause.</summary>
    public static bool IsReviewed(RiftMenuSurface surface) => surface == RiftMenuSurface.MainMenu;

    /// <summary>
    /// Surfaces deliberately **excluded by name**, each with its reason. Kept as a documented closed
    /// list so an exclusion is a recorded decision, not an omission a future reader has to re-derive.
    ///
    /// The pause menu is excluded because it sits over a **live board** — the exact hazard the
    /// in-match board gate already avoids (see <c>OverlaySwitchState.MatchActive</c>): a click target
    /// there would steal a click from a menu the player is using mid-run. It is also not a
    /// <c>MainMenu</c>, so the type-guarded presence patch cannot set it.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedByDesign = new[]
    {
        "PauseMenu (sits over a live board; the board-gate hazard)",
    };
}
