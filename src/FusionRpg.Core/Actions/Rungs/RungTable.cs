using System.Linq;

namespace FusionRpg.Core.Actions.Rungs;

/// <summary>Zero-alloc read of one rung's multipliers — a struct, never a dictionary lookup at resolve.</summary>
public readonly struct RungMultipliers
{
    public readonly int QPowerMilli;
    public readonly int CostMulti;
    public readonly int CdMulti;

    public RungMultipliers(int qPowerMilli, int costMulti, int cdMulti)
    {
        QPowerMilli = qPowerMilli;
        CostMulti = costMulti;
        CdMulti = cdMulti;
    }
}

/// <summary>
/// The closed set of structure axes a rung's budget may name (spec-rung-table.md §4, ideal §8.2).
/// Linkage is deliberately absent — it needs the effect-atom program's own extension (Phase 0) and
/// is not yet a structure a rung can spend its budget on.
/// </summary>
public static class StructureAxes
{
    public const string ScopeSplit = "scopeSplit";
    public const string RiderStatus = "riderStatus";
    public const string Condition = "condition";
    public const string Sequence = "sequence";
    public const string Consumption = "consumption";
    public const string Reaction = "reaction";
    public const string Restriction = "restriction";

    /// <summary>Named constants above, in one closed list — T30's <c>StructureBudgetGuard</c> is the
    /// first caller to spend individual axis ids rather than only validate the set as a whole.</summary>
    public static readonly IReadOnlyList<string> Closed = new[]
    {
        ScopeSplit, RiderStatus, Condition, Sequence, Consumption, Reaction, Restriction,
    };

    public static bool IsKnown(string axis) => Closed.Contains(axis, StringComparer.Ordinal);
}

/// <summary>
/// The loaded rung ladder — indexed by array, not dictionary, since rungs are contiguous 1..cap
/// (spec-rung-table.md §2, §5). One table, two readers (`A11`, `A3`): both resolve identical
/// multipliers for the same rung because both call this.
/// </summary>
public sealed class RungTable
{
    readonly RungRow[] _byRung; // index 0 == rung 1

    public int Cap { get; }
    public IReadOnlyList<RungRow> Rows { get; }

    public RungTable(int cap, IReadOnlyList<RungRow> rows)
    {
        Cap = cap;
        Rows = rows;
        _byRung = new RungRow[rows.Count];
        foreach (var row in rows) _byRung[row.Rung - 1] = row;
    }

    public bool TryGet(int rung, out RungRow row)
    {
        if (rung < 1 || rung > _byRung.Length) { row = null!; return false; }
        row = _byRung[rung - 1];
        return row is not null;
    }

    /// <summary>Zero-alloc resolve for the hot read path (`A3`, `A11`).</summary>
    public bool TryResolve(int rung, out RungMultipliers multipliers)
    {
        if (!TryGet(rung, out var row)) { multipliers = default; return false; }
        multipliers = new RungMultipliers(row.QPowerMilli, row.CostMulti, row.CdMulti);
        return true;
    }
}
