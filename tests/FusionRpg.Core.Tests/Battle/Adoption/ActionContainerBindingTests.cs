using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T41 (action-todo.md Phase 12, spec-action-container-binding.md) — proven from outside
/// `BattleEngine` the only way possible (`BattleRunState`/`Host`/`Bag` are all private): through
/// `BattleEngine.Resolve`'s public surface, its thrown exceptions, and the pre-existing
/// `onEffectHostReady` test seam (T14) that hands back a reference to the SAME `BattleEffectHost`
/// instance the constructor keeps mutating for the rest of the call — so inspecting the captured
/// reference after `Resolve` returns reflects every grant `BindContainers` added.
/// </summary>
public class ActionContainerBindingTests
{
    static BattleActorSetup Actor(string key, string side, IReadOnlyList<string>? equipped = null) => new()
    {
        Key = key, Side = side, SpeciesId = "t41-species", TypeId = 10_004, Level = 3,
        MaxHp = BattleRuleset.BaseHp(3), Atk = BattleRuleset.BaseAtk(3), Defense = BattleRuleset.BaseDefense(3),
        EquippedActionIds = equipped,
    };

    static CompiledAction DummyWithContainer(string actionId, string containerId) => new(
        // AE1.4: a SKILL must land on a real rung row (a skill whose effective rung has no row throws
        // by design); rung 1 is this fixture's own placeholder.
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: Array.Empty<ActionTag>(),
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: containerId,
        Envelope: FusionRpg.Core.Battle.Timeline.ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always, Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());

    static BattleSetup Setup(BattleActorSetup squad) => new()
    {
        WaveId = "t41-wave",
        Squad = new[] { squad },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    [Fact]
    public void No_container_needs_no_resolver_and_grants_nothing()
    {
        BattleEffectHost? captured = null;
        var report = BattleEngine.Resolve(Setup(Actor("squad:0", "squad")), seed: 1,
            onEffectHostReady: h => captured = h);

        Assert.NotNull(report);
        Assert.False(captured!.Bag.HasAnyGrant());
    }

    [Fact]
    public void A_real_container_binds_under_the_actors_own_owner_key()
    {
        var catalog = ActionCatalog.Build(new[] { DummyWithContainer("skill.cherry-bomb", "item.cherry") });
        var resolver = new DictionaryContainerEffectResolver(
            new Dictionary<string, IReadOnlyList<string>> { ["item.cherry"] = new[] { "fx.board_cherry" } });

        BattleEffectHost? captured = null;
        var report = BattleEngine.Resolve(
            Setup(Actor("squad:0", "squad", new[] { "skill.cherry-bomb" })), seed: 1,
            onEffectHostReady: h => captured = h, actionCatalog: catalog, containerResolver: resolver);

        Assert.NotNull(report);
        var owned = captured!.Bag.ForOwner("entity", EffectOwnerKeys.Entity("squad:0"));
        Assert.Contains(owned, g => g.EffectId == "fx.board_cherry");
    }

    [Fact]
    public void A_container_with_no_resolver_supplied_throws_loudly()
    {
        var catalog = ActionCatalog.Build(new[] { DummyWithContainer("skill.cherry-bomb", "item.cherry") });

        var ex = Assert.Throws<ArgumentException>(() => BattleEngine.Resolve(
            Setup(Actor("squad:0", "squad", new[] { "skill.cherry-bomb" })), seed: 1, actionCatalog: catalog));

        Assert.Contains("squad:0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IContainerEffectResolver", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolvable_container_throws_loudly()
    {
        var catalog = ActionCatalog.Build(new[] { DummyWithContainer("skill.cherry-bomb", "item.missing") });
        var resolver = new DictionaryContainerEffectResolver(
            new Dictionary<string, IReadOnlyList<string>> { ["item.cherry"] = new[] { "fx.board_cherry" } });

        var ex = Assert.Throws<ArgumentException>(() => BattleEngine.Resolve(
            Setup(Actor("squad:0", "squad", new[] { "skill.cherry-bomb" })), seed: 1,
            actionCatalog: catalog, containerResolver: resolver));

        Assert.Contains("squad:0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("item.missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pooled_container_is_rejected_the_same_way_an_unresolvable_one_is()
    {
        // Spec's own unified-rejection design (§ Testing strategy): IContainerEffectResolver has no
        // way to distinguish "pooled" from "does not exist" -- both are simply an empty result, and
        // both hit the identical loud rejection this test and the one above both exercise.
        var catalog = ActionCatalog.Build(new[] { DummyWithContainer("skill.roulette", "item.pooled") });
        var resolver = new DictionaryContainerEffectResolver(
            new Dictionary<string, IReadOnlyList<string>> { ["item.pooled"] = Array.Empty<string>() });

        var ex = Assert.Throws<ArgumentException>(() => BattleEngine.Resolve(
            Setup(Actor("squad:0", "squad", new[] { "skill.roulette" })), seed: 1,
            actionCatalog: catalog, containerResolver: resolver));

        Assert.Contains("item.pooled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_resolves_against_the_same_resolver_bind_deterministically()
    {
        var catalog = ActionCatalog.Build(new[] { DummyWithContainer("skill.cherry-bomb", "item.cherry") });
        var resolver = new DictionaryContainerEffectResolver(
            new Dictionary<string, IReadOnlyList<string>> { ["item.cherry"] = new[] { "fx.board_cherry" } });
        var setup = Setup(Actor("squad:0", "squad", new[] { "skill.cherry-bomb" }));

        BattleEffectHost? first = null, second = null;
        BattleEngine.Resolve(setup, seed: 7, onEffectHostReady: h => first = h, actionCatalog: catalog, containerResolver: resolver);
        BattleEngine.Resolve(setup, seed: 7, onEffectHostReady: h => second = h, actionCatalog: catalog, containerResolver: resolver);

        var firstGrant = Assert.Single(first!.Bag.ForOwner("entity", EffectOwnerKeys.Entity("squad:0")));
        var secondGrant = Assert.Single(second!.Bag.ForOwner("entity", EffectOwnerKeys.Entity("squad:0")));
        Assert.Equal(firstGrant.GrantId, secondGrant.GrantId);
    }
}
