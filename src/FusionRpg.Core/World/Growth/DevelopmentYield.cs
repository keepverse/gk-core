using FusionRpg.Core.World.Loam;

namespace FusionRpg.Core.World.Growth;

/// <summary>
/// world-map W55 (spec-sector-development.md §3): the yield half of empire-economy-ssot.md A8 —
/// "development must raise yield faster than it raises upkeep, or nobody will ever develop." The
/// upkeep half already existed (<see cref="LoamPolicy.DevelopmentAndDangerUpkeep"/>); this is the
/// missing other side of the same comparison, read by <see cref="Loam.LoamProduction.For(WorldSector)"/>.
///
/// Pure, one multiplication, no clamp — a sector's own <see cref="WorldSector.DevelopmentLevel"/> has
/// no hard cap (world-map W53's own producer never lowers it either), so this has none. Takes
/// <paramref name="yieldPerLevel"/> as an explicit parameter rather than reading
/// <see cref="LoamPolicy.DevelopmentYieldPerLevel"/> internally — the identical split this whole
/// module already establishes (<c>RecruitPolicy.PulseFor</c>, <c>GrowthPhases.Growth</c>): the real
/// caller (<c>LoamProduction.For</c>) reads the live, process-wide tuning and passes it in, so this
/// leaf stays independently testable at any rate — including one large enough to force a genuine
/// overflow — with zero risk of racing <c>LoamPolicy.Configure</c>'s shared static field against
/// another test class under xUnit's default parallelism.
/// </summary>
public static class DevelopmentYield
{
    /// <summary>`developmentLevel * yieldPerLevel` — widened to `long` before multiplying (AGENTS.md's
    /// overflow rule) and `checked` so a combination large enough to overflow throws rather than
    /// wraps into a negative yield.</summary>
    public static long For(int developmentLevel, long yieldPerLevel)
    {
        checked
        {
            return (long)developmentLevel * yieldPerLevel;
        }
    }
}
