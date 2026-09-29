namespace FusionRpg.Server;

/// <summary>
/// D2.16 (spec-delve-battle-profile.md §Structure — "`DelveBattleEndpoints.cs`, `RpgHub` gain
/// steer/declare/freeze/resume"). The live turn-by-turn surface itself is <c>RpgHub</c>'s own
/// <c>Steer</c>/<c>Declare</c>/<c>Resume</c> methods (SignalR, not HTTP — a per-turn dwell window is
/// exactly the low-latency, connection-scoped shape a hub method fits and a REST round-trip does not).
///
/// <para>What THIS file adds beyond the hub: a plain HTTP status read, for a client that has not (yet,
/// or again) established its SignalR connection — the moment right after a page reload, before
/// `session.ts`'s own reducer has anything to react to — to learn "is this fight still live, frozen,
/// or already resolved" without needing the hub at all. Mirrors <c>DelveEndpoints.cs</c>'s own
/// route-lambda-is-one-line-DI-binding-only shape, extracting the actual logic into an
/// <c>internal static Handle*</c> method for direct testability (that file's own established
/// convention, confirmed by <c>DelveDomainsAndStartEndpointsTests.cs</c>).</para>
/// </summary>
public static class DelveBattleEndpoints
{
    public static void MapDelveBattle(this WebApplication app)
    {
        var g = app.MapGroup("/api/delve-battle");

        g.MapGet("/{matchKey}/status", (string matchKey, DelveBattleSessionManager sessions) =>
            HandleGetStatus(matchKey, sessions));
    }

    /// <summary>Extracted from the route lambda so a test can call it directly without a live HTTP
    /// stack, matching <c>DelveEndpoints.HandleGetDelve</c>'s own established pattern.</summary>
    internal static IResult HandleGetStatus(string matchKey, DelveBattleSessionManager sessions)
    {
        var session = sessions.Find(matchKey);
        if (session is null) return Results.NotFound(new { reason = "session.not-found" });

        return Results.Ok(new DelveBattleStatusDto(
            matchKey,
            session.DelveId,
            session.PartyIndex,
            session.Frozen,
            HasReport: session.RunTask is { IsCompletedSuccessfully: true },
            DecisionCount: session.Trace.Count,
            PendingActorKey: session.PendingActorKey));
    }

    public sealed record DelveBattleStatusDto(
        string MatchKey, long DelveId, int PartyIndex, bool Frozen, bool HasReport, int DecisionCount,
        string? PendingActorKey);
}
