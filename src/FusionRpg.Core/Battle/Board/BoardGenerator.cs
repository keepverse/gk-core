namespace FusionRpg.Core.Battle.Board;

/// <summary>
/// A10 `battle-board` (spec-battle-board.md section 1) — a normal encounter's own board dimensions,
/// square, seeded, and bounded by <see cref="BattleBoardTuningPolicy"/>.
///
/// <para><b>Random size is part of the determinism surface</b> (the spec's own words): the roll is
/// derived from the caller's seed via <see cref="SeededRng.DeriveStream"/>, the same mixing pattern
/// every other per-encounter roll in the battle already uses (<c>DistrictLayout.DistrictSeed</c>,
/// <c>ExpeditionResolver</c>'s per-battle seed) — never an ambient <see cref="Random"/>, which would
/// make a replay from the same <c>(setup, seed)</c> a lie.</para>
///
/// <para><b>Siege never calls this.</b> A siege board's size comes from `DistrictLayout.Build`
/// (world-sector/slot data, a real place already), not a random roll — this generator exists only for
/// an encounter with no such source, the action program's own normal squad-vs-wave battles.</para>
/// </summary>
public static class BoardGenerator
{
    /// <summary>
    /// Rolls a square side length in the tuned <c>[MinSide, MaxSide]</c> interval, then widens to
    /// <paramref name="minSide"/> if the caller's own actual seating need (e.g. the larger of two
    /// squad sizes) exceeds what the roll produced — the random component decides how much LARGER
    /// than the seating floor the board is, never whether both sides fit at all. A caller that needs
    /// no wider floor than the tuned minimum passes 0.
    /// </summary>
    public static GridSpec Generate(ulong seed, int minSide = 0)
    {
        var tunedMin = BattleBoardTuningPolicy.MinSide;
        var tunedMax = BattleBoardTuningPolicy.MaxSide;

        var rng = SeededRng.DeriveStream(seed, "battle-board.size");
        var rolled = tunedMin + rng.NextInt(tunedMax - tunedMin + 1);

        var side = Math.Max(rolled, minSide);
        return new GridSpec(side, side);
    }
}
