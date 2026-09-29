using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Intel;

/// <summary>
/// Whether one report line belongs in this viewer's account of the turn — W-F1
/// (spec-world-wire.md §2, world-stage W14, fog defect C), read as three named rules rather than
/// one boolean expression, because the decision behind each is the thing a future reader needs
/// to find:
///
/// 1. Audience. A faction-scoped economy line has no sector and never did; it belongs to its
///    owner, not to everyone.
/// 2. Live sight, for a dynamic fact. Battles, marches, halts, arrivals — only shown while this
///    viewer is actually watching the ground right now (`StateOf(sectorId) == Watched`).
/// 3. Remembered sight, for a static fact. A claim, ownership changing — shown on any ground
///    this viewer has ever seen (`Believed(sectorId) is not null`), Civ VI's own rule.
///
/// 4. Nowhere in particular, but only the calendar. A `Calendar`-kind line (week/month/season)
///    carries no `Audience` and no `SectorId` and reveals no ground — shown to everyone
///    (world-map W59: found by playing this rule's own scenario. This branch previously
///    unconditionally returned `false` for *any* no-audience/no-sector entry, silently dropping
///    every calendar tick from the wire despite <c>TurnEngine.cs</c>'s own doc comment for the
///    season line stating "the season is visible in the turn report."
///    <c>WorldTurnReportFogTests</c> proved rules 1-3 in isolation but had no case for
///    this fourth one, which is why nothing caught it).
///
///    Scoped to `Calendar` specifically, not every sectorless entry: a `CommandAccepted`/
///    `CommandDropped` entry is also sectorless by construction (`TurnEngine.cs`'s admission
///    loop and every resolver's own `Drop` helper call `report.Add` with no `SectorId`/`Audience`
///    argument at all), but that per-commander accept/drop story already reaches its own viewer
///    correctly through the separate `Commands` array one level up in the turn-report projection
///    (its own <c>VisibleTo(WorldCommand, ...)</c> overload, still in `WorldEndpoints.cs`, states
///    "your own orders always") — broadening this rule to every kind would leak every other
///    commander's routine stand-fast accept/drop into `Entries` too, which is a real, separate,
///    larger fog question this task does not decide.
///
/// <para><b>Why it lives here (cross-program ask A2, owner-accepted 2026-09-21).</b> One rule, two
/// consumers (SOLID S): the turn-report projection and the world-turn notification source must show
/// a player the same lines. Moved unchanged from `WorldEndpoints.cs`; the only edits are the two
/// cross-assembly references above, which could not stay `&lt;see cref&gt;` from Core. The
/// `VisibleTo(WorldCommand, …)` overload stayed behind — this task moves the report-line rule
/// only.</para>
/// </summary>
public static class WorldReportVisibility
{
    public static bool VisibleTo(TurnReportEntry e, string? viewer, BelievedWorldView? believed)
    {
        if (believed is null || viewer is null) return true; // SIM / no viewer
        if (e.Kind == TurnReportKinds.Calendar) return true; // rule 4: the calendar, shown to everyone
        if (e.Audience is { } audience) return string.Equals(audience, viewer, StringComparison.Ordinal);
        if (e.SectorId is not { } sectorId) return false; // no audience, no ground, no calendar kind
        return IsStaticFact(e.Kind, e.Detail)
            ? believed.Believed(sectorId) is not null
            : believed.StateOf(sectorId) == IntelState.Watched;
    }

    /// <summary>
    /// The closed, named list W-F1 requires — never a prefix guess on free text beyond this. Add a
    /// detail token here only when it names a fact that stays true once you look away: a claim, or a
    /// sector's ownership/phase actually changing. Everything else defaults to dynamic — the safer
    /// direction to be wrong in, since a dynamic fact treated as static would leak stale information
    /// as if it were still happening.
    /// </summary>
    public static readonly string[] StaticFactDetailPrefixes = { "claim.", "loam.lost:" };

    public static bool IsStaticFact(string kind, string detail) =>
        kind == TurnReportKinds.Event
        && StaticFactDetailPrefixes.Any(prefix => detail.StartsWith(prefix, StringComparison.Ordinal));
}
