using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-obstacles` §5, the one genuinely new mechanic: damage on entry, single-use,
/// ignores cover, REVEALED to both sides (audit F9 — "unrevealed" contradicts §5.16 R6's no-hidden-
/// modifiers rule and §5.20's zero-hidden-information foundation; a visible mine is a denied cell the
/// attacker must pay to cross or route around, which is DENY working as intended — a hidden one is a
/// coin flip). Reacts to <see cref="Board.BoardState.Entered"/>, the program's ONE reviewed vocabulary
/// change (`Match.ScopeMembershipTransition.CellEntered`), spent here on a real mechanic.
///
/// <para><b>Deliberately standalone, not wired into `BattleRunState`'s constructor</b>: applying the
/// damage through the real pipeline (`DamageApplyPipeline`/`ApplyHp`, "never a direct HP write") needs
/// a live combat host, and — more fundamentally — nothing in the round loop calls
/// <see cref="Board.BoardState.Move"/> yet (no movement mechanic exists in the battle kernel today,
/// `siege-ai`'s job). A mine can only ever trigger via `Place` in the current codebase, which is a
/// degenerate case (starting on a cell is not "entering" it in the sense this mechanic means). This
/// class proves the MECHANISM — single-use, revealed, ignores cover — correctly and testably; wiring
/// its `Damage`/`Trigger` output through a real `ApplyHp` call is left for whoever implements
/// movement, the same honest-gap shape `siege-waves`'s field-cleared trigger already named.</para>
/// </summary>
public sealed class MineField
{
    readonly Dictionary<GridPos, long> _armed = new();

    /// <summary>Places a live mine at a cell — REVEALED, so both sides can query
    /// <see cref="IsArmedAt"/> freely; there is no "owner-only" view of this state.</summary>
    public void Arm(GridPos cell, long damage)
    {
        if (damage <= 0) throw new ArgumentOutOfRangeException(nameof(damage), "a mine with non-positive damage is not a mine.");
        _armed[cell] = damage;
    }

    /// <summary>F9: mines are visible to both sides — there is no faction-scoped overload of this
    /// query, on purpose.</summary>
    public bool IsArmedAt(GridPos cell) => _armed.ContainsKey(cell);

    /// <summary>
    /// Single-use: the mine is removed the instant it triggers, so a second entry (by the same or a
    /// different actor) finds nothing there — safe, not a second detonation. Returns the damage to
    /// apply, or null if no mine sits at this cell. <b>Ignores cover</b> by construction: this method
    /// returns a raw damage figure with no cover/obstruction reduction applied anywhere in its own
    /// logic, and a correct caller must not run the result through `siege-cover`'s per-shot math either
    /// — the contract `DamageSourceKind.Entry` exists to make explicit once that matrix exists.
    /// </summary>
    public long? Trigger(GridPos cell)
    {
        if (!_armed.TryGetValue(cell, out var damage)) return null;
        _armed.Remove(cell);
        return damage;
    }

    /// <summary>Wires this field to a real board: every <see cref="Board.BoardState.Entered"/> that
    /// lands on an armed cell triggers exactly once, single-use, and hands the caller the damage to
    /// apply through its own real pipeline (this class never touches HP itself).</summary>
    public void AttachTo(Board.BoardState board, Action<string, long> onTriggered)
    {
        if (board is null) throw new ArgumentNullException(nameof(board));
        if (onTriggered is null) throw new ArgumentNullException(nameof(onTriggered));
        board.Entered += (actorKey, cell) =>
        {
            if (Trigger(cell) is { } damage) onTriggered(actorKey, damage);
        };
    }
}
