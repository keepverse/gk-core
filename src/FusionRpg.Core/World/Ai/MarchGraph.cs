using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Topology;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// Where an army can actually walk, as one faction believes it
/// (spec-ai-commander.md §Two graphs, and not confusing them).
///
/// This is <see cref="LaneGraph"/> under <see cref="LaneLens.March"/>, built from belief rather than
/// the truth — the whole of the module's contribution here is *which* lens and *whose* map.
///
/// Belief makes it **optimistic**: a lane with neither end in sight reads open whether it is or not,
/// so a faction routes confidently over a bridge that is down and finds out by arriving. That is the
/// same optimism believed supply has, and it is deliberate — fog you can plan around is not fog.
///
/// **A test written against `first-light` cannot tell this from the supply lens.** All six of its
/// lanes carry supply, so the two coincide there and a policy built on the wrong one would pass
/// everything we have. The distinction needs a shape chosen to expose it.
/// </summary>
public static class MarchGraph
{
    /// <summary>
    /// The believed march graph.
    ///
    /// With no <paramref name="bannerElement"/> the climate lookup is passed but never consulted:
    /// pricing falls back to the ground's own cost, because a ley discount belongs to a particular
    /// legion rather than to the map. <see cref="ReachMap"/> is the caller that has a legion to name,
    /// and there the lookup decides whether that legion *expects* the discount — which is where fog
    /// reaches into route planning.
    /// </summary>
    public static LaneGraph Of(IWorldView view, IReadOnlySet<string>? include = null,
        ElementTypeId? bannerElement = null) =>
        LaneGraph.Build(view.SectorIds, view.Lanes, ClimateOf(view), include, LaneLens.March, bannerElement);

    /// <summary>
    /// What a faction knows about a sector's climate — which is what decides, for a legion carrying a
    /// matching banner, whether it expects a ley lane's discount. A sector it has never seen has no
    /// climate as far as it knows, so it **over-prices** that march. An army plans with what it has.
    ///
    /// Consulted by per-entity routing (`ReachMap`), not by <see cref="Of"/> — see above.
    /// </summary>
    public static Func<string, ElementTypeId?> ClimateOf(IWorldView view) =>
        sectorId => view.Believed(sectorId)?.Climate;
}
