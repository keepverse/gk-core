using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T10 (buff-debuff-scope-todo.md Phase 3) — live-PvZ host, grant-shape contract only. No new reader
/// is built here (the injector's own overlay/Funnel path already reads these grants, proven by
/// patron.aura) — what this task proves is that a G8-shaped kind is refused at construction time,
/// never silently issued as an inert (or actively wrong) per-entity grant.
/// </summary>
public class BattlefieldScopeLiveHostTests
{
    sealed class AlwaysAlly : IOwnSideOracle
    {
        public FusionRpg.Contracts.RelationKind? RelationOf(string ptr) => FusionRpg.Contracts.RelationKind.Ally;
    }

    static EffectBag NewBag() => new FoundationHarness().Bag;

    [Fact]
    public void A_G8_shaped_kind_on_the_live_host_is_refused_at_construction_not_at_grant_time()
    {
        var bag = NewBag();
        var ex = Assert.Throws<ScopeUnsupportedException>(() =>
            new BattlefieldOwnSideReactor(
                bag, new AlwaysAlly(), RelationKind.Ally,
                effectId: "fx.defense-aura", pluginId: "test", grantIdPrefix: "aura:t10",
                atomKindId: "stat.modify", host: ScopeHost.Live, channel: "defense"));

        Assert.Equal("stat.modify", ex.AtomKindId);
        Assert.Equal(ScopeHost.Live, ex.Host);
    }

    [Fact]
    public void The_identical_G8_kind_on_the_sim_host_constructs_fine()
    {
        // Same kind, same channel — only the host differs, and that alone changes the answer
        // (scope-model's own Assumption 2, proven here from the caller's side).
        var bag = NewBag();
        var reactor = new BattlefieldOwnSideReactor(
            bag, new AlwaysAlly(), RelationKind.Ally,
            effectId: "fx.defense-aura", pluginId: "test", grantIdPrefix: "aura:t10",
            atomKindId: "stat.modify", host: ScopeHost.Sim, channel: "defense");

        Assert.Equal(RelationKind.Ally, reactor.Wants);
    }

    [Fact]
    public void A_normal_kind_constructs_and_grants_on_the_live_host_too()
    {
        var bag = NewBag();
        bag.Catalog.Upsert(new EffectDef { EffectId = "fx.live-probe", EffectType = EffectTypes.Passive, Name = "live probe" });
        var reactor = new BattlefieldOwnSideReactor(
            bag, new AlwaysAlly(), RelationKind.Ally,
            effectId: "fx.live-probe", pluginId: "test", grantIdPrefix: "aura:t10-live",
            atomKindId: "resource.delta", host: ScopeHost.Live);

        reactor.OnMembershipChanged(new FusionRpg.Core.Match.ScopeMembershipEvent(
            "GGG", FusionRpg.Core.Match.ScopeMembershipTransition.Bound));

        Assert.True(bag.ForOwner("entity", EffectOwnerKeys.Entity("GGG")).Count > 0);
    }
}
