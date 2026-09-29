namespace FusionRpg.Core.Commanders;

/// <summary>
/// A faction — the side a species' progression, a kill and an allocation belong to. Open by design:
/// the world map's factions are data (<c>WorldFaction.FactionId</c>), and this is the same id space
/// (<c>"dave"</c>, <c>"zomboss"</c>). A commander <b>leads</b> an empire; it is never one. The two
/// well-known values are conveniences exactly as <c>ElementIds</c> names well-known elements: they are
/// not a closed set, and nothing enumerates them as "all empires". Which empires one save has is data,
/// owned by <c>save-identity</c>'s <c>rpg_save_empires</c>.
/// </summary>
public readonly record struct EmpireId(string Value)
{
    public static readonly EmpireId Dave = new("dave");
    public static readonly EmpireId Zomboss = new("zomboss");

    public override string ToString() => Value;
}
