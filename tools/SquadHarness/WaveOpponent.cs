using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §2: "Squad-vs-authored-wave is measurable but answers a different question,
/// and cannot run at the Θ this program measures... Squad-vs-wave ships as a mode behind
/// <c>--opponent wave</c> and is reported separately, never mixed into the transfer table."
///
/// <para>D46 (open question 1, closed 2026-09-05) makes mirror squads the PRIMARY verdict, so this is
/// content telemetry, not a prerequisite for anything: one squad against one fixed, authored
/// <see cref="WaveDef"/> at that wave's own pinned Θ (<c>WaveCatalog</c> ties every wave's enemy level
/// to its content index — 1/3/6/10), never the 23x23 mirror matrix.</para>
///
/// <para><b>Elements are NOT neutral here</b> (unlike every generated roster, §3): wave enemies are
/// real authored species with real elements (<c>WaveCatalog.Enemies</c>). <see cref="Coverage.WaveOpponentNoteText"/>
/// says so, and this mode's own output is never written into <c>_scope-transfer.json</c> or
/// <c>_squad-scope.json</c>.</para>
/// </summary>
public static class WaveOpponent
{
    public sealed record WaveMatchResult(
        string SquadId, string WaveId, long Victories, long Defeats, long Stalemates,
        long WinShareMilli, long HalfWidthMilli);

    public sealed record WaveOpponentReport(string WaveId, int WaveTheta, IReadOnlyList<WaveMatchResult> Results);

    /// <summary>Every named squad against the one fixed wave, at the WAVE's own Θ (its
    /// <see cref="WaveDef.ContentIndex"/>) -- a stomp at a mismatched Θ measures nothing (spec §2), so
    /// this mode never lets <c>--theta</c> override the content author's own pinned level.</summary>
    public static WaveOpponentReport Run(IReadOnlyList<RosterEntry> squadRoster, string waveId, ulong runSeed, long trials)
    {
        // WaveCatalog.Build() reads CreatureSpeciesCatalog.All, which throws until configured -- this
        // harness builds its OWN actors in memory and never reaches RpgStore (§13 "Never"), so it uses
        // the same store-free compiled default every non-store host bootstraps from
        // (CreatureSpeciesCatalog.ConfigureFromCompiledDefault's own doc: "every host calls this today").
        // Idempotent (Configure just replaces static state), so calling it on every invocation is safe.
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        var wave = WaveCatalog.Get(waveId);
        var theta = wave.ContentIndex;

        var results = squadRoster
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(squad => Measure(squad, wave, theta, runSeed, trials))
            .ToList();

        return new WaveOpponentReport(waveId, theta, results);
    }

    static WaveMatchResult Measure(RosterEntry squad, WaveDef wave, int theta, ulong runSeed, long trials)
    {
        long victories = 0, defeats = 0, stalemates = 0;
        for (var k = 0L; k < trials; k++)
        {
            // Same seed(a, d, k) scheme (spec §7), with the wave's own id standing in for "defender".
            var seed = Seeds.Mix(runSeed, squad.Id, wave.WaveId, k);
            var squadActors = squad.Actors
                .Select((a, i) => SquadMatch.ToActorSetup($"squad:{i}", "squad", a, theta))
                .ToList();
            var setup = new BattleSetup { Squad = squadActors, Wave = wave.Enemies, WaveId = wave.WaveId };
            var report = BattleEngine.Resolve(setup, seed);

            switch (report.Outcome)
            {
                case BattleOutcome.Victory: checked { victories++; } break;
                case BattleOutcome.Defeat: checked { defeats++; } break;
                default: checked { stalemates++; } break;
            }
        }

        var decided = checked(victories + defeats);
        var winShareMilli = decided == 0 ? 0L : checked(victories * 1000L) / decided;
        return new WaveMatchResult(squad.Id, wave.WaveId, victories, defeats, stalemates, winShareMilli,
            Resolution.HalfWidthMilli(decided));
    }
}
