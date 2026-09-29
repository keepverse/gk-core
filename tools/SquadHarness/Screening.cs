namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §9.2's "Two-stage design": "A screening pass at <c>--trials 3000</c> over all
/// 506 pairs; then <c>--refine</c> re-measures only the cells whose gap falls inside their own
/// half-width, at <c>--trials 40000</c>." A cell that already resolved cleanly at the screening trial
/// count is never re-run -- <see cref="Resolution.CannotCallWinner"/> is the exact per-cell test.
///
/// <para><b>Refine REPLACES a cell's trial count, it does not add to it.</b> The spec's own worked
/// half-widths (1.0pp at ~9,600, 0.5pp at ~38,400) are computed from <c>n</c> being the TOTAL trial
/// count, not screening-plus-refine, and §7's common random numbers mean re-running the same
/// <c>(attacker, defender)</c> pair at <c>--trials 40000</c> already reuses the identical first 3,000
/// trials by construction (same <c>seed(a, d, k)</c> for <c>k &lt; 3000</c>) -- so "refine" is simply
/// "measure this one cell deeper", never a second pass stacked on top of the first.</para>
/// </summary>
public sealed record ScreeningResult(HarnessRun Run, IReadOnlyList<string> RefinedPairKeys);

public static class Screening
{
    public static ScreeningResult RunWithRefine(
        string rosterKind, IReadOnlyList<RosterEntry> roster, RunSpec screenSpec, long? refineTrials, bool parallel)
    {
        var screened = Sweep.Run(rosterKind, roster, screenSpec, parallel);
        if (refineTrials is null)
            return new ScreeningResult(screened, Array.Empty<string>());

        var byId = roster.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var refineSpec = screenSpec with { Trials = refineTrials.Value };
        var refinedKeys = new List<string>();
        var finalPairs = new List<PairResult>(screened.Pairs.Count);

        foreach (var pair in screened.Pairs)
        {
            var decided = checked(pair.Victories + pair.Defeats);
            if (Resolution.CannotCallWinner(pair.WinShareMilli, decided))
            {
                var refined = SquadMatch.Measure(byId[pair.AttackerId], byId[pair.DefenderId], refineSpec);
                finalPairs.Add(refined);
                refinedKeys.Add($"{pair.AttackerId}->{pair.DefenderId}");
            }
            else
            {
                finalPairs.Add(pair);
            }
        }

        // Same canonical sort Sweep.Run itself applies -- refine touches values, never the ordering rule.
        var sorted = finalPairs
            .OrderBy(p => p.AttackerId, StringComparer.Ordinal)
            .ThenBy(p => p.DefenderId, StringComparer.Ordinal)
            .ToList();
        return new ScreeningResult(new HarnessRun(rosterKind, screened.ActorIds, sorted), refinedKeys);
    }
}
