using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Data;

namespace FusionRpg.Server.Notifications;

/// <summary>
/// world-notify-source spec §2 ("Faction to save") — the one seam between a world faction and the
/// save a notification is addressed to (ruling R17: the save is the routing key, map §Identity).
///
/// <para>It is an interface because v1 has exactly one human per world while the ruling that
/// replaced that assumption is already written: once the world's empires are data
/// (`empire-development-map.md` "Not multiplayer…"), the rule becomes "the faction whose empire is
/// the save's human empire", and only this implementation changes. The source itself never learns
/// how a faction becomes a save.</para>
/// </summary>
public interface IWorldFactionSaves
{
    /// <summary>The save this faction's notifications go to, or <c>null</c> when the faction is not a
    /// human player's — an AI faction (Zomboss included) is told nothing.</summary>
    SaveId? SaveOf(WorldFaction faction, WorldHeaderRow header);
}

/// <summary>v1: the world's one human is the `Player`-kind faction, and the world header's
/// `player_id` is the save that created it (`RpgStore.World.cs`). Every other kind maps to no
/// save, which is what keeps an AI faction from ever receiving a notification.</summary>
public sealed class WorldFactionSaves : IWorldFactionSaves
{
    public SaveId? SaveOf(WorldFaction faction, WorldHeaderRow header)
    {
        if (faction is null) throw new ArgumentNullException(nameof(faction));
        if (header is null) throw new ArgumentNullException(nameof(header));

        // AI gets nothing: a notification is addressed to a save and its human empire
        // (map §Identity, "Who receives a notification").
        if (faction.Kind != WorldFactionKind.Player) return null;
        return new SaveId(header.PlayerId);
    }
}
