using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Balance.Guards;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>class-system-todo.md P5.1 — <see cref="TerminationGuard"/>, the HARD half of
/// <c>balance-guard</c> (spec-balance-guard.md, read in full this session). Table in §6: three named
/// tests, all covered here.</summary>
///
/// <remarks><see cref="AptitudeTuningHub"/> is a bare, unsynchronized static (no lock, no
/// AsyncLocal) — xUnit runs different test CLASSES in parallel by default (only same-collection
/// classes serialize), so this and <c>DominanceGuardTests</c> — the only two files in
/// `FusionRpg.Core.Tests` that call <c>AptitudeTuningHub.Configure</c> directly, confirmed via repo
/// grep, 2026-08-27 — raced: a full-suite run failed
/// `DominanceGuardTests.Measure_appliesNoClock_matchesTheNoRoundLimitPredictorCallExactly` with a
/// large win-share mismatch even though the SAME test passed in an isolated filtered run seconds
/// earlier. Shares <c>PerfProbeTests</c>/<c>PerfProbeValueTests</c>'s own established fix for this
/// exact class of problem (`Diagnostics/PerfProbeTests.cs`'s own `[Collection("PerfProbe")]`) rather
/// than inventing a new pattern.</remarks>
[Collection("AptitudeTuningHub")]
[Trait("Category", "BalanceGuard")]
public class TerminationGuardTests
{
    // A synthetic tuning, mirroring ActorHubTests.cs's own inline-JSON pattern. Edge sources must be
    // REAL, roster-registered aptitude ids -- AptitudeAllocation.Single validates against
    // gk-data/packs/fusion/data/seed/aptitudes/roster.json, so invented names ("Offense"/"Vitality") are rejected; "Might"
    // and "Vigor" are reused here only as label sources for these edges' own kMilli values, unrelated
    // to whatever the shipped config points them at. "Might" feeds combat.power.omni (real, nonzero --
    // so IsOffenceLess is false and this pair is NOT filtered); "Vigor" feeds resource.max.hp and, at
    // an absurd kMilli, resource.regen.hp so far past any plausible damage rate that net attrition
    // cannot help but be <= 0 on both sides.
    static AptitudeTuning PlantedUnkillableTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1,
          "version": 1,
          "grant": { "aptitudePointsPerTheta": 2000, "skillPointsPerTheta": 0 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 1000, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power.omni": "magnitude", "resource.max.hp": "magnitude", "resource.regen.hp": "magnitude" },
          "edges": [
            { "channel": "combat.power.omni", "source": "Might", "kMilli": 1000 },
            { "channel": "resource.max.hp", "source": "Vigor", "kMilli": 10000 },
            { "channel": "resource.regen.hp", "source": "Vigor", "kMilli": 999999999000 }
          ]
        }
        """);

    static AptitudeAllocation PlantedAllocation() =>
        AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50)
        + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 50);

    // class-system-todo.md P7.4 — a build that is killable WITHOUT poise (moderate hp regen, real
    // offence) but plants a `resource.regen.poise` edge at an absurd rate, mirroring
    // PlantedUnkillableTuning's own "absurd Vigor hp-regen" idiom exactly. "Bulwark" is reused as the
    // poise-regen source, matching its real roster role ("guard — parry/block rate and strength" --
    // gk-data/packs/fusion/data/seed/aptitudes/roster.json) even though the coefficient itself is synthetic.
    static AptitudeTuning PlantedPoiseLiveTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1,
          "version": 1,
          "grant": { "aptitudePointsPerTheta": 2000, "skillPointsPerTheta": 0 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 1000, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power.omni": "magnitude", "resource.max.hp": "magnitude", "resource.regen.hp": "magnitude", "resource.regen.poise": "magnitude" },
          "edges": [
            { "channel": "combat.power.omni", "source": "Might", "kMilli": 1000 },
            { "channel": "resource.max.hp", "source": "Vigor", "kMilli": 10000 },
            { "channel": "resource.regen.hp", "source": "Vigor", "kMilli": 500 },
            { "channel": "resource.regen.poise", "source": "Bulwark", "kMilli": 999999999000 }
          ]
        }
        """);

    static AptitudeAllocation PoiseLiveAllocation() =>
        AptitudeAllocation.Single(AllocationScope.Commander, "Might", 34)
        + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 33)
        + AptitudeAllocation.Single(AllocationScope.Commander, "Bulwark", 33);

    static AptitudeAllocation PoiseLiveAllocationNoBulwark() =>
        AptitudeAllocation.Single(AllocationScope.Commander, "Might", 50)
        + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 50);

    [Fact]
    public void Assert_plantedUnkillablePair_throws()
    {
        var tuning = PlantedUnkillableTuning();
        AptitudeTuningHub.Configure(tuning);
        var a = PlantedAllocation();
        var b = PlantedAllocation();

        var ex = Assert.Throws<TerminationViolation>(() => TerminationGuard.Assert(new[] { a, b }, theta: 100));
        Assert.True(ex.NetAttritionA <= 0);
        Assert.True(ex.NetAttritionB <= 0);
    }

    [Fact]
    public void Assert_nullOrEmptyBuilds_reject()
    {
        // No AptitudeTuningHub.Configure needed: both checks below throw on argument validation,
        // before Assert ever reaches the tuning-dependent ToActor path.
        Assert.Throws<ArgumentNullException>(() => TerminationGuard.Assert(null!, 100));
        Assert.Throws<ArgumentException>(() => TerminationGuard.Assert(Array.Empty<AptitudeAllocation>(), 100));
    }

    [Fact]
    public void Assert_nonPositiveTheta_throws()
    {
        var a = AptitudeAllocation.Empty;
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminationGuard.Assert(new[] { a, a }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminationGuard.Assert(new[] { a, a }, -1));
    }

    [Fact]
    public void Assert_twoOffenceLessBuilds_areFilteredNotSpecialCased()
    {
        // spec-balance-guard.md §5: "a filter on the input, not a special case in the verdict." Two
        // empty allocations resolve combat.power.omni to exactly 0 on both sides -- IsOffenceLess for
        // both -- so BOTH ordered pairs ((0,1) and (1,0)) are filtered, PairsChecked stays 0, and
        // Assert does not throw even though (with baseDamage=0 too) neither side could possibly
        // damage the other.
        var tuning = PlantedUnkillableTuning(); // reused only for its channel registration; no points spent
        AptitudeTuningHub.Configure(tuning);
        var empty = AptitudeAllocation.Empty;

        var verdict = TerminationGuard.Assert(new[] { empty, empty }, theta: 100);

        Assert.Equal(0, verdict.PairsChecked);
        Assert.Equal(2, verdict.OffenceLessPairsFiltered); // (0,1) and (1,0) -- both ordered pairs.
    }

    [Fact]
    public void Assert_isGreenOnTheShippedConfig()
    {
        // Day one, and it stays a regression test (spec-balance-guard.md §6 test 3): the real, shipped
        // gk-core/data/tuning/aptitudes.v1.json, with a real, non-degenerate allocation on both sides, must not
        // throw. Mirrors the recovery-scale dial's own termination-invariant target (r=0.670,
        // memory: solved against measured r=1.33 down to 0.670) -- this is that invariant, executable.
        var tuning = AptitudeTuningLoader.Parse(File.ReadAllText(FindShippedAptitudesTuningPath()));
        AptitudeTuningHub.Configure(tuning);
        var a = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 40)
              + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 30)
              + AptitudeAllocation.Single(AllocationScope.Commander, "Onslaught", 30);
        var b = AptitudeAllocation.Single(AllocationScope.Commander, "Bulwark", 40)
              + AptitudeAllocation.Single(AllocationScope.Commander, "Fortitude", 30)
              + AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 30);

        // Assert(...) not throwing IS the claim (spec-balance-guard.md §6 test 3) -- NOT that every
        // individual net attrition is positive. One side's own net attrition can legitimately be
        // negative (that direction of the fight never resolves -- e.g. a pure tank may simply outlast
        // a glass cannon's own damage output forever) without the PAIR being unkillable, since the
        // OTHER side can still finish it. Only "both sides simultaneously <= 0" is the violation
        // Assert throws on. Tried the stronger claim first here (both individually positive) against
        // this specific hand-picked, unvalidated split -- it failed with one side at -996, which on
        // inspection is exactly that ordinary, non-violating case, not a guard bug.
        var verdict = TerminationGuard.Assert(new[] { a, b }, theta: 100);

        Assert.Equal(2, verdict.PairsChecked);
        Assert.Equal(0, verdict.OffenceLessPairsFiltered);
    }

    [Fact]
    public void Assert_v1_theRealTwelveCornerShape_threwOnVigorVsBulwark_theGapThatMotivatedPhase8()
    {
        // Discovered via P5.2's own verify step ("trinity --json diffed against
        // _baseline-dominance.json"), not planted: gk-core/tools/CombatSim's trinity command, run against
        // this SAME shipped gk-core/data/tuning/aptitudes.v1.json (the two files were byte-identical except
        // _meta comment text at the time), marked "Vigor v Bulwark" unending in its own
        // dominanceMatrix.unending grid -- and that grid was byte-identical to the one already
        // checked into docs/research/class-system/_baseline-dominance.json (measuredAt
        // 2026-08-26T21:17:41Z), so this was a pre-existing, already-captured fact about the shipped
        // config, not a regression introduced by that session's AptitudeResolver fix.
        //
        // This test reproduces that SAME finding through the Core TerminationGuard directly, at the
        // SAME corner shape trinity's own BestResponse.DominanceMatrix uses: every aptitude spiked to
        // `100 - floor*11`, the other eleven at `floor`, floor = 100/roster.Length/2 -- scaled x1000
        // here (AptitudeAllocation.Single takes `long`; the true floor/spike, 4.1667/54.1667, is not
        // integral) because Share() is a ratio, so scaling every point x1000 is the same corner, not
        // a different one, and was verified that session to move the measured net attritions by
        // under 4% (rounding-artifact hypothesis tested and rejected, not assumed away).
        //
        // FIXED in v2 (class-system-todo.md P8.3, published 2026-08-27) -- class-system-ideal.md
        // 5d.4b's own coupling warning ("5d and 8.8b... must be solved jointly") turned out to apply
        // far more broadly than this one pair (see docs/research/class-residual-2026-08-27.md's P8.3
        // section: 30 of 66 pairs, not one). See
        // Assert_theShippedConfig_hasZeroTerminationViolations_andNoAbsoluteDominantCorner below for
        // the live check against the now-fixed config. This test's remaining job is narrower and still
        // real: pin permanently, against `aptitudes.v1.json` specifically (stays on disk forever, T4),
        // that TerminationGuard -- independent of gk-core/tools/CombatSim's BestResponse.cs -- correctly
        // CAUGHT this violation on the config that motivated the fix, so the guard mechanism's own
        // proof against a real historical case survives independently of whatever v3, v4... ship next.
        // (A wider cross-check this session found TerminationGuard also flagged two pairs among the
        // same defense-only trio -- Bulwark-vs-Fortitude, Vigor-vs-Fortitude -- that gk-core/tools/CombatSim's
        // own trinity did NOT mark unending; Fortitude, Vigor and Bulwark carried zero direct
        // `combat.power.omni` edges each in v1, confirmed by reading gk-core/data/tuning/aptitudes.v1.json's
        // edges directly, which is consistent with TerminationGuard's own baseDamage=0 choice -- P5.1's
        // own decision, "so 'bought no offence at all' is exactly 'power resolves to 0'" -- being a
        // stricter zero point than whatever gk-core/tools/CombatSim's Build.At uses. NOT reproduced here as an
        // assertion: it was a live disagreement between the two engines, exactly the question P3.4
        // ("Resolver matches the simulator", deferred 2026-08-27 for a real concurrent-edit hazard)
        // already exists to answer, and asserting an unresolved cross-engine disagreement as fact would
        // be the wrong kind of confidence for a regression test to carry.)
        var tuning = AptitudeTuningLoader.Parse(File.ReadAllText(FindAptitudesTuningPath("v1")));
        AptitudeTuningHub.Configure(tuning);

        var roster = new[]
        {
            "Might", "Fortitude", "Vigor", "Onslaught", "Agility", "Composure",
            "Pierce", "Focus", "Bulwark", "Retribution", "Precision", "Ferocity",
        };
        const long floor = 4167; // 100_000 / 12 / 2, rounded -- see the x1000 note above.
        long Spike() => 100_000 - floor * (roster.Length - 1);
        AptitudeAllocation Corner(string spikeId) =>
            roster.Aggregate(AptitudeAllocation.Empty, (acc, id) =>
                acc + AptitudeAllocation.Single(AllocationScope.Commander, id, id == spikeId ? Spike() : floor));

        var vigor = Corner("Vigor");
        var bulwark = Corner("Bulwark");

        var ex = Assert.Throws<TerminationViolation>(() => TerminationGuard.Assert(new[] { vigor, bulwark }, theta: 100));
        Assert.True(ex.NetAttritionA <= 0);
        Assert.True(ex.NetAttritionB <= 0);
    }

    [Fact]
    public void Assert_terminationInvariant_actuallyReadsPoiseRegen_endToEnd()
    {
        // class-system-todo.md P7.4, spec-guard-economy.md §9 test 8: "the termination invariant
        // re-run and green with poise live... a new recovery source is exactly what could break it."
        // Proves the WIRING (Predictor.Predict now reads resource.regen.poise, added this task) is
        // actually load-bearing, not just present -- the SAME allocation shape, once WITHOUT any
        // Bulwark/poise-regen edge feeding it and once WITH one planted at an absurd rate, must move
        // from "terminates" to "does not" purely because poise regen is now counted. If this test
        // passed with poise regen silently ignored, adding the parameter to PhaseModel/Predictor would
        // have been dead code, not a real fix.
        var tuning = PlantedPoiseLiveTuning();
        AptitudeTuningHub.Configure(tuning);

        // WITHOUT poise (no Bulwark investment -- resource.regen.poise reads 0 for both sides, exactly
        // as it does on the real shipped tuning today): a normal, non-degenerate hp-regen pair
        // terminates fine, matching Assert_isGreenOnTheShippedConfig's own claim.
        var a1 = PoiseLiveAllocationNoBulwark();
        var b1 = PoiseLiveAllocationNoBulwark();
        var verdictNoPoise = TerminationGuard.Assert(new[] { a1, b1 }, theta: 100);
        Assert.Equal(2, verdictNoPoise.PairsChecked);

        // WITH poise (a third of the SAME point budget spent on Bulwark instead, feeding the planted
        // absurd resource.regen.poise edge): the SAME shape of build, but now with poise regen far
        // exceeding anything the opponent can deal -- must throw, proving Predictor actually read the
        // new channel and it moved the outcome.
        var a2 = PoiseLiveAllocation();
        var b2 = PoiseLiveAllocation();
        var ex = Assert.Throws<TerminationViolation>(() => TerminationGuard.Assert(new[] { a2, b2 }, theta: 100));
        Assert.True(ex.NetAttritionA <= 0);
        Assert.True(ex.NetAttritionB <= 0);
    }

    [Fact]
    public void Assert_v1_staminaDidNotBindForVigorAndAgility_theGapThatMotivatedP82()
    {
        // class-system-todo.md P8.2, spec-residual-fit.md §2.2: "stamina binds -- its cost (cited
        // 1,544 strike/round, spec-residual-fit.md:56) must EXCEED its regen." No code anywhere in
        // this repo computes that cost yet (action-costs is a separate, unimplemented program --
        // confirmed by grep, zero hits for "anchorCost"/"ActionCost" under gk-core/src/FusionRpg.Core), so it
        // is asserted here as the same cited constant the spec measured against, not a live read.
        //
        // Measured 2026-08-27 (docs/research/class-residual-2026-08-27.md): the recovery-dial fix
        // (class-system-ideal.md 5d.4a, recovery.scaleMilli 374) already dropped stamina regen below
        // 1,544 for TEN of twelve corners as a side effect nobody asked for -- only Vigor and Agility
        // remained above it. FIXED in v2 (P8.2, published 2026-08-27) -- see
        // Assert_theShippedConfig_staminaBindsForAllTwelveCorners below for the live check. This test
        // pins that fact permanently against `aptitudes.v1.json` specifically (v1 stays on disk
        // forever, T4), so the record of WHY v2 shipped survives independently of whatever v3, v4...
        // eventually change next.
        var tuning = AptitudeTuningLoader.Parse(File.ReadAllText(FindAptitudesTuningPath("v1")));
        AptitudeTuningHub.Configure(tuning);

        const double citedStrikeCostPerRound = 1544.0; // spec-residual-fit.md:56 -- no live source yet.
        var vigorRegen = TerminationGuard.ToActor("vigor", TwelveCorner("Vigor"), theta: 100)
            .Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("stamina"), 0);
        var agilityRegen = TerminationGuard.ToActor("agility", TwelveCorner("Agility"), theta: 100)
            .Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("stamina"), 0);

        Assert.True(vigorRegen > citedStrikeCostPerRound,
            $"v1: expected the historical gap (Vigor stamina.regen {vigorRegen} > cost {citedStrikeCostPerRound})");
        Assert.True(agilityRegen > citedStrikeCostPerRound,
            $"v1: expected the historical gap (Agility stamina.regen {agilityRegen} > cost {citedStrikeCostPerRound})");
    }

    [Fact]
    public void Assert_theShippedConfig_staminaBindsForAllTwelveCorners()
    {
        // class-system-todo.md P8.2, published as part of aptitudes.v2.json (2026-08-27, bundled with
        // the P8.3 termination fix into one coordinated version bump): resource.regen.stamina's Vigor
        // kMilli 1500->1063 and Agility kMilli 1200->990 (solved against a target ratio of 0.90,
        // matching Might/Bulwark's own already-shipped level -- not arbitrary). Checked against the
        // REAL shipped config directly -- no patching, this IS what ships to the game and server now
        // (RpgHost.cs / Server/Program.cs both load aptitudes.v2.json, confirmed by
        // AptitudeHostInjectionTests.cs).
        var tuning = AptitudeTuningLoader.Parse(File.ReadAllText(FindShippedAptitudesTuningPath()));
        AptitudeTuningHub.Configure(tuning);

        const double citedStrikeCostPerRound = 1544.0; // spec-residual-fit.md:56 -- no live source yet.
        foreach (var id in RosterTwelve)
        {
            var regen = TerminationGuard.ToActor(id, TwelveCorner(id), theta: 100)
                .Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("stamina"), 0);
            Assert.True(regen < citedStrikeCostPerRound,
                $"{id} corner: stamina.regen {regen} should be under cost {citedStrikeCostPerRound} on the shipped config");
        }

        // Theta-invariance sanity (P8.3's own acceptance line): both changed edges are magnitude-family
        // (resource.regen is a P(Theta) magnitude, familyRead-classified), same as every other edge on
        // the shared power ladder -- so a ratio against any OTHER P(Theta)-scaled quantity (like an
        // eventual live action cost) is Theta-free by construction, the P(Theta) term cancelling in the
        // ratio. Verified empirically at two more Theta points, not merely assumed from the ladder doc.
        var vigorAt20 = TerminationGuard.ToActor("vigor20", TwelveCorner("Vigor"), theta: 20)
            .Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("stamina"), 0);
        var vigorAt500 = TerminationGuard.ToActor("vigor500", TwelveCorner("Vigor"), theta: 500)
            .Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("stamina"), 0);
        Assert.True(vigorAt20 > 0, "Theta=20 corner should still resolve a positive regen");
        Assert.True(vigorAt500 > vigorAt20, "stamina.regen must grow with Theta, matching every other magnitude on the power ladder");
    }

    [Fact]
    public void Assert_v1_hadThirtyUnorderedTerminationViolations_theGapThatMotivatedP83()
    {
        // class-system-todo.md P8.3. P5.2's own test above caught ONE hand-picked pair
        // (Vigor-vs-Bulwark) via trinity's own dominance-matrix corner. This test sweeps ALL C(12,2)=66
        // unordered pairs through the SAME real TerminationGuard.Assert entry point and found the true
        // scope was far larger on v1: eight aptitudes (Fortitude/Vigor/Agility/Composure/Focus/Bulwark/
        // Retribution/Precision) share an identical, near-zero combat.power.omni floor -- none of them
        // sources a direct offense edge -- and formed a near-perfect mutual-stalemate clique (28 of
        // their own 28 possible pairs unending), plus two cross-cluster outliers (Fortitude-vs-
        // Onslaught, Bulwark-vs-Ferocity). Confirmed via the REAL, public TerminationGuard.Assert (not
        // a replicated actor-builder) so this was not a measurement artifact -- full derivation:
        // docs/research/class-residual-2026-08-27.md (P8.3 section). FIXED in v2 -- see
        // Assert_theShippedConfig_hasZeroTerminationViolations_andNoAbsoluteDominantCorner below.
        // Pinned permanently against `aptitudes.v1.json` (stays on disk forever, T4) as an exact count,
        // not ">0", so the historical record cannot silently drift.
        var tuning = AptitudeTuningLoader.Parse(File.ReadAllText(FindAptitudesTuningPath("v1")));
        AptitudeTuningHub.Configure(tuning);

        var violations = CountUnorderedTerminationViolations(RosterTwelve);

        Assert.Equal(30, violations);
    }

    [Fact]
    public void Assert_theShippedConfig_hasZeroTerminationViolations_andNoAbsoluteDominantCorner()
    {
        // class-system-todo.md P8.3, published as part of aptitudes.v2.json (2026-08-27). The joint fix
        // (docs/research/class-residual-2026-08-27.md, P8.3 section): class-system-ideal.md 5d.4b's own
        // warning ("the two invariants trade against each other... fixing either alone moves the
        // other") is not rhetorical -- measured directly: cutting recovery.scaleMilli alone far enough
        // to zero every one of v1's 30 violations creates an ABSOLUTE dominant corner (Might beats all
        // eleven others outright), a WORSE defect than the one it fixes. What actually closes both at
        // once, verified at Theta = 20/100/500/2000 (not just one point):
        //   1. resource.regen.hp, lowered ONLY for the seven aptitudes whose survival leans on it
        //      (Fortitude/Vigor/Agility/Composure/Focus/Bulwark/Precision) -- Retribution is
        //      deliberately EXCLUDED: its own hp-regen is what checks Might's own near-dominance
        //      (Might only loses to Retribution, 0.11-0.13 win share, on v1), and folding it into the
        //      same cut was tried first and measured to make Might fully dominant -- a real,
        //      planted-then-rejected mistake, not a guess skipped.
        //   2. mitigation.scaleMilli (this task's own new dial, AptitudeMitigation's doc comment) at
        //      300 -- because four of those seven (Fortitude/Agility/Composure/Bulwark) lean on
        //      combat.defense/dodge/parry/block/absorption/heal.power as their PRIMARY survival stat,
        //      not hp-regen (Bulwark's own parry+block kMilli alone is 11,000 against a 300 hp-regen
        //      floor), so step 1 alone only closed the clique from 30 down to 6.
        // Checked against the REAL shipped config directly -- no patching, this IS what ships to the
        // game and server now.
        var tuning = AptitudeTuningLoader.Parse(File.ReadAllText(FindShippedAptitudesTuningPath()));
        AptitudeTuningHub.Configure(tuning);

        foreach (var theta in new long[] { 20, 100, 500, 2000 })
        {
            var violations = CountUnorderedTerminationViolations(RosterTwelve, theta);
            Assert.Equal(0, violations);

            var actors = RosterTwelve.Select(id => TerminationGuard.ToActor(id, TwelveCorner(id), theta)).ToArray();
            for (var i = 0; i < actors.Length; i++)
            {
                var beatsEveryOther = Enumerable.Range(0, actors.Length).Where(j => j != i)
                    .All(j => Predictor.Predict(actors[i], actors[j]).WinShareA > 0.5);
                Assert.False(beatsEveryOther, $"{RosterTwelve[i]} must not become an absolute dominant corner at Theta={theta}");
            }
        }
    }

    static readonly string[] RosterTwelve =
    {
        "Might", "Fortitude", "Vigor", "Onslaught", "Agility", "Composure",
        "Pierce", "Focus", "Bulwark", "Retribution", "Precision", "Ferocity",
    };

    static int CountUnorderedTerminationViolations(string[] roster, long theta = 100)
    {
        var count = 0;
        for (var i = 0; i < roster.Length; i++)
            for (var j = i + 1; j < roster.Length; j++)
            {
                try { TerminationGuard.Assert(new[] { TwelveCorner(roster[i]), TwelveCorner(roster[j]) }, theta); }
                catch (TerminationViolation) { count++; }
            }
        return count;
    }

    static AptitudeAllocation TwelveCorner(string spikeId)
    {
        // Same x1000-scaled floor/spike shape as Assert_theRealTwelveCornerShape_throwsOnVigorVsBulwark
        // above -- AptitudeAllocation.Single takes long and the true floor (100/12/2 = 4.1667) is not
        // integral; share() is a ratio, so scaling every point x1000 is the same corner, not a
        // different one (already verified this session on the Bulwark test).
        const long floor = 4167;
        var spike = 100_000 - floor * (RosterTwelve.Length - 1);
        return RosterTwelve.Aggregate(AptitudeAllocation.Empty, (acc, id) =>
            acc + AptitudeAllocation.Single(AllocationScope.Commander, id, id == spikeId ? spike : floor));
    }

    // The currently-shipped config -- update this literal alongside RpgHost.cs/Server/Program.cs on
    // the NEXT version bump (matching this repo's own established convention: tuning file versions
    // are deliberately hardcoded and reviewed at each host, never auto-discovered as "latest" --
    // power-scale.v2.json's own RpgHost.cs comment is the precedent, T4.2).
    static string FindShippedAptitudesTuningPath() => FindAptitudesTuningPath("v2");

    // Same repo-root-finding pattern as gk-core/tests/FusionRpg.Guard.Tests/AptitudeHostInjectionTests.cs.
    static string FindAptitudesTuningPath(string version)
    {
        var filename = "aptitudes." + version + ".json";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", filename);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not locate data/tuning/" + filename + " above " + AppContext.BaseDirectory);
    }
}
