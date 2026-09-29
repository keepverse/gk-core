using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// combat-ai `decision-perf` (module 7, CAI1.14, spec-decision-perf.md): the decision path allocates
/// ZERO bytes, measured in bytes on the current thread with the warm+collect harness
/// `KernelAllocationTests` established — never milliseconds, never a fake collaborator standing in for
/// the allocator that actually leaked.
///
/// <para>Every optimisation here rewrites HOW a value is produced, never WHICH — so each one is paired
/// with an identity test against the implementation it replaced (the LINQ chain, the filtering loop).</para>
/// </summary>
public class DecisionAllocationTests
{
    const string ActorKey = "wave:0";

    static ActorDerivedSnapshot Snapshot(params (string resourceId, double max, double regen)[] resources)
    {
        var composer = new DerivedComposer(DerivedStatRegistry.CreateDefault());
        var mods = new List<DerivedModifier>
        {
            new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, 0.0, SourceId: "test"),
            new(DerivedStatChannels.ProgressionRealm, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
        };
        foreach (var (id, max, regen) in resources)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, max, SourceId: "test"));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, regen, SourceId: "test"));
        }
        return composer.Compose(mods);
    }

    static CostLedger Ledger(
        IReadOnlyDictionary<string, IReadOnlyList<ActionCostRow>> costs,
        ActorDerivedSnapshot derived, ActorResourcePools pools) =>
        new(costs, _ => pools, _ => derived, (_, _) => 1, () => 0L);

    /// <summary>
    /// Site 1 (spec-decision-perf.md): `CostLedger.RowsFor` allocated a `List&lt;ActionCostRow&gt;` on
    /// EVERY call, and `Check` is gate 3 of `UsabilityEvaluator`, so that was one list per candidate
    /// action per decision. Production collaborators, deliberately: a real `CostLedger` over real rows
    /// and a real `ActorResourcePools` — a fake here is exactly what hid this site in the first place.
    ///
    /// <para><b>Two assertions, because the path has two halves.</b> An action with NO rows never
    /// reaches `ActorResourcePools.Resolve`, so that half is now a hard zero. An action WITH rows also
    /// calls `Resolve`, which still allocates — `ResourceChannelReader.Max` calls
    /// `DerivedStatChannels.ResourceMax(id)`, which builds `$"resource.max.{id}"` per call
    /// (`DerivedStatChannels.cs:539`). Interning those ids is CAI1.14's own next site and is OUT of this
    /// lane's fence (`gk-core/src/FusionRpg.Core/Stats/**`); filed in the owning program's todo. The assertion
    /// that matters for THIS change is therefore comparative: `Check` must add exactly ZERO bytes of
    /// its own beyond the pool read it has to make.</para>
    /// </summary>
    [Fact]
    public void CostLedger_Check_allocates_zero_bytes_of_its_own()
    {
        var derived = Snapshot(("stamina", 1000, 0), ("spirit", 1000, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var onCommit = new ActionCostRow[]
        {
            new("act.three", "stamina", ValueSpec.Of(10), ActionCostTiming.OnCommit),
            new("act.three", "spirit", ValueSpec.Of(20), ActionCostTiming.OnCommit),
        };
        var costs = new Dictionary<string, IReadOnlyList<ActionCostRow>>
        {
            ["act.three"] = new ActionCostRow[]
            {
                onCommit[0], onCommit[1],
                new("act.three", "stamina", ValueSpec.Of(30), ActionCostTiming.PerTick),
            },
        };
        var ledger = Ledger(costs, derived, pools);

        // (a) No rows at all: the rows path must contribute EXACTLY nothing. This is the assertion the
        // site-1 fix is accountable for, and it is a hard zero.
        var noRows = Measure(() => ledger.Check(ActorKey, "act.absent"));
        Assert.True(noRows == 0, $"Check over an action with no cost rows allocated {noRows} bytes; budget is 0");

        // (b) With rows, the assertion is DIFFERENTIAL and exact: `Check` must add nothing beyond the
        // pool reads it has to make. Each row needs one `ActorResourcePools.Resolve`, so the baseline is
        // exactly those two calls; any extra byte is `Check`'s own. This is how the enumerator
        // allocation (a `foreach` over the `IReadOnlyList<ActionCostRow>` `RowsFor` returns) and the
        // filtered rows list were both found and then removed. The residual is NOT `Check`'s: it is
        // `ResourceChannelReader`'s uninterned `$"resource.max.{id}"`
        // (`gk-core/src/FusionRpg.Core/Stats/Derived/DerivedStatChannels.cs:539`), on a path this lane's file
        // fence excludes, so it is named in the evidence rather than asserted away here.
        var poolsOnly = Measure(() =>
        {
            pools.Resolve("stamina", 0, derived);
            pools.Resolve("spirit", 0, derived);
        });
        var withRows = Measure(() => ledger.Check(ActorKey, "act.three"));
        Assert.True(withRows == poolsOnly,
            $"Check over two OnCommit rows allocated {withRows} bytes while the two pool reads it makes " +
            $"cost {poolsOnly}; Check's own budget is 0");
    }

    /// <summary>Warm three times (so a tiered-JIT promotion charge cannot land in the measured pass),
    /// collect, then measure one call. Returns bytes allocated on this thread.</summary>
    static long Measure(Action measured)
    {
        for (var pass = 0; pass < 3; pass++) measured();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        measured();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// The identity half of the site-1 change: precomputing the per-`(actionId, timing)` rows at
    /// construction must return the SAME rows in the SAME order as the filtering loop it replaced. The
    /// rows are private, so the check goes through the public surface: the FIRST unaffordable OnCommit
    /// row's resource id is what `Check` reports, so row order is observable.
    /// </summary>
    [Fact]
    public void RowsFor_returns_the_same_rows_in_the_same_order_as_the_filtering_implementation()
    {
        var derived = Snapshot(("stamina", 5, 0), ("spirit", 500, 0), ("qi", 500, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);

        var authored = new ActionCostRow[]
        {
            new("act.mixed", "stamina", ValueSpec.Of(50), ActionCostTiming.OnCommit), // 5 < 50 -> refuses
            new("act.mixed", "spirit", ValueSpec.Of(10), ActionCostTiming.PerTick),   // filtered out
            new("act.mixed", "qi", ValueSpec.Of(10), ActionCostTiming.OnCommit),      // affordable
        };
        var costs = new Dictionary<string, IReadOnlyList<ActionCostRow>> { ["act.mixed"] = authored };
        var ledger = Ledger(costs, derived, pools);

        // The reference: literally the loop this replaced.
        var expected = new List<ActionCostRow>();
        foreach (var row in authored)
            if (row.When == ActionCostTiming.OnCommit)
                expected.Add(row);

        Assert.Equal(2, expected.Count);
        Assert.Equal("stamina", expected[0].ResourceId);
        Assert.Equal("qi", expected[1].ResourceId);

        var result = ledger.Check(ActorKey, "act.mixed");
        Assert.False(result.IsUsable);
        // The first row in AUTHORED order is the one that refuses — a rebuilt or reordered list would
        // report "qi" instead.
        Assert.Equal("stamina", result.Detail);
    }

    /// <summary>
    /// `Argmax` identity (spec-decision-perf.md: "Argmax_picks_the_same_candidate_as_the_LINQ_chain
    /// over shuffled inputs"). The shipped chain is `OrderByDescending(Score).ThenBy(ActorKey)`; the
    /// indexed loop must agree for every rotation of the same input, including a deliberate score tie.
    /// </summary>
    [Fact]
    public void Argmax_picks_the_same_candidate_as_the_LINQ_chain_over_shuffled_inputs()
    {
        var w = new ScoringWeights(1, 0, 0, 0, 0, 0, 0, 2, 32);
        var candidates = new List<TargetCandidate>
        {
            Candidate("charlie", hitChanceMilli: 500),
            Candidate("alpha", hitChanceMilli: 900),
            Candidate("bravo", hitChanceMilli: 900),   // ties alpha on score; ordinal decides
            Candidate("delta", hitChanceMilli: 100),
        };

        for (var rotation = 0; rotation < candidates.Count; rotation++)
        {
            var shuffled = new List<TargetCandidate>(candidates.Count);
            for (var i = 0; i < candidates.Count; i++)
                shuffled.Add(candidates[(i + rotation) % candidates.Count]);

            var reference = shuffled
                .OrderByDescending(c => CandidateScorer.Score(c, currentRound: 0, w))
                .ThenBy(c => c.ActorKey, StringComparer.Ordinal)
                .First();

            var viaCore = CandidateScorer.ChooseTarget(shuffled, currentRound: 0, w);
            Assert.Equal(reference.ActorKey, viaCore!.Value.ActorKey);
        }
    }

    /// <summary>
    /// `TargetStage.BuildCapped` identity: the cap must yield exactly the set `Take(n)` would, and it
    /// must count only candidates that PASSED the skip filters — a cap applied before filtering would
    /// silently score fewer real candidates than the authored bound.
    /// </summary>
    [Fact]
    public void Capping_the_candidate_loop_yields_the_identical_set_Take_produced()
    {
        // 6 live keys: "me" (self), 2 allies (same side), 1 unreadable enemy, 2 readable enemies.
        var live = new[] { "me", "ally1", "ally2", "hidden", "enemy1", "enemy2" };
        var sides = new Dictionary<string, int>(StringComparer.Ordinal)
        { ["me"] = 0, ["ally1"] = 0, ["ally2"] = 0, ["hidden"] = 1, ["enemy1"] = 1, ["enemy2"] = 1 };
        var readable = new HashSet<string>(StringComparer.Ordinal) { "me", "ally1", "ally2", "enemy1", "enemy2" };

        var expected = live
            .Where(k => k != "me" && sides[k] != 0 && readable.Contains(k))
            .Take(1)
            .ToArray();

        var built = new List<string>();
        TargetStage.BuildCapped("me", mySide: 0, live,
            sideOf: k => sides[k], isReadable: k => readable.Contains(k),
            maxCandidatesScored: 1,
            (string key, out TargetCandidate c) => { built.Add(key); c = Candidate(key, hitChanceMilli: 1); return true; });

        Assert.Equal(expected, built);        // the cap counted post-filter candidates only
        Assert.Equal("enemy1", built.Single());
    }

    /// <summary>
    /// Site 4's identity contract, named by the acceptance list: the trace line must record the SAME
    /// top three after `TopThree` stopped being a `Select`/`OrderBy`/`ThenBy`/`Take`/`ToList` chain.
    /// Compared against the LINQ chain itself, including a deliberate score tie, and against the
    /// reusable-buffer path the siege source now uses.
    /// </summary>
    [Fact]
    public void A_battle_with_a_trace_still_records_the_same_top_three()
    {
        var w = new ScoringWeights(1, 0, 0, 0, 0, 0, 0, 2, 32);
        var candidates = new List<TargetCandidate>
        {
            Candidate("delta", hitChanceMilli: 100),
            Candidate("bravo", hitChanceMilli: 900),
            Candidate("alpha", hitChanceMilli: 900),   // ties bravo -> ordinal decides
            Candidate("echo", hitChanceMilli: 50),
            Candidate("charlie", hitChanceMilli: 500),
        };

        var reference = candidates
            .Select(c => (ActorKey: c.ActorKey, Breakdown: CandidateScorer.ScoreBreakdownOf(c, 0, w)))
            .OrderByDescending(x => x.Breakdown.Total)
            .ThenBy(x => x.ActorKey, StringComparer.Ordinal)
            .Take(3)
            .ToList();

        Assert.Equal(3, reference.Count);
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, reference.Select(x => x.ActorKey));

        // The list-returning API agrees...
        Assert.Equal(reference, CandidateScorer.TopThree(candidates, currentRound: 0, w));

        // ...and so does the reusable caller buffer, with the SAME formatted trace line.
        var buffer = new (string ActorKey, ScoreBreakdown Breakdown)[3];
        var count = CandidateScorer.TopThreeInto(candidates, 0, w, buffer);
        Assert.Equal(3, count);
        Assert.Equal(reference, buffer);
        Assert.Equal(CandidateScorer.FormatTopThree(reference), CandidateScorer.FormatTopThree(buffer, count));

        // A shorter candidate set writes only what exists, and writes nothing stale into slot 0.
        var one = new List<TargetCandidate> { Candidate("solo", hitChanceMilli: 1) };
        var shortBuffer = new (string ActorKey, ScoreBreakdown Breakdown)[3];
        Assert.Equal(1, CandidateScorer.TopThreeInto(one, 0, w, shortBuffer));
        Assert.Equal("solo", shortBuffer[0].ActorKey);
    }

    /// <summary>
    /// The re-entry assumption behind CAI1.14's reused buffers (acceptance list:
    /// `Re_entering_a_reused_decision_buffer_throws_instead_of_sharing_it`). `CostLedger.TryPay` now
    /// keeps its per-row amounts in a reusable scratch array, so an inner call would overwrite the
    /// outer call's validated amounts and pass 2 would spend figures pass 1 never checked — silent
    /// corruption, not a wrong number. It must throw instead. Driven through the real collaborator
    /// seam: the re-entrant call is made from inside `poolsFor`, i.e. inside the guarded window.
    /// </summary>
    [Fact]
    public void Re_entering_a_reused_decision_buffer_throws_instead_of_sharing_it()
    {
        var derived = Snapshot(("stamina", 1000, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = new Dictionary<string, IReadOnlyList<ActionCostRow>>
        {
            ["act.two"] = new ActionCostRow[]
            {
                new("act.two", "stamina", ValueSpec.Of(10), ActionCostTiming.OnCommit),
                new("act.two", "stamina", ValueSpec.Of(20), ActionCostTiming.OnCommit),
            },
        };

        CostLedger? ledger = null;
        var reenter = false;
        ledger = new CostLedger(
            costs,
            _ =>
            {
                if (reenter)
                    Assert.Throws<InvalidOperationException>(
                        () => ledger!.TryPay(ActorKey, "act.two", ActionCostTiming.OnCommit, rng: null));
                return pools;
            },
            _ => derived,
            (_, _) => 1,
            () => 0L);

        // A non-re-entrant payment still succeeds and spends both rows.
        var first = ledger.TryPay(ActorKey, "act.two", ActionCostTiming.OnCommit, rng: null);
        Assert.Equal(CostPayResult.Success, first);

        // ...and the guard fires exactly once for the re-entrant attempt.
        reenter = true;
        Assert.Equal(CostPayResult.Success, ledger.TryPay(ActorKey, "act.two", ActionCostTiming.OnCommit, rng: null));

        // The ledger is usable again after the guarded window closes.
        reenter = false;
        Assert.Equal(CostPayResult.Success, ledger.TryPay(ActorKey, "act.two", ActionCostTiming.OnCommit, rng: null));
    }

    static TargetCandidate Candidate(string key, int hitChanceMilli) => new(
        ActorKey: key, BaseTier: 0, Aggression: 0, HitChanceMilli: hitChanceMilli,
        ObjectiveClassMilli: 0, IsKillingBlow: false, TargetMissingHpMilli: 0,
        TargetCanCounter: false, IncomingThreatMilli: 0);

    /// <summary>
    /// Site 4's identity contract (spec-decision-perf.md): the allocation-free pipeline must choose the
    /// same candidate as the allocating overload for every rotation of the same pool, every selection
    /// mode, and a pool larger than `MaxCandidatesScored` (so `Take`'s replacement, `poolCount`, is
    /// exercised on a rotating window rather than one fixed prefix).
    /// </summary>
    [Fact]
    public void ChooseTarget_into_a_reused_scratch_matches_the_allocating_overload()
    {
        var w = new ScoringWeights(1, 1, 1, 1, 1, 1, 1, 2, 4);
        var pool = new List<TargetCandidate>();
        for (var i = 0; i < 9; i++) pool.Add(Candidate($"k{i}", hitChanceMilli: (i * 137) % 1000));

        var selections = new SelectionPolicy?[]
        {
            null,
            new SelectionPolicy(SelectionMode.Argmax, 1000, ""),
            new SelectionPolicy(SelectionMode.Argmax, 500, "ai.select"),
            new SelectionPolicy(SelectionMode.SeededWeighted, 1000, "ai.select"),
            new SelectionPolicy(SelectionMode.SeededWeighted, 250, "ai.select"),
        };

        var scratch = new SelectionScratch();
        foreach (var selection in selections)
            for (var rotation = 0; rotation < pool.Count; rotation++)
            {
                var rotated = new List<TargetCandidate>(pool.Count);
                for (var i = 0; i < pool.Count; i++) rotated.Add(pool[(i + rotation) % pool.Count]);

                var expected = CandidateScorer.ChooseTarget(rotated, currentRound: 2, w, selection, runSeed: 42);
                var actual = CandidateScorer.ChooseTargetInto(scratch, rotated, currentRound: 2, w, selection, runSeed: 42);
                Assert.Equal(expected?.ActorKey, actual?.ActorKey);
            }
    }

    /// <summary>Site 4's whole point: once the scratch is warm, the rank/cut/select pipeline allocates
    /// nothing at all. Measured with the same harness as the rest of this file; trace-free and
    /// RNG-free (Argmax), so 0 is the only legitimate number.</summary>
    [Fact]
    public void Selection_into_a_warm_scratch_allocates_zero_bytes()
    {
        var w = new ScoringWeights(1, 1, 1, 1, 1, 1, 1, 2, 32);
        var pool = new List<TargetCandidate>();
        for (var i = 0; i < 24; i++) pool.Add(Candidate($"k{i}", hitChanceMilli: (i * 41) % 1000));
        var scratch = new SelectionScratch();

        for (var pass = 0; pass < 3; pass++) CandidateScorer.ChooseTargetInto(scratch, pool, 1, w);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        CandidateScorer.ChooseTargetInto(scratch, pool, 1, w);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes == 0, $"the warm selection pipeline allocated {bytes} bytes; budget is 0");
    }

    /// <summary>Site 3's buffer half: `BuildCappedInto` is the per-decision candidate build, and with a
    /// warm caller-owned pair it must allocate nothing. The three collaborators are pre-bound here, the
    /// way production binds them (a per-decision method-group-to-delegate conversion would be the very
    /// allocation under test), so the measurement is of the stage itself.</summary>
    [Fact]
    public void BuildCappedInto_allocates_zero_bytes_once_warm()
    {
        var keys = new List<string> { "me" };
        for (var i = 0; i < 40; i++) keys.Add($"e{i:D2}");

        var eligible = new List<string>();
        var result = new List<TargetCandidate>();
        var calls = 0;
        Func<string, int> sideOf = k => k == "me" ? 0 : 1;
        Func<string, bool> isReadable = _ => true;
        TargetStage.TryBuildCandidate build = (string k, out TargetCandidate c) =>
        { calls++; c = Candidate(k, hitChanceMilli: 1); return true; };

        // The cap comes from a `ScoringWeights` (the closed-code projection of the profile's own scoring
        // block) rather than being written as a bare literal at the assertion: this test pins the WORK
        // BOUND's contract — the stage builds its cap and no more — not a population reading.
        var cap = new ScoringWeights(70, 50, 15, 10, 10, 1, 120, 2, 32).MaxCandidatesScored;

        for (var pass = 0; pass < 3; pass++)
            TargetStage.BuildCappedInto("me", 0, keys, sideOf, isReadable, cap, build, eligible, result);
        Assert.Equal(cap, result.Count);
        var warmCalls = calls;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        TargetStage.BuildCappedInto("me", 0, keys, sideOf, isReadable, 32, build, eligible, result);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(32, calls - warmCalls); // the cap, counted (never timed)
        Assert.True(bytes == 0, $"BuildCappedInto allocated {bytes} bytes once warm; budget is 0");
    }

    /// <summary>
    /// The one way the cap could select a different set: counting an actor the SKIP FILTERS dropped.
    /// The cap is applied to the already-filtered, view-ordered set, so a skipped key never occupies a
    /// cap slot — asserted with the skipped keys positioned BEFORE the eligible ones, which is the
    /// arrangement that would expose an off-by-one.
    /// </summary>
    [Fact]
    public void The_cap_counts_only_candidates_that_passed_the_skip_filters()
    {
        // Six skipped keys first (self, three allies, two unreadable enemies), then five readable
        // enemies. Cap 2 must build the FIRST TWO READABLE ones, not the first two keys overall.
        var live = new[] { "me", "ally1", "ally2", "ally3", "hidden1", "hidden2", "e1", "e2", "e3", "e4", "e5" };
        var sides = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["me"] = 0, ["ally1"] = 0, ["ally2"] = 0, ["ally3"] = 0,
            ["hidden1"] = 1, ["hidden2"] = 1, ["e1"] = 1, ["e2"] = 1, ["e3"] = 1, ["e4"] = 1, ["e5"] = 1,
        };
        var readable = new HashSet<string>(StringComparer.Ordinal)
        { "me", "ally1", "ally2", "ally3", "e1", "e2", "e3", "e4", "e5" };

        var built = new List<string>();
        TargetStage.BuildCappedInto("me", mySide: 0, live,
            sideOf: k => sides[k], isReadable: k => readable.Contains(k),
            maxCandidatesScored: 2,
            tryBuildCandidate: (string k, out TargetCandidate c) =>
            { built.Add(k); c = Candidate(k, hitChanceMilli: 1); return true; },
            eligible: new List<string>(), result: new List<TargetCandidate>());

        Assert.Equal(new[] { "e1", "e2" }, built);
    }

    /// <summary>
    /// The row's `No LINQ remains on a decision path` line, as a focused source scan rather than a
    /// measured claim: each policy round's zero-byte assertion is the real proof and is blocked by the
    /// uninterned channel id (filed), so this pins the SHAPE of the decision path instead. Two
    /// deliberate exclusions, both stated so the scan stays meaningful: `.Max(`/`.Min(` are absent
    /// because they match `Math.Max`/`Math.Min` (not LINQ), and comment/doc lines are stripped because
    /// the path's own comments NAME the LINQ chains they replaced.
    /// </summary>
    [Fact]
    public void No_LINQ_remains_on_a_decision_path()
    {
        var repo = FindRepoRoot();
        string[] decisionPath =
        {
            "src/FusionRpg.Core/Actions/Ai/CandidateScorer.cs",
            "src/FusionRpg.Core/Actions/Ai/TargetStage.cs",
            "src/FusionRpg.Core/Actions/Ai/ActionStage.cs",
            "src/FusionRpg.Core/Actions/Ai/CoreIntentPolicy.cs",
            "src/FusionRpg.Core/Actions/Cost/CostLedger.cs",
            "src/FusionRpg.Core/Actions/UsabilityEvaluator.cs",
            "src/FusionRpg.Core/Battle/Siege/SiegeAiIntentSource.cs",
        };
        string[] linqTokens =
        {
            ".Select(", ".SelectMany(", ".Where(", ".OrderBy(", ".OrderByDescending(", ".GroupBy(",
            ".ToList(", ".ToArray(", ".ToDictionary(", ".Any(", ".All(", ".First(", ".FirstOrDefault(",
            ".Aggregate(", ".Distinct(", ".Skip(", ".Take(",
        };

        var offences = new List<string>();
        foreach (var relative in decisionPath)
        {
            var file = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"decision-path file not found: {file}");

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                var code = lines[i].Split("//")[0];
                foreach (var token in linqTokens)
                    if (code.Contains(token, StringComparison.Ordinal))
                        offences.Add($"{relative}:{i + 1}: {token}");
            }
        }

        Assert.True(offences.Count == 0,
            "LINQ on a decision path (it allocates enumerators and delegates per decision):\n" +
            string.Join("\n", offences));
    }

    static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
    }

    /// <summary>`SelectionScratch`'s own re-entry assumption, asserted rather than assumed: a nested
    /// decision through the same scratch must THROW, because the inner call's `Clear()` would otherwise
    /// discard the outer decision's candidate state and change its answer silently. The outer decision
    /// still completes once the guard has fired.</summary>
    [Fact]
    public void Re_entering_a_selection_scratch_throws_instead_of_sharing_it()
    {
        var w = new ScoringWeights(1, 1, 1, 1, 1, 1, 1, 2, 32);
        var pool = new List<TargetCandidate> { Candidate("a", 10), Candidate("b", 20) };
        var scratch = new SelectionScratch();

        var reentrant = new ReentrantCandidates(pool, () =>
            Assert.Throws<InvalidOperationException>(
                () => CandidateScorer.ChooseTargetInto(scratch, pool, 0, w)));

        var chosen = CandidateScorer.ChooseTargetInto(scratch, reentrant, 0, w);
        Assert.Equal("b", chosen!.Value.ActorKey); // the outer decision survived the refused nested call

        // ...and the scratch is usable again once the window closed.
        Assert.Equal("b", CandidateScorer.ChooseTargetInto(scratch, pool, 0, w)!.Value.ActorKey);
    }

    /// <summary>A candidate list that fires once, on its first indexed read — i.e. inside
    /// <c>ChooseTargetInto</c>'s guarded window — so a nested decision can be driven through the same
    /// scratch without a production seam that exists only for the test.</summary>
    sealed class ReentrantCandidates : IReadOnlyList<TargetCandidate>
    {
        readonly List<TargetCandidate> _inner;
        readonly Action _onFirstRead;
        bool _fired;

        public ReentrantCandidates(List<TargetCandidate> inner, Action onFirstRead)
        {
            _inner = inner;
            _onFirstRead = onFirstRead;
        }

        public TargetCandidate this[int index]
        {
            get
            {
                if (!_fired) { _fired = true; _onFirstRead(); }
                return _inner[index];
            }
        }

        public int Count => _inner.Count;
        public IEnumerator<TargetCandidate> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
