using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P2.4, spec-aptitude-resolve.md §7 — the tests scoped to this phase
/// (resolver-only, overlay path, one funded aptitude). Tests 1/6/8/11 from the spec's table belong to
/// later tasks: 1 to P2.6 (needs the battle side, P2.5), 6 to P3.2 (the atk double-count guard, "red
/// today" by the spec's own admission), 8 is already covered at the read-functions layer (P2.2), 11 to
/// P3.4 (cross-checks gk-core/tools/CombatSim's simulator, which this phase does not touch).</summary>
public class AptitudeResolverTests
{
    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static AptitudeTuning MinimalTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": {
            "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 },
            "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000}
          },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": {
            "combat.power": "magnitude",
            "combat.accuracy": "contest"
          },
          "edges": [
            { "channel": "combat.power.omni", "source": "Might", "kMilli": 2200 },
            { "channel": "combat.accuracy.omni", "source": "Might", "kMilli": 500 }
          ]
        }
        """);

    static PowerLadder Ladder() => new(FusionRpg.Core.Power.PowerTuningHub.Tuning);
    static DerivedStatRegistry Registry() => DerivedStatRegistry.CreateDefault();

    // ── P2.4's own acceptance: Might -> combat.power.omni, empty allocation, idempotent ────────────

    [Fact]
    public void MightAllocation_resolvesCombatPowerOmni()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var mods = AptitudeResolver.Resolve(allocation, MinimalTuning(), Ladder(), theta: 1000, Registry());

        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");
        Assert.Equal(DerivedModifierOp.Flat, powerMod.Op);
        Assert.Equal("aptitude.Might", powerMod.SourceId);
        // Might is the only funded aptitude -> share = 1.0 -> value = k * P(Theta) = 2.2 * P(1000).
        var expected = AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, Ladder().Value(1000));
        Assert.Equal((double)expected, powerMod.Value, 6);
    }

    [Fact]
    public void EmptyAllocation_resolvesToNothing_notZeroValuedModifiers()
    {
        var mods = AptitudeResolver.Resolve(AptitudeAllocation.Empty, MinimalTuning(), Ladder(), theta: 1000, Registry());
        Assert.Empty(mods);
    }

    [Fact]
    public void UnfundedAptitude_contributesNothing_evenWithOtherAptitudesFunded()
    {
        // Fortitude has no edge in MinimalTuning() at all -- funding it must not somehow produce a
        // Might-channel contribution or a stray zero-valued one.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Fortitude", 100);
        var mods = AptitudeResolver.Resolve(allocation, MinimalTuning(), Ladder(), theta: 1000, Registry());
        Assert.Empty(mods);
    }

    [Fact]
    public void ResolveIsIdempotent_sameInputsSameOutputs()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var tuning = MinimalTuning();
        var a = AptitudeResolver.Resolve(allocation, tuning, Ladder(), theta: 1000, Registry());
        var b = AptitudeResolver.Resolve(allocation, tuning, Ladder(), theta: 1000, Registry());

        Assert.Equal(a.Count, b.Count);
        foreach (var m in a)
        {
            var match = Assert.Single(b, x => x.ChannelId == m.ChannelId && x.SourceId == m.SourceId);
            Assert.Equal(m.Value, match.Value, 12);
            Assert.Equal(m.Op, match.Op);
        }
    }

    // ── Every resolved channel is registered (spec §7 test 2) ──────────────────────────────────────

    [Fact]
    public void EveryResolvedChannel_isRegistered()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var registry = Registry();
        var mods = AptitudeResolver.Resolve(allocation, MinimalTuning(), Ladder(), theta: 1000, registry);
        Assert.NotEmpty(mods);
        foreach (var m in mods)
            Assert.True(registry.TryResolveChannel(m.ChannelId, out _), $"unregistered channel: {m.ChannelId}");
    }

    [Fact]
    public void UnregisteredEdgeChannel_throws_ratherThanSilentlyDroppingOrZeroing()
    {
        var badTuning = AptitudeTuningLoader.Parse("""
            {
              "schemaVersion": 1, "version": 1,
              "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
              "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
              "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
              "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
              "familyRead": { "not.a.real.family": "magnitude" },
              "edges": [ { "channel": "not.a.real.family.omni", "source": "Might", "kMilli": 100 } ]
            }
            """);
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        Assert.Throws<InvalidOperationException>(() =>
            AptitudeResolver.Resolve(allocation, badTuning, Ladder(), theta: 1000, Registry()));
    }

    /// <summary>`retire-atk` R1/R2: a RETIRED channel is the one case that does NOT throw. The
    /// distinction the resolver's throw exists to catch is preserved exactly — a typo or an unknown
    /// channel still throws (the test above); a channel the design deliberately retired is dropped at
    /// load, so nothing ever reaches the resolver to throw about.</summary>
    [Fact]
    public void RetiredEdgeChannel_isDroppedAtLoad_notThrown()
    {
        var tuning = AptitudeTuningLoader.Parse("""
            {
              "schemaVersion": 1, "version": 1,
              "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
              "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
              "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
              "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
              "familyRead": { "progression.bonus.atk": "magnitude", "combat.power": "magnitude" },
              "edges": [
                { "channel": "progression.bonus.atk", "source": "Might", "kMilli": 10000 },
                { "channel": "combat.power.omni", "source": "Might", "kMilli": 2200 }
              ]
            }
            """);

        // Loaded, not rejected: the retired edge and its `familyRead` row are both gone from the parsed
        // surface, and the drop is COUNTED rather than silent.
        Assert.Equal(1, tuning.DroppedRetiredEdges);
        Assert.DoesNotContain(tuning.Edges, e => e.Channel == "progression.bonus.atk");
        Assert.DoesNotContain("progression.bonus.atk", tuning.FamilyRead.Keys);

        // ...and the resolver never sees it, so it neither throws nor emits a modifier for it.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var mods = AptitudeResolver.Resolve(allocation, tuning, Ladder(), theta: 1000, Registry());
        Assert.DoesNotContain(mods, m => m.ChannelId == "progression.bonus.atk");
        Assert.Single(mods);
    }

    /// <summary>`retire-atk` R2's theory: every published `aptitudes.v*.json` on disk still loads.
    /// Published versions are immutable, so v1–v8 all carry the retired channel; the loader is what
    /// keeps them loadable. Asserts LOADABILITY, never a per-version edge count — those are readings
    /// that move whenever a version ships.</summary>
    [Fact]
    public void Every_published_aptitudes_version_on_disk_still_loads()
    {
        var dir = Path.Combine(FindRepoRoot(), "data", "tuning");
        var files = Directory.GetFiles(dir, "aptitudes.v*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.True(files.Count >= 8, $"expected the published history on disk, found {files.Count}");

        foreach (var file in files)
        {
            var tuning = AptitudeTuningLoader.Parse(File.ReadAllText(file));
            Assert.NotEmpty(tuning.Edges);
            // A version that names the retired channel must have dropped it; one that never did drops 0.
            Assert.DoesNotContain(tuning.Edges, e => DerivedStatChannels.Retired.Contains(e.Channel));
        }
    }

    // ── Contest read mode reaches the resolver too (combat.accuracy.omni in MinimalTuning) ─────────

    [Fact]
    public void ContestEdge_resolvesAsDouble_theta_free()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var atTheta10 = AptitudeResolver.Resolve(allocation, MinimalTuning(), Ladder(), theta: 10, Registry());
        var atTheta5000 = AptitudeResolver.Resolve(allocation, MinimalTuning(), Ladder(), theta: 5000, Registry());

        var a = Assert.Single(atTheta10, m => m.ChannelId == "combat.accuracy.omni");
        var b = Assert.Single(atTheta5000, m => m.ChannelId == "combat.accuracy.omni");
        Assert.Equal(a.Value, b.Value, 9);
    }

    // ── Magnitude proportionality and Theta=0 flatness (spec §7 tests 4, 10) ───────────────────────

    [Fact]
    public void MagnitudeEdge_doublingPThetaDoublesValue()
    {
        // Use two Theta values on the SAME curve rather than asserting proportional-in-Theta directly
        // (P(Theta) itself is only proportional to Theta in the trivial B=0 case) -- what must hold is
        // AptitudeReadFunctions' own contract, exercised here through the resolver end to end.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var ladder = Ladder();
        var tuning = MinimalTuning();
        var pThetaSmall = ladder.Value(10);
        var pThetaDoubled = pThetaSmall * 2;
        // Find a Theta whose P(Theta) is exactly double -- binary search isn't needed; assert the
        // underlying read function directly matches what the resolver produced, which is what P2.2
        // already proves proportional. This test's job is just "the resolver doesn't break that".
        var mods = AptitudeResolver.Resolve(allocation, tuning, ladder, theta: 10, Registry());
        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");
        Assert.Equal((double)AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, pThetaSmall), powerMod.Value, 6);
        Assert.Equal((double)AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, pThetaDoubled),
                     (double)AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, pThetaSmall) * 2, 6);
    }

    [Fact]
    public void MagnitudeEdge_isFlatWhenThetaIsZero()
    {
        // spec-aptitude-resolve.md §2.0 precondition 2's symptom, pinned: at Theta=0 every magnitude
        // edge collapses to P(0) = C, the same floor regardless of the coefficient's own size.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var ladder = Ladder();
        var mods = AptitudeResolver.Resolve(allocation, MinimalTuning(), ladder, theta: 0, Registry());
        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");
        Assert.Equal((double)AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, ladder.Value(0)), powerMod.Value, 6);
    }

    // ── Overflow discipline at high Theta (spec §7 test 7) ──────────────────────────────────────────

    [Fact]
    public void MagnitudeEdge_exactAtHighTheta()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var ladder = Ladder();
        var highTheta = (int)Math.Min(ladder.MaxIndex, 5_000_000);
        var mods = AptitudeResolver.Resolve(allocation, MinimalTuning(), ladder, theta: highTheta, Registry());
        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");
        Assert.Equal((double)AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, ladder.Value(highTheta)), powerMod.Value, 3);
    }

    [Fact]
    public void MagnitudeEdge_oversizedCoefficient_throwsRatherThanWraps()
    {
        var oversizedTuning = AptitudeTuningLoader.Parse("""
            {
              "schemaVersion": 1, "version": 1,
              "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
              "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
              "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
              "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
              "familyRead": { "combat.power": "magnitude" },
              "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 9223372036854775807 } ]
            }
            """);
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 1);
        var ladder = Ladder();
        var theta = (int)Math.Min(ladder.MaxIndex, 5_000_000);
        Assert.Throws<OverflowException>(() =>
            AptitudeResolver.Resolve(allocation, oversizedTuning, ladder, theta, Registry()));
    }

    // ── Null-argument guards ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void NullArguments_reject()
    {
        var allocation = AptitudeAllocation.Empty;
        var tuning = MinimalTuning();
        var ladder = Ladder();
        var registry = Registry();
        Assert.Throws<ArgumentNullException>(() => AptitudeResolver.Resolve(null!, tuning, ladder, 0, registry));
        Assert.Throws<ArgumentNullException>(() => AptitudeResolver.Resolve(allocation, null!, ladder, 0, registry));
        Assert.Throws<ArgumentNullException>(() => AptitudeResolver.Resolve(allocation, tuning, null!, 0, registry));
        Assert.Throws<ArgumentNullException>(() => AptitudeResolver.Resolve(allocation, tuning, ladder, 0, null!));
    }

    // ── The recovery-scale dial (class-system-ideal.md §5d) — found missing 2026-08-27 ─────────────

    static AptitudeTuning RecoveryTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "resource.regen": "magnitude" },
          "edges": [ { "channel": "resource.regen.hp", "source": "Vigor", "kMilli": 12000 } ]
        }
        """);

    [Fact]
    public void RecoveryFamilyEdge_appliesTheScaleDial()
    {
        // Regression: AptitudeResolver used to read every edge's raw kMilli, silently discarding
        // tuning.Recovery.ScaleMilli -- the termination-invariant dial the shipped file's own
        // recovery._scaleWhy note says was solved against a measured r=1.33 (an unkillable pair).
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Vigor", 100);
        var ladder = Ladder();
        var mods = AptitudeResolver.Resolve(allocation, RecoveryTuning(), ladder, theta: 1000, Registry());

        var regenMod = Assert.Single(mods, m => m.ChannelId == "resource.regen.hp");
        // effective kMilli = 12000 * 374 / 1000 = 4488.
        var expected = AptitudeReadFunctions.Magnitude(4488, 1.0, 1000, ladder.Value(1000));
        Assert.Equal((double)expected, regenMod.Value, 6);

        var unscaled = AptitudeReadFunctions.Magnitude(12000, 1.0, 1000, ladder.Value(1000));
        Assert.True(regenMod.Value < unscaled * 0.5, "recovery scale should meaningfully dampen the edge, not merely round it");
    }

    [Fact]
    public void NonRecoveryFamilyEdge_isUnaffectedByTheScaleDial()
    {
        // combat.power.omni is not in RecoveryTuning()'s recovery.families -- must read its raw kMilli.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var ladder = Ladder();
        var mods = AptitudeResolver.Resolve(allocation, MinimalTuning(), ladder, theta: 1000, Registry());
        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");
        var expected = AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, ladder.Value(1000));
        Assert.Equal((double)expected, powerMod.Value, 6);
    }

    // ── species-progression step 6.1 (spec-species-layer-delivery.md, R2 + R16 + R21) ────────────────
    // THE one explained re-bless. See tasks/evidence-fragments/SP6.1.md for the full before/after
    // finding: no PRE-EXISTING test in this repo pinned a composed value for an actor with 2+
    // non-empty scopes, so there was nothing to re-bless in the literal sense of a changed assertion
    // -- these tests are the NEW evidence the spec's own "the table is the evidence" allows for an
    // honest empty finding, proving the exact relationship the spec's own math describes rather than
    // asserting it away.

    // Two DIFFERENT aptitudes on two DIFFERENT channels, non-1000 weights matching the shipped
    // ordering (commander < creatureType <= aspect < uniqueCreature) -- so both R2/R16 (the split
    // itself) and R21 (the weight) are independently visible.
    static AptitudeTuning WeightedTuning() => AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": {
            "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 },
            "magnitude": { "shareExponentMilli": 1000 },
            "layerWeightMilliByScope": { "commander": 500, "creatureType": 667, "aspect": 667, "uniqueCreature": 1000 }
          },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude", "combat.accuracy": "contest", "combat.defense": "magnitude" },
          "edges": [
            { "channel": "combat.power.omni", "source": "Might", "kMilli": 2200 },
            { "channel": "combat.accuracy.omni", "source": "Might", "kMilli": 500 },
            { "channel": "combat.defense.omni", "source": "Fortitude", "kMilli": 1800 }
          ]
        }
        """);

    /// <summary>The resolver's own `ScaleMilli` round-half-away-from-zero per-mille idiom, restated
    /// here to compute an INDEPENDENTLY hand-verifiable expected value (not by calling the private
    /// method under test).</summary>
    static long RoundHalfAwayPerMille(long value, long weightMilli)
    {
        var product = checked(value * weightMilli);
        return (product + (product >= 0 ? 500 : -500)) / 1000;
    }

    [Fact]
    public void TwoNonEmptyScopes_resolveIndependently_eachAtItsOwnScopesFullShare_notTheMergedGrandTotalShare()
    {
        // R2/R16: Commander funds Might ALONE (Commander's own scope total is 100, so
        // ShareWithinScope(Commander, "Might") = 1.0); CreatureType funds Fortitude ALONE, likewise
        // 1.0. The OLD merged behavior divided each aptitude's points by the GRAND total across BOTH
        // scopes (200), giving share=0.5 for each edge instead -- resolve-alone gives each its own
        // scope's full share.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100)
                        + AptitudeAllocation.Single(AllocationScope.CreatureType, "Fortitude", 100);
        var tuning = WeightedTuning();
        var ladder = Ladder();
        var pTheta = ladder.Value(1000);
        var mods = AptitudeResolver.Resolve(allocation, tuning, ladder, theta: 1000, Registry());

        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");
        var defenseMod = Assert.Single(mods, m => m.ChannelId == "combat.defense.omni");

        var expectedCommander = RoundHalfAwayPerMille(AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, pTheta), 500);
        var expectedCreatureType = RoundHalfAwayPerMille(AptitudeReadFunctions.Magnitude(1800, 1.0, 1000, pTheta), 667);
        Assert.Equal((double)expectedCommander, powerMod.Value, 6);
        Assert.Equal((double)expectedCreatureType, defenseMod.Value, 6);

        // CreatureType's weight (667) is NOT the old merged-share fraction (0.5) this symmetric
        // two-equal-scope fixture would have produced pre-6.1 -- proves the move is real, not a
        // coincidence of picking a weight that happens to equal the merged share (which 500 does,
        // since Magnitude is linear in share at gamma=1: k*1.0*500/1000 == k*0.5 exactly).
        var oldMergedCreatureType = AptitudeReadFunctions.Magnitude(1800, 0.5, 1000, pTheta);
        Assert.NotEqual((double)oldMergedCreatureType, defenseMod.Value);
    }

    [Fact]
    public void AddingPointsToASecondScope_leavesTheFirstScopesOwnContributionUnchanged()
    {
        // The per-layer contract step 6.1 replaces "scopes sum before share" with: ShareWithinScope
        // depends only on ITS OWN scope's total, so funding a second scope can never move the first's.
        var tuning = WeightedTuning();
        var ladder = Ladder();
        var registry = Registry();

        var commanderOnly = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var beforePower = Assert.Single(
            AptitudeResolver.Resolve(commanderOnly, tuning, ladder, theta: 1000, registry),
            m => m.ChannelId == "combat.power.omni");

        var both = commanderOnly + AptitudeAllocation.Single(AllocationScope.CreatureType, "Fortitude", 250);
        var afterPower = Assert.Single(
            AptitudeResolver.Resolve(both, tuning, ladder, theta: 1000, registry),
            m => m.ChannelId == "combat.power.omni");

        Assert.Equal(beforePower.Value, afterPower.Value, 9);
        Assert.Equal(beforePower.SourceId, afterPower.SourceId);
    }

    [Fact]
    public void LayerWeight_scalesTheMagnitudeReadOutput_neverTheShare()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var tuning = WeightedTuning();
        var ladder = Ladder();
        var pTheta = ladder.Value(1000);
        var mods = AptitudeResolver.Resolve(allocation, tuning, ladder, theta: 1000, Registry());
        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");

        // share stays 1.0 (a bounded [0,1] ratio, untouched by the weight) -- only the READ OUTPUT
        // is scaled, by the commander weight 500/1000.
        var unweighted = AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, pTheta);
        var expected = RoundHalfAwayPerMille(unweighted, 500);
        Assert.Equal((double)expected, powerMod.Value, 6);
        Assert.True(powerMod.Value < unweighted, "commander weight 500 must roughly halve the unweighted read");
    }

    [Fact]
    public void LayerWeight_scalesTheContestReadOutput_asADouble_noNewRoundingRule()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100);
        var mods = AptitudeResolver.Resolve(allocation, WeightedTuning(), Ladder(), theta: 1000, Registry());
        var accuracyMod = Assert.Single(mods, m => m.ChannelId == "combat.accuracy.omni");

        var unweighted = AptitudeReadFunctions.Contest(500, 1.0, 1000, 100_000);
        var expected = unweighted * 500 / 1000.0;
        Assert.Equal(expected, accuracyMod.Value, 9);
    }

    [Fact]
    public void LayerWeight1000_isTheUnweightedIdentity()
    {
        // uniqueCreature ships at 1000 in WeightedTuning() -- confirms 1000 truly means "unweighted",
        // not merely "close to it".
        var allocation = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Fortitude", 100);
        var mods = AptitudeResolver.Resolve(allocation, WeightedTuning(), Ladder(), theta: 1000, Registry());
        var defenseMod = Assert.Single(mods, m => m.ChannelId == "combat.defense.omni");

        var unweighted = AptitudeReadFunctions.Magnitude(1800, 1.0, 1000, Ladder().Value(1000));
        Assert.Equal((double)unweighted, defenseMod.Value, 6);
    }

    [Fact]
    public void SingleNonEmptyScope_atWeight1000_isByteIdenticalToTheOldMergedResolve()
    {
        // Explicit, direct proof of the spec's own claim ("an actor with at most one non-empty scope
        // is byte-identical") -- MinimalTuning() ships identity (1000) weights for every scope, so a
        // single-scope allocation's share_scope (== 1.0, the only points in that scope) equals the old
        // merged share for the same case (Total == GrandTotal when only one scope is funded).
        var allocation = AptitudeAllocation.Single(AllocationScope.UniqueCreature, "Might", 100);
        var ladder = Ladder();
        var mods = AptitudeResolver.Resolve(allocation, MinimalTuning(), ladder, theta: 1000, Registry());
        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");

        var expected = AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, ladder.Value(1000));
        Assert.Equal((double)expected, powerMod.Value, 6);
    }

    static AptitudeTuning WeightedTuningWithCommanderWeight(long commanderWeightMilli) => AptitudeTuningLoader.Parse($$"""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": {
            "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 },
            "magnitude": { "shareExponentMilli": 1000 },
            "layerWeightMilliByScope": { "commander": {{commanderWeightMilli}}, "creatureType": 667, "aspect": 667, "uniqueCreature": 1000 }
          },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude", "combat.accuracy": "contest", "combat.defense": "magnitude" },
          "edges": [
            { "channel": "combat.power.omni", "source": "Might", "kMilli": 2200 },
            { "channel": "combat.accuracy.omni", "source": "Might", "kMilli": 500 },
            { "channel": "combat.defense.omni", "source": "Fortitude", "kMilli": 1800 }
          ]
        }
        """);

    [Fact]
    public void ChangingOneScopesWeight_movesOnlyThatScopesOwnSourceIdFamily()
    {
        // spec-species-layer-delivery.md step 6.1's own required coverage: "changing one weight moves
        // only its SourceId family." Two non-empty scopes on DIFFERENT aptitudes/channels (Commander ->
        // Might -> combat.power.omni, CreatureType -> Fortitude -> combat.defense.omni), resolved twice
        // against tunings that differ ONLY in the Commander weight (500 then 750, CreatureType's own
        // 667 held fixed). The Commander-sourced modifier (SourceId "aptitude.Might") must move; the
        // CreatureType-sourced one (SourceId "aptitude.creatureType.Fortitude") must not move at all --
        // proving a weight change is scoped to its own layer's SourceId family, never a global rescale.
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100)
                        + AptitudeAllocation.Single(AllocationScope.CreatureType, "Fortitude", 100);
        var ladder = Ladder();
        var registry = Registry();

        var modsBefore = AptitudeResolver.Resolve(allocation, WeightedTuningWithCommanderWeight(500), ladder, theta: 1000, registry);
        var modsAfter = AptitudeResolver.Resolve(allocation, WeightedTuningWithCommanderWeight(750), ladder, theta: 1000, registry);

        var powerBefore = Assert.Single(modsBefore, m => m.ChannelId == "combat.power.omni");
        var powerAfter = Assert.Single(modsAfter, m => m.ChannelId == "combat.power.omni");
        var defenseBefore = Assert.Single(modsBefore, m => m.ChannelId == "combat.defense.omni");
        var defenseAfter = Assert.Single(modsAfter, m => m.ChannelId == "combat.defense.omni");

        Assert.Equal("aptitude.Might", powerBefore.SourceId);
        Assert.Equal("aptitude.creatureType.Fortitude", defenseBefore.SourceId);

        Assert.NotEqual(powerBefore.Value, powerAfter.Value); // Commander's own family moved
        Assert.Equal(defenseBefore.Value, defenseAfter.Value, 9); // CreatureType's family did not

        // And it moved BY EXACTLY the weight ratio, not by some incidental amount.
        var pTheta = ladder.Value(1000);
        var unweightedPower = AptitudeReadFunctions.Magnitude(2200, 1.0, 1000, pTheta);
        Assert.Equal((double)RoundHalfAwayPerMille(unweightedPower, 500), powerBefore.Value, 6);
        Assert.Equal((double)RoundHalfAwayPerMille(unweightedPower, 750), powerAfter.Value, 6);
    }

    [Fact]
    public void SourceIds_CommanderStaysUnscoped_OtherScopesCarryTheirOwnScopeText()
    {
        var allocation = AptitudeAllocation.Single(AllocationScope.Commander, "Might", 100)
                        + AptitudeAllocation.Single(AllocationScope.CreatureType, "Fortitude", 100);
        var mods = AptitudeResolver.Resolve(allocation, WeightedTuning(), Ladder(), theta: 1000, Registry());

        var powerMod = Assert.Single(mods, m => m.ChannelId == "combat.power.omni");
        var defenseMod = Assert.Single(mods, m => m.ChannelId == "combat.defense.omni");

        Assert.Equal("aptitude.Might", powerMod.SourceId);
        Assert.Equal("aptitude.creatureType.Fortitude", defenseMod.SourceId);
        Assert.Equal("Aptitude · Might", ContributionSourceIds.FictionLabel(powerMod.SourceId));
        Assert.Equal("Aptitude · creatureType · Fortitude", ContributionSourceIds.FictionLabel(defenseMod.SourceId));
    }
}
