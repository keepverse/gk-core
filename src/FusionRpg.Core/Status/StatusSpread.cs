using FusionRpg.Contracts;
using FusionRpg.Core.Combat;

namespace FusionRpg.Core.Status;

public sealed record StatusResistedEvent(
    string StatusId,
    string HostPtr,
    string? AttackerPtr,
    string GrantId,
    StatusResistReason Reason,
    double Delta,
    DateTimeOffset At);

public sealed record StatusSpreadRequest(
    StatusInstance Source,
    IReadOnlyList<string> CandidateHostPtrs,
    int HopDepth,
    double SpreadChance,
    StatusApplyInput Template);

/// <summary>Spread re-Apply per hop — full L2b each host.</summary>
public static class StatusSpread
{
    public static IReadOnlyList<StatusApplyOutcome> Execute(
        StatusRuntime runtime,
        StatusSpreadRequest request,
        IStatusRng rng,
        DateTimeOffset now)
    {
        var hopLimit = request.Template.SpreadMaxHops > 0
            ? Math.Min(request.Template.SpreadMaxHops, StatusPolicy.ProcDepthLimitDefault)
            : StatusPolicy.ProcDepthLimitDefault;
        if (request.HopDepth > hopLimit)
            return Array.Empty<StatusApplyOutcome>();

        var results = new List<StatusApplyOutcome>();
        foreach (var host in request.CandidateHostPtrs)
        {
            if (string.Equals(host, request.Source.HostPtr, StringComparison.OrdinalIgnoreCase))
                continue;
            if (rng.NextUnit() >= request.SpreadChance)
                continue;

            var input = request.Template with
            {
                HostPtr = host,
                AttackerLess = string.IsNullOrWhiteSpace(request.Template.AttackerPtr)
            };
            var outcome = runtime.Apply(input, rng, now);
            results.Add(outcome);
        }

        return results;
    }

    public static IReadOnlyList<string> ResolveCandidates(
        StatusInstance source,
        TargetSpec? spreadTarget,
        BoardSnapshot board)
    {
        if (spreadTarget != null)
        {
            var ev = new EffectEventDto
            {
                TargetPtr = source.HostPtr,
                ActorPtr = source.AttackerPtr
            };
            return TargetResolver.Resolve(spreadTarget, board, ev)
                .Where(p => !string.Equals(p, source.HostPtr, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return RowNeighbors(source.HostPtr, board);
    }

    public static IReadOnlyList<string> RowNeighbors(string hostPtr, BoardSnapshot board, string sideFilter = "")
    {
        var self = board.FindPtr(hostPtr);
        if (self == null) return Array.Empty<string>();
        return board.Entities
            .Where(e => e.Row == self.Row
                        && !string.Equals(e.Ptr, hostPtr, StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrEmpty(sideFilter)
                            || string.Equals(e.Side, sideFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.Ptr)
            .ToList();
    }
}
