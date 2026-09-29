namespace FusionRpg.Core.Stats;

/// <summary>Pure gates for PvzStats apply / reapply (unit-testable, no Unity).</summary>
public static class PvzStatsApplyGate
{
    public static bool ShouldWrite(bool hasScaleMods, bool hasAbsolute, bool hasPvzStats) =>
        hasScaleMods || hasAbsolute || hasPvzStats;

    /// <summary>
    /// Compose Flat/Increased/More when Tab-A scales, PvzStats, or Foundation Effect session mods are present.
    /// </summary>
    public static bool ShouldComposeScales(bool hasScaleMods, bool hasPvzStats, bool hasEffectSessionMods = false) =>
        hasScaleMods || hasPvzStats || hasEffectSessionMods;

    /// <summary>Reapply living when PvzStats revision advanced (including clear to empty).</summary>
    public static bool ShouldReapplyPvz(long appliedRevision, long currentRevision) =>
        appliedRevision != currentRevision;

    public static bool ShouldPushOnDirty(
        long cheatDocRevision,
        long appliedCheatRevision,
        long pvzRevision,
        long appliedPvzRevision,
        bool hasPlantScale,
        bool hasZombieScale) =>
        cheatDocRevision != appliedCheatRevision
        || ShouldReapplyPvz(appliedPvzRevision, pvzRevision)
        || hasPlantScale
        || hasZombieScale;
}
