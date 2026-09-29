using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Siege;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Battle.Board;

/// <summary>
/// base-defense `siege-construction` (decision 27, 2026-09-06): one battle's worth of everything
/// <c>structure.place</c>'s executor needs about the live siege board, built ONCE by whoever assembles
/// the board (<c>DistrictAssaultResolver</c>) and threaded into <see cref="BattleEffectHost"/> —
/// closing <see cref="ConstructionPlacement"/>'s own named gap ("the tactical board has no mapping
/// from a <see cref="GridPos"/> cell to a world-layer <c>SlotKind</c> today — that data flow is
/// `siege-resolver`'s job once it assembles a real board") for the one caller that finally has the
/// world-side data (<c>WorldSector.Slots</c>) to build it.
/// </summary>
public sealed class ConstructionBoardContext
{
    public BoardState Board { get; }
    public int BoardSide { get; }
    public int CoreSideMilli { get; }
    public int RampartThickness { get; }

    /// <summary>Board cell → the world slot that sits there, keyed by <see cref="DistrictLayout.CellForSlot"/>'s
    /// own deterministic mapping — the SAME cell <see cref="DistrictAssaultResolver"/>'s own
    /// `PlaceStructures` already places an existing structure at. Every legal construction target is one
    /// of these cells; there is no "arbitrary open cell" placement (a `WorldSlot` is the only thing the
    /// world layer can persist a structure onto).</summary>
    public IReadOnlyDictionary<GridPos, (int SlotIndex, SlotKind Kind)> SlotByCell { get; }

    /// <summary>What `structure.place` actually did this battle, in firing order — read back by
    /// <see cref="DistrictAssaultResolver"/> once <c>BattleEngine.Resolve</c> returns, to build the
    /// <see cref="World.Turn.SlotOutcome.StructurePlaced"/> entries that cross the seam. A plain list
    /// the sink appends to, the same shape <c>BattleEffectHost.LastApplied</c> already uses for FA10
    /// deltas.</summary>
    public List<StructurePlacementRecord> Placed { get; } = new();

    /// <summary>
    /// `Built`'s own world-scoped budget for THIS battle only — seeded once from the sector's real
    /// current stock by whoever builds this context (<c>DistrictAssaultResolver</c>), spent down as
    /// <see cref="SpendBuilt"/> fires. Deliberately the sector's RAW stock, not a live
    /// <see cref="BoardEconomy.SiegeDepot"/> instance: <c>SiegeDepot</c> is a pure, immutable value
    /// type with no live per-battle tracker anywhere in production today (confirmed by grep — its own
    /// `Seed*`/`Credit*`/`Spend*` methods all return a NEW instance; nothing holds one across a real
    /// battle) — threading ITS OWN board-earned/world-seed split through here is real, separate,
    /// un-started work, named rather than silently built halfway. This is the honest v1: the SAME
    /// number `ConstructionCost`'s own doc comment always assumed a caller would supply.
    /// </summary>
    public long RemainingRubble { get; private set; }
    public long RemainingIronwork { get; private set; }

    public ConstructionBoardContext(
        BoardState board, int boardSide, int coreSideMilli, int rampartThickness,
        IReadOnlyDictionary<GridPos, (int SlotIndex, SlotKind Kind)> slotByCell,
        long sectorRubble = 0, long sectorIronwork = 0)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        BoardSide = boardSide;
        CoreSideMilli = coreSideMilli;
        RampartThickness = rampartThickness;
        SlotByCell = slotByCell ?? throw new ArgumentNullException(nameof(slotByCell));
        if (sectorRubble < 0) throw new ArgumentOutOfRangeException(nameof(sectorRubble));
        if (sectorIronwork < 0) throw new ArgumentOutOfRangeException(nameof(sectorIronwork));
        RemainingRubble = sectorRubble;
        RemainingIronwork = sectorIronwork;
    }

    /// <summary>True when <paramref name="def"/>'s own `Built` cost fits the REMAINING battle
    /// budget — reuses <see cref="ConstructionCost.CanAffordBuilt"/> verbatim (Law 1), never a second
    /// affordability rule.</summary>
    public bool CanAffordBuilt(StructureDef def) => ConstructionCost.CanAffordBuilt(RemainingRubble, RemainingIronwork, def);

    /// <summary>Debits <paramref name="def"/>'s own `Built` cost from the remaining battle budget —
    /// reuses <see cref="ConstructionCost.SpendBuilt"/> verbatim (Law 1). Throws exactly when that
    /// pure function would (spending past the balance is a caller bug, matching `SiegeDepot`'s own
    /// "never a silent floor" precedent), leaving the balance untouched on failure.</summary>
    public void SpendBuilt(StructureDef def)
    {
        var (rubble, ironwork) = ConstructionCost.SpendBuilt(RemainingRubble, RemainingIronwork, def);
        RemainingRubble = rubble;
        RemainingIronwork = ironwork;
    }
}

/// <summary>One successful `structure.place` firing this battle.</summary>
public sealed record StructurePlacementRecord(int SlotIndex, string StructureId, bool Instant);
