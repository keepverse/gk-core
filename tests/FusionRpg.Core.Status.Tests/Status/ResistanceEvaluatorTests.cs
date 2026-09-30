using System.Linq;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Status;

public class ResistanceEvaluatorTests
{
    static readonly ResistanceEvaluator Eval = new();

    static StatusApplyRequest Req(string statusId = "wither") => new(
        statusId,
        HostPtr: "Z1",
        AttackerPtr: "P1",
        BaseMagnitude: 20,
        BaseDuration: 5000);

    [Fact]
    public void Neutral_stub_tier_power_contributes_to_delta()
    {
        // T3.1 (power-plan.md, ResistFromPowerRatio 0->1.0): this test used to assert delta==1.0 for
        // two IDENTICAL actors -- it encoded the bug (attacker's tier power counted, defender's did
        // not, so an even match never contested at zero). Now asserts 0.0, the correct value.
        var attacker = ActorDerivedSnapshot.StubNeutral();
        var defender = ActorDerivedSnapshot.StubNeutral();
        var delta = ResistanceEvaluator.ComputeDelta("wither", StatusL2bCategory.Dot, attacker, defender);
        Assert.Equal(0.0, delta, 3);
        Assert.Equal(1.0, ResistanceEvaluator.ComputeNetFactor(delta));
    }

    // T3.1's red test (spec-status-contest.md §5): matched pair at every Theta must contest at
    // delta=0. Originally written and proven "even under the still-un-retired ProgressionPowerCurve"
    // (T3.1 landed before T3.2); updated here for T3.2 ("retire the curve") to construct
    // ProgressionPower = Theta directly, matching the new progression.power = Theta rule -- the
    // property this test proves (matched pair -> delta=0) is unaffected by which wave built it.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(50)]
    [InlineData(1000)]
    public void MatchedPair_ContestsAtDeltaZero_AtEveryTheta(int theta)
    {
        var matched = ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, theta),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0)
        });

        var delta = ResistanceEvaluator.ComputeDelta("wither", StatusL2bCategory.Dot, matched, matched);
        Assert.Equal(0.0, delta, 3);
        Assert.Equal(1.0, ResistanceEvaluator.ComputeNetFactor(delta));
    }

    [Fact]
    public void Delta_IsAntisymmetric()
    {
        // T3.2: Theta directly (12 vs 10), not curve-transformed -- see the note above.
        var stronger = ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 12.0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0)
        });
        var weaker = ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 10.0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0)
        });

        var ahead = ResistanceEvaluator.ComputeDelta("wither", StatusL2bCategory.Dot, stronger, weaker);
        var behind = ResistanceEvaluator.ComputeDelta("wither", StatusL2bCategory.Dot, weaker, stronger);
        Assert.Equal(ahead, -behind, 3);
    }

    // T3.2's own red test (spec-status-contest.md S5, power-todo.md T3.2): matched pair at Theta=12
    // must flip from the OLD curve's netFactor=4096 to the NEW linear formula's netFactor=1.0. T3.1
    // already fixed the delta=0 half (ResistFromPowerRatio); this proves the SECOND half --
    // ComputeNetFactor's own formula no longer produces a curve-shaped blowout for ANY delta, not
    // just for the specific matched-pair case T3.1 covers.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    [InlineData(25)]
    public void NetFactor_IsLinearInDelta_NotACliff(double delta)
    {
        var expected = 1.0 + delta / StatusPolicy.NetFactorScale;
        Assert.Equal(Math.Clamp(expected, StatusPolicy.MinNetFactor, StatusPolicy.MaxNetFactor),
            ResistanceEvaluator.ComputeNetFactor(delta), 6);
    }

    [Fact]
    public void RedTest_MatchedPairAtTheta12_NetFactorFlips4096To1()
    {
        // The exact SSOT S6.0 scenario, both fixes landed: was netFactor=4096 (T3.1: delta=0 fixes
        // this already, independent of T3.2) -- this test's OWN point is that ComputeNetFactor(0)
        // is 1.0 via the general linear formula now, with NO delta==0 special case in the source
        // (asserted directly below, not just by the numeric outcome, which a reintroduced special
        // case would also satisfy).
        Assert.Equal(1.0, ResistanceEvaluator.ComputeNetFactor(0.0));

        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRootForThisFile(), "src", "FusionRpg.Core", "Status", "ResistanceEvaluator.cs"));
        var netFactorBody = System.Text.RegularExpressions.Regex.Match(source, @"ComputeNetFactor\(double delta\)([\s\S]*?)\n    \}").Groups[1].Value;
        Assert.DoesNotContain("1e-9", netFactorBody);
        Assert.DoesNotContain("Abs(delta)", netFactorBody);
    }

    static string RepoRootForThisFile()
    {
        return KeepverseRoots.Core();
    }

    [Fact]
    public void Neutral_stub_p_apply_near_half()
    {
        var result = Eval.Evaluate(
            Req(),
            ActorDerivedSnapshot.StubNeutral(),
            ActorDerivedSnapshot.StubNeutral(),
            new FixedStatusRng(0.0));
        Assert.True(result.Applied);
        Assert.InRange(result.PApply, 0.49, 0.51);
        Assert.Equal(1.0, result.NetFactor);
    }

    [Fact]
    public void Delta_negative_ten_potency_floor_skips_roll()
    {
        // T3.2: the floor is no longer "any negative delta" (the old Clamp(delta,0,Max) cliff) -- it
        // is delta <= -NetFactorScale (-10). StatusResistOmni raised 5.0 -> 10.0 so this test's own
        // setup actually produces delta=-10 (matching its name) under the new linear formula:
        // totalPower=1 (attacker StubNeutral), totalResist=1*1.0(ResistFromPowerRatio)+10=11, delta=-10.
        var attacker = ActorDerivedSnapshot.StubNeutral();
        var defender = ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusResistOmni, 10.0)
        });
        var result = Eval.Evaluate(Req(), attacker, defender, new FixedStatusRng(0.0));
        Assert.False(result.Applied);
        Assert.Equal(StatusResistReason.PotencyFloor, result.ResistReason);
        Assert.Equal(0, result.PApply);
    }

    [Fact]
    public void Omni_resist_1M_vs_power_100_potency_floor()
    {
        var attacker = ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusPower(statusId: "rot"), 100)
        });
        var defender = ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionPower, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.ProgressionRealm, 1.0),
            new KeyValuePair<string, double>(DerivedStatChannels.StatusResistOmni, 1_000_000)
        });
        var result = Eval.Evaluate(Req("rot"), attacker, defender, new FixedStatusRng(0.0));
        Assert.False(result.Applied);
        Assert.Equal(StatusResistReason.PotencyFloor, result.ResistReason);
    }

    [Theory]
    [InlineData(-1500, 0.01)]
    [InlineData(0, 0.50)]
    [InlineData(50, 0.62)]
    [InlineData(1500, 0.99)]
    public void Golden_apply_chance_table(double delta, double expectedApprox)
    {
        var scale = 100.0;
        var p = ResistanceEvaluator.Sigmoid(delta / scale);
        Assert.InRange(p, expectedApprox - 0.02, expectedApprox + 0.02);
    }

    // T3.2 (audit F4): netFactor = 1 + delta/NetFactorScale, not a raw clamp(delta). -10 and 0 are
    // unchanged coincidentally (both formulas floor at exactly 0, and agree at exactly 1.0, for
    // these two inputs) -- 50 moves from the old raw-delta value (50, a 50x multiplier -- the exact
    // cliff audit F4 named) to 1+50/10=6.0, still a large but no longer absurd swing.
    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 1.0)]
    [InlineData(50, 6.0)]
    public void Golden_potency_table(double delta, double expectedNet)
    {
        Assert.Equal(expectedNet, ResistanceEvaluator.ComputeNetFactor(delta));
    }

    [Fact]
    public void Complete_immunity_blocks_before_roll()
    {
        var defender = ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.StatusImmune("poison"), 1.0)
        });
        var result = Eval.Evaluate(
            Req("poison") with { ImmunityTags = new[] { "poison" } },
            ActorDerivedSnapshot.StubNeutral(),
            defender,
            new FixedStatusRng(0.0));
        Assert.False(result.Applied);
        Assert.Equal(StatusResistReason.Immunity, result.ResistReason);
    }

    [Fact]
    public void Attacker_less_uses_zero_power()
    {
        // T3.1: genuinely unchanged, but not for the reason the spec originally claimed, and not by
        // accident either. First pass: assumed unchanged. Second pass (direct computation): found
        // AttackerLess() zeroes only the ATTACKER's channels, so a normal StubNeutral() defender's
        // own tier power now counts as resist same as any real actor -- delta = 0 - 1*1.0 = -1.
        // Third pass (running BattleStatusTests): -1 sends every scripted DoT/CC to netFactor's
        // MinNetFactor floor (0.0) -- Dot_kills_through_rounds went Victory -> Stalemate. That is a
        // real defect, not "expected golden movement" -- an attacker-less application has no real
        // attacker side to contest tier power WITH, so the contest is symmetric-exclude, not
        // one-sided. ComputeDelta's attackerLess parameter (added here) restores 0.0, correctly this
        // time: proven by the full battle suite passing again, not just this unit re-passing.
        var attackerLess = ActorDerivedSnapshot.AttackerLess();
        var defender = ActorDerivedSnapshot.StubNeutral();
        var delta = ResistanceEvaluator.ComputeDelta("blight", StatusL2bCategory.Contagion, attackerLess, defender, attackerLess: true);
        Assert.Equal(0.0, delta, 3);
    }

    [Fact]
    public void Grant_chance_combines_with_p_apply()
    {
        var result = Eval.Evaluate(
            Req() with { GrantChance = 0.5 },
            ActorDerivedSnapshot.StubNeutral(),
            ActorDerivedSnapshot.StubNeutral(),
            new FixedStatusRng(0.0));
        Assert.True(result.Applied);
        Assert.InRange(result.PFinal, 0.24, 0.26);
    }

    // T3.2 ("retire the curve"): Progression_power_curve_feeds_delta deleted here, not updated --
    // it tested that ProgressionPowerCurve.PowerFromLevel's OUTPUT correctly fed ComputeDelta, and
    // the curve it exercised no longer exists (ProgressionPowerCurve.cs deleted the same commit).
    // Its coverage intent -- "a power value correctly reaches delta" -- is already proven by
    // MatchedPair_ContestsAtDeltaZero_AtEveryTheta and Delta_IsAntisymmetric below, now exercising
    // progression.power = Theta directly.

    // ============================================================================================
    // spec-status-potency.md §6 -- the two-delta split (T4.1). Matched-tier attacker/defender pairs
    // (ProgressionPower=1, ProgressionRealm=1) throughout, isolating whatever extra term each test
    // adds from the tier-power contest, which cancels to 0 for a matched pair (T3.1).
    // ============================================================================================

    static ActorDerivedSnapshot MatchedTier(params (string Channel, double Value)[] extra)
    {
        var values = new List<KeyValuePair<string, double>>
        {
            new(DerivedStatChannels.ProgressionPower, 1.0),
            new(DerivedStatChannels.ProgressionRealm, 1.0)
        };
        foreach (var (channel, value) in extra)
            values.Add(new KeyValuePair<string, double>(channel, value));
        return ActorDerivedSnapshot.FromValues(values);
    }

    [Fact]
    public void AllStatusGoldensUnchanged()
    {
        // spec-status-potency.md §2.2 -- the acceptance test. All four new families default to 0, so
        // durationDelta/intensityDelta collapse to the SAME single `delta` Phase 1 already computed,
        // and every netFactor the result carries (Phase 1's own, plus both new potency ones) is
        // identical -- proven against a non-trivial delta (not 0, which every formula agrees on
        // trivially) so a reintroduced coupling bug would actually surface here.
        var attacker = MatchedTier((DerivedStatChannels.StatusPowerOmni, 20.0));
        var defender = MatchedTier();
        var request = Req();

        var result = Eval.Evaluate(request, attacker, defender, new FixedStatusRng(0.0));

        var expectedDelta = ResistanceEvaluator.ComputeDelta("wither", StatusL2bCategory.Dot, attacker, defender);
        var expectedNetFactor = ResistanceEvaluator.ComputeNetFactor(expectedDelta);
        Assert.Equal(3.0, expectedNetFactor); // non-trivial baseline, not the delta=0 case

        Assert.True(result.Applied);
        Assert.Equal(expectedNetFactor, result.NetFactor);
        Assert.Equal(expectedNetFactor, result.DurationNetFactor);
        Assert.Equal(expectedNetFactor, result.IntensityNetFactor);
        Assert.Equal(request.BaseDuration * expectedNetFactor, result.EffectiveDuration, 6);
        Assert.Equal(request.BaseMagnitude * expectedNetFactor, result.EffectiveMagnitude, 6);
    }

    [Fact]
    public void LongWeakIsExpressible()
    {
        // spec-status-potency.md §1 -- the objective. Duration up, intensity down, independently.
        // Locked together before this module (both moved by the same netFactor); this is only
        // expressible because duration/intensity now read distinct channels.
        var attacker = MatchedTier(
            (DerivedStatChannels.StatusDuration("wither"), 15.0),
            (DerivedStatChannels.StatusIntensity("wither"), -5.0));
        var defender = MatchedTier();
        var request = Req();

        var result = Eval.Evaluate(request, attacker, defender, new FixedStatusRng(0.0));

        Assert.True(result.Applied);
        Assert.True(result.DurationNetFactor > 1.0, $"expected longer duration, got {result.DurationNetFactor}");
        Assert.True(result.IntensityNetFactor < 1.0, $"expected weaker intensity, got {result.IntensityNetFactor}");
        Assert.True(result.EffectiveDuration > request.BaseDuration);
        Assert.True(result.EffectiveMagnitude < request.BaseMagnitude);
    }

    [Fact]
    public void ShortBrutalIsExpressible()
    {
        // The mirror of LongWeakIsExpressible.
        var attacker = MatchedTier(
            (DerivedStatChannels.StatusDuration("wither"), -5.0),
            (DerivedStatChannels.StatusIntensity("wither"), 15.0));
        var defender = MatchedTier();
        var request = Req();

        var result = Eval.Evaluate(request, attacker, defender, new FixedStatusRng(0.0));

        Assert.True(result.Applied);
        Assert.True(result.DurationNetFactor < 1.0, $"expected shorter duration, got {result.DurationNetFactor}");
        Assert.True(result.IntensityNetFactor > 1.0, $"expected brutal intensity, got {result.IntensityNetFactor}");
        Assert.True(result.EffectiveDuration < request.BaseDuration);
        Assert.True(result.EffectiveMagnitude > request.BaseMagnitude);
    }

    [Fact]
    public void DeltaZeroStillOne()
    {
        // Both new deltas honour the same T3.2 rule Phase 1's delta already does: 0 -> netFactor 1.0.
        var neutral = ActorDerivedSnapshot.StubNeutral();
        var durationDelta = ResistanceEvaluator.ComputePotencyDelta(
            "wither", StatusL2bCategory.Dot, neutral, neutral, attackerLess: false, element: null, family: "duration");
        var intensityDelta = ResistanceEvaluator.ComputePotencyDelta(
            "wither", StatusL2bCategory.Dot, neutral, neutral, attackerLess: false, element: null, family: "intensity");

        Assert.Equal(0.0, durationDelta, 3);
        Assert.Equal(0.0, intensityDelta, 3);
        Assert.Equal(1.0, ResistanceEvaluator.ComputeNetFactor(durationDelta));
        Assert.Equal(1.0, ResistanceEvaluator.ComputeNetFactor(intensityDelta));
    }

    [Fact]
    public void PotencyFloorOnIntensityOnly()
    {
        // spec-status-potency.md §2.2 -- a zero-DURATION status is instantaneous (a legitimate effect,
        // still Applied); a zero-INTENSITY status does nothing and IS Resisted. The floor checks
        // intensity only.
        var defender = MatchedTier();

        var zeroDurationAttacker = MatchedTier((DerivedStatChannels.StatusDuration("wither"), -1000.0));
        var instantResult = Eval.Evaluate(Req(), zeroDurationAttacker, defender, new FixedStatusRng(0.0));
        Assert.True(instantResult.Applied);
        Assert.Equal(0.0, instantResult.EffectiveDuration);
        Assert.NotEqual(0.0, instantResult.EffectiveMagnitude);

        var zeroIntensityAttacker = MatchedTier((DerivedStatChannels.StatusIntensity("wither"), -1000.0));
        var resistedResult = Eval.Evaluate(Req(), zeroIntensityAttacker, defender, new FixedStatusRng(0.0));
        Assert.False(resistedResult.Applied);
        Assert.Equal(StatusResistReason.PotencyFloor, resistedResult.ResistReason);
    }

    [Fact]
    public void PartialImmunityScalesBoth()
    {
        // (1 - immuneReduction) applies to BOTH potency axes -- partial immunity blunts a status
        // overall, not selectively by axis.
        var attacker = MatchedTier((DerivedStatChannels.StatusPowerOmni, 20.0));
        var defender = MatchedTier();
        var request = Req("poison") with { ImmunityTags = new[] { "poison" } };

        var baseline = Eval.Evaluate(request, attacker, defender, new FixedStatusRng(0.0));

        var partiallyImmuneDefender = MatchedTier((DerivedStatChannels.StatusImmuneReduction("poison"), 0.5));
        var reduced = Eval.Evaluate(request, attacker, partiallyImmuneDefender, new FixedStatusRng(0.0));

        Assert.True(baseline.Applied);
        Assert.True(reduced.Applied);
        Assert.Equal(baseline.DurationNetFactor * 0.5, reduced.DurationNetFactor, 6);
        Assert.Equal(baseline.IntensityNetFactor * 0.5, reduced.IntensityNetFactor, 6);
    }

    [Fact]
    public void ElementResistRead()
    {
        // spec-status-potency.md §2.3 (Q1) -- status.resist.fire already resolved through the open
        // prefix; nothing read it before this module. A fire-tagged status now pays it.
        var attacker = ActorDerivedSnapshot.StubNeutral();
        var defender = MatchedTier(("status.resist.fire", 15.0));

        var taggedDelta = ResistanceEvaluator.ComputeDelta(
            "wither", StatusL2bCategory.Dot, attacker, defender, element: "fire");
        var untaggedDelta = ResistanceEvaluator.ComputeDelta(
            "wither", StatusL2bCategory.Dot, attacker, defender, element: null);

        Assert.Equal(untaggedDelta - 15.0, taggedDelta, 6);
    }

    [Fact]
    public void UntaggedContributesNothing()
    {
        // T5: a missing element tag is a genuine absence, not a default. Even though the defender
        // DOES carry status.resist.fire, an untagged status (element: null) must not read it -- the
        // delta with no tag must equal the delta against a defender with no fire resist at all, and
        // omitting the parameter must behave identically to passing null explicitly (proving the
        // default really is "nothing", not a silent fallback to some other channel).
        var attacker = ActorDerivedSnapshot.StubNeutral();
        var defenderWithFireResist = MatchedTier(("status.resist.fire", 15.0));
        var cleanDefender = MatchedTier();

        var untaggedDelta = ResistanceEvaluator.ComputeDelta(
            "wither", StatusL2bCategory.Dot, attacker, defenderWithFireResist, element: null);
        var omittedDelta = ResistanceEvaluator.ComputeDelta(
            "wither", StatusL2bCategory.Dot, attacker, defenderWithFireResist);
        var cleanDelta = ResistanceEvaluator.ComputeDelta(
            "wither", StatusL2bCategory.Dot, attacker, cleanDefender, element: null);

        Assert.Equal(untaggedDelta, omittedDelta, 6);
        Assert.Equal(cleanDelta, untaggedDelta, 6);
    }
}

public class StatusCategoryRegistryTests
{
    [Theory]
    [InlineData("wither", StatusL2bCategory.Dot)]
    [InlineData("butter", StatusL2bCategory.Cc)]
    [InlineData("blight", StatusL2bCategory.Contagion)]
    public void Known_ids_map_to_category(string statusId, string category)
    {
        Assert.Equal(category, StatusCategoryRegistry.GetRequiredCategory(statusId));
    }

    [Fact]
    public void All_twenty_four_locked_ids_are_registered()
    {
        // NOT an exact-count assertion: StatusCategoryRegistry.Register (T16, action-todo.md) is a
        // real, additive extension point (status-ssot.md §3 — "New status id: Register def in Core
        // catalog"), and other in-process tests (ExhaustionPolicyTests) legitimately register their
        // own ids into this SAME static, process-wide dictionary. An exact-count assertion here is
        // the identical defect ActorChannelsTests's old "exactly 84 channels" row already was —
        // "left in place it becomes a red test on day one" once a second registrant exists. The 24
        // locked ids (status-ssot.md §9, 21 -> 24 with D2.19's `nerve.*` block) must always be a
        // SUBSET of whatever else got registered.
        var locked = StatusCatalogBootstrap.CreateDefault().All().Select(d => d.StatusId);
        foreach (var id in locked)
            Assert.Contains(id, StatusCategoryRegistry.AllStatusIds);
        // pin: closed-vocabulary StatusCatalogBootstrap — status-ssot.md §9, a new status id is a reviewed change
        Assert.Equal(24, locked.Count());
    }
}

public class StatusCatalogTests
{
    [Fact]
    public void Bootstrap_registers_24_ids()
    {
        var catalog = StatusCatalogBootstrap.CreateDefault();
        Assert.Equal(24, catalog.All().Count);
    }

    [Fact]
    public void Unknown_statusId_rejects()
    {
        var catalog = StatusCatalogBootstrap.CreateDefault();
        Assert.Throws<UnknownStatusIdException>(() => catalog.GetRequired("not_a_status"));
    }

    [Fact]
    public void Elemental_family_mutex_defs_exist()
    {
        var catalog = StatusCatalogBootstrap.CreateDefault();
        Assert.Equal("elemental", catalog.GetRequired("freeze").Family);
        Assert.Equal(StatusStacking.Replace, catalog.GetRequired("freeze").Stacking);
    }
}
