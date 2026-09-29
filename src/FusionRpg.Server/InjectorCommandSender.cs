using FusionRpg.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// The one place a server feature pushes a command to the injector.
///
/// There were already two copies of this five-line enqueue-and-send
/// (<c>Program.cs</c> and <c>UniqueActorService.cs</c>); rift-gate's
/// <c>POST /api/overlay/leave</c> needed the same thing, and the spec forbids a third. Rather than
/// duplicate it again, the body lives here and <c>Program.cs</c>'s helper delegates — so the injector
/// command seam has exactly one implementation reachable from an endpoint. The copy inside
/// <c>UniqueActorService</c> is deliberately left alone: it is scoped to that service's own domain
/// flow and folding it in is a separate change, out of this program's fence.
///
/// The inbox enqueue is the reliable path; the SignalR push is best-effort, so a dropped connection
/// still delivers on the injector's next poll.
/// </summary>
public sealed class InjectorCommandSender
{
    readonly IHubContext<RpgHub> _hub;
    readonly InjectorCommandInbox _inbox;

    public InjectorCommandSender(IHubContext<RpgHub> hub, InjectorCommandInbox inbox)
    {
        _hub = hub;
        _inbox = inbox;
    }

    public async Task SendAsync(CommandDto cmd)
    {
        _inbox.Enqueue(cmd);
        try
        {
            await _hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("Command", cmd).ConfigureAwait(false);
        }
        catch
        {
            /* inbox poll is the reliable path */
        }
    }
}
