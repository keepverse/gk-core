using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Match;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>aura-skill T21b: the real, production `IOwnSideOracle` for the SPECIMEN case — ownership
/// by which player deployed the specimen, not by mechanical plant/zombie side.
///
/// <para><b>save-identity SE4.27 (D4):</b> rewritten off the old "compare to my own player id" shape —
/// after save-identity a specimen's `player_id` names a save every empire of it shares, so id comparison
/// can no longer tell Ally from Enemy. The oracle now answers the absolute question "is the registered
/// owner's controller human?", needs no "my id" parameter, and a third registered empire (any controller
/// that is not Human) resolves as an enemy with zero code change — the D4 fix, executable.</para></summary>
public class SpecimenOwnershipOracleTests
{
    [Fact]
    public void A_specimen_owned_by_a_human_controlled_empire_is_an_ally()
    {
        var oracle = new SpecimenOwnershipOracle(_ => EmpireController.Human);
        Assert.Equal(RelationKind.Ally, oracle.RelationOf("S1"));
    }

    [Fact]
    public void A_specimen_owned_by_an_ai_controlled_empire_is_an_enemy()
    {
        var oracle = new SpecimenOwnershipOracle(_ => EmpireController.Ai);
        Assert.Equal(RelationKind.Enemy, oracle.RelationOf("S1"));
    }

    [Fact]
    public void An_untracked_ptr_resolves_to_null_genuinely_unknown()
    {
        var oracle = new SpecimenOwnershipOracle(_ => null);
        Assert.Null(oracle.RelationOf("ghost"));
    }

    [Fact]
    public void Different_ptrs_can_resolve_to_different_controllers_off_the_same_oracle()
    {
        var controllers = new Dictionary<string, EmpireController> { ["S1"] = EmpireController.Human, ["S2"] = EmpireController.Ai };
        var oracle = new SpecimenOwnershipOracle(ptr => controllers.TryGetValue(ptr, out var v) ? v : null);
        Assert.Equal(RelationKind.Ally, oracle.RelationOf("S1"));
        Assert.Equal(RelationKind.Enemy, oracle.RelationOf("S2"));
        Assert.Null(oracle.RelationOf("S3"));
    }

    /// <summary>The D4 fix made executable: a THIRD registered empire (neither the human's nor the one
    /// AI empire the game ships today) still resolves as an enemy, with no code edit to the oracle —
    /// because it never branches on which empire, only on whether the controller is human.</summary>
    [Fact]
    public void A_third_registered_empire_resolves_as_an_enemy_with_no_code_edit()
    {
        var controllers = new Dictionary<string, EmpireController>
        {
            ["human-specimen"] = EmpireController.Human,
            ["zomboss-specimen"] = EmpireController.Ai,
            ["a-third-empires-specimen"] = EmpireController.Ai, // a hypothetical third empire's own controller
        };
        var oracle = new SpecimenOwnershipOracle(ptr => controllers.TryGetValue(ptr, out var v) ? v : null);

        Assert.Equal(RelationKind.Ally, oracle.RelationOf("human-specimen"));
        Assert.Equal(RelationKind.Enemy, oracle.RelationOf("zomboss-specimen"));
        Assert.Equal(RelationKind.Enemy, oracle.RelationOf("a-third-empires-specimen"));
    }

    [Fact]
    public void Constructing_with_a_null_resolver_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SpecimenOwnershipOracle(null!));
    }

    // ---- integration: BattlefieldOwnSideReactor runs with the REAL oracle, not a fake ----

    static EffectBag NewBag()
    {
        var harness = new FoundationHarness();
        harness.Bag.Catalog.Upsert(new EffectDef
        {
            EffectId = "fx.t21b-probe",
            EffectType = EffectTypes.Passive,
            Name = "T21b probe",
        });
        return harness.Bag;
    }

    static bool HasGrant(EffectBag bag, string ptr) =>
        bag.ForOwner("entity", EffectOwnerKeys.Entity(ptr)).Count > 0;

    [Fact]
    public void Reactor_with_the_real_oracle_grants_a_human_owned_specimen()
    {
        var bag = NewBag();
        var controllers = new Dictionary<string, EmpireController> { ["S1"] = EmpireController.Human };
        var oracle = new SpecimenOwnershipOracle(ptr => controllers.TryGetValue(ptr, out var v) ? v : null);
        var reactor = new BattlefieldOwnSideReactor(
            bag, oracle, RelationKind.Ally, "fx.t21b-probe", "test", "aura:t21b",
            "resource.delta", ScopeHost.Sim);

        reactor.OnMembershipChanged(new ScopeMembershipEvent("S1", ScopeMembershipTransition.Bound));

        Assert.True(HasGrant(bag, "S1"));
    }

    [Fact]
    public void Reactor_with_the_real_oracle_never_grants_an_ai_owned_specimen()
    {
        var bag = NewBag();
        var controllers = new Dictionary<string, EmpireController> { ["S2"] = EmpireController.Ai };
        var oracle = new SpecimenOwnershipOracle(ptr => controllers.TryGetValue(ptr, out var v) ? v : null);
        var reactor = new BattlefieldOwnSideReactor(
            bag, oracle, RelationKind.Ally, "fx.t21b-probe", "test", "aura:t21b",
            "resource.delta", ScopeHost.Sim);

        reactor.OnMembershipChanged(new ScopeMembershipEvent("S2", ScopeMembershipTransition.Bound));

        Assert.False(HasGrant(bag, "S2"));
    }

    [Fact]
    public void An_ally_seeking_reactor_and_an_enemy_seeking_reactor_split_the_same_ownership_map_correctly()
    {
        // The old test proved two PLAYERS' perspectives never crossed. There is exactly one human
        // empire per save, so the real symmetry to prove now is a single map correctly splitting
        // Ally-seeking grants from Enemy-seeking grants, never both firing on the same specimen.
        var controllers = new Dictionary<string, EmpireController> { ["S1"] = EmpireController.Human, ["S2"] = EmpireController.Ai };
        EmpireController? Resolve(string ptr) => controllers.TryGetValue(ptr, out var v) ? v : null;

        var allyBag = NewBag();
        var enemyBag = NewBag();
        var allySeekingReactor = new BattlefieldOwnSideReactor(
            allyBag, new SpecimenOwnershipOracle(Resolve), RelationKind.Ally, "fx.t21b-probe", "test", "aura:ally",
            "resource.delta", ScopeHost.Sim);
        var enemySeekingReactor = new BattlefieldOwnSideReactor(
            enemyBag, new SpecimenOwnershipOracle(Resolve), RelationKind.Enemy, "fx.t21b-probe", "test", "aura:enemy",
            "resource.delta", ScopeHost.Sim);

        allySeekingReactor.OnMembershipChanged(new ScopeMembershipEvent("S1", ScopeMembershipTransition.Bound));
        allySeekingReactor.OnMembershipChanged(new ScopeMembershipEvent("S2", ScopeMembershipTransition.Bound));
        enemySeekingReactor.OnMembershipChanged(new ScopeMembershipEvent("S1", ScopeMembershipTransition.Bound));
        enemySeekingReactor.OnMembershipChanged(new ScopeMembershipEvent("S2", ScopeMembershipTransition.Bound));

        Assert.True(HasGrant(allyBag, "S1"));
        Assert.False(HasGrant(allyBag, "S2"));
        Assert.False(HasGrant(enemyBag, "S1"));
        Assert.True(HasGrant(enemyBag, "S2"));
    }
}
