using FusionRpg.Contracts;
using FusionRpg.Core.Overlay;

namespace FusionRpg.Server;

/// <summary>
/// rift-gate <c>overlay-hide</c>: the web FE's Leave control asks the host to close the overlay window.
///
/// The route is named for the player action (<c>Leave</c>), not the mechanism — the command carried is
/// <see cref="OverlayCommandNames.Hide"/> ("overlay.hide"). It rides the SAME server→injector command
/// seam every other host command uses (<see cref="InjectorCommandSender"/>), so the injector host hides
/// in process and the launcher host relays one pipe verb. One close path; no second transport.
///
/// Deliberately touches NO story/onboarding state: closing the window must never be recorded as
/// finishing or skipping the prologue. That contract is asserted by <c>OverlayLeaveEndpointsTests</c>.
/// </summary>
public static class OverlayEndpoints
{
    public static void MapOverlay(this WebApplication app)
    {
        app.MapPost("/api/overlay/leave", async (InjectorCommandSender sender) =>
        {
            await sender.SendAsync(new CommandDto { Name = OverlayCommandNames.Hide });
            // Hiding is idempotent and non-blocking: we do not wait on the window, so "accepted"
            // is the honest answer rather than a claim that the window is already gone.
            return Results.Ok(new { ok = true });
        });
    }
}
