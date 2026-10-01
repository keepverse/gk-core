using FusionRpg.Core.Workspace;
using System.Text.Json;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §5/Project structure -- the two on-disk artifacts. Both are JSON WRITERS only:
/// neither touches <c>gk-core/data/tuning</c> (§13 "Never" list), and both carry an <c>at</c> provenance field
/// that is excluded from the determinism hash (the hash is computed over the underlying
/// <see cref="HarnessRun"/>/<see cref="TransferReport.TransferResult"/> content before this wrapper adds
/// provenance, exactly as §9.1 requires).
/// </summary>
public static class Artifacts
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ScopeTransferPath(string repoRoot) =>
        Path.Combine(KeepverseRoots.Workspace(), "_scope-transfer.json", "docs", "research", "passive-tree", "_scope-transfer.json");

    public static string SquadScopePath(string repoRoot) =>
        Path.Combine(KeepverseRoots.Workspace(), "_squad-scope.json", "docs", "research", "passive-tree", "_squad-scope.json");

    /// <summary>Every proposed value is a number AND a half-width (F2 acceptance) -- this is the one
    /// shape every cell in both artifacts uses, so a reader can never mistake a point estimate for a
    /// resolved measurement.</summary>
    public sealed record ValueWithHalfWidth(long WinShareMilli, long HalfWidthMilli);

    public static string WriteTransfer(TransferReport.TransferResult result, string? outPath = null)
    {
        var payload = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            note = "docs/research measurement, not a golden. Changes whenever tuning or trial counts change " +
                   "-- pinning this file would make a rebalance indistinguishable from a determinism break " +
                   "(spec-squad-harness.md 'Testing strategy').",
            theta = result.Theta,
            screeningTrials = result.ScreeningTrials,
            refineTrials = result.RefineTrials,
            allocationShape = result.AllocationShape.ToString(),
            rows = result.Rows.Select(r => new
            {
                buildClass = r.BuildClass,
                duelClosedForm = new ValueWithHalfWidth(r.DuelClosedForm.WinShareMilli, r.DuelClosedForm.HalfWidthMilli),
                duelTrials = new ValueWithHalfWidth(r.DuelTrials.WinShareMilli, r.DuelTrials.HalfWidthMilli),
                squadTrials = new ValueWithHalfWidth(r.SquadTrials.WinShareMilli, r.SquadTrials.HalfWidthMilli),
            }),
            orderingByColumn = result.OrderingByColumn,
            transfers = result.Transfers,
            whyNot = result.WhyNot,
            excludedSquadIds = result.ExcludedSquadIds,
            closedFormDriftWarnings = result.ClosedFormDriftWarnings,
            duelHash = result.DuelHash,
            squadHash = result.SquadHash,
            coverage = result.Coverage,
        };

        var json = JsonSerializer.Serialize(payload, Options);
        var path = outPath ?? ScopeTransferPath(TuningBootstrap.FindRepoRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        return json;
    }

    /// <summary>The full 506-cell (or 8,190-cell) matrix, one row per ordered pair, every cell carrying
    /// its half-width and its <c>lowConfidence</c>/<c>refined</c> status -- spec §5: "The full 506-cell
    /// matrix goes to <c>_squad-scope.json</c>."</summary>
    public static string WriteSquadScope(
        string rosterKind, ScreeningResult screening, RunSpec spec, AllocationShape? shape, string? outPath = null)
    {
        var refinedSet = new HashSet<string>(screening.RefinedPairKeys, StringComparer.Ordinal);
        var payload = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            note = "docs/research measurement, not a golden -- see spec-squad-harness.md 'Testing strategy'.",
            rosterKind,
            theta = spec.Theta,
            screeningTrials = spec.Trials,
            allocationShape = shape?.ToString(),
            hash = DeterminismHash.Hash(screening.Run),
            actorIds = screening.Run.ActorIds,
            pairs = screening.Run.Pairs.Select(p =>
            {
                var decided = checked(p.Victories + p.Defeats);
                return new
                {
                    attackerId = p.AttackerId,
                    defenderId = p.DefenderId,
                    victories = p.Victories,
                    defeats = p.Defeats,
                    stalemates = p.Stalemates,
                    winShareMilli = p.WinShareMilli,
                    halfWidthMilli = Resolution.HalfWidthMilli(decided),
                    lowConfidence = SquadMatch.IsLowConfidence(p),
                    refined = refinedSet.Contains($"{p.AttackerId}->{p.DefenderId}"),
                };
            }),
            coverage = Coverage.Standard(),
        };

        var json = JsonSerializer.Serialize(payload, Options);
        var path = outPath ?? SquadScopePath(TuningBootstrap.FindRepoRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        return json;
    }
}
