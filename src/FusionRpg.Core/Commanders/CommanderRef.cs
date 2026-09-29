namespace FusionRpg.Core.Commanders;

/// <summary>
/// The unit holding the commander role. Open by owner ruling (2026-09-17): a commander is a unique
/// creature carrying one more role, so a new commander is a directory row, never a new member of a
/// type. The stable string form keeps the load-bearing <c>commander:</c> prefix, so it can never
/// collide with a faction id (<see cref="EmpireId"/>, bare <c>"dave"</c>) or a battle actor key
/// (<c>"squad:N"</c> / <c>"wave:N"</c>).
/// </summary>
public readonly record struct CommanderRef(string StableId)
{
    public override string ToString() => StableId;
}
