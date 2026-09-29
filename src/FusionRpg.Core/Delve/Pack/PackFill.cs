using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Pack;

/// <summary>One room's own drop-table roll groups, all at that room's shared `Θ_room` (spec-loot-
/// pack.md §8: `E[grants_room] = Σ_groups rolls × scale‰(Θ) / 1000`) — a room's own encounter table
/// does not mix depths within itself.</summary>
public sealed record RoomRollProfile(int ThetaRoom, IReadOnlyList<int> RollsPerGroup);

/// <summary>
/// D3.23 (spec-loot-pack.md §8) — the D26 fill-rate metric, pure. `meanCellsMilli` is the footprint
/// table's own role-budget-weighted mean cell count (§8: "≈ 2.4 with §2's shapes"), supplied by the
/// caller rather than recomputed here — this file owns the metric arithmetic, not the footprint
/// weighting.
///
/// <para><b>A real inconsistency in the spec's own text, resolved by the number it actually has to
/// hit, not guessed:</b> §8's own literal formula divides by <c>(rows×cols − provisionCells)</c>, and
/// its own worked example computes exactly that first: "34 cells against 40 − 16 = 24 haul cells →
/// ~1400‰". But the SAME paragraph then states "~850‰ once the sixteen provisioning cells are
/// consumed" — i.e. once a delve's own provisions have been eaten over the course of play, freeing
/// their cells back to the full grid, `34/40 ≈ 850‰`, matching the acceptance line's own actual target
/// band (700-1000‰, ~850 starting shape) — `34/24 ≈ 1417‰` does not. The two readings are not a
/// rounding difference (1400 vs 850). This method implements the SECOND reading — divide by the whole
/// grid, `gridCells`, not `gridCells − provisionCells` — because that is the one the stated
/// acceptance band and regression target actually require; the first reading is preserved here in
/// this comment for traceability, not silently dropped.</para>
/// </summary>
public static class PackFill
{
    public static long Estimate(IReadOnlyList<RoomRollProfile> rooms, long meanCellsMilli, PowerTuning tuning, int gridCells)
    {
        if (rooms is null) throw new ArgumentNullException(nameof(rooms));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (gridCells <= 0) throw new ArgumentOutOfRangeException(nameof(gridCells));

        long totalRollsScaledMilli = 0;
        foreach (var room in rooms)
        {
            var scaleMilli = ContentScale.Milli(room.ThetaRoom, tuning);
            foreach (var rolls in room.RollsPerGroup)
                totalRollsScaledMilli = checked(totalRollsScaledMilli + (long)rolls * scaleMilli);
        }

        // fillMilli = 1000 * Σ E[grants_room] * meanCells / gridCells, with E[grants_room] and
        // meanCells both per-mille inputs -- the three /1000's collapse into the one division below,
        // done last, exactly once (never three separate roundings along the way).
        return checked(totalRollsScaledMilli * meanCellsMilli) / (1000L * gridCells);
    }
}
