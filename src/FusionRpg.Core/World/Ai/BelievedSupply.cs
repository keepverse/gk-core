using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// The chain home as a faction *believes* it runs (spec-ai-commander.md §Believed supply).
///
/// Same rule as <see cref="SupplyGraph.ConnectedSectors"/> — walk out from your Seats over
/// supply-carrying lanes, never crossing ground held against you — asked of belief instead of truth.
/// Every input changes, and every one of them changes in the direction of over-confidence:
///
/// <list type="bullet">
/// <item><b>Ownership is remembered.</b> A sector taken from you while you were not looking is
///       still yours as far as you know, and your believed chain still runs through it. This is
///       the divergence that matters, and the one with a test.</item>
/// <item><b>Hostile forces are remembered</b>, so a zone of control that moved in unseen does not
///       bar you, and one that left still does.</item>
/// </list>
///
/// Two divergences that sound obvious are **not** available here, and both for the same reason:
/// holding a sector grants full sight of it, and supply only ever walks between sectors you hold.
/// So an unseen lane cannot be inside your own chain — every lane in it has both ends visible — and
/// a Seat cannot be merely glimpsed, because there is no way to believe you own ground you have only
/// seen from next door. Believing a cut lane is intact is a *march*-planning mistake, not a supply
/// one, and that is where <see cref="MarchGraph"/> makes it.
///
/// The result is that a faction believes it is supplied right up until it starves. That is the
/// intended behaviour and it has a test: fog you can plan around is not fog.
/// </summary>
public static class BelievedSupply
{
    /// <summary>Sectors this faction believes it can still reach from a Seat it believes it holds.</summary>
    public static IReadOnlySet<string> ConnectedSectors(IWorldView view)
    {
        bool Usable(string sectorId)
        {
            var believed = view.Believed(sectorId);
            if (believed is null) return false;   // never seen it: cannot be counting on it
            if (!string.Equals(believed.OwnerFactionId, view.FactionId, StringComparison.Ordinal)) return false;

            return !HeldAgainst(view, sectorId);
        }

        var seats = view.SectorIds
            .Where(id => Usable(id)
                         && view.Believed(id)!.Slots.Any(s => s.SlotTypeId == SlotTypeCatalog.SeatSlotTypeId))
            .ToList();

        return SupplyReach.From(seats, SupplyReach.LinksOf(view.Lanes), Usable);
    }

    /// <summary>
    /// Whether somebody hostile is remembered as standing here. The memory can be stale in both
    /// directions — an enemy that left is still feared, one that arrived unseen is not — which is
    /// the same trade every other belief-side reading makes.
    /// </summary>
    static bool HeldAgainst(IWorldView view, string sectorId)
    {
        if (view.Believed(sectorId) is not { } believed) return false;

        foreach (var force in believed.Forces)
            if (ZoneOfControl.Projects(force.Kind)
                && ZoneOfControl.IsHostile(force.OwnerFactionId, view.FactionId))
                return true;

        return false;
    }
}
