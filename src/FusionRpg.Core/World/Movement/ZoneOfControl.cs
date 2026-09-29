namespace FusionRpg.Core.World.Movement;

/// <summary>
/// Where a force's presence is felt (spec-world-movement.md §Zone of control). Two consequences and
/// no more: a legion *entering* a hostile-held sector stops there, and supply refuses to route
/// through it. That is deliberately the whole rule — it makes position matter before a single blow
/// is struck, which is the cheapest strategic depth on offer.
/// </summary>
public static class ZoneOfControl
{
    /// <summary>
    /// Wave 1 has no diplomacy: anyone not you is against you. It lives behind one call so alliances
    /// change one line instead of every caller.
    /// </summary>
    public static bool IsHostile(string factionA, string factionB) =>
        !string.Equals(factionA, factionB, StringComparison.Ordinal);

    /// <summary>
    /// Guards defend the vein or the lair, not the ground, so they project nothing — marching past
    /// one is free, and taking it is a deliberate `clear` order.
    /// </summary>
    public static bool Projects(WorldEntity entity) => Projects(entity.Kind);

    /// <summary>
    /// The same rule asked of a kind alone, because a *remembered* force is not a
    /// <see cref="WorldEntity"/> — belief keeps less than the world does. One rule, two shapes to ask
    /// it about; a second copy in the AI would be a rule that has to be kept in step by hand.
    /// </summary>
    public static bool Projects(WorldEntityKind kind) => kind != WorldEntityKind.Guard;

    /// <summary>True when someone hostile to <paramref name="factionId"/> is standing in the sector.</summary>
    public static bool IsHeldAgainst(WorldState world, string sectorId, string factionId)
    {
        foreach (var e in world.Entities)
            if (string.Equals(e.AtSectorId, sectorId, StringComparison.Ordinal)
                && Projects(e)
                && IsHostile(e.OwnerFactionId, factionId))
                return true;

        return false;
    }
}
