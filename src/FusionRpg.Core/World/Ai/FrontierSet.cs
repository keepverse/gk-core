using FusionRpg.Core.World.Intel;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// The edge of the empire and the edge of the map, which under fog are two different edges
/// (spec-ai-commander.md §ReachMap and the believed frontier).
///
/// **There is deliberately no "unknown neighbour" set here, and the reason is a fact about the
/// model rather than a simplification.** `Visibility` makes every sector you own an observation post
/// with a one-lane radius, so everything adjacent to your territory is at all times *at least*
/// glimpsed. A neighbour you have never laid eyes on cannot exist. The spec claimed otherwise; a
/// surviving mutant proved it could not happen, and a set that is always empty is a lie in a type.
///
/// Unknown ground is still a target — but it is a *reach* question ("what is within `ExploreTurns`")
/// rather than an adjacency one, which is how the Explore rule is written anyway. Ask
/// <see cref="ReachMap"/> and <see cref="IWorldView.Believed"/>, not this.
/// </summary>
public static class FrontierSet
{
    /// <param name="Held">Believed-yours, and touching something that is not.</param>
    /// <param name="Contested">Not yours, next to something of yours. Always seen — see above.</param>
    public readonly record struct Frontier(
        IReadOnlyList<string> Held,
        IReadOnlyList<string> Contested);

    public static Frontier Of(IWorldView view)
    {
        var graph = MarchGraph.Of(view);

        bool Mine(string sectorId) =>
            view.Believed(sectorId) is { } believed
            && string.Equals(believed.OwnerFactionId, view.FactionId, StringComparison.Ordinal);

        var held = new SortedSet<string>(StringComparer.Ordinal);
        var contested = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            if (!Mine(edge.FromSectorId)) continue;
            if (Mine(edge.ToSectorId)) continue;

            // The near side is frontier because something foreign touches it.
            held.Add(edge.FromSectorId);
            contested.Add(edge.ToSectorId);
        }

        return new Frontier(held.ToList(), contested.ToList());
    }
}
