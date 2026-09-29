using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// A18f (spec-action-dispatch-generalization.md) — T55.2/T55.3/T55.4's real integration proof. Built
/// 2026-09-06 during the A19 audit, AFTER discovering the todo's own claimed evidence for this file
/// cited tests that were never actually written (a stale/fabricated evidence record from an earlier
/// compacted context — caught via `git log --all` showing zero history for this path, corrected here
/// rather than left standing). The production fix itself
/// (`TimelineDispatch.cs`'s `runner.CurrentEnvelope(ev.OwnerKey) ?? state.BasicAttackEnvelopeCompiled`)
/// was real and already in the tree; only the test proving it was missing.
///
/// <para>Built on `BattleGoldenTests.CloseSetup()`, the same proven-reliable base
/// `ActionCostsCooldownsAdoptionTests.cs` (A19) uses — a hand-rolled actor pair risks stalemating or
/// resolving in one swing before the observable ever gets a chance to matter.</para>
/// </summary>
public class ActionDispatchGeneralizationTests
{
    /// <summary>A basic-attack-shaped Skill whose `EffectivenessChannel` is the SUPPORT category's own
    /// channel (`skill.effectiveness.support`) — deliberately NOT the attack category's channel
    /// (`skill.effectiveness.attack`) the hardcoded `BasicAttackCompiled` envelope reads. If the
    /// resolve branch ever silently fell back to the hardcoded envelope instead of this committed one,
    /// a boost on THIS channel would have no effect and a boost on attack's channel would — the exact
    /// inversion this file's tests are built to catch.</summary>
    static CompiledAction SupportSkill(string actionId = "skill.support") => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with
        {
            ActionId = actionId,
            EffectivenessChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategorySupport),
        },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: System.Array.Empty<CompiledActionCost>(),
        Scopes: System.Array.Empty<ActionScopeRow>());

    static BattleSetup EquipSquadZero(string actionId, params BattleChannelMod[] channelMods) =>
        BattleGoldenTests.CloseSetup() with
        {
            Squad = BattleGoldenTests.CloseSetup().Squad.Select((a, i) => i == 0
                ? a with { EquippedActionIds = new[] { actionId }, ChannelMods = channelMods }
                : a).ToArray(),
        };

    static BattleReport Run(BattleSetup setup, ActionCatalog catalog, BattleTrace? trace = null) =>
        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog);

    /// <summary>Forces the ATOMIC dispatch path (`RunBasicAttackStep`) — every shipped profile has
    /// `UsesTimelineDispatch = true` today (`BattleModeProfile.cs:221,235,251`), so this synthetic
    /// override is the only way to reach the atomic path at all, exactly as `TimelineDispatch.cs`'s
    /// own file header says this shape exists to let a test do.</summary>
    static BattleReport RunAtomic(BattleSetup setup, ActionCatalog catalog, BattleTrace? trace = null) =>
        BattleEngine.Resolve(setup, seed: 5501, trace: trace, actionCatalog: catalog,
            profile: BattleModeProfileCatalog.ClassicRound with { UsesTimelineDispatch = false });

    [Fact]
    public void The_fixture_itself_neither_stalemates_nor_ends_in_one_swing()
    {
        var catalog = ActionCatalog.Build(new[] { SupportSkill() });
        var setup = EquipSquadZero("skill.support");
        var trace = new BattleTrace();

        Run(setup, catalog, trace);

        var squadZeroHits = trace.Targets.Count(t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
        Assert.True(squadZeroHits > 1, $"expected more than one declared attack; got {squadZeroHits}");
    }

    [Fact]
    public void A_boost_on_the_actions_own_support_effectiveness_channel_changes_the_battle()
    {
        var boostedChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategorySupport);
        var catalog = ActionCatalog.Build(new[] { SupportSkill() });

        var baselineTrace = new BattleTrace();
        Run(EquipSquadZero("skill.support"), catalog, baselineTrace);

        var boostedTrace = new BattleTrace();
        Run(EquipSquadZero("skill.support", new BattleChannelMod(boostedChannel, 5000)), catalog, boostedTrace);

        // Round 1's apply against squad:0's own declared target must differ once the committed
        // skill's own channel is boosted -- if the resolve branch silently used the hardcoded
        // basic-attack envelope instead, this channel would never be read at all and the two traces
        // would be byte-identical.
        Assert.NotEqual(string.Join("|", baselineTrace.Applies), string.Join("|", boostedTrace.Applies));
    }

    [Fact]
    public void A_boost_on_the_basic_attacks_own_attack_effectiveness_channel_stays_inert()
    {
        // The channel the HARDCODED BasicAttackCompiled envelope reads -- NOT the equipped skill's
        // own channel. If the resolve branch ever silently fell back to that hardcoded envelope, this
        // boost (not the one in the test above) would be the one that moved the battle.
        var attackChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategoryAttack);
        var catalog = ActionCatalog.Build(new[] { SupportSkill() });

        var baselineTrace = new BattleTrace();
        Run(EquipSquadZero("skill.support"), catalog, baselineTrace);

        var boostedTrace = new BattleTrace();
        Run(EquipSquadZero("skill.support", new BattleChannelMod(attackChannel, 5000)), catalog, boostedTrace);

        Assert.Equal(string.Join("|", baselineTrace.Applies), string.Join("|", boostedTrace.Applies));
    }

    [Fact]
    public void T55_3_RunBasicAttackStep_still_threads_its_own_envelope_never_the_hardcoded_one()
    {
        // Same proof as the timeline-dispatch test above, run through the ATOMIC path instead --
        // `RunBasicAttackStep`/`DeclareBasicAttack` were verified by direct code reading to already
        // thread their own local `envelope` correctly (never had T55.2's bug), and this pins that
        // fact as a real, running regression guard rather than leaving it as an unverified claim.
        var boostedChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategorySupport);
        var catalog = ActionCatalog.Build(new[] { SupportSkill() });

        var baselineTrace = new BattleTrace();
        RunAtomic(EquipSquadZero("skill.support"), catalog, baselineTrace);

        var boostedTrace = new BattleTrace();
        RunAtomic(EquipSquadZero("skill.support", new BattleChannelMod(boostedChannel, 5000)), catalog, boostedTrace);

        Assert.NotEqual(string.Join("|", baselineTrace.Applies), string.Join("|", boostedTrace.Applies));
    }

    /// <summary>
    /// T60.2 (spec-action-resolution-by-category.md criterion 3): RENAMED and REWRITTEN, not deleted,
    /// so the history of what changed and why survives in this file (spec's own acceptance criterion
    /// 3). The ORIGINAL test (`T55_4_a_non_attack_category_action_still_deals_attack_shaped_damage_today`)
    /// asserted TODAY'S wrong behavior on purpose, as a named, deliberate limitation A18f's own spec
    /// planted — this is what makes that framing obsolete: A22 (T60.1) closes the gap, so this test now
    /// asserts the CORRECTED behavior instead.
    /// </summary>
    /// <summary>A magnitude threshold no ordinary `CloseSetup()` attack crosses on its own (the same
    /// empirically-found value `ActionCostsCooldownsAdoptionTests.BoostedHitMagnitudeThreshold` already
    /// uses against this exact fixture), so a boosted hit is identifiable by SIZE alone -- necessary
    /// because `BattleTrace.Apply` records the TARGET's key, not the attacker's, so a raw " squad:0 "
    /// or round-number substring match cannot isolate squad:0's OWN hit from every other actor's.</summary>
    const long BoostedHitMagnitudeThreshold = 100;

    [Fact]
    public void T60_1_a_non_attack_category_action_no_longer_rolls_an_attack_shaped_hit()
    {
        var boostedChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategorySupport);
        var supportAction = SupportSkill() with { Category = ActionCategory.Support };
        var catalog = ActionCatalog.Build(new[] { supportAction });
        var setup = EquipSquadZero("skill.support", new BattleChannelMod(boostedChannel, 20_000));
        var trace = new BattleTrace();

        Run(setup, catalog, trace);

        // Criterion 1: a target is still DECLARED (the action still resolves, commits, and picks a
        // target the same as any other action) but calculator.Compute is never called for it -- proven
        // by the absence of a boosted-magnitude Applies entry that a real hit roll (with this same
        // 20,000-permille boost) would otherwise produce, mirroring T56.3's own "declared but never
        // landed" trace-based technique, not by reading the code and assuming the branch was taken.
        Assert.Contains(trace.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
        Assert.DoesNotContain(trace.Applies, a =>
        {
            var parts = a.Split(' ');
            return long.TryParse(parts[2], out var delta) && System.Math.Abs(delta) >= BoostedHitMagnitudeThreshold;
        });
    }

    /// <summary>T60.1's own verify line: "one case per non-Attack category" — Defense/Movement/Status,
    /// the three the test above (Support) does not already cover, same proof technique.</summary>
    [Theory]
    [InlineData(ActionCategory.Defense)]
    [InlineData(ActionCategory.Movement)]
    [InlineData(ActionCategory.Status)]
    public void T60_1_every_non_attack_category_skips_the_hit_roll(ActionCategory category)
    {
        var boostedChannel = DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategorySupport);
        var action = SupportSkill($"skill.{category}") with { Category = category };
        var catalog = ActionCatalog.Build(new[] { action });
        var setup = EquipSquadZero(action.ActionId, new BattleChannelMod(boostedChannel, 20_000));
        var trace = new BattleTrace();

        Run(setup, catalog, trace);

        Assert.Contains(trace.Targets, t => t.Contains(" squad:0->", System.StringComparison.Ordinal));
        Assert.DoesNotContain(trace.Applies, a =>
        {
            var parts = a.Split(' ');
            return long.TryParse(parts[2], out var delta) && System.Math.Abs(delta) >= BoostedHitMagnitudeThreshold;
        });
    }

    /// <summary>A Support-category action with a real, long cooldown -- same shape as
    /// `ActionCostsCooldownsAdoptionTests.CooldownGatedAttackSkill` (T56.4), Support instead of Attack,
    /// `Class = CooldownClass.Specific` for the same reason T56.4 found: `NoOp`'s default
    /// `CooldownClass.None` silently no-ops both the check and the arm regardless of `CooldownTicks`.</summary>
    static CompiledAction CooldownGatedSupportSkill(string actionId) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: new[] { ActionTag.Offensive },
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Category: ActionCategory.Support,
        Envelope: ActionEnvelope.NoOp with
        {
            ActionId = actionId,
            Class = CooldownClass.Specific,
            CooldownTicks = 1_000_000,
            StartsAt = CooldownStart.Resolve,
        },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always,
        Costs: System.Array.Empty<CompiledActionCost>(),
        Scopes: System.Array.Empty<ActionScopeRow>());

    /// <summary>
    /// T60.1 criterion 2: the cooldown arms the moment a non-Attack action resolves, unconditionally --
    /// never gated on a hit outcome that was never rolled. Same "commit once, then refused every round
    /// after" proof T56.4 already established for an Attack-category skill, now for a Support one whose
    /// own resolution never reaches a hit roll at all.
    /// </summary>
    [Fact]
    public void T60_1_a_non_attack_actions_cooldown_arms_unconditionally_on_resolve()
    {
        var catalog = ActionCatalog.Build(new[] { CooldownGatedSupportSkill("skill.support-cooldown-gated") });
        var setup = EquipSquadZero("skill.support-cooldown-gated");
        var trace = new BattleTrace();

        Run(setup, catalog, trace);

        var squadZeroAttacks = trace.Targets.Where(t => t.Contains(" squad:0->", System.StringComparison.Ordinal)).ToList();
        Assert.Single(squadZeroAttacks); // committed once, then refused every round after by its own cooldown
        Assert.StartsWith("1 ", squadZeroAttacks[0]);
    }
}
