using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Match;
using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T7 (buff-debuff-scope-todo.md Phase 3) — the shared front end. Target/type/uniqueCreature must each
/// reach exactly the entities they should on a real multi-entity board, and no others.
/// </summary>
public class BattlefieldScopeTests
{
    static BoardEntitySnap Entity(string ptr, int typeId, string side = "zombie") => new()
    {
        Ptr = ptr, TypeId = typeId, Side = side, Col = 0, Row = 0, Living = true,
    };

    static readonly BoardEntitySnap[] Board =
    {
        Entity("AAA", typeId: 1),
        Entity("BBB", typeId: 2),
        Entity("CCC", typeId: 1),
        Entity("DDD", typeId: 3),
    };

    [Fact]
    public void A_target_selector_resolves_to_exactly_that_one_ptr()
    {
        var who = new WhoSelector { Kind = WhoKind.Target, TargetPtr = "0xAAA" };
        var ptrs = BattlefieldScopeExecutor.ResolvePtrs(who, Board);

        Assert.Equal(new[] { "AAA" }, ptrs);
    }

    [Fact]
    public void A_type_selector_resolves_to_every_matching_entity_and_no_others()
    {
        var who = new WhoSelector { Kind = WhoKind.Type, TypeIds = new[] { 1 } };
        var ptrs = BattlefieldScopeExecutor.ResolvePtrs(who, Board);

        Assert.Equal(new[] { "AAA", "CCC" }, ptrs);
        Assert.DoesNotContain("BBB", ptrs);
        Assert.DoesNotContain("DDD", ptrs);
    }

    [Fact]
    public void A_type_selector_matching_multiple_type_ids_unions_them()
    {
        var who = new WhoSelector { Kind = WhoKind.Type, TypeIds = new[] { 2, 3 } };
        var ptrs = BattlefieldScopeExecutor.ResolvePtrs(who, Board);

        Assert.Equal(new[] { "BBB", "DDD" }, ptrs);
    }

    [Fact]
    public void A_type_selector_with_no_matches_returns_empty_not_throwing()
    {
        var who = new WhoSelector { Kind = WhoKind.Type, TypeIds = new[] { 999 } };
        Assert.Empty(BattlefieldScopeExecutor.ResolvePtrs(who, Board));
    }

    [Fact]
    public void A_unique_creature_selector_resolves_through_the_real_binding_facet()
    {
        var facet = new MatchUniqueBindingsFacet();
        facet.TryBeginPending("inst-creature-1", "corr-1", "zombie", typeId: 5);
        facet.TryBindOnSpawn("corr-1", null, "0xEEE", out _);

        var who = new WhoSelector { Kind = WhoKind.UniqueCreature, InstanceId = "inst-creature-1" };
        var ptrs = BattlefieldScopeExecutor.ResolvePtrs(who, Board, facet);

        Assert.Equal(new[] { "EEE" }, ptrs);
    }

    [Fact]
    public void A_unique_creature_selector_for_a_still_pending_never_bound_specimen_resolves_empty()
    {
        var facet = new MatchUniqueBindingsFacet();
        facet.TryBeginPending("inst-creature-2", "corr-2", "plant", typeId: 5);
        // Never bound to a live ptr.

        var who = new WhoSelector { Kind = WhoKind.UniqueCreature, InstanceId = "inst-creature-2" };
        Assert.Empty(BattlefieldScopeExecutor.ResolvePtrs(who, Board, facet));
    }

    [Fact]
    public void A_relation_selector_refuses_the_one_shot_resolve_rather_than_silently_answering_wrong()
    {
        var who = new WhoSelector { Kind = WhoKind.Relation, Relation = FusionRpg.Contracts.RelationKind.Ally };
        Assert.Throws<InvalidOperationException>(() => BattlefieldScopeExecutor.ResolvePtrs(who, Board));
    }

    [Fact]
    public void BuildGrants_produces_one_entity_owned_grant_per_resolved_ptr()
    {
        var grants = BattlefieldScopeExecutor.BuildGrants(
            new[] { "AAA", "CCC" }, effectId: "fx.test-aura", pluginId: "commander-test", grantIdPrefix: "aura:src1");

        Assert.Equal(2, grants.Count);
        Assert.All(grants, g => Assert.Equal("entity", g.OwnerKind));
        Assert.All(grants, g => Assert.Equal("fx.test-aura", g.EffectId));
        Assert.All(grants, g => Assert.Equal("commander-test", g.PluginId));
        Assert.Contains(grants, g => g.GrantId == "aura:src1:AAA");
        Assert.Contains(grants, g => g.GrantId == "aura:src1:CCC");
    }
}
