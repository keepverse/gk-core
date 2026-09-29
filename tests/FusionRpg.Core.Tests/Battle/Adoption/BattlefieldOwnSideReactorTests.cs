using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Match;
using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T8 (buff-debuff-scope-todo.md Phase 3) — own/enemy side, event-driven. A fake
/// <see cref="IOwnSideOracle"/> stands in for the not-yet-built specimen-ownership bridge; this test
/// proves the grant/withdraw REACTION to a membership event, which is this task's own scope.
/// Constructed against `resource.delta` (T10's own added "normal case" table entry) — the mechanism
/// under test here is grant/withdraw, not the G8 distinction, which
/// <c>BattlefieldScopeLiveHostTests</c> covers directly.
/// </summary>
public class BattlefieldOwnSideReactorTests
{
    sealed class FakeOracle : IOwnSideOracle
    {
        readonly Dictionary<string, RelationKind?> _map = new(StringComparer.OrdinalIgnoreCase);
        public void Set(string ptr, RelationKind? relation) => _map[ptr] = relation;
        public RelationKind? RelationOf(string ptr) => _map.TryGetValue(ptr, out var r) ? r : null;
    }

    static EffectBag NewBag()
    {
        var harness = new FoundationHarness();
        harness.Bag.Catalog.Upsert(new EffectDef
        {
            EffectId = "fx.t8-probe",
            EffectType = EffectTypes.Passive,
            Name = "T8 probe",
        });
        return harness.Bag;
    }

    static bool HasGrant(EffectBag bag, string ptr) =>
        bag.ForOwner("entity", EffectOwnerKeys.Entity(ptr)).Count > 0;

    static BattlefieldOwnSideReactor NewReactor(EffectBag bag, IOwnSideOracle oracle, RelationKind wants, string grantIdPrefix) =>
        new(bag, oracle, wants, "fx.t8-probe", "test", grantIdPrefix, "resource.delta", ScopeHost.Sim);

    [Fact]
    public void Bound_grants_when_the_oracle_says_own_side()
    {
        var bag = NewBag();
        var oracle = new FakeOracle();
        oracle.Set("AAA", RelationKind.Ally);
        var reactor = NewReactor(bag, oracle, RelationKind.Ally, "aura:t8");

        reactor.OnMembershipChanged(new ScopeMembershipEvent("AAA", ScopeMembershipTransition.Bound));

        Assert.True(HasGrant(bag, "AAA"));
    }

    [Fact]
    public void Bound_does_NOT_grant_when_the_oracle_says_the_other_side()
    {
        var bag = NewBag();
        var oracle = new FakeOracle();
        oracle.Set("BBB", RelationKind.Enemy);
        var reactor = NewReactor(bag, oracle, RelationKind.Ally, "aura:t8");

        reactor.OnMembershipChanged(new ScopeMembershipEvent("BBB", ScopeMembershipTransition.Bound));

        Assert.False(HasGrant(bag, "BBB"));
    }

    [Fact]
    public void Cleared_withdraws_a_granted_entity()
    {
        var bag = NewBag();
        var oracle = new FakeOracle();
        oracle.Set("CCC", RelationKind.Ally);
        var reactor = NewReactor(bag, oracle, RelationKind.Ally, "aura:t8");
        reactor.OnMembershipChanged(new ScopeMembershipEvent("CCC", ScopeMembershipTransition.Bound));
        Assert.True(HasGrant(bag, "CCC"));

        reactor.OnMembershipChanged(new ScopeMembershipEvent("CCC", ScopeMembershipTransition.Cleared));

        Assert.False(HasGrant(bag, "CCC"));
    }

    [Fact]
    public void Cleared_on_an_entity_that_was_never_granted_anything_is_a_safe_no_op()
    {
        var bag = NewBag();
        var oracle = new FakeOracle(); // never configured for "DDD" — RelationOf returns null
        var reactor = NewReactor(bag, oracle, RelationKind.Ally, "aura:t8");

        var ex = Record.Exception(() =>
            reactor.OnMembershipChanged(new ScopeMembershipEvent("DDD", ScopeMembershipTransition.Cleared)));

        Assert.Null(ex);
        Assert.False(HasGrant(bag, "DDD"));
    }

    [Fact]
    public void MindControlToggled_into_own_side_grants_and_back_out_withdraws()
    {
        // The exact hypno-zombie-creature case the ideal document's own §4.1 finding is about.
        var bag = NewBag();
        var oracle = new FakeOracle();
        var reactor = NewReactor(bag, oracle, RelationKind.Ally, "aura:t8");

        oracle.Set("EEE", RelationKind.Ally); // now considered ours (a player-owned creature, hypnotized on)
        reactor.OnMembershipChanged(new ScopeMembershipEvent("EEE", ScopeMembershipTransition.MindControlToggled, MindControlledNow: true));
        Assert.True(HasGrant(bag, "EEE"));

        oracle.Set("EEE", RelationKind.Enemy); // hypno wore off / reverted
        reactor.OnMembershipChanged(new ScopeMembershipEvent("EEE", ScopeMembershipTransition.MindControlToggled, MindControlledNow: false));
        Assert.False(HasGrant(bag, "EEE"));
    }

    [Fact]
    public void An_enemy_side_reactor_wants_Enemy_not_Ally()
    {
        var bag = NewBag();
        var oracle = new FakeOracle();
        oracle.Set("FFF", RelationKind.Enemy);
        var debuffReactor = NewReactor(bag, oracle, RelationKind.Enemy, "debuff:t8");

        debuffReactor.OnMembershipChanged(new ScopeMembershipEvent("FFF", ScopeMembershipTransition.Bound));

        Assert.True(HasGrant(bag, "FFF"));
        Assert.Equal(RelationKind.Enemy, debuffReactor.Wants);
    }
}
