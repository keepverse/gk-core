using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World.Movement;

namespace FusionRpg.Core.World.Topology;

/// <summary>One traversable step between two sectors, and what it costs to walk it.</summary>
public readonly record struct LaneStepEdge(string FromSectorId, string ToSectorId, int Cost);

/// <summary>
/// Which question the graph is answering (spec-ai-commander.md §Two graphs, and not confusing them).
///
/// The two differ by exactly one rule and are never interchangeable. A deep rift and a temporal
/// current carry no supply and *are* marchable, so building fear or reach on the supply lens would
/// leave an enemy two days away invisible because the road between you carries no grain — which is
/// not how being attacked works.
///
/// A lens parameter rather than two builders: the alternative is a second copy of this file
/// differing by one predicate, which is the version that silently drifts apart.
/// </summary>
public enum LaneLens
{
    /// <summary>Could this lane hold an empire together? Connectivity, reconnection cost, supply.</summary>
    Supply,

    /// <summary>Can an army put its feet on it? Threat spread and reach.</summary>
    March
}

/// <summary>
/// The lane network as a plain graph, scoped to a set of sectors (spec-world-topology.md).
///
/// The scoping is what makes one implementation answer three questions: pass nothing for the map's
/// own topology, a faction's holdings for that empire's internal connectivity, or those holdings
/// minus a hostile-held sector for what a zone of control actually costs.
///
/// **This is the supply lens, not the march lens.** An edge exists where a lane could hold an empire
/// together, so a deep rift and a temporal current — which carry no supply — are absent even though an
/// army can walk down both. March distance is a different question with a different answer, and
/// `ai-commander`'s `ReachMap` asks it separately over <see cref="LaneCost"/>.
///
/// Everything is built in ordinal id order. Tarjan and Floyd–Warshall both depend on traversal
/// order — two orders give different-but-equally-valid answers, and a replay cannot survive that.
/// </summary>
public sealed class LaneGraph
{
    readonly Dictionary<string, int> _index;

    LaneGraph(IReadOnlyList<string> sectors, IReadOnlyList<LaneStepEdge> edges, Dictionary<string, int> index)
    {
        Sectors = sectors;
        Edges = edges;
        _index = index;
    }

    /// <summary>In ordinal id order.</summary>
    public IReadOnlyList<string> Sectors { get; }

    /// <summary>In the order their lanes appear, which is ordinal by lane id.</summary>
    public IReadOnlyList<LaneStepEdge> Edges { get; }

    public int Count => Sectors.Count;

    public bool Contains(string sectorId) => _index.ContainsKey(sectorId);

    public int IndexOf(string sectorId) => _index.TryGetValue(sectorId, out var i)
        ? i
        : throw new KeyNotFoundException($"Sector '{sectorId}' is outside this graph's filter.");

    /// <param name="include">Null means every sector; otherwise only these.</param>
    public static LaneGraph Build(
        WorldState world, IReadOnlySet<string>? include = null, LaneLens lens = LaneLens.Supply) =>
        Build(
            world.Sectors.Select(s => s.SectorId).ToList(),
            world.Lanes,
            sectorId => world.Sectors
                .FirstOrDefault(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal))?.Climate,
            include,
            lens);

    /// <summary>
    /// The graph from ids and lanes alone, plus a way to price them.
    ///
    /// Split out because a faction policy may not touch <c>WorldState</c> — it holds beliefs, not
    /// the truth — and this is everything the graph actually reads. The truth-side overload above is
    /// the same call with the world supplying all three.
    /// </summary>
    /// <param name="bannerElement">
    /// Whose march this is, for ley pricing. Null — the default — is the map's own topology: a ley
    /// discount belongs to a particular legion, not to the ground. `ReachMap` is the one caller that
    /// has a legion to name.
    /// </param>
    public static LaneGraph Build(
        IReadOnlyList<string> sectorIds, IReadOnlyList<WorldLane> lanes,
        Func<string, ElementTypeId?> climateOf, IReadOnlySet<string>? include = null,
        LaneLens lens = LaneLens.Supply, ElementTypeId? bannerElement = null)
    {
        var sectors = sectorIds
            .Where(id => include is null || include.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sectors.Count; i++) index[sectors[i]] = i;

        var edges = new List<LaneStepEdge>();
        foreach (var lane in lanes.OrderBy(l => l.LaneId, StringComparer.Ordinal))
        {
            if (!IsTraversable(lane, lens)) continue;
            if (!index.ContainsKey(lane.FromSectorId) || !index.ContainsKey(lane.ToSectorId)) continue;

            var cost = LaneCost.For(lane, bannerElement, climateOf);

            edges.Add(new LaneStepEdge(lane.FromSectorId, lane.ToSectorId, cost));

            // Directed lanes are walked one way only. No shipped lane type is both one-way and
            // supply-carrying, so this branch does not fire today — it is here because silently
            // walking a directed supply lane backwards would be a very quiet bug.
            if (!LaneTypeCatalog.Get(lane.TypeId).OneWay)
                edges.Add(new LaneStepEdge(lane.ToSectorId, lane.FromSectorId, cost));
        }

        return new LaneGraph(sectors, edges, index);
    }

    static bool IsTraversable(WorldLane lane, LaneLens lens)
    {
        // Severed is impassable to both, and a shut gate bars both — these are the same refusals
        // `MarchResolver` makes, so a route planned through one is a route the engine would drop.
        if (lane.State == LaneState.Severed) return false;

        var type = LaneTypeCatalog.Get(lane.TypeId);
        if (type.Gated && lane.GateKeyId != null) return false;

        // The one difference. Grain needs a road; an army only needs somewhere to put its feet.
        return lens == LaneLens.March || type.CarriesSupply;
    }
}
