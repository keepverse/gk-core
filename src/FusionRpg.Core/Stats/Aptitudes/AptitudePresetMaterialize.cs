namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>One aptitude row in a build preset — target permille plus optional D13 abs/‰ clamps.</summary>
public sealed record AptitudePresetRowSpec(
    string AptitudeId,
    long TargetPermille,
    long? MinAbs = null,
    long? MaxAbs = null,
    long? MinPermille = null,
    long? MaxPermille = null);

/// <summary>D13 materialize result — shares plus leftover (E2: leftover is legal, never redistributed).</summary>
public sealed record AptitudePresetMaterializeResult(
    bool Ok,
    string Reason,
    IReadOnlyDictionary<string, long> Shares,
    long Leftover);

/// <summary>
/// aptitude-sheet AS-3.1 — D13 materialize + E2 leftover-legal + E5 sum-1000 validation.
/// Shared with auto-assign (AS-3.3). Magnitudes are <c>long</c>; widen before multiply; /1000 last;
/// overflow throws (checked). <c>lo &gt; hi</c> refuses with a named reason — never silent clamp.
/// </summary>
public static class AptitudePresetMaterialize
{
    public const long RequiredPermilleSum = 1000L;

    /// <summary>E5 — Save requires the twelve primaries' <c>targetPermille</c> to sum to exactly 1000.</summary>
    public static (bool Ok, string Reason, long Sum) ValidateTargetPermilleSum(
        IReadOnlyList<AptitudePresetRowSpec> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        long sum = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.AptitudeId) || !AptitudeCatalog.IsAptitudeId(row.AptitudeId))
                return (false, "aptitudes.unknownid", sum);
            if (!seen.Add(row.AptitudeId))
                return (false, "presets.duplicateAptitude", sum);
            if (row.TargetPermille < 0 || row.TargetPermille > RequiredPermilleSum)
                return (false, "presets.targetPermille.range", sum);
            checked { sum += row.TargetPermille; }
        }

        if (seen.Count != AptitudeCatalog.Count)
            return (false, "presets.rows.incomplete", sum);
        if (sum != RequiredPermilleSum)
            return (false, "presets.targetPermille.sum", sum);
        return (true, "", sum);
    }

    /// <summary>
    /// Materialize a preset against a binding budget (D13). Unset abs or ‰ axis is ignored.
    /// Leftover after clamp is LEGAL (E2) — do not redistribute into other aptitudes.
    /// </summary>
    public static AptitudePresetMaterializeResult Materialize(
        IReadOnlyList<AptitudePresetRowSpec> rows, long budget)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        if (budget < 0)
            return new AptitudePresetMaterializeResult(false, "presets.budget.negative",
                new Dictionary<string, long>(StringComparer.Ordinal), 0);

        var check = ValidateTargetPermilleSum(rows);
        if (!check.Ok)
            return new AptitudePresetMaterializeResult(false, check.Reason,
                new Dictionary<string, long>(StringComparer.Ordinal), 0);

        var shares = new Dictionary<string, long>(StringComparer.Ordinal);
        long spent = 0;
        foreach (var row in rows)
        {
            long? lo = null;
            long? hi = null;

            if (row.MinAbs is long minAbs) lo = minAbs;
            if (row.MinPermille is long minPm)
            {
                long fromPm;
                checked { fromPm = budget * minPm / 1000L; }
                lo = lo is long existing ? Math.Max(existing, fromPm) : fromPm;
            }

            if (row.MaxAbs is long maxAbs) hi = maxAbs;
            if (row.MaxPermille is long maxPm)
            {
                long fromPm;
                checked { fromPm = budget * maxPm / 1000L; }
                hi = hi is long existing ? Math.Min(existing, fromPm) : fromPm;
            }

            if (lo is long loV && hi is long hiV && loV > hiV)
            {
                return new AptitudePresetMaterializeResult(
                    false, "presets.materialize.loGtHi",
                    new Dictionary<string, long>(StringComparer.Ordinal), 0);
            }

            long raw;
            checked { raw = budget * row.TargetPermille / 1000L; }

            var share = raw;
            if (lo is long floor && share < floor) share = floor;
            if (hi is long ceiling && share > ceiling) share = ceiling;
            if (share < 0) share = 0;

            shares[row.AptitudeId] = share;
            checked { spent += share; }
        }

        var leftover = budget - spent;
        // E2: leftover may be > 0; overspend should not happen from target≤1000 + clamps, but if
        // minAbs floors push past budget the allocate path still refuses — surface it here.
        if (leftover < 0)
            return new AptitudePresetMaterializeResult(false, "presets.materialize.overspend", shares, leftover);

        return new AptitudePresetMaterializeResult(true, "", shares, leftover);
    }
}
