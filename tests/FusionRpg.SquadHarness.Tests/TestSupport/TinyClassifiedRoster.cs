using FusionRpg.Tools.SquadHarness;

namespace FusionRpg.SquadHarness.Tests.TestSupport;

/// <summary>
/// A small, REAL subset of the production rosters -- one build per build class, taken directly from
/// <see cref="SquadRoster.Duels"/>/<see cref="SquadRoster.Squads"/> rather than hand-built, so the four
/// classes ("corner"/"hybrid2"/"hybrid3"/"spread") classify exactly as production does. This exists
/// because the full rosters (91 duel builds x 90 opponents, 23 squads x 22 opponents) measure at roughly
/// 20ms per <c>BattleEngine.Resolve</c> call on this machine (see <see cref="TransferReport"/>'s own doc
/// on why) -- a full sweep even at one trial takes minutes, and F2's tests need to run in seconds.
/// </summary>
public static class TinyClassifiedRoster
{
    /// <summary>One corner, one hybrid2, one hybrid3, one spread -- 4 builds, 12 ordered pairs.</summary>
    public static IReadOnlyList<NamedBuild> Duels()
    {
        var all = SquadRoster.Duels();
        return new[]
        {
            all.First(b => b.Kind == "corner"),
            all.First(b => b.Kind == "hybrid2"),
            all.First(b => b.Kind == "hybrid3"),
            all.First(b => b.Kind == "spread"),
        };
    }

    /// <summary>One mono-&lt;apt&gt; (corner), one mono-hybrid2-*, one mono-hybrid3-*, mono-spread --
    /// the four squads <see cref="TransferReport.SquadBuildClass"/> classifies -- plus one excluded
    /// archetype (a posture squad) so exclusion is exercised too. 5 builds, 20 ordered pairs.</summary>
    public static IReadOnlyList<SquadBuild> Squads(AllocationShape shape = AllocationShape.PerActor)
    {
        var all = SquadRoster.Squads(shape);
        return new[]
        {
            all.First(s => TransferReport.SquadBuildClass(s.Id) == "corner"),
            all.First(s => TransferReport.SquadBuildClass(s.Id) == "hybrid2"),
            all.First(s => TransferReport.SquadBuildClass(s.Id) == "hybrid3"),
            all.First(s => TransferReport.SquadBuildClass(s.Id) == "spread"),
            all.First(s => TransferReport.SquadBuildClass(s.Id) is null), // an excluded archetype
        };
    }
}
