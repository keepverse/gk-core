using FusionRpg.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// D5.11's live-push wave (2026-09-08) — the wire vocabulary for a live delve-room fight, designed as a
/// direct, cited mapping onto <c>session.ts</c>'s own <c>SessionEvent</c> union
/// (<c>gk-web/web/fusion-rpg-web/src/stages/delve/session.ts</c>) rather than a parallel one:
///
/// <list type="bullet">
/// <item><description><see cref="Declared"/> → <c>{type:"declared", source}</c>, <c>source</c> reusing
/// <c>DecisionSource</c>'s own two members verbatim ("player"/"timeout"), matching that file's own doc
/// comment ("mirrors DecisionSource... not a client invention").</description></item>
/// <item><description><see cref="Frozen"/> → <c>{type:"connection-dropped"}</c>. Every freeze cause —
/// three consecutive timeouts, an explicit steer-away, or a genuinely dropped connection — lands on the
/// identical <c>status:"frozen"</c> terminal state; the reducer has no separate variant per cause, so
/// this one push covers all three. The three-timeout cause is ALSO independently derivable client-side
/// by counting `Declared{source:"timeout"}` pushes (mirroring `LiveFreezeTrigger` exactly) — pushing
/// this event too for that same cause is a harmless, idempotent duplicate
/// (`sessionReducer`'s own `"connection-dropped"` case is unconditional), not a bug.</description></item>
/// <item><description><see cref="Resumed"/> → <c>{type:"resumed", replayCount}</c> and
/// <see cref="ReplayConsumed"/> → <c>{type:"replay-consumed"}</c>, pushed together by
/// <see cref="DelveBattleSessionManager"/> at the moment a session (re)starts over a persisted trace:
/// <c>replayCount</c> new <c>ReplayConsumed</c> pushes fire in the same call, immediately, rather than
/// synchronized to each individual replayed decision inside <c>InteractiveIntentSource.Replay</c> (which
/// exposes no such hook, and — deliberately, per <c>DecisionSource.Timeout</c>'s own doc comment — replay
/// is never re-timed, so there is no wall-clock moment worth waiting for between them). The whole prefix
/// length is known upfront from the trace itself, so this is an honest, complete signal, not an
/// approximation.</description></item>
/// <item><description><see cref="TurnStarted"/> is deliberately NOT one of <c>session.ts</c>'s existing
/// variants — whose turn it is does not change that reducer's own
/// status/consecutiveTimeouts/replayRemaining — it is new information a future live-input consumer needs
/// to accept a <c>Declare</c> call and render the dwell countdown.</description></item>
/// </list>
/// </summary>
public static class DelveLiveEventNames
{
    public const string Declared = "DelveDeclared";
    public const string Frozen = "DelveFightFrozen";
    public const string TurnStarted = "DelveTurnStarted";
    public const string Resumed = "DelveResumed";
    public const string ReplayConsumed = "DelveReplayConsumed";
}

/// <summary>
/// The seam between <see cref="DelveBattleSessionManager"/>'s session-lifecycle decisions and the real
/// transport that gets them to a browser. Deliberately NOT <see cref="IHubContext{THub}"/> of
/// <see cref="RpgHub"/> directly: a plain interface keeps <c>DelveBattleSessionManagerTests</c> able to
/// capture every push with a fake, no live SignalR server needed — matching this program's own
/// <c>onDecisionPersisted</c>/<c>onFrozen</c> callback precedent (<see cref="DelveBattleSession"/>)
/// rather than reaching for a mocking library this test project does not otherwise depend on.
/// </summary>
public interface IDelveLivePush
{
    void Push(string eventName, object payload);
}

/// <summary>
/// The one production implementation — broadcasts to <see cref="RpgConstants.WebGroup"/>, the same
/// group every other real-time push in this program already uses
/// (<c>RpgHub.NotifyDelveUpdatedAsync</c>, <c>PvzStatsUpdated</c>), and the same "broadcast, let the
/// client filter by id" discipline: every payload carries its own <c>matchKey</c> so a client watching
/// several delve tabs can tell which fight a push is about.
///
/// <para>Fire-and-forget, matching every other broadcast in this program
/// (<c>RpgHub.PushGrantSnapshotAsync</c>, <c>DelveEndpoints.NotifyDelveUpdatedAsync</c>) — a live delve
/// push is best-effort telemetry about an ALREADY-durable state change (the session's own in-memory
/// state, the persisted decision trace), never the write itself, so a dropped WebSocket frame costs a
/// client a live update, not correctness.</para>
/// </summary>
public sealed class HubDelveLivePush : IDelveLivePush
{
    readonly IHubContext<RpgHub> _hub;

    public HubDelveLivePush(IHubContext<RpgHub> hub) => _hub = hub ?? throw new ArgumentNullException(nameof(hub));

    public void Push(string eventName, object payload) =>
        _ = _hub.Clients.Group(RpgConstants.WebGroup).SendAsync(eventName, payload);
}
