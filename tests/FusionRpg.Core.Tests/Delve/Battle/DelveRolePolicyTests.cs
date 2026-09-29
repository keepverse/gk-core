using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Delve.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Battle;

/// <summary>
/// combat-ai `delve-automated-wiring` A (CAI3.4, spec-delve-automated-wiring.md §5): the
/// `IBattleView.DownedAllyKeysOf` read, without which the `ally-downed` selector is inert.
///
/// <para><b>What is proven here and what is not, stated up front.</b> These tests cover the READ's
/// contract at the interface: the default answer (empty, which is the byte-identity claim for every
/// profile without `DownedOnDeplete`) and the two wrappers that must FORWARD it rather than hide it — the
/// forwarding is where a real defect would live, because `BattleRunState.TraitView` wraps the run state
/// and a non-forwarding wrapper would answer "nobody is down" for every delve. The row's
/// battle-integration assertion (`Downed_party_members_are_absent_from_LiveActorKeys_and_present_in_DownedAllyKeysOf`)
/// needs a live `BattleRunState`, which is a private nested class inside `BattleEngine` that no test
/// constructs — the same accessibility decision CAI3.1 is waiting on.</para>
/// </summary>
public class DelveRolePolicyTests
{
    /// <summary>A view that answers the downed read for one actor, so the WRAPPERS can be observed
    /// carrying it rather than swallowing it.</summary>
    sealed class DownedView : IBattleView
    {
        readonly string[] _downed;
        public DownedView(params string[] downed) => _downed = downed;

        public IReadOnlyList<string> LiveActorKeys => Array.Empty<string>();
        public int SideOf(string actorKey) => 0;
        public GridPos? PositionOf(string actorKey) => null;
        public EntityFacts FactsOf(string actorKey) => default;
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) => Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey) => null;
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => null;
        public int AggressionOf(string actorKey) => 0;
        public IReadOnlyList<string> DownedAllyKeysOf(string actorKey) => _downed;
    }

    /// <summary>An implementor that does NOT override the read — every view that predates CAI3.4, and
    /// every profile that does not set `DownedOnDeplete`.</summary>
    sealed class UnwiredView : IBattleView
    {
        public IReadOnlyList<string> LiveActorKeys => Array.Empty<string>();
        public int SideOf(string actorKey) => 0;
        public GridPos? PositionOf(string actorKey) => null;
        public EntityFacts FactsOf(string actorKey) => default;
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) => Array.Empty<CompiledAction>();
        public ActorDerivedSnapshot? DerivedOf(string actorKey) => null;
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => null;
        public int AggressionOf(string actorKey) => 0;
    }

    /// <summary>The byte-identity half of the row: a view that does not answer the downed read reports
    /// NOBODY down, which is what every non-`delve` profile means.</summary>
    [Fact]
    public void DownedAllyKeysOf_is_empty_for_a_view_that_does_not_override_it()
    {
        // Through the INTERFACE deliberately: a default implementation is not reachable from a concrete
        // reference, which is itself part of the contract being asserted.
        IBattleView unwired = new UnwiredView();
        Assert.Empty(unwired.DownedAllyKeysOf("any:actor"));
    }

    /// <summary>The correctness point that matters: `BattleRunState.TraitView` WRAPS the run state, so a
    /// wrapper that did not forward would report "nobody is down" for every delve even when the real view
    /// says otherwise.</summary>
    [Fact]
    public void A_trait_aware_view_forwards_the_downed_roster()
    {
        var wrapped = new TraitAwareBattleView(new DownedView("ally:downed"), Array.Empty<ITraitDecorator>());
        Assert.Equal(new[] { "ally:downed" }, wrapped.DownedAllyKeysOf("self"));
    }

    /// <summary>Fog hides ENEMIES; who on your own side is down is not fog-gated, the same reasoning
    /// `GarrisonedStructureKeyOf`/`ObjectivePositionOf` already carry on this interface.</summary>
    [Fact]
    public void A_fogged_view_forwards_the_downed_roster_ungated()
    {
        var fogged = new FoggedBattleView(
            new DownedView("ally:downed"), viewerSide: 0,
            visionRangeOf: _ => 0, blocksVision: _ => false);

        Assert.Equal(new[] { "ally:downed" }, fogged.DownedAllyKeysOf("self"));
    }

    static CompiledAction Held(string id, params ActionTag[] tags) => new(
        id, ActionKind.Skill, 1, tags, true, 1, false, false, "container." + id,
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    /// <summary>The hand-built basic attack, excluded from every tally by its empty `ContainerId`.</summary>
    static CompiledAction BasicAttack() => new(
        "basic.attack", ActionKind.Basic, 0, new[] { ActionTag.Offensive }, true, 0, false, true, "",
        ActionEnvelope.NoOp with { ActionId = "basic.attack" },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    /// <summary>One case per branch of §4's derivation, against the closed `ActionTag` enum — the action
    /// ids here are synthetic on purpose, because the rule must never read a corpus action id.</summary>
    [Fact]
    public void Role_is_derived_from_held_action_tags_and_ties_break_in_the_stated_order()
    {
        Assert.Equal(AiRole.Support,
            DelveBattle.RoleOf(isWaveActor: false, new[] { Held("a.support", ActionTag.Heal) }));
        Assert.Equal(AiRole.Frontliner,
            DelveBattle.RoleOf(isWaveActor: false, new[] { Held("a.guard", ActionTag.Defensive) }));
        Assert.Equal(AiRole.Frontliner,
            DelveBattle.RoleOf(isWaveActor: false, new[] { Held("a.rampart", ActionTag.Construct) }));
        Assert.Equal(AiRole.Striker,
            DelveBattle.RoleOf(isWaveActor: false, new[] { Held("a.hit", ActionTag.Offensive) }));
        Assert.Equal(AiRole.Striker,
            DelveBattle.RoleOf(isWaveActor: false, new[] { Held("a.move", ActionTag.Movement) }));

        // The STATED tie order: support beats an equal frontliner bucket, frontliner beats striker.
        Assert.Equal(AiRole.Support, DelveBattle.RoleOf(isWaveActor: false,
            new[] { Held("a.support", ActionTag.Buff), Held("a.guard", ActionTag.Defensive) }));
        Assert.Equal(AiRole.Frontliner, DelveBattle.RoleOf(isWaveActor: false,
            new[] { Held("a.guard", ActionTag.Defensive), Held("a.hit", ActionTag.Offensive) }));

        // A larger bucket still wins outright, so the tie order is not a blanket precedence.
        Assert.Equal(AiRole.Frontliner, DelveBattle.RoleOf(isWaveActor: false,
            new[] { Held("a.support", ActionTag.Heal), Held("g1", ActionTag.Defensive),
                    Held("g2", ActionTag.Defensive), Held("g3", ActionTag.Construct) }));
    }

    /// <summary>No loadout — only the hand-built basic attack — is a striker, which is today's behaviour
    /// expressed as a role. The basic attack is excluded by its empty `ContainerId`, so its own
    /// `Offensive` tag never reaches a bucket.</summary>
    [Fact]
    public void An_actor_with_no_loadout_is_a_striker()
    {
        Assert.Equal(AiRole.Striker, DelveBattle.RoleOf(isWaveActor: false, new[] { BasicAttack() }));

        // Two basic attacks behave the same: the exclusion is by container, not by count.
        Assert.Equal(AiRole.Striker, DelveBattle.RoleOf(isWaveActor: false,
            new[] { BasicAttack(), BasicAttack() }));
    }

    /// <summary>`PartyIndex is null` is the enemy row BEFORE any tally — asserted with a loadout that
    /// would otherwise derive `frontliner`, so a tally running first would be visible.</summary>
    [Fact]
    public void A_wave_actor_is_the_enemy_row_before_any_tally()
    {
        Assert.Equal(AiRole.Striker, DelveBattle.RoleOf(isWaveActor: true,
            new[] { Held("a.guard", ActionTag.Defensive), Held("a.support", ActionTag.Heal) }));
    }
}
