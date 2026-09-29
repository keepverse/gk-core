namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// One entity's facts, as the leaves need them. A flat readonly struct so the whole predicate path
/// stays allocation-free — no dictionaries, no strings compared per hit, no I/O.
/// </summary>
/// <param name="Side">0 plant · 1 zombie · 2 bullet. An int, not a string, for the same reason.</param>
/// <param name="TypeId">Game type id.</param>
/// <param name="HpMilli">Current HP as per-mille of MaxHp, so the leaf never divides.</param>
/// <param name="ElementId">Element ordinal; -1 when the entity has none.</param>
/// <param name="Row">Board row, -1 when off-board.</param>
/// <param name="Col">Board column, -1 when off-board.</param>
/// <param name="IsMindControlled">Charm state.</param>
/// <param name="IsKiller">True when this entity dealt the killing blow in the current event.</param>
/// <param name="StatusMask">Bitmask of active statuses; the compiler interns status ids to bits.</param>
/// <param name="Stock0Qty">Quantity for interned stock slot 0 (<see cref="LeafId.HoldsStock"/>,
/// `P0.4`, 2026-08-28). Four named slots, not a dictionary/array — flat and allocation-free like
/// every other field here, and bounded by <see cref="PredicateCompiler.MaxNodes"/>: a 16-node tree
/// cannot author more than a handful of distinct `holdsStock` leaves, so four is generous, not
/// arbitrary. The interned index (stockId → 0-3) is resolved once at compile time, mirroring how
/// <see cref="StatusMask"/> interns status ids to bits.</param>
/// <param name="Stock1Qty">Interned stock slot 1.</param>
/// <param name="Stock2Qty">Interned stock slot 2.</param>
/// <param name="Stock3Qty">Interned stock slot 3.</param>
/// <param name="Band">`event-deck` D3.7 (<see cref="LeafId.BandIs"/>): a raw ordinal, e.g. the room's
/// `DangerBand` when this is the `Target` side of an event's facts. 0 when the caller has none to
/// report — the same harmless default <see cref="Stock0Qty"/> already uses for a non-event context.</param>
/// <param name="RoomKind">D3.7 (<see cref="LeafId.RoomKindIs"/>): a raw room-kind ordinal, `Target` side.</param>
/// <param name="HaulCount">D3.7 (<see cref="LeafId.HaulAtLeast"/>): occupied pack cells, `Self` side —
/// the party's own count, populated once by whoever builds the facts (`loot-pack`'s future job; 0 is a
/// legitimate "no pack yet" reading, never a sentinel).</param>
/// <param name="DownedCount">D3.7 (<see cref="LeafId.PartyDownedCount"/>): standing-party downed
/// members, `Self` side.</param>
public readonly record struct EntityFacts(
    int Side,
    int TypeId,
    int HpMilli,
    int ElementId,
    int Row,
    int Col,
    bool IsMindControlled,
    bool IsKiller,
    ulong StatusMask,
    int Stock0Qty = 0,
    int Stock1Qty = 0,
    int Stock2Qty = 0,
    int Stock3Qty = 0,
    int Band = 0,
    int RoomKind = 0,
    int HaulCount = 0,
    int DownedCount = 0);

/// <summary>
/// The narrow, readonly window a compiled predicate evaluates against: the bound actor and the other
/// entity in the event. The module never reaches into <c>StatusRuntime</c> or the board itself —
/// whoever builds this struct does that once, not once per leaf.
///
/// <para>Instrumentable on purpose: <see cref="Reads"/> counts fact accesses, which is how the
/// short-circuit test proves `And(false, expensive)` never touches the second leaf.</para>
/// </summary>
public struct FactReader
{
    readonly EntityFacts _self;
    readonly EntityFacts _target;

    public FactReader(EntityFacts self, EntityFacts target)
    {
        _self = self;
        _target = target;
        Reads = 0;
    }

    /// <summary>Fact accesses so far. Test instrumentation; no branch depends on it.</summary>
    public int Reads { get; private set; }

    EntityFacts Pick(Subject subject)
    {
        Reads++;
        return subject == Subject.Self ? _self : _target;
    }

    public int Side(Subject s) => Pick(s).Side;
    public int TypeId(Subject s) => Pick(s).TypeId;
    public int HpMilli(Subject s) => Pick(s).HpMilli;
    public int ElementId(Subject s) => Pick(s).ElementId;
    public int Row(Subject s) => Pick(s).Row;
    public int Col(Subject s) => Pick(s).Col;
    public bool IsMindControlled(Subject s) => Pick(s).IsMindControlled;
    public bool IsKiller(Subject s) => Pick(s).IsKiller;

    /// <summary>Status test by interned bit — never by string comparison.</summary>
    public bool HasStatusBit(Subject s, int bit) =>
        bit >= 0 && bit < 64 && (Pick(s).StatusMask & (1UL << bit)) != 0;

    /// <summary>The `holdsStock` reader — a narrow, readonly probe over an already-resolved
    /// quantity, following <see cref="HpMilli"/>'s shape exactly (spec-predicate-tree.md: "FactReader
    /// gains a narrow, readonly stock probe following HpMilli's shape"). <paramref name="stockIndex"/>
    /// is the COMPILE-TIME interned slot (0-3, mirroring <see cref="HasStatusBit"/>'s bit-interning);
    /// an out-of-range index (an unresolvable stockId, or the 5th distinct one authored) reads as 0
    /// rather than throwing — the same "false, not throwing" posture position leaves already use with
    /// no board (spec-usability-conditions.md §5).</summary>
    public int StockQty(Subject s, int stockIndex)
    {
        var facts = Pick(s);
        return stockIndex switch
        {
            0 => facts.Stock0Qty,
            1 => facts.Stock1Qty,
            2 => facts.Stock2Qty,
            3 => facts.Stock3Qty,
            _ => 0,
        };
    }

    /// <summary>D3.7 (<see cref="LeafId.BandIs"/>): the raw band ordinal.</summary>
    public int Band(Subject s) => Pick(s).Band;

    /// <summary>D3.7 (<see cref="LeafId.RoomKindIs"/>): the raw room-kind ordinal.</summary>
    public int RoomKind(Subject s) => Pick(s).RoomKind;

    /// <summary>D3.7 (<see cref="LeafId.HaulAtLeast"/>): occupied pack cells.</summary>
    public int HaulCount(Subject s) => Pick(s).HaulCount;

    /// <summary>D3.7 (<see cref="LeafId.PartyDownedCount"/>): standing-party downed member count.</summary>
    public int DownedCount(Subject s) => Pick(s).DownedCount;
}
