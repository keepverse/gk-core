namespace FusionRpg.Core.World.Movement;

/// <summary>
/// The two door checks lifted out of <see cref="MarchResolver"/> (party-dungeon delve-scope,
/// 2026-09-05, decisions.md:133) so the map's march and the delve's room-to-room step read one
/// rule instead of two copies that could drift. `MarchResolver.Validate` keeps calling this rather
/// than re-inlining the checks — no behaviour change, same refusal strings.
/// </summary>
public static class LaneGate
{
    /// <summary>`at` is the sector the mover currently stands at, about to cross `lane`. Returns
    /// null when the lane is passable.</summary>
    public static MarchRefusal? Refusal(LaneTypeDef type, WorldLane lane, string at)
    {
        if (type.OneWay && !string.Equals(lane.FromSectorId, at, StringComparison.Ordinal))
            return new MarchRefusal("lane.one-way");
        if (type.Gated && lane.GateKeyId != null) return new MarchRefusal("lane.gated");
        return null;
    }
}
