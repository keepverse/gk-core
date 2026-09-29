namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// build-preset BP2.12 (spec-capture-current.md): points → shares of the spent total, so a
/// one-click capture can turn "what I actually allocated right now" into a saved preset. Goes the
/// OTHER way from <see cref="AptitudePresetMaterialize.Materialize"/> (shares → points) — this is
/// not a second favour-to-points function (empire-progression D3 forbids one); points are still
/// produced only by <c>Materialize</c>.
/// </summary>
public static class AptitudePresetCapture
{
    /// <summary>
    /// Every one of the twelve primaries gets a row — <see cref="AptitudePresetMaterialize.ValidateTargetPermilleSum"/>'s
    /// own E5 rule refuses an incomplete row set, so an aptitude absent from <paramref name="points"/>
    /// (never spent) still gets a row, at <c>TargetPermille</c> 0. Rows sum to exactly
    /// <see cref="AptitudePresetMaterialize.RequiredPermilleSum"/> by the largest-remainder method:
    /// each share starts at its floor, and the 1000-sum's leftover (always in <c>[0, Count-1]</c>,
    /// the largest-remainder method's own bound) is handed out one per-mille each to the aptitudes
    /// with the largest fractional remainder, ties broken by aptitude id (ordinal) for a
    /// deterministic result. Caps stay unset — a captured preset is a lean, not exact points.
    ///
    /// <para><c>long</c> throughout; ×1000 before ÷spent, divide once (docs/architecture/numeric-types.md numeric rules 2 and
    /// 5). An empty or non-positive allocation returns no rows — there is nothing to keep.</para>
    /// </summary>
    public static IReadOnlyList<AptitudePresetRowSpec> FromAllocation(IReadOnlyDictionary<string, long> points)
    {
        if (points is null) throw new ArgumentNullException(nameof(points));
        var spent = checked(points.Values.Sum());
        if (spent <= 0) return Array.Empty<AptitudePresetRowSpec>();

        var baseShare = new Dictionary<string, long>(StringComparer.Ordinal);
        var remainder = new Dictionary<string, long>(StringComparer.Ordinal);
        long baseSum = 0;
        foreach (var aptitude in AptitudeCatalog.All)
        {
            var p = points.TryGetValue(aptitude.Id, out var v) ? v : 0L;
            long scaled;
            checked { scaled = p * 1000L; }
            var b = scaled / spent;
            baseShare[aptitude.Id] = b;
            remainder[aptitude.Id] = scaled - b * spent; // comparable across rows: same divisor (spent)
            checked { baseSum += b; }
        }

        // Flooring each share never overshoots the target sum, so this is always >= 0; the
        // largest-remainder method's own bound keeps it below AptitudeCatalog.Count.
        var leftover = AptitudePresetMaterialize.RequiredPermilleSum - baseSum;
        var byLargestRemainder = AptitudeCatalog.All
            .OrderByDescending(a => remainder[a.Id])
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => a.Id)
            .ToList();

        var share = new Dictionary<string, long>(baseShare, StringComparer.Ordinal);
        for (var i = 0; i < leftover; i++)
            share[byLargestRemainder[i]] += 1;

        return AptitudeCatalog.All
            .Select(a => new AptitudePresetRowSpec(a.Id, share[a.Id]))
            .ToList();
    }
}
