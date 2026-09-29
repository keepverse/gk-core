using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;

namespace FusionRpg.Server.Notifications;

/// <summary>notify-service spec §2 - one addressed draft. <see cref="SaveId"/> is `players.id` (R17).</summary>
public sealed record AddressedDraft(SaveId SaveId, NotificationDraft Draft);

/// <summary>notify-service spec §3 step 3 - everything a source needs for ONE resolved turn.
/// `Report` is null when the stored body was trimmed (never a replay, spec §3 step 4).</summary>
public sealed record WorldTurnNotificationContext(
    string WorldId,
    int ResolvedTurn,
    bool IsLatestResolved,
    WorldHeaderRow Header,
    WorldState CurrentWorld,
    TurnReport? Report);

/// <summary>notify-service spec §3 - open for extension (SOLID O/D): a new domain adds a class that
/// implements this and registers it in DI. The pump depends on the interface, never on a concrete
/// source, and never edits for a new one.</summary>
public interface IWorldTurnNotificationSource
{
    string SourceId { get; }

    /// <summary>Drafts for ONE resolved turn, each already addressed to a player. Pure over the
    /// context plus read-only store reads; never writes, never pushes.</summary>
    IEnumerable<AddressedDraft> Collect(WorldTurnNotificationContext ctx);
}
