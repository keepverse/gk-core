using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-economy` (spec-siege-economy.md): board income by occupation (never by durable
/// world possession — §2b's own rule, enforced here by omission: this file reads no world-layer
/// possession field at all), a battle-scoped depot that reconciles spend-only, and F11's
/// capture-transfers-the-stockpile rule.
///
/// <para>Deliberately decoupled from <see cref="Board.BoardState"/>'s live occupant tracking and from
/// <see cref="WorldState"/>'s turn engine, the same scoping `siege-objective`'s own
/// <see cref="SiegeCombatant"/> record already established: a caller supplies plain data
/// (<see cref="BoardNode"/>/<see cref="BoardOccupant"/>), and wiring a real board/turn phase into that
/// shape is `siege-resolver`'s job. Named, un-started scope this module does not build: the
/// exhausted-node "reports it once" EVENT (only the yields-nothing PREDICATE is built here — an event
/// needs the same kind of transition-tracking `ScopeMembershipEvents` already built for a different
/// vocabulary, and inventing a second one here was not attempted under this session's budget), and the
/// `SlotOutcome`→"which stockpile does this specific capture take" mapping (a sector can host more than
/// one Storage structure; which one a given capture attaches to is a real open question the spec's own
/// worked snippet does not resolve either — <see cref="RecoveredOnCapture"/> is the pure numeric core,
/// left for the caller to invoke with whatever amount it decides is "this capture's stockpile").
/// </para>
/// </summary>
public readonly record struct BoardNode(GridPos Cell, bool Exhausted);

/// <summary>Who is standing on a cell, for board-income purposes. <see cref="Kind"/> gates out
/// structures (`combatant-kind`'s rule: a structure does not garrison another structure).</summary>
public readonly record struct BoardOccupant(GridPos Cell, string Side, CombatantKind Kind);

/// <summary>One node's income this round, already resolved to a side.</summary>
public sealed record BoardYield(GridPos Cell, string Side, long LoamAmount, long IronworkAmount);

public static class BoardEconomy
{
    /// <summary>
    /// What every occupied, non-exhausted node yields this round — ordinal by cell index (§1: "two
    /// nodes yielding into the same depot on the same round always do so in the same order... it is a
    /// sum"), never board-iteration order or dictionary order.
    /// </summary>
    public static IReadOnlyList<BoardYield> YieldsFor(
        GridSpec spec, IReadOnlyList<BoardNode> nodes, IReadOnlyList<BoardOccupant> occupants,
        long loamPerRound, long ironworkPerRound)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        if (nodes is null) throw new ArgumentNullException(nameof(nodes));
        if (occupants is null) throw new ArgumentNullException(nameof(occupants));
        if (loamPerRound < 0) throw new ArgumentOutOfRangeException(nameof(loamPerRound));
        if (ironworkPerRound < 0) throw new ArgumentOutOfRangeException(nameof(ironworkPerRound));

        var occupantByCell = new Dictionary<GridPos, BoardOccupant>();
        foreach (var occ in occupants) occupantByCell[occ.Cell] = occ; // one occupant per cell, by construction

        var results = new List<BoardYield>();
        foreach (var node in nodes.OrderBy(n => spec.IndexOf(n.Cell)))
        {
            if (node.Exhausted) continue;
            if (!occupantByCell.TryGetValue(node.Cell, out var occupant)) continue;
            if (occupant.Kind != CombatantKind.Animate) continue; // a structure does not garrison another
            results.Add(new BoardYield(node.Cell, occupant.Side, loamPerRound, ironworkPerRound));
        }

        return results;
    }

    /// <summary>`structure-state`'s own tunable, reused rather than duplicated — a node's per-harvest
    /// depletion is the SAME mechanism `StructurePolicy.DepletionPerHarvestMilli` already names, applied
    /// on a harvest-not-time trigger instead of the turn-phase caller `structure-state` left unwired.
    /// No clamp: `StructurePolicy.IsExhausted` is the terminal check regardless of exact magnitude, the
    /// same lack-of-clamp precedent `WorldSlot.SlotDepletionMilli`'s own field already carries.</summary>
    public static int AdvanceDepletionMilli(int slotDepletionMilli, bool yieldedThisRound) =>
        yieldedThisRound
            ? checked(slotDepletionMilli + StructurePolicy.DepletionPerHarvestMilli)
            : slotDepletionMilli;
}

/// <summary>
/// One side's spendable budget inside one siege (§2). Reconciled spend-only: <see cref="LoamSpentFromWorld"/>/
/// <see cref="IronworkSpentFromWorld"/> are the ONLY figures that cross back to
/// <see cref="WorldSector"/> at battle end — board income, once spent, evaporates rather than reducing
/// world stock, which is what makes it battle-scoped rather than a silent mint.
///
/// <para>Internally tracks the board-earned and world-seeded portions SEPARATELY (never just one
/// running total) so spending can draw board income first, world stock second (§2: "a well-run siege
/// that lives off the land costs the empire nothing") — <see cref="Loam"/>/<see cref="Ironwork"/>
/// expose only the sum, matching the spec's own public shape.</para>
/// </summary>
public sealed class SiegeDepot
{
    readonly long _boardLoam;
    readonly long _worldSeedLoam;
    readonly long _boardIronwork;
    readonly long _worldSeedIronwork;
    readonly long _boardRubble;
    readonly long _worldSeedRubble;

    public long Loam => checked(_boardLoam + _worldSeedLoam);
    public long Ironwork => checked(_boardIronwork + _worldSeedIronwork);

    /// <summary>base-defense `siege-construction` (2026-09-06): the bulk material `Built` also spends
    /// (decisions 16/17/34) — found never wired into this class at all despite the module's own
    /// docs assuming it (zero references to a Rubble field anywhere in this file before this change).
    /// Same shape as <see cref="Ironwork"/> exactly: board-earned first, world-seeded second.</summary>
    public long Rubble => checked(_boardRubble + _worldSeedRubble);
    public long LoamSpentFromWorld { get; }
    public long IronworkSpentFromWorld { get; }
    public long RubbleSpentFromWorld { get; }

    SiegeDepot(long boardLoam, long worldSeedLoam, long boardIronwork, long worldSeedIronwork,
        long boardRubble, long worldSeedRubble,
        long loamSpentFromWorld, long ironworkSpentFromWorld, long rubbleSpentFromWorld)
    {
        if (boardLoam < 0) throw new ArgumentOutOfRangeException(nameof(boardLoam));
        if (worldSeedLoam < 0) throw new ArgumentOutOfRangeException(nameof(worldSeedLoam));
        if (boardIronwork < 0) throw new ArgumentOutOfRangeException(nameof(boardIronwork));
        if (worldSeedIronwork < 0) throw new ArgumentOutOfRangeException(nameof(worldSeedIronwork));
        if (boardRubble < 0) throw new ArgumentOutOfRangeException(nameof(boardRubble));
        if (worldSeedRubble < 0) throw new ArgumentOutOfRangeException(nameof(worldSeedRubble));
        _boardLoam = boardLoam;
        _worldSeedLoam = worldSeedLoam;
        _boardIronwork = boardIronwork;
        _worldSeedIronwork = worldSeedIronwork;
        _boardRubble = boardRubble;
        _worldSeedRubble = worldSeedRubble;
        LoamSpentFromWorld = loamSpentFromWorld;
        IronworkSpentFromWorld = ironworkSpentFromWorld;
        RubbleSpentFromWorld = rubbleSpentFromWorld;
    }

    /// <summary>The defender's seed: a FRACTION (`depotSeedMilli`) of the sector's own stockpile is
    /// reachable during the siege — never the whole stock, and never the attacker's number.</summary>
    public static SiegeDepot SeedFromSectorStock(long sectorLoam, long sectorIronwork, long sectorRubble, int depotSeedMilli)
    {
        if (sectorLoam < 0) throw new ArgumentOutOfRangeException(nameof(sectorLoam));
        if (sectorIronwork < 0) throw new ArgumentOutOfRangeException(nameof(sectorIronwork));
        if (sectorRubble < 0) throw new ArgumentOutOfRangeException(nameof(sectorRubble));
        if (depotSeedMilli is <= 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(depotSeedMilli));
        return new SiegeDepot(0, checked(sectorLoam * depotSeedMilli / 1000), 0, checked(sectorIronwork * depotSeedMilli / 1000),
            0, checked(sectorRubble * depotSeedMilli / 1000), 0, 0, 0);
    }

    /// <summary>The attacker's seed: whatever the legion carried in, unscaled — finite, and never
    /// ironwork or rubble (decision 27's whole reason for four acquisition paths: an attacker has no
    /// empire stockpile to draw from at all).</summary>
    public static SiegeDepot SeedFromCarriedLoam(long carriedLoam)
    {
        if (carriedLoam < 0) throw new ArgumentOutOfRangeException(nameof(carriedLoam));
        return new SiegeDepot(0, carriedLoam, 0, 0, 0, 0, 0, 0, 0);
    }

    public SiegeDepot CreditLoam(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        return new SiegeDepot(checked(_boardLoam + amount), _worldSeedLoam, _boardIronwork, _worldSeedIronwork, _boardRubble, _worldSeedRubble,
            LoamSpentFromWorld, IronworkSpentFromWorld, RubbleSpentFromWorld);
    }

    public SiegeDepot CreditIronwork(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        return new SiegeDepot(_boardLoam, _worldSeedLoam, checked(_boardIronwork + amount), _worldSeedIronwork, _boardRubble, _worldSeedRubble,
            LoamSpentFromWorld, IronworkSpentFromWorld, RubbleSpentFromWorld);
    }

    public SiegeDepot CreditRubble(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        return new SiegeDepot(_boardLoam, _worldSeedLoam, _boardIronwork, _worldSeedIronwork, checked(_boardRubble + amount), _worldSeedRubble,
            LoamSpentFromWorld, IronworkSpentFromWorld, RubbleSpentFromWorld);
    }

    public SiegeDepot SpendLoam(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount > Loam) throw new InvalidOperationException($"SiegeDepot: cannot spend {amount} loam against a balance of {Loam}.");
        var fromBoard = Math.Min(_boardLoam, amount);
        var fromWorld = amount - fromBoard;
        return new SiegeDepot(checked(_boardLoam - fromBoard), checked(_worldSeedLoam - fromWorld), _boardIronwork, _worldSeedIronwork,
            _boardRubble, _worldSeedRubble, checked(LoamSpentFromWorld + fromWorld), IronworkSpentFromWorld, RubbleSpentFromWorld);
    }

    public SiegeDepot SpendIronwork(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount > Ironwork) throw new InvalidOperationException($"SiegeDepot: cannot spend {amount} ironwork against a balance of {Ironwork}.");
        var fromBoard = Math.Min(_boardIronwork, amount);
        var fromWorld = amount - fromBoard;
        return new SiegeDepot(_boardLoam, _worldSeedLoam, checked(_boardIronwork - fromBoard), checked(_worldSeedIronwork - fromWorld),
            _boardRubble, _worldSeedRubble, LoamSpentFromWorld, checked(IronworkSpentFromWorld + fromWorld), RubbleSpentFromWorld);
    }

    public SiegeDepot SpendRubble(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount > Rubble) throw new InvalidOperationException($"SiegeDepot: cannot spend {amount} rubble against a balance of {Rubble}.");
        var fromBoard = Math.Min(_boardRubble, amount);
        var fromWorld = amount - fromBoard;
        return new SiegeDepot(_boardLoam, _worldSeedLoam, _boardIronwork, _worldSeedIronwork,
            checked(_boardRubble - fromBoard), checked(_worldSeedRubble - fromWorld),
            LoamSpentFromWorld, IronworkSpentFromWorld, checked(RubbleSpentFromWorld + fromWorld));
    }

    /// <summary>
    /// Audit F11: taking a Storage structure takes what is in it, proportional to SURVIVING HP —
    /// burning a granary to the ground destroys the grain; taking it intact takes the lot. Guards
    /// `maxHp &lt;= 0` (an indestructible structure, a legal shipped value on all four existing rows)
    /// by skipping the HP proportion entirely rather than dividing by zero — indestructible means the HP
    /// concept does not apply, so the full amount (before `captureRecoveryMilli`'s own scaling) is
    /// recovered. Same discipline as `structure-state.RepairCost`: widen before multiplying, two
    /// divides (by `maxHp`, then by 1000), each exactly once, `checked` throughout.
    /// </summary>
    public static long RecoveredOnCapture(long stored, long structureHp, long maxHp, int captureRecoveryMilli)
    {
        if (stored < 0) throw new ArgumentOutOfRangeException(nameof(stored));
        if (captureRecoveryMilli < 0) throw new ArgumentOutOfRangeException(nameof(captureRecoveryMilli));

        if (maxHp <= 0) return checked(stored * captureRecoveryMilli / 1000);

        var hp = Math.Max(0, structureHp);
        return checked(stored * hp * captureRecoveryMilli / maxHp / 1000);
    }
}
