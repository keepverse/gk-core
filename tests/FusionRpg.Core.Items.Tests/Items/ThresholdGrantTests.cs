using System.Reflection;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Thresholds;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `threshold-grants` (item module 12) — the one evaluator, its three consumers, and the recovery
/// curve's SHAPE rather than only its ends.
/// </summary>
public class ThresholdGrantTests
{
    internal static string RepoRoot() => DropVolumeTests.RepoRoot();

    internal static FrameMixTuning Tuning() => FrameMixTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-frame-mix.v1.json")));

    internal static IReadOnlyList<ItemRoleDef> Registry() => ItemRoleRegistry.Parse(
        File.ReadAllText(Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_registry", "core.v1.json")));

    internal static IReadOnlyDictionary<ItemRole, long> HybridCore() =>
        FrameMixPredicate.HybridCoreBudget(Registry());

    // ---- the generic machine -----------------------------------------------------------------------

    static SetDef TwoAndFour(string setId) => new(
        setId, setId,
        new[]
        {
            new SetMemberDef("item.a", ItemRole.ArmamentPrimary, ItemFrame.Humanoid),
            new SetMemberDef("item.b", ItemRole.CoreGuard, ItemFrame.Humanoid),
            new SetMemberDef("item.c", ItemRole.Mantle, ItemFrame.Humanoid),
            new SetMemberDef("item.d", ItemRole.Footing, ItemFrame.Humanoid),
        },
        new[]
        {
            new SetTierDef(2, ThresholdContainerIds.SetTier(setId, 2), true),
            new SetTierDef(4, ThresholdContainerIds.SetTier(setId, 4), false),
        });

    [Fact]
    public void Grants_are_cumulative_at_four_pieces()
    {
        var set = TwoAndFour("ember-legion");
        var worn = new[]
        {
            new EquippedPiece(ItemRole.ArmamentPrimary, "item.a"),
            new EquippedPiece(ItemRole.CoreGuard, "item.b"),
            new EquippedPiece(ItemRole.Mantle, "item.c"),
            new EquippedPiece(ItemRole.Footing, "item.d"),
        };

        var grant = ThresholdEvaluator.Grant(SetEvaluator.Consumer(set), SetEvaluator.Hits(worn, new[] { set }));

        Assert.Equal(4, grant.Count);
        Assert.Equal(new[] { "set.ember-legion-02", "set.ember-legion-04" }, grant.WantedContainerIds);
    }

    [Fact]
    public void Unequipping_the_fourth_piece_withdraws_only_the_four_piece_tier()
    {
        var set = TwoAndFour("ember-legion");
        var three = new[]
        {
            new EquippedPiece(ItemRole.ArmamentPrimary, "item.a"),
            new EquippedPiece(ItemRole.CoreGuard, "item.b"),
            new EquippedPiece(ItemRole.Mantle, "item.c"),
        };

        var (grant, diff) = ThresholdEvaluator.Evaluate(
            SetEvaluator.Consumer(set), SetEvaluator.Hits(three, new[] { set }),
            new[] { "set.ember-legion-02", "set.ember-legion-04" });

        Assert.Equal(3, grant.Count);
        Assert.Equal(new[] { "set.ember-legion-04" }, diff.ToWithdraw);
        Assert.Empty(diff.ToBind);
        Assert.Equal(new[] { "set.ember-legion-02" }, diff.Unchanged);
    }

    [Fact]
    public void Re_evaluation_is_withdraw_and_rebind_never_a_patch()
    {
        // The reconcile is TOTAL: a stale row under this source that the current count does not want is
        // withdrawn, even though nothing about the wanted set changed. A partial update is how derived
        // state drifts.
        var set = TwoAndFour("ember-legion");
        var two = new[]
        {
            new EquippedPiece(ItemRole.ArmamentPrimary, "item.a"),
            new EquippedPiece(ItemRole.CoreGuard, "item.b"),
        };

        var (_, diff) = ThresholdEvaluator.Evaluate(
            SetEvaluator.Consumer(set), SetEvaluator.Hits(two, new[] { set }),
            new[] { "set.ember-legion-02", "set.ember-legion-04", "set.ember-legion-06" });

        Assert.Equal(new[] { "set.ember-legion-04", "set.ember-legion-06" }, diff.ToWithdraw);
    }

    [Fact]
    public void The_evaluator_is_pure_and_runs_with_no_game_process()
    {
        // SC8: everything this module computes lives in FusionRpg.Core, which references no Unity
        // assembly and opens no file. Asserted structurally rather than by review.
        var asm = typeof(ThresholdEvaluator).Assembly;
        Assert.Equal("FusionRpg.Core", asm.GetName().Name);
        Assert.DoesNotContain(asm.GetReferencedAssemblies(),
            a => a.Name is not null && a.Name.StartsWith("UnityEngine", StringComparison.Ordinal));

        foreach (var t in new[]
                 {
                     typeof(ThresholdEvaluator), typeof(FrameMixPredicate),
                     typeof(SetEvaluator), typeof(CharmResonance), typeof(ThresholdContainerIds),
                 })
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(FileInfo)
                                                          || p.ParameterType == typeof(DirectoryInfo)
                                                          || p.ParameterType == typeof(Stream));
    }

    [Fact]
    public void Three_consumers_share_one_evaluator_with_no_forked_copy()
    {
        // The module's whole claim, asserted by instantiating all three from the same generic type and
        // driving all three through the same Grant call.
        var tuning = Tuning();
        var core = HybridCore();

        var set = TwoAndFour("ember-legion");
        var setConsumer = SetEvaluator.Consumer(set);
        var frameMixConsumer = FrameMixPredicate.Consumer(tuning, core);
        var charmConsumer = CharmResonance.Consumer("offense", new[]
        {
            new CharmResonanceRow("offense", 2, ThresholdContainerIds.CharmResonance("offense", 2), "charm.res-offense-2"),
        });

        Assert.Equal(typeof(ThresholdConsumer<>), setConsumer.GetType().GetGenericTypeDefinition());
        Assert.Equal(typeof(ThresholdConsumer<>), frameMixConsumer.GetType().GetGenericTypeDefinition());
        Assert.Equal(typeof(ThresholdConsumer<>), charmConsumer.GetType().GetGenericTypeDefinition());

        Assert.Equal(2, ThresholdEvaluator.Grant(setConsumer, SetEvaluator.Hits(new[]
        {
            new EquippedPiece(ItemRole.ArmamentPrimary, "item.a"),
            new EquippedPiece(ItemRole.CoreGuard, "item.b"),
        }, new[] { set })).Count);

        Assert.Equal(2, ThresholdEvaluator.Grant(charmConsumer, new[]
        {
            new HeldCharm("charm.off-ctrl-001", "offense"),
            new HeldCharm("charm.off-ctrl-002", "offense"),
        }).Count);

        Assert.Equal(160, ThresholdEvaluator.Grant(frameMixConsumer, new[]
        {
            new EquippedRoleFrame(ItemRole.ArmamentPrimary, ItemFrame.Humanoid),
            new EquippedRoleFrame(ItemRole.CoreGuard, ItemFrame.Plant),
            new EquippedRoleFrame(ItemRole.ArmamentSecondary, ItemFrame.Plant),
        }).Count);
    }

    // ---- D3's predicate shape ----------------------------------------------------------------------

    [Fact]
    public void Frame_mix_is_a_min_over_two_buckets_not_a_count_over_one()
    {
        var core = HybridCore();

        // Ten humanoid roles, two plant. A count over ONE predicate would answer 12 (or 10); the
        // mechanism answers the SMALLER bucket, which is the two plant roles.
        var body = new List<EquippedRoleFrame>();
        foreach (var role in new[]
                 {
                     ItemRole.ArmamentPrimary, ItemRole.CoreGuard, ItemRole.ArmamentSecondary,
                     ItemRole.JewelMajor, ItemRole.Manipulator, ItemRole.Mantle, ItemRole.Girdle,
                     ItemRole.Footing, ItemRole.Infusion, ItemRole.Retinue,
                 })
            body.Add(new EquippedRoleFrame(role, ItemFrame.Humanoid));
        body.Add(new EquippedRoleFrame(ItemRole.JewelMinorA, ItemFrame.Plant));
        body.Add(new EquippedRoleFrame(ItemRole.JewelMinorB, ItemFrame.Plant));

        Assert.Equal(30, FrameMixPredicate.MinorityMilli(body, core));   // 15 + 15, not 770 and not 12
    }

    [Fact]
    public void Frame_mix_is_weighted_by_budget_permille()
    {
        var core = HybridCore();

        // Same 6/6 SPLIT BY COUNT, two different bodies. If the predicate counted items they would be
        // identical; weighted, they are 230 and 400 permille apart.
        var cheapSix = new[]
        {
            ItemRole.JewelMinorA, ItemRole.JewelMinorB, ItemRole.Retinue,
            ItemRole.Footing, ItemRole.Infusion, ItemRole.Girdle,
        };
        var dearSix = new[]
        {
            ItemRole.ArmamentPrimary, ItemRole.CoreGuard, ItemRole.ArmamentSecondary,
            ItemRole.JewelMajor, ItemRole.Manipulator, ItemRole.Mantle,
        };

        var cheapMinority = Body(cheapSix, dearSix);
        var evenMinority = Body(new[] { ItemRole.ArmamentPrimary, ItemRole.CoreGuard, ItemRole.JewelMinorA, ItemRole.JewelMinorB, ItemRole.Retinue, ItemRole.Infusion },
                                new[] { ItemRole.ArmamentSecondary, ItemRole.JewelMajor, ItemRole.Manipulator, ItemRole.Mantle, ItemRole.Girdle, ItemRole.Footing });

        Assert.Equal(230, FrameMixPredicate.MinorityMilli(cheapMinority, core));
        Assert.Equal(400, FrameMixPredicate.MinorityMilli(evenMinority, core));

        IReadOnlyList<EquippedRoleFrame> Body(IEnumerable<ItemRole> plant, IEnumerable<ItemRole> humanoid) =>
            plant.Select(r => new EquippedRoleFrame(r, ItemFrame.Plant))
                 .Concat(humanoid.Select(r => new EquippedRoleFrame(r, ItemFrame.Humanoid)))
                 .ToList();
    }

    [Fact]
    public void A_six_six_split_of_the_cheapest_roles_concedes_230_not_400_permille()
    {
        // THE defect, as a fixture. jewel-minor-a 15 + jewel-minor-b 15 + retinue 40 + footing 50 +
        // infusion 50 + girdle 60 = 230 of an 800 permille body: 28.75%, not half.
        var core = HybridCore();
        var cheapest = new[]
        {
            ItemRole.JewelMinorA, ItemRole.JewelMinorB, ItemRole.Retinue,
            ItemRole.Footing, ItemRole.Infusion, ItemRole.Girdle,
        };

        Assert.Equal(230, cheapest.Sum(r => core[r]));
        Assert.Equal(6, cheapest.Length);
        Assert.NotEqual(400, cheapest.Sum(r => core[r]));
    }

    [Fact]
    public void The_hybrid_core_used_by_the_predicate_is_twelve_roles_summing_to_800()
    {
        var core = HybridCore();
        // Formula, not a literal (population-pin SE3.3, 2026-09-19): every ItemRole except the four
        // named exclusions below (D3's three dropped roles plus Standard).
        Assert.Equal(Enum.GetValues<ItemRole>().Length - 4, core.Count);
        Assert.Equal(800, core.Values.Sum());

        // D3's three dropped roles, enumerated so a registry regression is a named failure.
        Assert.DoesNotContain(ItemRole.WardArray, core.Keys);
        Assert.DoesNotContain(ItemRole.HeadGuard, core.Keys);
        Assert.DoesNotContain(ItemRole.Sense, core.Keys);
        Assert.DoesNotContain(ItemRole.Standard, core.Keys);
    }

    // ---- the recovery curve ------------------------------------------------------------------------

    static long Recovery(long minorityMilli) => FrameMixPredicate.EffectiveBudgetMilli(minorityMilli, Tuning());

    [Fact]
    public void A_cherry_picked_ten_two_body_sits_at_the_800_floor()
    {
        // The abuse side. 10/2 conceding the two jewel-minor roles is 30 permille: 815, barely off 800.
        Assert.Equal(800, Recovery(0));
        Assert.Equal(815, Recovery(30));
    }

    [Fact]
    public void An_even_budget_split_reaches_parity()
    {
        Assert.Equal(1000, Recovery(400));
    }

    [Fact]
    public void A_ten_two_body_recovers_strictly_less_than_a_seven_five_body_which_recovers_less_than_parity()
    {
        // ⭐ The curve's SHAPE, not just its ends. Without this row a step function at minorityMilli 40
        // passes the two tests above and D3's whole mechanism costs one cheap role.
        var tenTwo = Recovery(30);     // jewel-minor-a + jewel-minor-b
        var sevenFive = Recovery(170); // the five lightest: 15 + 15 + 40 + 50 + 50
        var parity = Recovery(400);

        Assert.Equal(815, tenTwo);
        Assert.Equal(885, sevenFive);
        Assert.Equal(1000, parity);
        Assert.True(tenTwo < sevenFive && sevenFive < parity);
    }

    [Fact]
    public void The_recovery_curve_is_strictly_increasing_over_the_whole_range()
    {
        // Property 3, over every 2 permille from 0 to 400 — no flat interval anywhere. (Every 2 rather
        // than every 1 because the shipped slope is +1 per 2 conceded and the interpolation is exact
        // integer arithmetic: a 1-permille step is genuinely flat under a half-slope, which is the
        // rounding, not a free prefix. The MONOTONIC half is asserted at every single permille below.)
        var tuning = Tuning();
        long prev = -1;
        for (long m = 0; m <= tuning.ParityMinorityMilli; m += 2)
        {
            var v = FrameMixPredicate.EffectiveBudgetMilli(m, tuning);
            Assert.True(v > prev, $"f({m}) = {v} did not increase past {prev}");
            prev = v;
        }

        long last = -1;
        for (long m = 0; m <= tuning.ParityMinorityMilli; m++)
        {
            var v = FrameMixPredicate.EffectiveBudgetMilli(m, tuning);
            Assert.True(v >= last, $"f({m}) = {v} fell below {last}");
            last = v;
        }
    }

    [Fact]
    public void A_step_function_knot_list_is_refused_at_load_with_a_reason_code()
    {
        // The exact cheat the curve exists to prevent, expressed as a knot list a balance pass could
        // plausibly write: everything from 40 permille up is already at parity.
        const string stepJson = """
            {
              "hybridCore": { "budgetTotalMilli": 800, "parityMinorityMilli": 400 },
              "recoveryCurve": { "knots": [
                { "minorityMilli": 0,   "effectiveBudgetMilli": 800 },
                { "minorityMilli": 40,  "effectiveBudgetMilli": 1000 },
                { "minorityMilli": 400, "effectiveBudgetMilli": 1000 }
              ]},
              "tiers": { "containerIdFormat": "set.frame-mix-{ordinal:D2}", "sourceKey": "frame-mix", "priority": 0 }
            }
            """;

        var ex = Assert.Throws<FrameMixTuningRejection>(() => FrameMixTuning.Parse(stepJson));
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, ex.Rejection.Reason);
        Assert.Contains("threshold.frame-mix-curve-not-strictly-increasing", ex.Rejection.Detail);
    }

    [Fact]
    public void A_falling_or_duplicated_knot_is_refused_too()
    {
        const string dupX = """
            {
              "hybridCore": { "budgetTotalMilli": 800, "parityMinorityMilli": 400 },
              "recoveryCurve": { "knots": [
                { "minorityMilli": 0,   "effectiveBudgetMilli": 800 },
                { "minorityMilli": 200, "effectiveBudgetMilli": 900 },
                { "minorityMilli": 200, "effectiveBudgetMilli": 950 },
                { "minorityMilli": 400, "effectiveBudgetMilli": 1000 }
              ]},
              "tiers": { "containerIdFormat": "x", "sourceKey": "frame-mix", "priority": 0 }
            }
            """;
        Assert.Contains("threshold.frame-mix-curve-knots-unordered",
            Assert.Throws<FrameMixTuningRejection>(() => FrameMixTuning.Parse(dupX)).Rejection.Detail);

        const string wrongFloor = """
            {
              "hybridCore": { "budgetTotalMilli": 800, "parityMinorityMilli": 400 },
              "recoveryCurve": { "knots": [
                { "minorityMilli": 0,   "effectiveBudgetMilli": 900 },
                { "minorityMilli": 400, "effectiveBudgetMilli": 1000 }
              ]},
              "tiers": { "containerIdFormat": "x", "sourceKey": "frame-mix", "priority": 0 }
            }
            """;
        Assert.Contains("threshold.frame-mix-curve-floor-wrong",
            Assert.Throws<FrameMixTuningRejection>(() => FrameMixTuning.Parse(wrongFloor)).Rejection.Detail);

        const string wrongParity = """
            {
              "hybridCore": { "budgetTotalMilli": 800, "parityMinorityMilli": 400 },
              "recoveryCurve": { "knots": [
                { "minorityMilli": 0,   "effectiveBudgetMilli": 800 },
                { "minorityMilli": 400, "effectiveBudgetMilli": 1400 }
              ]},
              "tiers": { "containerIdFormat": "x", "sourceKey": "frame-mix", "priority": 0 }
            }
            """;
        Assert.Contains("threshold.frame-mix-curve-parity-wrong",
            Assert.Throws<FrameMixTuningRejection>(() => FrameMixTuning.Parse(wrongParity)).Rejection.Detail);
    }

    [Fact]
    public void The_shipped_default_curve_is_linear_and_reproduces_D3s_own_breakpoints()
    {
        var tuning = Tuning();

        // Linear: +1 permille of effective budget per 2 permille conceded, at every knot and between.
        foreach (var knot in tuning.Knots)
            Assert.Equal(800 + knot.MinorityMilli / 2, knot.EffectiveBudgetMilli);
        for (long m = 0; m <= 400; m += 7)
            Assert.Equal(800 + m / 2, FrameMixPredicate.EffectiveBudgetMilli(m, tuning));

        // D3's own table tops out at +200 across a twelve-role body, and that is exactly parity here.
        Assert.Equal(200, Recovery(400) - Recovery(0));

        // Its four authored knots above the floor step by an equal amount — the faithful translation
        // of a table that is itself linear in item count (0 / +70 / +140 / +200).
        var steps = tuning.Knots.Zip(tuning.Knots.Skip(1),
            (a, b) => b.EffectiveBudgetMilli - a.EffectiveBudgetMilli).ToList();
        Assert.All(steps, s => Assert.Equal(steps[0], s));
    }

    [Fact]
    public void A_two_heaviest_role_concession_beats_a_five_lightest_role_concession()
    {
        // Budget orders the recovery, not item count — proven from the surprising direction. A 10/2
        // body conceding armament-primary + core-guard (280) beats a 7/5 body conceding the five
        // lightest (170), even though it concedes fewer than half as many items.
        var core = HybridCore();
        var twoHeaviest = core[ItemRole.ArmamentPrimary] + core[ItemRole.CoreGuard];
        var fiveLightest = core[ItemRole.JewelMinorA] + core[ItemRole.JewelMinorB] + core[ItemRole.Retinue]
                           + core[ItemRole.Footing] + core[ItemRole.Infusion];

        Assert.Equal(280, twoHeaviest);
        Assert.Equal(170, fiveLightest);
        Assert.Equal(940, Recovery(twoHeaviest));
        Assert.Equal(885, Recovery(fiveLightest));
        Assert.True(Recovery(twoHeaviest) > Recovery(fiveLightest));
    }

    [Fact]
    public void A_minorityMilli_above_400_throws_and_is_never_clamped()
    {
        var tuning = Tuning();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameMixPredicate.EffectiveBudgetMilli(401, tuning));
        Assert.Contains("impossible by construction", ex.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameMixPredicate.EffectiveBudgetMilli(-1, tuning));
    }

    [Fact]
    public void The_frame_mix_weight_sum_overflows_by_throwing_never_by_wrapping()
    {
        var absurd = new Dictionary<ItemRole, long> { [ItemRole.ArmamentPrimary] = long.MaxValue / 2 };
        var body = Enumerable.Repeat(new EquippedRoleFrame(ItemRole.ArmamentPrimary, ItemFrame.Humanoid), 4).ToList();
        Assert.Throws<OverflowException>(() => FrameMixPredicate.MinorityMilli(body, absurd));
    }

    // ---- two partial sets, and the cap that must not exist -----------------------------------------

    [Fact]
    public void Two_partial_sets_grant_two_independent_two_piece_tiers()
    {
        var a = TwoAndFour("ember-legion");
        var b = new SetDef("tidebound", "Tidebound",
            new[]
            {
                new SetMemberDef("item.e", ItemRole.JewelMajor, ItemFrame.Plant),
                new SetMemberDef("item.f", ItemRole.Girdle, ItemFrame.Plant),
                new SetMemberDef("item.g", ItemRole.Retinue, ItemFrame.Plant),
                new SetMemberDef("item.h", ItemRole.Infusion, ItemFrame.Plant),
            },
            new[] { new SetTierDef(2, ThresholdContainerIds.SetTier("tidebound", 2), true) });

        var worn = new[]
        {
            new EquippedPiece(ItemRole.ArmamentPrimary, "item.a"),
            new EquippedPiece(ItemRole.CoreGuard, "item.b"),
            new EquippedPiece(ItemRole.JewelMajor, "item.e"),
            new EquippedPiece(ItemRole.Girdle, "item.f"),
        };

        var progress = SetEvaluator.Progress(worn, new[] { a, b });
        Assert.Equal(2, progress.Count);
        Assert.All(progress, p => Assert.Equal(2, p.Count));
        Assert.Equal(new[] { "set.ember-legion-02" }, progress.Single(p => p.SetId == "ember-legion").WantedContainerIds);
        Assert.Equal(new[] { "set.tidebound-02" }, progress.Single(p => p.SetId == "tidebound").WantedContainerIds);
    }

    [Fact]
    public void Withdrawing_one_partial_set_leaves_the_other_intact()
    {
        var a = TwoAndFour("ember-legion");
        var b = TwoAndFour("tidebound");

        // ember-legion drops to one piece; tidebound is untouched. The reconcile is per SOURCE, so
        // ember's diff never sees tidebound's row and cannot withdraw it.
        var worn = new[] { new EquippedPiece(ItemRole.ArmamentPrimary, "item.a") };
        var hits = SetEvaluator.Hits(worn, new[] { a });

        var (_, diff) = ThresholdEvaluator.Evaluate(SetEvaluator.Consumer(a), hits, new[] { "set.ember-legion-02" });
        Assert.Equal(new[] { "set.ember-legion-02" }, diff.ToWithdraw);

        Assert.Equal("set:ember-legion", SetEvaluator.Consumer(a).SourceKey);
        Assert.Equal("set:tidebound", SetEvaluator.Consumer(b).SourceKey);
        Assert.NotEqual(SetEvaluator.Consumer(a).SourceKey, SetEvaluator.Consumer(b).SourceKey);
    }

    [Fact]
    public void The_evaluator_carries_no_max_active_sets_parameter()
    {
        // ⛔ The cap that must not exist. Reflection rather than review, so it cannot be reintroduced
        // quietly under a balance name — I5 §3.6: the slot budget is the cap, and it is structural.
        var forbidden = new[] { "maxactivesets", "maxsets", "setcap", "activesetlimit", "maxpartialsets" };

        foreach (var t in new[] { typeof(ThresholdEvaluator), typeof(SetEvaluator), typeof(ThresholdConsumer<>), typeof(FrameMixTuning) })
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                foreach (var p in m.GetParameters())
                    Assert.DoesNotContain(p.Name?.ToLowerInvariant() ?? "", forbidden);

            foreach (var p in t.GetProperties())
                Assert.DoesNotContain(p.Name.ToLowerInvariant(), forbidden);
        }
    }

    [Fact]
    public void Seven_partial_sets_on_a_pure_frame_are_legal()
    {
        // Weird, probably bad, and exactly what a build space is supposed to contain.
        var roles = new[]
        {
            ItemRole.ArmamentPrimary, ItemRole.CoreGuard, ItemRole.ArmamentSecondary, ItemRole.JewelMajor,
            ItemRole.Manipulator, ItemRole.Mantle, ItemRole.Girdle, ItemRole.Footing, ItemRole.Infusion,
            ItemRole.Retinue, ItemRole.JewelMinorA, ItemRole.JewelMinorB, ItemRole.WardArray, ItemRole.HeadGuard,
        };

        var sets = new List<SetDef>();
        var worn = new List<EquippedPiece>();
        for (var i = 0; i < 7; i++)
        {
            var id = $"partial-{i}";
            var r1 = roles[i * 2];
            var r2 = roles[i * 2 + 1];
            sets.Add(new SetDef(id, id,
                new[]
                {
                    new SetMemberDef($"item.{id}-1", r1, ItemFrame.Humanoid),
                    new SetMemberDef($"item.{id}-2", r2, ItemFrame.Humanoid),
                    new SetMemberDef($"item.{id}-3", ItemRole.Sense, ItemFrame.Humanoid),
                },
                new[] { new SetTierDef(2, ThresholdContainerIds.SetTier(id, 2), true) }));
            worn.Add(new EquippedPiece(r1, $"item.{id}-1"));
            worn.Add(new EquippedPiece(r2, $"item.{id}-2"));
        }

        var progress = SetEvaluator.Progress(worn, sets);
        Assert.Equal(7, progress.Count);
        Assert.All(progress, p => Assert.Single(p.WantedContainerIds));
    }

    [Fact]
    public void Counting_is_per_role_not_per_item()
    {
        // ssot-sets.md §4.5: two copies of the same set ring in jewel-minor-a and -b count as ONE,
        // because the member row declares one role. The cheese closes with no special case.
        var set = new SetDef("ringcheese", "Ringcheese",
            new[]
            {
                new SetMemberDef("item.ring", ItemRole.JewelMinorA, ItemFrame.Humanoid),
                new SetMemberDef("item.torso", ItemRole.CoreGuard, ItemFrame.Humanoid),
                new SetMemberDef("item.boot", ItemRole.Footing, ItemFrame.Humanoid),
                new SetMemberDef("item.cloak", ItemRole.Mantle, ItemFrame.Humanoid),
            },
            new[] { new SetTierDef(2, ThresholdContainerIds.SetTier("ringcheese", 2), true) });

        var worn = new[]
        {
            new EquippedPiece(ItemRole.JewelMinorA, "item.ring"),
            new EquippedPiece(ItemRole.JewelMinorB, "item.ring"),   // a second copy, a different role
        };

        var grant = ThresholdEvaluator.Grant(SetEvaluator.Consumer(set), SetEvaluator.Hits(worn, new[] { set }));
        Assert.Equal(1, grant.Count);
        Assert.Empty(grant.WantedContainerIds);
    }

    // ---- ids, scopes and priorities ---------------------------------------------------------------

    [Fact]
    public void Tier_container_ids_sort_ordinally_in_numeric_order()
    {
        var ids = new[] { 2, 4, 6, 10, 12 }.Select(p => ThresholdContainerIds.SetTier("x", p)).ToList();
        var sorted = ids.OrderBy(i => i, StringComparer.Ordinal).ToList();
        Assert.Equal(ids, sorted);
        Assert.Equal("set.x-02", ids[0]);
        Assert.Equal("set.x-10", ids[3]);

        // The unpadded spelling is exactly the defect the pad prevents.
        var unpadded = new[] { "set.x-10", "set.x-2" }.OrderBy(i => i, StringComparer.Ordinal).ToList();
        Assert.Equal("set.x-10", unpadded[0]);
    }

    [Fact]
    public void A_set_id_that_ends_in_two_digits_is_refused_because_it_would_collide_with_a_tier_id()
    {
        Assert.Throws<ArgumentException>(() => ThresholdContainerIds.SetTier("ember-legion-04", 2));
        // The shipped corpus uses a THREE-digit sequence, which cannot collide with a two-digit pad.
        Assert.Equal("set.ember-legion-001-02", ThresholdContainerIds.SetTier("ember-legion-001", 2));
    }

    [Fact]
    public void Charm_resonance_binds_at_unique_actor_scope()
    {
        // ⭐ D33(a), asserted. ssot-charms §3.1 reverses from option C to option B.
        Assert.True(CharmResonance.RefuseUnsupportedScope(new OwnerScope(OwnerKind.UniqueActor, "spec-1")).IsOk);
    }

    [Fact]
    public void No_charm_atom_is_ever_written_at_player_scope()
    {
        // ⛔ The live correctness bug stays refused: StatApplyScope returns true unconditionally for
        // player:, and match matches BOTH sides, so a player-scoped +atk charm buffs the zombies.
        var refusal = CharmResonance.RefuseUnsupportedScope(new OwnerScope(OwnerKind.Player, "p1"));
        Assert.Equal(AtomRejectionReason.ScopeUnsupported, refusal.Reason);
        Assert.Contains("buffs the zombies", refusal.Detail);

        Assert.Equal(AtomRejectionReason.ScopeUnsupported,
            CharmResonance.RefuseUnsupportedScope(OwnerScope.Match).Reason);
    }

    [Fact]
    public void A_set_tier_never_binds_at_match_scope()
    {
        Assert.True(SetEvaluator.RefuseUnsupportedScope(new OwnerScope(OwnerKind.UniqueActor, "spec-1")).IsOk);
        var refusal = SetEvaluator.RefuseUnsupportedScope(OwnerScope.Match);
        Assert.Equal(AtomRejectionReason.ScopeUnsupported, refusal.Reason);
        Assert.Contains("team buff", refusal.Detail);
    }

    [Fact]
    public void Set_and_frame_mix_tiers_bind_at_priority_zero_and_charms_at_minus_one_hundred()
    {
        Assert.Equal(0, ThresholdContainerIds.SetPriority);
        Assert.Equal(-100, ThresholdContainerIds.CharmPriority);
        Assert.Equal(0, Tuning().TierPriority);
        Assert.Equal(-100, CharmResonance.Consumer("offense", Array.Empty<CharmResonanceRow>()).Priority);
    }

    [Fact]
    public void Breakpoints_come_from_tuning_not_from_code()
    {
        // No ladder literal survives in C#: the frame-mix breakpoints are DERIVED from the knot list,
        // so moving a knot moves the tier and the two cannot drift.
        var tuning = Tuning();
        var breakpoints = tuning.TierBreakpoints();

        Assert.Equal(tuning.Knots.Count(k => k.MinorityMilli != 0), breakpoints.Count);
        Assert.Equal(new[] { "set.frame-mix-01", "set.frame-mix-02", "set.frame-mix-03", "set.frame-mix-04" },
            breakpoints.Select(b => b.ContainerId));
        Assert.Equal(new long[] { 100, 200, 300, 400 }, breakpoints.Select(b => b.At));

        // Move a knot, and the breakpoint follows it — nothing in code pins 100/200/300/400.
        var moved = tuning with
        {
            Knots = new[]
            {
                new FrameMixKnot(0, 800), new FrameMixKnot(50, 825), new FrameMixKnot(400, 1000),
            },
        };
        FrameMixTuning.Validate(moved);
        Assert.Equal(new long[] { 50, 400 }, moved.TierBreakpoints().Select(b => b.At));
    }
}
