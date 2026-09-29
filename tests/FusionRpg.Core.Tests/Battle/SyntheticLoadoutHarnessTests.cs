using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// A20 (spec-synthetic-loadout-harness.md). Built on `BattleGoldenTests.CloseSetup()`'s own real
/// actors rather than a hand-rolled pair, for the same reason every other A18f/A19 test in this
/// program already gives: a fixture proven to neither stalemate nor resolve in one swing.
/// </summary>
public class SyntheticLoadoutHarnessTests
{
    static CompiledAction NoOpSkill(string actionId, int cooldownTicks = 0) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with
        {
            ActionId = actionId,
            // T56.4's own finding: Class must be Specific for CooldownTicks to mean anything at all --
            // CooldownClass.None (ActionEnvelope.NoOp's default) makes CooldownLedger.TrySlot refuse to
            // key any slot, silently no-opping both the check and the arm.
            Class = cooldownTicks > 0 ? CooldownClass.Specific : CooldownClass.None,
            CooldownTicks = cooldownTicks,
        },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());

    static BattleActorSetup Subject() => BattleGoldenTests.CloseSetup().Squad[0];
    static IReadOnlyList<BattleActorSetup> Opponents() => BattleGoldenTests.CloseSetup().Wave;

    [Fact]
    public void T57_1_Vary_changes_only_the_loadout()
    {
        var template = Subject();
        var variant = SyntheticLoadoutBuilder.Vary(template, new[] { "skill.a" });

        Assert.Equal(new[] { "skill.a" }, variant.EquippedActionIds);
        // Normalize the ONE field that's allowed to differ back, then the records must be equal --
        // asserted directly via record equality (every field), not spot-checked one at a time.
        Assert.Equal(template, variant with { EquippedActionIds = template.EquippedActionIds });
    }

    [Fact]
    public void T57_2_the_same_seed_tuple_reproduces_a_byte_identical_battle()
    {
        var catalog = ActionCatalog.Build(new[] { NoOpSkill("skill.a") });
        var subject = SyntheticLoadoutBuilder.Vary(Subject(), new[] { "skill.a" });

        var first = LoadoutComparator.RunVariant(subject, Opponents(), baseSeed: 777, variantIndex: 0, runs: 1, actionCatalog: catalog);
        var second = LoadoutComparator.RunVariant(subject, Opponents(), baseSeed: 777, variantIndex: 0, runs: 1, actionCatalog: catalog);

        Assert.Equal(first, second);
    }

    [Fact]
    public void T57_2_a_different_variantIndex_is_not_required_to_reproduce_the_same_result()
    {
        // The complementary half of #2's own point: (baseSeed, variantIndex, runIndex) is the WHOLE
        // key -- changing variantIndex alone (same baseSeed, same loadout, same runs) must derive a
        // genuinely different seed, or two variants compared under the same baseSeed would secretly
        // replay each other's draws.
        var catalog = ActionCatalog.Build(new[] { NoOpSkill("skill.a") });
        var subject = SyntheticLoadoutBuilder.Vary(Subject(), new[] { "skill.a" });

        var variant0 = LoadoutComparator.RunVariant(subject, Opponents(), baseSeed: 777, variantIndex: 0, runs: 1, actionCatalog: catalog);
        var variant1 = LoadoutComparator.RunVariant(subject, Opponents(), baseSeed: 777, variantIndex: 1, runs: 1, actionCatalog: catalog);

        Assert.NotEqual(variant0, variant1);
    }

    [Fact]
    public void T57_3_mechanically_identical_loadouts_under_different_ids_are_statistically_indistinguishable()
    {
        // Two different action ids, same envelope shape in every other respect -- the harness's own
        // null-hypothesis proof that it measures the LOADOUT'S MECHANICS, not the id string or an
        // unrelated confound.
        var catalog = ActionCatalog.Build(new[] { NoOpSkill("skill.a"), NoOpSkill("skill.b") });
        const int runs = 150;

        var resultA = LoadoutComparator.RunVariant(
            SyntheticLoadoutBuilder.Vary(Subject(), new[] { "skill.a" }), Opponents(), baseSeed: 9001, variantIndex: 0, runs, catalog);
        var resultB = LoadoutComparator.RunVariant(
            SyntheticLoadoutBuilder.Vary(Subject(), new[] { "skill.b" }), Opponents(), baseSeed: 9001, variantIndex: 1, runs, catalog);

        Assert.True(Math.Abs(resultA.WinRate - resultB.WinRate) < 0.15,
            $"win rates too far apart for mechanically identical loadouts: {resultA.WinRate} vs {resultB.WinRate}");
        Assert.True(Math.Abs(resultA.MeanDamagePerRound - resultB.MeanDamagePerRound) < resultA.MeanDamagePerRound * 0.25,
            $"mean damage/round too far apart for mechanically identical loadouts: {resultA.MeanDamagePerRound} vs {resultB.MeanDamagePerRound}");
    }

    [Fact]
    public void T57_4_a_real_cooldown_difference_produces_a_measurably_different_aggregate()
    {
        // The actual acceptance bar this whole reopening exists to reach (spec §criteria #4): a
        // loadout that can fire every round vs. one gated on a real, A19-enforced long cooldown must
        // show up as a real, large gap in mean damage per round -- not noise, not a coin flip.
        var catalog = ActionCatalog.Build(new[]
        {
            NoOpSkill("skill.always"),
            // fires once, then never again. NoOpSkill's envelope leaves `StartsAt` at its declared
            // default (`CooldownStart.Resolve`) -- exercising `ActionRunner.FinishResolution`'s own
            // arming site (`ActionRunner.cs:361`), a THIRD independent one T56.4's own fixture (which
            // set `StartsAt = Commit` explicitly) never reached. Found the hard way: the RED/GREEN cycle
            // below caught this file's own first draft leaving that site un-disabled -- see
            // action-todo.md's T56.4 entry for the full, corrected four-call-site picture.
            NoOpSkill("skill.rare", cooldownTicks: 1_000_000),
        });
        const int runs = 100;

        var always = LoadoutComparator.RunVariant(
            SyntheticLoadoutBuilder.Vary(Subject(), new[] { "skill.always" }), Opponents(), baseSeed: 4242, variantIndex: 0, runs, catalog);
        var rare = LoadoutComparator.RunVariant(
            SyntheticLoadoutBuilder.Vary(Subject(), new[] { "skill.rare" }), Opponents(), baseSeed: 4242, variantIndex: 1, runs, catalog);

        Assert.True(always.MeanDamagePerRound > rare.MeanDamagePerRound * 2,
            $"expected the always-usable loadout to deal at least double the rare loadout's mean damage/round; " +
            $"got always={always.MeanDamagePerRound}, rare={rare.MeanDamagePerRound}");
    }

    [Fact]
    public void T74_the_optional_unlock_pair_reaches_the_engine()
    {
        // T74/A33: `RunVariant` gained the same optional pair `BattleEngine.Resolve` takes, so a balance
        // pass can price a loadout at a real rung. A comparison harness's default is still the authored
        // rung (asserted by every other test above, which pass no pair) — this proves the supplied pair
        // actually threads rather than being accepted and dropped.
        var asked = new List<string>();
        var catalog = ActionCatalog.Build(new[] { NoOpSkill("skill.a") });
        var subject = SyntheticLoadoutBuilder.Vary(Subject(), new[] { "skill.a" });

        LoadoutComparator.RunVariant(subject, Opponents(), baseSeed: 777, variantIndex: 0, runs: 1,
            actionCatalog: catalog,
            unlockStateFor: key => { asked.Add(key); return UnlockState.Empty(); },
            unlockTuning: new UnlockTuning(
                P1Milli: 1000, DeltaMilli: 500, FloorMilli: 1, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));

        Assert.Contains(subject.Key, asked);
    }

    [Fact]
    public void T57_5_the_harness_never_touches_a_shipped_golden_fixture()
    {
        // Golden-safety (spec §"Golden-safety"): the harness builds its own synthetic actors and
        // opponents every call -- run it, then confirm a real, blessed golden fixture still resolves
        // to its own locked shape afterward (`Golden_outcomes_hold_their_shapes`'s own claim: stomp is
        // Victory). `BattleGoldenTests.Hash` itself is private to that file (by design -- only its own
        // tests assert the exact bytes), so this checks the SHAPE the harness could plausibly disturb
        // via shared/static state, not the byte-exact hash `BattleGoldenTests` already owns.
        var catalog = ActionCatalog.Build(new[] { NoOpSkill("skill.a") });
        LoadoutComparator.RunVariant(
            SyntheticLoadoutBuilder.Vary(Subject(), new[] { "skill.a" }), Opponents(), baseSeed: 1, variantIndex: 0, runs: 5, catalog);

        var report = BattleEngine.Resolve(BattleGoldenTests.StompSetup(), 2001);
        Assert.Equal(BattleOutcome.Victory, report.Outcome);
    }
}
