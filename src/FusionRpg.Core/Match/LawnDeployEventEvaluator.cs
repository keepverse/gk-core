using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Match;

/// <summary>The evaluator's own verdict for one check — <see cref="Fires"/> names exactly one
/// <see cref="CaseId"/>, never a batch (spec's own "yes/no + which case").</summary>
public sealed record LawnDeployEventResult(bool Fires, string? CaseId)
{
    public static readonly LawnDeployEventResult NoFire = new(false, null);
}

/// <summary>Per-run bookkeeping the CALLER threads across repeated <see cref="LawnDeployEventEvaluator.Evaluate"/>
/// calls within one match — never held inside the evaluator itself, which stays a pure function (spec's
/// own "not a rules engine" boundary). A case that already fired this run never fires again; once
/// <see cref="TotalFired"/> reaches the tuning's own <c>MaxFiresPerRun</c>, nothing else fires either.</summary>
public sealed record LawnDeployEventRunState(IReadOnlySet<string> FiredCaseIds, int TotalFired)
{
    public static readonly LawnDeployEventRunState Fresh = new(new HashSet<string>(StringComparer.Ordinal), 0);

    public LawnDeployEventRunState WithFired(string caseId) =>
        new(new HashSet<string>(FiredCaseIds, StringComparer.Ordinal) { caseId }, TotalFired + 1);
}

/// <summary>
/// creature-lawn-deploy T2.2/T2.3 (spec-lawn-deploy-events.md) — decides, once per call, whether a
/// plant-side unique-creature deploy becomes available this instant. Mirrors <see cref="Delve.Events.AmbushDraw"/>'s
/// own pure-function-over-state shape exactly: every tunable arrives as a parameter (never a bare
/// literal, never a static-hub read internally), and randomness is derived per <c>(matchSeed, caseId)</c>
/// via <see cref="Battle.SeededRng.DeriveStream"/> — never <c>System.Random</c>, never wall-clock.
///
/// <para>Cases are checked in the tuning file's own declared order (stable, not a set) so two cases
/// whose conditions are BOTH true on the same call resolve deterministically to the first-declared one,
/// not whichever the runtime dictionary enumeration happens to visit first.</para>
/// </summary>
public static class LawnDeployEventEvaluator
{
    public static LawnDeployEventResult Evaluate(
        MatchSnapshot match, LawnDeployRosterSnapshot roster, LawnDeployEventsTuning tuning,
        LawnDeployEventRunState runState, ulong matchSeed)
    {
        if (match is null) throw new ArgumentNullException(nameof(match));
        if (roster is null) throw new ArgumentNullException(nameof(roster));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (runState is null) throw new ArgumentNullException(nameof(runState));

        // No eligible creature to offer -- nothing this evaluator could usefully fire for, regardless of
        // how loudly the board state itself is asking for backup.
        if (roster.Eligible.Count == 0) return LawnDeployEventResult.NoFire;
        if (runState.TotalFired >= tuning.MaxFiresPerRun) return LawnDeployEventResult.NoFire;

        foreach (var c in tuning.Cases)
        {
            if (runState.FiredCaseIds.Contains(c.CaseId)) continue;
            if (!ConditionMet(match, c)) continue;

            var stream = SeededRng.DeriveStream(matchSeed, $"lawn-deploy-event:{c.CaseId}");
            if (stream.NextPerMille() < c.FireChanceMilli)
                return new LawnDeployEventResult(Fires: true, CaseId: c.CaseId);
        }

        return LawnDeployEventResult.NoFire;
    }

    static bool ConditionMet(MatchSnapshot match, LawnDeployEventCaseTuning c)
    {
        if (c.ZombieCountAtLeast is { } zAtLeast && match.ZombieCount < zAtLeast) return false;
        if (c.PlantCountAtMost is { } pAtMost && match.PlantCount > pAtMost) return false;
        return true;
    }
}
