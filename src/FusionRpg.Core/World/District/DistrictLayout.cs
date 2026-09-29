using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;

namespace FusionRpg.Core.World.District;

/// <summary>
/// The district's three zones (spec-district-layout.md §3). An ordinal — the model behind it lives
/// in <see cref="GridSpec"/>'s own <see cref="CellTerrain"/>, this is a coarser, human-facing label
/// over the same cells, useful for tests and for anything that wants to reason about "the wall"
/// without re-deriving geometry.
/// </summary>
public enum DistrictZone
{
    /// <summary>Outside the wall. Where a besieger deploys and where obstacles are dug.</summary>
    Approach,
    /// <summary>The wall line itself — Blocking terrain except at gates.</summary>
    Rampart,
    /// <summary>Inside. Slots live here; the win condition stands here (decision 26).</summary>
    Core
}

/// <summary>Which board edge an attacker enters on (spec-district-layout.md §4).</summary>
public enum BoardEdge { North, South, East, West }

public sealed class DistrictLayoutRejection : Exception
{
    public DistrictLayoutRejection(string message) : base(message) { }
}

/// <summary>
/// Turns a sector into a board, the same way every time (spec-district-layout.md). The pure function
/// <c>(sectorId, worldSeed, slots) → GridSpec</c>, plus the stability contract that is the module's
/// actual specification — S1 (byte-stable on replay), S2 (stable across turns), S3 (unchanged by
/// capture), S4 (stable under slot growth). <c>GridSpec</c> is derived, never persisted and never
/// hashed (spec §7) — nothing here touches <see cref="WorldCanonical"/>, so this module moves zero
/// goldens.
/// </summary>
public static class DistrictLayout
{
    /// <summary>
    /// Order matters and is part of the contract (spec §Numeric types): hashing
    /// <paramref name="worldSeed"/> and <paramref name="sectorId"/> in any other order, or with any
    /// other mixer, produces a different board. Reuses <see cref="SeededRng"/>'s existing
    /// stream-derivation mixer — never a new hash function (the same defect class as a private
    /// <c>f(level)</c>).
    /// </summary>
    public static ulong DistrictSeed(ulong worldSeed, string sectorId) =>
        SeededRng.DeriveStream(worldSeed, sectorId).NextULong();

    /// <summary>
    /// Which board edge the attacker enters on: derived from the lane they arrived by (spec §4), so
    /// marching the long way round genuinely changes the assault. Falls back to
    /// <see cref="BoardEdge.North"/> when the attacker is already standing in the sector (no lane) —
    /// deterministic, and the case exists because a garrison that turns on its host has no approach
    /// march.
    /// </summary>
    public static BoardEdge EntryEdgeFor(WorldState world, WorldEntity attacker, string sectorId)
    {
        if (attacker.OnLaneId is null) return BoardEdge.North;

        // Ordering by LaneId ordinal before picking makes this replay-stable if the lane list ever
        // carried more than one row for the same id (must not happen, but trusting first-match would
        // make that latent bug silently non-deterministic instead of loud) -- the same discipline
        // ReachMap/LegionSupply already apply elsewhere in this program.
        var lane = world.Lanes
            .Where(l => string.Equals(l.LaneId, attacker.OnLaneId, StringComparison.Ordinal))
            .OrderBy(l => l.LaneId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (lane is null) return BoardEdge.North;

        // Reuses SeededRng's existing mixer (DeriveStream -> Fnv1a64), never a new hash: the same
        // lane id always resolves to the same edge, and two different lanes usually resolve to
        // different edges (a 4-way bucket, so not guaranteed every time -- acceptable for a cosmetic
        // approach-direction pick, not a determinism-load-bearing one).
        return (BoardEdge)SeededRng.DeriveStream(0, lane.LaneId).NextInt(4);
    }

    /// <summary>
    /// Board dimensions for a sector's development level. **A lookup, never a formula**
    /// (spec-district-layout.md §2 — "The grid does not grow. The placement budget does.") A level
    /// past the highest authored tier plateaus at that tier's side: the grid stops growing: further
    /// development buys build slots and tower tier (P(Θ)) instead, never board size. This is a
    /// board-bounded structural dimension, not a power-shaped scale — see the spec's own boxed
    /// correction on why a board side is deliberately NOT on the power ladder.
    /// </summary>
    public static int SideFor(int developmentLevel)
    {
        var table = SiegeTuningPolicy.District.SideByBaseTier;
        var tier = Math.Max(0, developmentLevel);

        // The largest authored tier key <= tier; if none (tier below every authored key), the
        // smallest authored key -- a table always names at least one row (SiegeTuningLoader enforces
        // this), so this always resolves.
        var applicable = table.Keys.Where(k => k <= tier).ToList();
        var chosenTier = applicable.Count > 0 ? applicable.Max() : table.Keys.Min();
        return table[chosenTier];
    }

    /// <summary>
    /// Where slot <paramref name="slotIndex"/> sits. A function of the slot's OWN index and the
    /// district seed — never of how many slots exist (S4). Adding slot 7 must leave slots 0..6
    /// exactly where they were, because a player who built a wall around their granary did not
    /// consent to it moving when they built a barracks.
    /// </summary>
    public static GridPos CellForSlot(ulong districtSeed, int slotIndex, GridSpec spec, GridPos coreCenter, int coreSide)
    {
        if (slotIndex < 0) throw new DistrictLayoutRejection($"CellForSlot: slotIndex must be >= 0; got {slotIndex}");

        var spiral = CanonicalCoreSpiral(coreCenter, coreSide);
        if (slotIndex >= spiral.Count)
            throw new DistrictLayoutRejection(
                $"CellForSlot: slot {slotIndex} has no cell in a {coreSide}x{coreSide} Core " +
                $"({spiral.Count} cells) -- more slots than the Core can hold at this base tier.");

        // The rotation is picked from the district seed ONCE, and applied to the whole fixed spiral
        // -- never re-derived per slot count, which is what makes slot i's cell independent of the
        // slot list's length (S4). Every dihedral transform preserves Chebyshev distance from the
        // centre, so the rotated list is still ordered ring-by-ring; only its orientation changes.
        var transform = DihedralTransforms[new SeededRng(districtSeed).NextInt(DihedralTransforms.Length)];
        var offset = spiral[slotIndex];
        var rotated = transform(offset);
        var cell = new GridPos(coreCenter.Row + rotated.Row, coreCenter.Col + rotated.Col);

        if (!spec.Contains(cell))
            throw new DistrictLayoutRejection($"CellForSlot: slot {slotIndex} resolved to {cell}, outside the board.");

        return cell;
    }

    /// <summary>
    /// Every cell of a <paramref name="coreSide"/>-by-<paramref name="coreSide"/> square, as offsets
    /// from its own centre, in a FIXED total order: ascending Chebyshev distance (ring by ring),
    /// then row, then column as a tie-break within a ring. A pure function of the Core's geometry
    /// alone — never of slot count — which is exactly what S4 needs: this list's first N entries
    /// never change no matter how large N grows past it (bounded only by the Core's own cell count).
    /// </summary>
    static List<GridPos> CanonicalCoreSpiral(GridPos coreCenter, int coreSide)
    {
        var half = coreSide / 2;
        var offsets = new List<GridPos>();
        for (var dr = -half; dr < coreSide - half; dr++)
        for (var dc = -half; dc < coreSide - half; dc++)
            offsets.Add(new GridPos(dr, dc));

        offsets.Sort((a, b) =>
        {
            var da = Math.Max(Math.Abs(a.Row), Math.Abs(a.Col));
            var db = Math.Max(Math.Abs(b.Row), Math.Abs(b.Col));
            if (da != db) return da.CompareTo(db);
            if (a.Row != b.Row) return a.Row.CompareTo(b.Row);
            return a.Col.CompareTo(b.Col);
        });
        return offsets;
    }

    // The 8 symmetries of a square (the dihedral group D4), acting on an offset from the centre.
    // Every one preserves Chebyshev distance from the centre and maps the Core square onto itself
    // exactly, so applying any single one to the whole canonical spiral can never introduce a
    // collision or push a cell outside the Core.
    static readonly Func<GridPos, GridPos>[] DihedralTransforms =
    {
        p => new GridPos(p.Row, p.Col),
        p => new GridPos(-p.Col, p.Row),
        p => new GridPos(-p.Row, -p.Col),
        p => new GridPos(p.Col, -p.Row),
        p => new GridPos(p.Row, -p.Col),
        p => new GridPos(-p.Row, p.Col),
        p => new GridPos(p.Col, p.Row),
        p => new GridPos(-p.Col, -p.Row),
    };

    /// <summary>
    /// The zone a cell falls in, purely from board geometry — never from slot occupancy. Core is a
    /// centred square of side <c>coreSideMilli</c> per-mille of the board side (divided by 1000
    /// exactly once, last); Rampart is the ring of <paramref name="rampartThickness"/> cells around
    /// it; everything else is Approach.
    ///
    /// <para><b>Optional 6-argument overload, resolved 2026-09-07 (17.11, decision 47)</b>: a warded
    /// lane pushes the Rampart/Approach boundary further from centre, but ONLY on the wedge facing the
    /// attacker's own entry edge — mirroring `fortressRampartBonus`'s own additive-to-thickness
    /// contract exactly (spec-district-layout.md §5), just scoped to one wedge instead of the whole
    /// ring. Every existing call site (`ConstructionPlacement.CanPlace`, `ObjectivePositionFor`'s own
    /// internal formula, `OpenCellsInZone`) keeps calling the 4-argument form above, byte-identical
    /// forever — this overload exists ONLY for a caller that has an attacker edge and a real ward
    /// level (`DistrictAssaultResolver`, once wired).</para>
    /// </summary>
    public static DistrictZone ZoneOf(GridPos p, int side, int coreSideMilli, int rampartThickness)
    {
        var center = new GridPos(side / 2, side / 2);
        var coreSideCells = Math.Max(1, checked(coreSideMilli * side) / 1000);
        var coreHalfCeil = (coreSideCells + 1) / 2;

        var dr = Math.Abs(p.Row - center.Row);
        var dc = Math.Abs(p.Col - center.Col);
        var chebyshev = Math.Max(dr, dc);

        if (chebyshev < coreHalfCeil) return DistrictZone.Core;
        if (chebyshev < coreHalfCeil + rampartThickness) return DistrictZone.Rampart;
        return DistrictZone.Approach;
    }

    public static DistrictZone ZoneOf(GridPos p, int side, int coreSideMilli, int rampartThickness,
        BoardEdge attackerEdge, int wardExtraDepth)
    {
        if (wardExtraDepth < 0) throw new ArgumentOutOfRangeException(nameof(wardExtraDepth));
        var center = new GridPos(side / 2, side / 2);
        var coreSideCells = Math.Max(1, checked(coreSideMilli * side) / 1000);
        var coreHalfCeil = (coreSideCells + 1) / 2;

        var dr = Math.Abs(p.Row - center.Row);
        var dc = Math.Abs(p.Col - center.Col);
        var chebyshev = Math.Max(dr, dc);

        if (chebyshev < coreHalfCeil) return DistrictZone.Core;
        var effectiveRampart = wardExtraDepth > 0 && InWedgeFacing(p, center, attackerEdge)
            ? checked(rampartThickness + wardExtraDepth)
            : rampartThickness;
        if (chebyshev < coreHalfCeil + effectiveRampart) return DistrictZone.Rampart;
        return DistrictZone.Approach;
    }

    /// <summary>
    /// One of four mutually-exclusive wedges a cell falls in relative to <paramref name="center"/> —
    /// the same "no two equally-plausible answers" discipline `BoardPathfinder`'s own fixed neighbour
    /// order already establishes. Row-distance vs column-distance ties (an exact diagonal) resolve to
    /// the North/South wedge, never East/West — a fixed, arbitrary-but-consistent priority, not a gap.
    /// The centre cell itself (<c>dr == dc == 0</c>) belongs to no wedge, which never matters: `ZoneOf`
    /// only consults this after the Core check already failed, and the centre is always Core.
    /// </summary>
    static bool InWedgeFacing(GridPos p, GridPos center, BoardEdge edge)
    {
        var dr = p.Row - center.Row;
        var dc = p.Col - center.Col;
        var absDr = Math.Abs(dr);
        var absDc = Math.Abs(dc);
        return edge switch
        {
            BoardEdge.North => dr < 0 && absDr >= absDc,
            BoardEdge.South => dr > 0 && absDr >= absDc,
            BoardEdge.East => dc > 0 && absDc > absDr,
            BoardEdge.West => dc < 0 && absDc > absDr,
            _ => throw new DistrictLayoutRejection($"InWedgeFacing: unknown edge '{edge}'."),
        };
    }

    /// <summary>
    /// Builds the board for one sector (spec-district-layout.md §1-§6). Pure: the same
    /// <c>(worldSeed, sector, entryEdge)</c> always produces the same <see cref="GridSpec"/> — never
    /// seeded from turn, owner, or a clock (S1-S3). Slot cells are placed by <see cref="CellForSlot"/>,
    /// which by construction never moves an existing slot when the list grows (S4).
    /// </summary>
    public static GridSpec Build(WorldSector sector, ulong worldSeed, BoardEdge entryEdge)
    {
        if (sector is null) throw new ArgumentNullException(nameof(sector));

        var side = SideFor(sector.DevelopmentLevel);
        var district = SiegeTuningPolicy.District;
        var seed = DistrictSeed(worldSeed, sector.SectorId);

        // §5: SectorTypeFlags.Fortress -- read, not assumed. Verified at HEAD (2026-09-05): no
        // shipped SectorTypeCatalog row sets it (SectorTypeCatalog.cs's five seed rows: Home, two
        // NoBase, Nexus, Boss). This is a WIRING GAP, reported as one -- the mechanism below is real
        // and reachable the moment a template sets the flag, it is simply unreached by any content
        // that ships today.
        var sectorType = SectorTypeCatalog.All.FirstOrDefault(t => t.TypeId == sector.TypeId);
        var isFortress = sectorType is not null && sectorType.Flags.HasFlag(SectorTypeFlags.Fortress);
        var rampartThickness = district.RampartThickness + (isFortress ? district.FortressRampartBonus : 0);
        var gateCount = Math.Max(1, district.GateCount - (isFortress ? 1 : 0));

        var cells = new CellTerrain[side * side];
        var spec0 = new GridSpec(side, side); // dimension-only helper for IndexOf during generation
        var center = new GridPos(side / 2, side / 2);

        for (var r = 0; r < side; r++)
        for (var c = 0; c < side; c++)
        {
            var p = new GridPos(r, c);
            var zone = ZoneOf(p, side, district.CoreSideMilli, rampartThickness);
            cells[spec0.IndexOf(p)] = zone == DistrictZone.Rampart ? CellTerrain.Blocking : CellTerrain.Open;
        }

        // Gates: cardinal midpoints on the Rampart ring, rotated by the district seed, with the
        // entry edge's own gate ALWAYS present -- otherwise a besieger must breach before they can
        // act, which is not a difficulty setting (spec §3).
        var edges = new[] { BoardEdge.North, BoardEdge.South, BoardEdge.East, BoardEdge.West };
        var rotation = new SeededRng(seed).NextInt(4);
        var orderedEdges = edges.Select((_, i) => edges[(i + rotation) % 4])
            .OrderBy(e => e == entryEdge ? 0 : 1) // entry edge's gate is placed first, guaranteeing it survives gateCount trimming
            .ToList();

        foreach (var edge in orderedEdges.Take(gateCount))
        {
            var gateCell = CardinalMidpoint(edge, side);
            OpenGateColumnOrRow(cells, spec0, gateCell, edge, side, center, district.CoreSideMilli, rampartThickness);
        }

        // §5: ruined/depleted slots are rubble -- Rough terrain, no structure. Already-declared enum
        // values (SlotState.Ruined/Depleted), zero new vocabulary. Applied AFTER gates so a ruined
        // slot cannot silently re-block a gate cell.
        for (var i = 0; i < sector.Slots.Count; i++)
        {
            var slot = sector.Slots[i];
            if (slot.State is not (SlotState.Ruined or SlotState.Depleted)) continue;
            var cellForRuin = CellForSlot(seed, slot.SlotIndex, spec0, center, CoreSideCells(side, district.CoreSideMilli));
            var idx = spec0.IndexOf(cellForRuin);
            if (cells[idx] != CellTerrain.Blocking) cells[idx] = CellTerrain.Rough;
        }

        return new GridSpec(side, side, cells);
    }

    /// <summary>Widened from `private` to `internal` for `siege-resolver`'s own
    /// `DistrictAssaultResolver` (same assembly), which must place a structure at the SAME cell
    /// <see cref="Build"/> itself uses for a ruined slot — re-deriving this independently rather than
    /// exposing it would risk a private, drifting second curve for board-side-to-Core-cell-count.</summary>
    internal static int CoreSideCells(int boardSide, int coreSideMilli) => Math.Max(1, checked(coreSideMilli * boardSide) / 1000);

    /// <summary>
    /// base-defense `siege-ai` R3 (spec-siege-ai.md §4): "the objective" a side with no target in
    /// reach advances toward — the Core zone's own centre for an attacker (the SAME `center` formula
    /// <see cref="ZoneOf"/> already computes internally — re-derived here rather than exposed as a
    /// shared field since it is a one-line integer midpoint, not a curve worth a second name), the
    /// gate/breach it must intercept for a defender (<see cref="CardinalMidpoint"/>, the SAME cell
    /// <see cref="Build"/> itself already opens a corridor through).
    ///
    /// <para><b>Deliberately a pure, standalone function — not yet wired into `IBattleView` or
    /// `AiCandidate.ObjectiveClassMilli`</b>, this program's own established "mechanism before wiring"
    /// precedent (`siege-fog`, `SiegeAiIntentSource`, `RetargetLedger`, `ConstructionAi`). The real
    /// remaining gap this leaves precisely scoped: a real `IBattleView` caller needs to know WHICH
    /// side is attacking for a given battle (`DistrictLayout.EntryEdgeFor`'s own result, computed at
    /// `siege-resolver` time but not currently threaded into `BattleRunState`) before it can supply
    /// `isAttacker`/`attackerEdge` here — that plumbing, not this formula, is the un-started part.</para>
    /// </summary>
    /// <summary>
    /// base-defense `siege-ai` 17.11 (decision 47): the lane's own `WardLevel` an attacker is
    /// approaching a sector on, or 0 for every case that cannot resolve one (no current lane, an
    /// unknown lane id) — the SAME graceful-default posture <see cref="EntryEdgeFor"/> already
    /// established for its own "unknown -> North" fallback, and the SAME lane-resolution lookup
    /// (ordinal-first, replay-stable) reused verbatim rather than re-derived, Law 1.
    /// </summary>
    public static int WardLevelFor(WorldState world, WorldEntity attacker)
    {
        if (attacker.OnLaneId is null) return 0;

        var lane = world.Lanes
            .Where(l => string.Equals(l.LaneId, attacker.OnLaneId, StringComparison.Ordinal))
            .OrderBy(l => l.LaneId, StringComparer.Ordinal)
            .FirstOrDefault();
        return lane?.WardLevel ?? 0;
    }

    /// <summary>
    /// base-defense `siege-ai` 17.11: perpendicular distance from a board edge, 0 AT the edge,
    /// increasing inward — the same cardinal convention <see cref="CardinalMidpoint"/> already fixes
    /// (`North` = row 0, `South` = row `side-1`, `East` = col `side-1`, `West` = col 0).
    /// </summary>
    public static int DistanceFromEdge(GridPos cell, BoardEdge edge, int side)
    {
        if (side <= 0) throw new ArgumentOutOfRangeException(nameof(side));
        return edge switch
        {
            BoardEdge.North => cell.Row,
            BoardEdge.South => side - 1 - cell.Row,
            BoardEdge.East => side - 1 - cell.Col,
            BoardEdge.West => cell.Col,
            _ => throw new DistrictLayoutRejection($"DistanceFromEdge: unknown edge '{edge}'."),
        };
    }

    public static GridPos ObjectivePositionFor(bool isAttacker, BoardEdge attackerEdge, int side)
    {
        if (side <= 0) throw new ArgumentOutOfRangeException(nameof(side));
        return isAttacker
            ? new GridPos(side / 2, side / 2) // the Core's own centre — ZoneOf's own `center` formula
            : CardinalMidpoint(attackerEdge, side); // the gate the attacker breaches
    }

    internal static GridPos CardinalMidpoint(BoardEdge edge, int side) => edge switch
    {
        BoardEdge.North => new GridPos(0, side / 2),
        BoardEdge.South => new GridPos(side - 1, side / 2),
        BoardEdge.East => new GridPos(side / 2, side - 1),
        BoardEdge.West => new GridPos(side / 2, 0),
        _ => throw new DistrictLayoutRejection($"CardinalMidpoint: unknown edge '{edge}'."),
    };

    /// <summary>Opens a one-cell-wide corridor of Open terrain from the board edge through to the
    /// Core, along the cardinal line named by <paramref name="edge"/> — the gate itself, not just a
    /// single breached brick in an otherwise solid ring.</summary>
    static void OpenGateColumnOrRow(CellTerrain[] cells, GridSpec spec0, GridPos edgeCell, BoardEdge edge, int side,
        GridPos center, int coreSideMilli, int rampartThickness)
    {
        var (dr, dc) = edge switch
        {
            BoardEdge.North => (1, 0),
            BoardEdge.South => (-1, 0),
            BoardEdge.East => (0, -1),
            BoardEdge.West => (0, 1),
            _ => throw new DistrictLayoutRejection($"OpenGateColumnOrRow: unknown edge '{edge}'."),
        };

        var p = edgeCell;
        while (spec0.Contains(p) && ZoneOf(p, side, coreSideMilli, rampartThickness) != DistrictZone.Core)
        {
            cells[spec0.IndexOf(p)] = CellTerrain.Open;
            p = new GridPos(p.Row + dr, p.Col + dc);
        }
    }
}
