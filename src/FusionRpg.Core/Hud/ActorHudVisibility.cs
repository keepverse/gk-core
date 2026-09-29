namespace FusionRpg.Core.Hud;

/// <summary>
/// Pure glance gate for Band B world HUD — whether a snapshot warrants a slot this frame
/// (actor-hud-unity visual correction). Injector passes F9 mute as <paramref name="shieldBarEnabled"/>.
/// </summary>
public static class ActorHudVisibility
{
    public static bool ShouldShow(ActorHudSnapshot snapshot, bool shieldBarEnabled)
    {
        if (snapshot.Identity.Tier != ActorHudTier.Normal)
            return true;
        if (snapshot.Identity.Flags.Count > 0)
            return true;
        if (!string.Equals(snapshot.Identity.Role, "vanilla", StringComparison.OrdinalIgnoreCase))
            return true;
        if (snapshot.Statuses.Count > 0 || snapshot.Overflow.StatusCount > 0)
            return true;
        if (snapshot.Identity.LevelBand is not null)
            return true;
        if (shieldBarEnabled && snapshot.Resources?.Shield is { Max: > 0, Hp: > 0 })
            return true;
        return false;
    }
}
