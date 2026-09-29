namespace FusionRpg.Core.Overlay;

/// <summary>
/// Names for the **presentation** commands the web FE may send to the host over the server→injector
/// command seam.
///
/// These ride <c>CheatCommandRunner</c>'s drain, whose name is a misnomer: that drain already carries
/// refresh/presentation commands (<c>reload-stats</c>, <c>pvz.stats.reload</c>, <c>power.index.reload</c>,
/// …), not only cheats. The class name is **not** renamed by this program — that would touch every
/// existing caller — so this constant is where the vocabulary is named correctly instead of dropping a
/// bare literal into the drain.
///
/// <see cref="Hide"/> closes the overlay window **without acknowledging any story**: the story ledger is
/// written only by the FE's own ack path. One Esc (or one Leave) must not burn the prologue.
/// </summary>
public static class OverlayCommandNames
{
    /// <summary>Close the overlay window. Idempotent, non-blocking, and story-neutral.</summary>
    public const string Hide = "overlay.hide";
}
