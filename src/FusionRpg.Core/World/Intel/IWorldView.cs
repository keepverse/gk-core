namespace FusionRpg.Core.World.Intel;

/// <summary>
/// The world as one faction knows it (spec-world-intel.md §On the wire).
///
/// After this exists, **nothing outside the engine reads `WorldState` directly** — not a policy, not
/// a projection. That is the whole guarantee: an AI cannot accidentally consult the truth if it has
/// no way to ask for it, and a leak becomes a compile error rather than a subtle advantage nobody
/// notices for six months.
///
/// Three things are public knowledge and always answered in full: which factions exist, the sector
/// ids, and the lanes between them. You can see that six places exist and how the roads join them;
/// you just do not know what is in them. Hiding the graph itself would make the map unreadable.
/// </summary>
public interface IWorldView
{
    string FactionId { get; }
    int CurrentTurn { get; }

    /// <summary>Public: who is playing.</summary>
    IReadOnlyList<WorldFaction> Factions { get; }

    /// <summary>Public: the shape of the map, in ordinal order.</summary>
    IReadOnlyList<string> SectorIds { get; }

    /// <summary>
    /// Public in shape, believed in state: where every road goes, how long and how wide, but a lane
    /// with neither end in sight always reads <see cref="LaneState.Open"/> whether it is or not.
    /// You find out a bridge is down by going to look at it.
    /// </summary>
    IReadOnlyList<WorldLane> Lanes { get; }

    /// <summary>Your own forces, in full. You know what you brought.</summary>
    IReadOnlyList<WorldEntity> OwnForces { get; }

    /// <summary>Null when this faction has never seen the sector — an absent snapshot *is* "unknown".</summary>
    IntelSnapshot? Believed(string sectorId);

    /// <summary>The derived ladder: <c>Watched</c>, <c>Scouted</c>, <c>Rumored</c> or <c>Unknown</c>.</summary>
    IntelState StateOf(string sectorId);

    /// <summary>How many turns stale the belief is; zero when it was seen this turn.</summary>
    int AgeOf(string sectorId);

    /// <summary>
    /// What this sector's <c>LoamStock</c> actually is, if this faction owns it — self-knowledge of
    /// your own resource, not fog (spec-loam-ai-survival.md). "Only the owner" (loam-model) still
    /// holds: this answers zero for anyone else's ground, and it is a live read of your own current
    /// holding rather than a remembered snapshot, because <see cref="IntelSnapshot"/> is deliberately
    /// never where stock lives — not even for the owner.
    /// </summary>
    long OwnLoamStock(string sectorId);

    /// <summary>
    /// Where this faction's own last order sent an entity, or null if it gave none.
    ///
    /// <para><b>Self-knowledge, not fog and not new state.</b> It sits beside
    /// <see cref="OwnForces"/> ("you know what you brought") for the same reason: a commander knows
    /// what it ordered. The answer is derived from <c>rpg_world_commands</c>, which <i>is</i> the save
    /// — commands are never trimmed — so nothing is stored to support it.</para>
    ///
    /// <para><b>It cannot affect replay.</b> `IFactionPolicy`'s own contract is that replay reads the
    /// command log and never re-runs a policy, so no policy input reaches a replayed hash by any
    /// path. This is the same property that lets Zomboss's brain be rewritten without invalidating a
    /// save (spec-ai-commander.md §Momentum, amended 2026-08-31).</para>
    /// </summary>
    string? LastOrderedDestination(string entityId);
}

/// <summary>
/// The real implementation: answers from what the faction believes, never from what is true.
///
/// Visibility is recomputed here rather than stored on the snapshot, because "can I see it *now*"
/// changes the moment a legion moves, while "when did I last see it" does not.
/// </summary>
public sealed class BelievedWorldView : IWorldView
{
    readonly WorldState _world;
    readonly FactionIntel _intel;
    readonly IReadOnlyDictionary<string, SectorSight> _sight;
    readonly IReadOnlyDictionary<string, string> _lastOrdered;
    static readonly Dictionary<string, string> EmptyOrders = new(StringComparer.Ordinal);

    /// <param name="lastOrderedDestinations">
    /// Entity id → the sector this faction's previous-turn order sent it to. Supplied by the caller
    /// that owns the command log; empty when unknown, which simply disables momentum rather than
    /// changing any other answer.
    /// </param>
    public BelievedWorldView(
        WorldState world,
        string factionId,
        IReadOnlyDictionary<string, string>? lastOrderedDestinations = null)
    {
        _world = world;
        FactionId = factionId;
        _lastOrdered = lastOrderedDestinations ?? EmptyOrders;
        _intel = world.Intel.FirstOrDefault(i => string.Equals(i.FactionId, factionId, StringComparison.Ordinal))
                 ?? new FactionIntel { FactionId = factionId };
        _sight = Visibility.SeenBy(world, factionId);

        SectorIds = world.Sectors.Select(s => s.SectorId).ToList();
        OwnForces = world.Entities
            .Where(e => string.Equals(e.OwnerFactionId, factionId, StringComparison.Ordinal))
            .ToList();

        // The road is drawn on the map — where it goes, how long, how wide. Whether it is still
        // passable is not: that is learned by looking. A lane with neither end in sight reads open,
        // so a faction routes over it confidently and finds out the hard way, which is what fog is
        // supposed to feel like. Handing back the truth here would be the quietest possible leak.
        Lanes = world.Lanes
            .Select(lane => SeesNow(lane.FromSectorId) || SeesNow(lane.ToSectorId)
                ? lane
                : lane with { State = LaneState.Open })
            .ToList();
    }

    public string? LastOrderedDestination(string entityId) =>
        _lastOrdered.TryGetValue(entityId, out var dest) ? dest : null;

    public string FactionId { get; }
    public int CurrentTurn => _world.CurrentTurn;
    public IReadOnlyList<WorldFaction> Factions => _world.Factions;
    public IReadOnlyList<string> SectorIds { get; }

    /// <summary>
    /// Public in shape, believed in state — see the constructor. A lane you cannot see reads open.
    /// </summary>
    public IReadOnlyList<WorldLane> Lanes { get; }
    public IReadOnlyList<WorldEntity> OwnForces { get; }

    public IntelSnapshot? Believed(string sectorId) => _intel.Of(sectorId);

    public IntelState StateOf(string sectorId) =>
        IntelLadder.StateOf(Believed(sectorId), _world.CurrentTurn, SeesNow(sectorId));

    public int AgeOf(string sectorId) =>
        Believed(sectorId) is { } snapshot ? IntelLadder.AgeOf(snapshot, _world.CurrentTurn) : 0;

    public long OwnLoamStock(string sectorId)
    {
        var sector = _world.Sectors.FirstOrDefault(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal));
        return sector is not null && string.Equals(sector.OwnerFactionId, FactionId, StringComparison.Ordinal)
            ? sector.LoamStock
            : 0;
    }

    bool SeesNow(string sectorId) =>
        _sight.TryGetValue(sectorId, out var level) && level != SectorSight.None;
}
