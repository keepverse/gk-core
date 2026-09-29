namespace FusionRpg.Core.Hud;

/// <summary>Maps pinned <c>progression.power</c> Θ to a compact lawn badge — never raw Θ on wire.</summary>
public static class PowerBandDisplay
{
    /// <summary>Θ → display int in 1..<see cref="ActorHudTuningHub.Tuning"/>.BadgeMax.</summary>
    public static int FromTheta(long theta)
    {
        var max = ActorHudTuningHub.Tuning.BadgeMax;
        if (max < 1)
            throw new InvalidOperationException("actor-hud tuning: badgeMax must be at least 1");

        if (theta <= 0)
            return 1;

        return theta >= max ? max : (int)theta;
    }
}
