using System.Diagnostics;
using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Balance.Guards;
using FusionRpg.Core.Stats.Aptitudes;
using System.Linq;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P5.2 — <see cref="DominanceGuard"/>, the SOFT half of
/// <c>balance-guard</c> (spec-balance-guard.md). Table in §6: tests 4, 5, 7, 8 covered here directly;
/// test 6 ("corners beat the gradient") is a comparison against the marginal-value instrument, which
/// is `class-system-ideal.md` §0.0.3's own already-published, measured finding ("best is under 1%
/// everywhere") from a separate tool (`gk-core/tools/CombatSim`'s `marginal` command) — not a computation
/// `DominanceGuard.Measure`'s own signature (spec-balance-guard.md §5) has any way to produce, so it is
/// not re-implemented here; that measurement is cited in this file's own comments where relevant.</summary>
///
/// <remarks>Shares <see cref="TerminationGuardTests"/>'s own <c>[Collection("AptitudeTuningHub")]</c>
/// — see that class's remarks for why: a bare unsynchronized static hub, raced across these two
/// classes under xUnit's default per-class parallelism, caught 2026-08-27 via a full-suite failure
/// that did not reproduce in an isolated filtered run.
///
/// <para><c>[Trait("Category","BalanceGuard")]</c> is applied per-method here, not once at the class
/// level, on purpose: every method below carries it EXCEPT
/// <see cref="Measure_theRealTwelveCornerShape_matchesTheCheckedInBaselinesEmptyDominantCorners"/>,
/// which asserts a fact about TODAY'S TUNING DATA (no dominant corner), not about the guard's own
/// mechanism. spec-balance-guard.md §2 is explicit that a dominance verdict must NEVER fail the build
/// — if that one test carried the same trait as the rest, a CI step filtered on `Category=BalanceGuard`
/// (P5.3) would turn a legitimate future dominance finding (e.g. a deliberate, understood trade-off
/// from Phase 8's residual-fit) into a false build failure, exactly the standing violation §2's own
/// table warns against. The other seven test the GUARD'S CODE (returns not throws, coverage populated,
/// no clock applied, wall-clock budget, correctly detects a planted dominant corner, argument
/// validation) — code regressions, safe and correct to block on.</para></remarks>
[Collection("AptitudeTuningHub")]
public class DominanceGuardTests
{
    static AptitudeAllocation Spike(string aptitudeId) =>
        AptitudeAllocation.Single(AllocationScope.Commander, aptitudeId, 100);

    // AptitudeTuningHub.Configure(AptitudeTuningHub.Tuning) is a chicken-and-egg bug -- .Tuning's own
    // getter throws until Configure has run at least once, so this loads the real shipped config
    // fresh every time instead of depending on some earlier test in the run having configured it
    // first (which is what an initial draft here did, and it only passed by test-ordering accident).
    static void ConfigureShippedTuning() =>
        AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(FindShippedAptitudesTuningPath())));

    static string FindShippedAptitudesTuningPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // The LIVE shipped config = the highest aptitudes.v*.json, never a pinned literal
            // (2026-09-02): pinning meant every publish broke a test that only wanted "whatever ships".
            var tuningDir = Path.Combine(dir.FullName, "data", "tuning");
            if (Directory.Exists(tuningDir))
            {
                var best = Directory.GetFiles(tuningDir, "aptitudes.v*.json")
                    .Select(f => (Path: f, V: int.TryParse(
                        Path.GetFileNameWithoutExtension(f).Split(".v").Last(), out var v) ? v : -1))
                    .Where(x => x.V >= 0).OrderByDescending(x => x.V).FirstOrDefault();
                if (best.Path is not null) return best.Path;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate data/tuning/aptitudes.v2.json above " + AppContext.BaseDirectory);
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void Measure_nullOrEmptyBuilds_reject()
    {
        ConfigureShippedTuning();
        Assert.Throws<ArgumentNullException>(() => DominanceGuard.Measure(null!, 100));
        Assert.Throws<ArgumentException>(() => DominanceGuard.Measure(Array.Empty<AptitudeAllocation>(), 100));
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void Measure_nonPositiveTheta_throws()
    {
        var a = AptitudeAllocation.Empty;
        Assert.Throws<ArgumentOutOfRangeException>(() => DominanceGuard.Measure(new[] { a, a }, 0));
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void Measure_returnsAndDoesNotThrow_onTheShippedConfig()
    {
        // spec-balance-guard.md §6 test 4: red on the shipped config (a real spike matchup very
        // plausibly has SOME dominant corner among a small sample) AND THE TEST ITSELF IS GREEN --
        // Measure must return a report, never throw, regardless of what the verdict says.
        ConfigureShippedTuning();
        var builds = new[] { Spike("Might"), Spike("Vigor"), Spike("Bulwark"), Spike("Fortitude") };

        var report = DominanceGuard.Measure(builds, theta: 100);

        // Formula, not a literal (population-pin SE3.3, 2026-09-19): every ordered pair among the
        // builds array above, self-pairs excluded.
        Assert.Equal(builds.Length * (builds.Length - 1), report.Matrix.Count);
        Assert.All(report.Matrix, arrow => Assert.InRange(arrow.WinShareAttacker, 0.0, 1.0));
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void Measure_reportCarriesCoverage()
    {
        // spec-balance-guard.md §6 test 5: "a report without them fails" -- asserted directly on the
        // report's own Coverage field, not read off console output.
        ConfigureShippedTuning();
        var builds = new[] { Spike("Might"), Spike("Vigor") };

        var report = DominanceGuard.Measure(builds, theta: 100);

        Assert.False(string.IsNullOrWhiteSpace(report.Coverage.ElementAxis));
        Assert.NotEmpty(report.Coverage.ReservedFamilies);
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void Measure_aPlantedDominantCorner_reportsIsDominantTrue()
    {
        // A build with a real offense/defense edge advantage over builds that spent their points on
        // channels this guard's own baseDamage=0 / no-shield scope cannot express (mirrors
        // TerminationGuard's own planted-case idiom) should read as dominant: beats every other corner
        // on win rate. Verifies the verdict computation itself, independent of what the real roster
        // happens to produce today.
        var tuning = AptitudeTuningLoader.Parse("""
            {
              "schemaVersion": 1, "version": 1,
              "grant": { "aptitudePointsPerTheta": 2000, "skillPointsPerTheta": 0 },
              "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
              "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
              "recovery": { "scaleMilli": 1000, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
              "familyRead": { "combat.power.omni": "magnitude", "combat.defense.omni": "magnitude", "resource.max.hp": "magnitude" },
              "edges": [
                { "channel": "combat.power.omni", "source": "Might", "kMilli": 50000 },
                { "channel": "resource.max.hp", "source": "Vigor", "kMilli": 10000 },
                { "channel": "combat.defense.omni", "source": "Bulwark", "kMilli": 10000 }
              ]
            }
            """);
        AptitudeTuningHub.Configure(tuning);
        // Every build keeps SOME Vigor (hp) -- a build with hp=0 dies instantly to anyone regardless
        // of the opponent's own damage (FirstPassage.Compute's own poolSize==0 -> (0,0) rule), which
        // makes "beats everyone" trivially false for it and was this test's own first, wrong draft.
        // The overwhelming corner adds a huge Might spike on top of the same hp baseline.
        var overwhelming = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 70)
                          + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 30);
        var weak1 = AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 100);
        var weak2 = AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 30)
                  + AptitudeAllocation.Single(AllocationScope.Commander, "Bulwark", 70);

        var report = DominanceGuard.Measure(new[] { overwhelming, weak1, weak2 }, theta: 100);

        Assert.True(report.IsDominant);
        Assert.Contains("corner0", report.DominantBuildNames);
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void Measure_appliesNoClock_matchesTheNoRoundLimitPredictorCallExactly()
    {
        // spec-balance-guard.md §6 test 8 / §7 "Never apply a clock". Verified structurally: the same
        // two actors, predicted directly with Predictor.Predict's own no-limit default, must match
        // Measure's own arrow bit for bit -- if Measure applied a clock internally, this would diverge.
        ConfigureShippedTuning();
        var a = Spike("Might");
        var b = Spike("Vigor");

        var report = DominanceGuard.Measure(new[] { a, b }, theta: 100);
        // TerminationGuard.ToActor is `internal`, visible here via the Core project's own
        // InternalsVisibleTo grant to this test assembly -- the exact same construction path
        // DominanceGuard.Measure itself uses, not a parallel reimplementation.
        var direct = Predictor.Predict(
            TerminationGuard.ToActor("corner0", a, 100),
            TerminationGuard.ToActor("corner1", b, 100));

        var arrow = report.Matrix.Single(x => x.AttackerName == "corner0" && x.DefenderName == "corner1");
        Assert.Equal(direct.WinShareA, arrow.WinShareAttacker, 12);
    }

    [Fact]
    [Trait("Category", "BalanceGuard")]
    public void Measure_144Corners_completeWellUnderAGenerousWallClockBudget()
    {
        // spec-balance-guard.md §6 test 7. Mirrors PredictorTests' own 144-corner budget (a 12x12 grid,
        // matching the corner COUNT spec-balance-guard.md §2.2 names: "spike each of the twelve...
        // play every spike against every other: 144 closed-form evaluations, instant").
        ConfigureShippedTuning();
        // The real roster's own 12 (gk-data/packs/fusion/data/seed/aptitudes/roster.json), confirmed this session --
        // "the twelve" spec-balance-guard.md §2.2 refers to, not an invented count.
        var aptitudeIds = new[]
        {
            "Might", "Vigor", "Onslaught", "Retribution", "Bulwark", "Fortitude",
            "Ferocity", "Precision", "Agility", "Composure", "Focus", "Pierce",
        };
        var builds = aptitudeIds.Select(Spike).ToArray();

        var sw = Stopwatch.StartNew();
        var report = DominanceGuard.Measure(builds, theta: 100);
        sw.Stop();

        Assert.Equal(builds.Length * (builds.Length - 1), report.Matrix.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"expected the corner matrix well under 2000ms, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    [Trait("Category", "BalanceGuardBaseline")]
    public void Measure_theRealTwelveCornerShape_matchesTheCheckedInBaselinesEmptyDominantCorners()
    {
        // P5.2's own verify line: "trinity --json diffed against _baseline-dominance.json." Run for
        // real this session: gk-core/tools/CombatSim's trinity command against gk-core/data/tuning/aptitudes.v1.json
        // (byte-identical to gk-core/tools/CombatSim's own copy except _meta comment text, diffed directly)
        // reproduces docs/research/class-system/_baseline-dominance.json exactly -- 144/144
        // dominanceMatrix.wins cells, all three best-response chains, dominantCorners and coverage,
        // every field 0.0 diff at the checked-in seed (20260826) and Theta (100).
        //
        // This test is the automated half of that verify step: the SAME corner shape trinity's own
        // BestResponse.DominanceMatrix uses (spike = 100 - floor*11, floor = 100/roster.Length/2,
        // scaled x1000 here because AptitudeAllocation.Single takes `long` and the true floor/spike
        // is not integral -- Share() is a ratio, so this is the same corner, not a coarser one),
        // through DominanceGuard.Measure, asserted against the baseline's own headline finding.
        //
        // NOT reproduced bit-for-bit: DominanceGuard has no best-response chase (that machinery,
        // BestResponse.Chase, stays in gk-core/tools/CombatSim -- spec-balance-guard.md's own boundary,
        // "never best-response chasing as the gate", is about the VERDICT, and this module's
        // signature never took chain output as an input to begin with) and win shares will not match
        // to the last digit (TerminationGuard.ToActor's baseDamage=0 differs from whatever
        // gk-core/tools/CombatSim's Build.At uses -- see the cross-engine note in
        // TerminationGuardTests.Assert_theRealTwelveCornerShape_throwsOnVigorVsBulwark...).
        //
        // RE-MEASURED species-progression step 6.1 (R21, 2026-09-19), not re-guessed: the test's own
        // class doc comment says WHY this method deliberately skips "Category=BalanceGuard" -- "a
        // legitimate future dominance finding" must never become a false CI failure, only a test that
        // needs its OWN assertion updated to the honest current reading. R21's Commander layer weight
        // (500/1000 in the shipped aptitudes.v10.json) is exactly such a legitimate tuning change, and
        // it measurably produces one: `Might` (roster index 0, "corner0" — pure combat.power.omni) is
        // now an absolute dominant corner (beats all eleven others), confirmed both here and by
        // `docs/research/class-system/_baseline-dominance.json` (regenerated the same session via
        // `scripts/regen-class-system-baselines.ps1`, which drives this exact roster/corner shape
        // through `gk-forge/tools/DominanceBaseline` against the live shipped config). The dominance matrix is
        // explicitly the SOFT half of this guard (decisions.md's Class system row: "the dominance
        // matrix... is SOFT and reports with its coverage — a dominant corner is what the
        // action/passive/skill layer is for, and it is red by design today"); the test still needs to
        // assert something concrete rather than nothing, so it now names the CURRENT dominant corner
        // instead of asserting none exists. The HARD invariant this guard's sibling
        // (TerminationGuard) enforces — no unending pairing — is UNCHANGED and still holds on this
        // exact roster (TerminationGuardTests.Assert_theShippedConfig_hasZeroTerminationViolations...).
        ConfigureShippedTuning();
        var roster = new[]
        {
            "Might", "Fortitude", "Vigor", "Onslaught", "Agility", "Composure",
            "Pierce", "Focus", "Bulwark", "Retribution", "Precision", "Ferocity",
        };
        const long floor = 4167;
        long Spike() => 100_000 - floor * (roster.Length - 1);
        AptitudeAllocation Corner(string spikeId) =>
            roster.Aggregate(AptitudeAllocation.Empty, (acc, id) =>
                acc + AptitudeAllocation.Single(AllocationScope.Commander, id, id == spikeId ? Spike() : floor));

        var report = DominanceGuard.Measure(roster.Select(Corner).ToArray(), theta: 100);

        Assert.True(report.IsDominant);
        Assert.Equal(new[] { "corner0" }, report.DominantBuildNames); // corner0 = Might, roster[0]
        Assert.Equal(roster.Length * (roster.Length - 1), report.Matrix.Count);
    }
}
