namespace FusionRpg.Core.World;

/// <summary>
/// T13 (buff-debuff-scope-todo.md Phase 4). Own-side and unique-creature resolution for the world-map
/// WHERE. No delivery mechanism here — that is T12's `ScopeModifierMilli` field plus whichever future
/// content-specific consumer reads it (resolved during audit: `UpkeepHandicapMilli` itself has no
/// single compute path, so this module does not need one either).
/// </summary>
public static class WorldMapScopeExecutor
{
    /// <summary>
    /// Own-side resolution: a plain `OwnerFactionId` comparison, structurally identical to
    /// `ZoneOfControl.IsHostile`'s own "pure faction-id comparison" (spec-ai-commander.md).
    /// </summary>
    public static bool IsOwnSide(WorldEntity entity, string casterFactionId) =>
        string.Equals(entity.OwnerFactionId, casterFactionId, StringComparison.Ordinal);

    /// <summary>
    /// Unique-creature resolution: walk `Entities[].Members[]` for a matching `InstanceId` — the
    /// world-map equivalent of `MatchUniqueBindingsFacet.TryGet` (battlefield). Null when the
    /// specimen has no legion presence on this world right now.
    /// </summary>
    public static WorldEntity? FindEntityForInstance(WorldState world, string instanceId)
    {
        foreach (var entity in world.Entities)
        {
            foreach (var member in entity.Members)
            {
                if (string.Equals(member.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                    return entity;
            }
        }
        return null;
    }
}
