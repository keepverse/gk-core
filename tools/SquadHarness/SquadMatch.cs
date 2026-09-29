using FusionRpg.Core.Battle;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>One roster entry, either scope: a duel build (one actor) or a squad build (six). Carries a
/// stable <see cref="Id"/> that <see cref="Seeds.Mix"/> hashes directly -- never a positional index --
/// so a pair's seed depends on nothing but the two Ids and the trial index (spec §7's determinism, and
/// this module's own shuffle/subset-independence tests).</summary>
public sealed record RosterEntry(string Id, IReadOnlyList<AptitudeAllocation> Actors)
{
    public static RosterEntry From(NamedBuild build) => new(build.Id, new[] { build.Allocation });
    public static RosterEntry From(SquadBuild build) => new(build.Id, build.Actors);
}

/// <summary>One measurement's parameters. <c>Seed</c> has no default anywhere upstream of this record --
/// <c>Program.cs</c>'s CLI parse is where "no default" is enforced (spec §"Commands": "a seed nobody
/// chose behaves like one somebody did").</summary>
public readonly record struct RunSpec(long Theta, long Trials, ulong RunSeed);

/// <summary>
/// spec-squad-harness.md §8/§9.1 -- one ordered pair's outcome, in the shape the determinism hash reads.
/// Every field is <c>string</c> or <c>long</c> (or, on <see cref="HarnessRun"/>, a list of those) --
/// this module's own <c>No_double_reaches_the_hash_input</c> test checks it by reflection, because a
/// hash of a floating value is non-deterministic across runtimes (docs/architecture/numeric-types.md numeric rules).
/// </summary>
public sealed record PairResult(
    string AttackerId, string DefenderId,
    long Victories, long Defeats, long Stalemates, long WinShareMilli);

/// <summary>The canonical, hashable output of one sweep. <see cref="Pairs"/> is always sorted by
/// <c>(AttackerId, DefenderId)</c> ordinal before this record is built (see <see cref="Sweep.Run"/>), so
/// shuffling the roster construction order changes which pairs exist, never what a surviving pair
/// says.</summary>
public sealed record HarnessRun(string RosterKind, IReadOnlyList<string> ActorIds, IReadOnlyList<PairResult> Pairs);

/// <summary>
/// One ordered pair, resolved over the shipped engine -- spec-squad-harness.md's own Code style block,
/// reproduced here. Nothing here re-implements combat: the numbers come back out of
/// <see cref="BattleEngine.Resolve"/>, which is already byte-identical for a given (setup, seed,
/// platform).
///
/// <para><b>Stalemates leave the denominator</b> (decisions.md:103 -- "win rate is the metric... never
/// under a clock"). <see cref="BattleEngine"/> HAS a horizon (its <c>maxBattleTick</c>), so this trial
/// harness cannot inherit the closed form's no-clock property; excluding stalemates from the
/// denominator and reporting them separately is the closest honest equivalent.</para>
///
/// <para><b>long, checked, divide last.</b> Counts are magnitudes and must not be <c>int</c>: 506 pairs
/// at 40,000 trials is inside <c>int</c> today and would not be after one <c>--trials</c> change, which
/// is exactly the "small only at the calibration point" defect AGENTS.md names. The share is a BOUNDED
/// RATIO (0..1000) and so exempt from the long-magnitude rule, but it is still widened before the
/// multiply and divided by 1000 exactly once, at the end.</para>
/// </summary>
public static class SquadMatch
{
    /// <summary>spec §8: a cell whose stalemate rate exceeds this is refused, not scored -- carried here
    /// as a bounded per-mille ratio (structural threshold, exempt from the magnitude-cap rule), not
    /// folded into <see cref="PairResult"/> so the hashed record stays exactly the six fields above.</summary>
    public const long StalemateFlagMilli = 200; // 20%, spec §8

    public static bool IsLowConfidence(PairResult r)
    {
        var total = checked(r.Victories + r.Defeats + r.Stalemates);
        if (total == 0) return false;
        var stalemateMilli = checked(r.Stalemates * 1000L) / total;
        return stalemateMilli > StalemateFlagMilli;
    }

    /// <summary>Builds one <see cref="BattleActorSetup"/> from an allocation, carried as a Hub input
    /// (battle-hub-fuse T6: <c>AptitudeResolver.ResolveForBattle</c> is deleted -- the allocation reaches
    /// battle as <see cref="BattleHubInputs.Aptitude"/>, resolved through the same
    /// <see cref="AptitudeResolver.Resolve"/> the overlay seam uses) -- the trial-path twin §11 names,
    /// never <c>TerminationGuard</c>'s closed-form <c>ToActor</c> (the closed-form/duel-only helper).
    /// Elements stay null (neutral) throughout -- spec §3.</summary>
    public static BattleActorSetup ToActorSetup(string key, string side, AptitudeAllocation allocation, int theta)
    {
        return new BattleActorSetup
        {
            Key = key,
            Side = side,
            SpeciesId = "squad-harness",
            Level = theta,
            MaxHp = BattleRuleset.BaseHp(theta),
            Atk = BattleRuleset.BaseAtk(theta),
            Defense = BattleRuleset.BaseDefense(theta),
            HubInputs = new BattleHubInputs { Aptitude = allocation },
        };
    }

    public static BattleSetup ToBattleSetup(RosterEntry attacker, RosterEntry defender, int theta)
    {
        // spec §2: squad-vs-squad is expressible today by placing the opposing side on "wave" -- a
        // labelling convention, not a mechanic. The attacking build is always "squad".
        var squad = attacker.Actors
            .Select((a, i) => ToActorSetup($"squad:{i}", "squad", a, theta))
            .ToList();
        var wave = defender.Actors
            .Select((a, i) => ToActorSetup($"wave:{i}", "wave", a, theta))
            .ToList();
        return new BattleSetup { Squad = squad, Wave = wave };
    }

    /// <summary>One ordered pair -> long counts, seeded, checked. The seed keys off the two builds'
    /// own stable <see cref="RosterEntry.Id"/> strings (<see cref="Seeds.Mix"/>), never a positional
    /// index -- see <see cref="Seeds"/>'s own doc for why a position would break re-running a
    /// subset of a roster.</summary>
    public static PairResult Measure(RosterEntry attacker, RosterEntry defender, RunSpec spec)
    {
        if (spec.Theta <= 0) throw new ArgumentOutOfRangeException(nameof(spec), spec.Theta, "theta must be positive");
        if (spec.Theta > int.MaxValue)
            // spec §7's numeric-types table: "the harness rejects a Theta above int.MaxValue loudly at
            // parse rather than casting" -- BattleActorSetup.Level is int.
            throw new ArgumentOutOfRangeException(nameof(spec), spec.Theta, "theta exceeds int.MaxValue (the engine boundary)");
        var theta = (int)spec.Theta;

        long victories = 0, defeats = 0, stalemates = 0;
        for (var k = 0L; k < spec.Trials; k++)
        {
            // Common random numbers (spec §7): the seed is a pure function of the pair and the trial
            // index, so the SAME k is the same randomness whichever roster/column reads it.
            var seed = Seeds.Mix(spec.RunSeed, attacker.Id, defender.Id, k);
            var setup = ToBattleSetup(attacker, defender, theta);
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
        return new PairResult(attacker.Id, defender.Id, victories, defeats, stalemates, winShareMilli);
    }
}

/// <summary>Runs every ordered pair of a roster and produces the canonical, hashable
/// <see cref="HarnessRun"/>. spec §7: "Parallelism is permitted only where each trial's seed is already
/// independent of execution order and results are re-sorted... before aggregation" -- both the parallel
/// and serial paths funnel through this one sort, which is what makes them agree by hash
/// (<c>Parallel_and_serial_agree</c>).</summary>
public static class Sweep
{
    public static HarnessRun Run(string rosterKind, IReadOnlyList<RosterEntry> roster, RunSpec spec, bool parallel = false)
    {
        // Every build seeds off its OWN Id (Seeds.Mix), never a positional index, so the pairs below
        // can be built in whatever order this list happens to enumerate -- construction order never
        // reaches the seed, which is what makes Reordering_the_roster_does_not_move_a_cell hold even
        // when the roster passed in is a NARROWED subset of a larger one (spec §9.2's refine pass).
        var sortedIds = roster.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var byId = roster.ToDictionary(r => r.Id, StringComparer.Ordinal);

        var pairs = new List<(RosterEntry Attacker, RosterEntry Defender)>();
        foreach (var aId in sortedIds)
        foreach (var dId in sortedIds)
        {
            if (aId == dId) continue;
            pairs.Add((byId[aId], byId[dId]));
        }

        IEnumerable<PairResult> results = parallel
            ? pairs.AsParallel().Select(p => SquadMatch.Measure(p.Attacker, p.Defender, spec)).AsSequential()
            : pairs.Select(p => SquadMatch.Measure(p.Attacker, p.Defender, spec));

        // Re-sorted before aggregation regardless of path (spec §7) -- AsParallel().Select() does not
        // preserve input order, so this sort is load-bearing, not defensive.
        var sorted = results
            .OrderBy(r => r.AttackerId, StringComparer.Ordinal)
            .ThenBy(r => r.DefenderId, StringComparer.Ordinal)
            .ToList();

        return new HarnessRun(rosterKind, sortedIds, sorted);
    }
}
