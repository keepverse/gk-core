using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Scope;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T9 (buff-debuff-scope-todo.md Phase 3) — SIM host wiring. `BattleEffectHost.Bag` is already a
/// public property (confirmed reading `BattleEffects.cs:61`) — no new settable property was actually
/// needed for a `BattlefieldOwnSideReactor` to attach to it. What T9 actually proves: the reactor
/// built in T8 works against a real `BattleEffectHost.Bag` from an actual `BattleEngine.Resolve` run,
/// using the same `onEffectHostReady` seam every A18a-e test this session already used.
///
/// **Named gap, found by implementation, not hidden:** `membership-events` (T5/T6) is entirely tied
/// to `MatchRuntime` — the live-PvZ match FSM. SIM/`BattleEngine` battles have no `MatchRuntime`, no
/// `MatchUniqueBindingsFacet`, and today no equivalent "an actor spawned/died" signal source of their
/// own. This test proves the REACTOR works on the SIM host; it does not claim SIM has its own
/// membership-event source yet — that is real, additional, unscoped work, not assumed solved here.
/// </summary>
public class BattlefieldScopeSimHostTests
{
    static BattleActorSetup Actor(string key, string side) => new()
    {
        Key = key, Side = side, SpeciesId = "t9-species", TypeId = 10_009, Level = 6,
        MaxHp = BattleRuleset.BaseHp(6), Atk = BattleRuleset.BaseAtk(6), Defense = BattleRuleset.BaseDefense(6),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "t9-wave",
        Squad = new[] { Actor("squad:0", "squad") },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    [Fact]
    public void A_BattlefieldOwnSideReactor_grants_through_a_real_BattleEffectHost_Bag()
    {
        BattleEffectHost? captured = null;
        var report = BattleEngine.Resolve(Setup(), seed: 3, onEffectHostReady: h =>
        {
            captured = h;
            h.Bag.Catalog.Upsert(new EffectDef
            {
                EffectId = "fx.t9-probe",
                EffectType = EffectTypes.Passive,
                Name = "T9 probe",
            });

            var oracle = new StaticOracle(RelationKind.Ally);
            var reactor = new BattlefieldOwnSideReactor(
                h.Bag, oracle, RelationKind.Ally, "fx.t9-probe", "test", "aura:t9",
                "resource.delta", ScopeHost.Sim);

            // Simulating a spawn signal directly — SIM has no membership-event source of its own yet
            // (the named gap above), so this proves the reactor itself, not an end-to-end SIM pipeline.
            reactor.OnMembershipChanged(new FusionRpg.Core.Match.ScopeMembershipEvent(
                "squad:0", FusionRpg.Core.Match.ScopeMembershipTransition.Bound));
        });

        Assert.NotNull(captured);
        Assert.True(captured!.Bag.ForOwner("entity", EffectOwnerKeys.Entity("squad:0")).Count > 0);
        Assert.True(report.Rounds > 0);
    }

    sealed class StaticOracle : FusionRpg.Core.Battle.IOwnSideOracle
    {
        readonly RelationKind _relation;
        public StaticOracle(RelationKind relation) => _relation = relation;
        public RelationKind? RelationOf(string ptr) => _relation;
    }
}
