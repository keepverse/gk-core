using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Match;
using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>aura-skill T21a: the real, production `IOwnSideOracle` for the mechanical case (plant vs.
/// zombie, mind-control-adjusted) — `AlwaysRelationOracle` (`DebugScopeRuntime.cs`) is a debug-only
/// stub that answers the same relation for every ptr; this replaces it in production. Deliberately
/// does not attempt creature-specimen ownership (a separate, harder, unbuilt bridge — T21b).</summary>
public class MechanicalOwnSideOracleTests
{
    static BoardEntitySnap Plant(string ptr, bool mindControlled = false) =>
        new() { Ptr = ptr, Side = "plant", TypeId = 0, MindControlled = mindControlled };

    static BoardEntitySnap Zombie(string ptr, bool mindControlled = false) =>
        new() { Ptr = ptr, Side = "zombie", TypeId = 0, MindControlled = mindControlled };

    [Fact]
    public void From_Daves_perspective_a_plant_is_an_ally()
    {
        var oracle = new MechanicalOwnSideOracle("plant", ptr => Plant(ptr));
        Assert.Equal(RelationKind.Ally, oracle.RelationOf("P1"));
    }

    [Fact]
    public void From_Daves_perspective_a_zombie_is_an_enemy()
    {
        var oracle = new MechanicalOwnSideOracle("plant", ptr => Zombie(ptr));
        Assert.Equal(RelationKind.Enemy, oracle.RelationOf("Z1"));
    }

    [Fact]
    public void From_Zombosss_perspective_the_relations_are_reversed()
    {
        var oracle = new MechanicalOwnSideOracle("zombie", ptr => ptr == "P1" ? Plant(ptr) : Zombie(ptr));
        Assert.Equal(RelationKind.Enemy, oracle.RelationOf("P1"));
        Assert.Equal(RelationKind.Ally, oracle.RelationOf("Z1"));
    }

    [Fact]
    public void A_mind_controlled_zombie_fights_for_the_plant_side()
    {
        var oracle = new MechanicalOwnSideOracle("plant", ptr => Zombie(ptr, mindControlled: true));
        Assert.Equal(RelationKind.Ally, oracle.RelationOf("Z1"));
    }

    [Fact]
    public void A_mind_controlled_plant_fights_for_the_zombie_side()
    {
        var oracle = new MechanicalOwnSideOracle("zombie", ptr => Plant(ptr, mindControlled: true));
        Assert.Equal(RelationKind.Ally, oracle.RelationOf("P1"));
    }

    [Fact]
    public void An_untracked_ptr_resolves_to_null_genuinely_unknown()
    {
        var oracle = new MechanicalOwnSideOracle("plant", _ => null);
        Assert.Null(oracle.RelationOf("ghost"));
    }

    [Fact]
    public void Constructing_with_an_empty_side_throws()
    {
        Assert.Throws<ArgumentException>(() => new MechanicalOwnSideOracle("", _ => null));
    }

    // ---- integration: BattlefieldOwnSideReactor runs with the REAL oracle, not a fake ----

    static EffectBag NewBag()
    {
        var harness = new FoundationHarness();
        harness.Bag.Catalog.Upsert(new EffectDef
        {
            EffectId = "fx.t21-probe",
            EffectType = EffectTypes.Passive,
            Name = "T21 probe",
        });
        return harness.Bag;
    }

    static bool HasGrant(EffectBag bag, string ptr) =>
        bag.ForOwner("entity", EffectOwnerKeys.Entity(ptr)).Count > 0;

    [Fact]
    public void Reactor_with_the_real_oracle_grants_a_plant_on_Daves_own_side_reactor()
    {
        var bag = NewBag();
        var board = new Dictionary<string, BoardEntitySnap> { ["P1"] = Plant("P1") };
        var oracle = new MechanicalOwnSideOracle("plant", ptr => board.GetValueOrDefault(ptr));
        var reactor = new BattlefieldOwnSideReactor(
            bag, oracle, RelationKind.Ally, "fx.t21-probe", "test", "aura:t21",
            "resource.delta", ScopeHost.Sim);

        reactor.OnMembershipChanged(new ScopeMembershipEvent("P1", ScopeMembershipTransition.Bound));

        Assert.True(HasGrant(bag, "P1"));
    }

    [Fact]
    public void Reactor_with_the_real_oracle_never_grants_an_enemy_zombie_to_Daves_reactor()
    {
        var bag = NewBag();
        var board = new Dictionary<string, BoardEntitySnap> { ["Z1"] = Zombie("Z1") };
        var oracle = new MechanicalOwnSideOracle("plant", ptr => board.GetValueOrDefault(ptr));
        var reactor = new BattlefieldOwnSideReactor(
            bag, oracle, RelationKind.Ally, "fx.t21-probe", "test", "aura:t21",
            "resource.delta", ScopeHost.Sim);

        reactor.OnMembershipChanged(new ScopeMembershipEvent("Z1", ScopeMembershipTransition.Bound));

        Assert.False(HasGrant(bag, "Z1"));
    }

    [Fact]
    public void Reactor_withdraws_when_a_mind_controlled_ally_flips_back()
    {
        // MindControlToggled: an entity that was granted (fighting for our side) flips away -- the
        // reactor must withdraw, driven by the REAL oracle re-evaluating the CURRENT board state.
        var bag = NewBag();
        var board = new Dictionary<string, BoardEntitySnap> { ["Z1"] = Zombie("Z1", mindControlled: true) };
        var oracle = new MechanicalOwnSideOracle("plant", ptr => board.GetValueOrDefault(ptr));
        var reactor = new BattlefieldOwnSideReactor(
            bag, oracle, RelationKind.Ally, "fx.t21-probe", "test", "aura:t21",
            "resource.delta", ScopeHost.Sim);
        reactor.OnMembershipChanged(new ScopeMembershipEvent("Z1", ScopeMembershipTransition.Bound));
        Assert.True(HasGrant(bag, "Z1")); // mind-controlled zombie fights for the plants right now

        board["Z1"] = Zombie("Z1", mindControlled: false); // control lapses
        reactor.OnMembershipChanged(new ScopeMembershipEvent("Z1", ScopeMembershipTransition.MindControlToggled));

        Assert.False(HasGrant(bag, "Z1"));
    }

    [Fact]
    public void Both_sides_perspective_reactors_resolve_correctly_off_the_SAME_board()
    {
        // "ownership resolves correctly for both sides in battle" -- proven with two reactors reading
        // the identical board data, one per commander's own perspective.
        var board = new Dictionary<string, BoardEntitySnap>
        {
            ["P1"] = Plant("P1"),
            ["Z1"] = Zombie("Z1"),
        };
        var daveBag = NewBag();
        var zombossBag = NewBag();
        var daveOracle = new MechanicalOwnSideOracle("plant", ptr => board.GetValueOrDefault(ptr));
        var zombossOracle = new MechanicalOwnSideOracle("zombie", ptr => board.GetValueOrDefault(ptr));
        var daveReactor = new BattlefieldOwnSideReactor(
            daveBag, daveOracle, RelationKind.Ally, "fx.t21-probe", "test", "aura:dave",
            "resource.delta", ScopeHost.Sim);
        var zombossReactor = new BattlefieldOwnSideReactor(
            zombossBag, zombossOracle, RelationKind.Ally, "fx.t21-probe", "test", "aura:zomboss",
            "resource.delta", ScopeHost.Sim);

        foreach (var ptr in new[] { "P1", "Z1" })
        {
            daveReactor.OnMembershipChanged(new ScopeMembershipEvent(ptr, ScopeMembershipTransition.Bound));
            zombossReactor.OnMembershipChanged(new ScopeMembershipEvent(ptr, ScopeMembershipTransition.Bound));
        }

        Assert.True(HasGrant(daveBag, "P1"));
        Assert.False(HasGrant(daveBag, "Z1"));
        Assert.False(HasGrant(zombossBag, "P1"));
        Assert.True(HasGrant(zombossBag, "Z1"));
    }
}
