using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E6's bind gate. Load-time validation proves a row is <b>well-formed</b>; this proves it is
/// <b>executable here</b>. The same container may bind on the lawn and be rejected in battle, and
/// that is correct rather than a bug.
/// </summary>
public class BindGateTests
{
    static AtomRow Atom(string kindId, string paramsJson = "{}") => new()
    {
        AtomId = "atom.sample.t1", KindId = kindId, FamilyId = "atom.sample", Variant = "", Tier = 1,
        ParamsJson = paramsJson,
    };

    static AtomRow StatModify(string channel) =>
        Atom("stat.modify", "{\"channel\":\"" + channel + "\",\"op\":\"flat\",\"amount\":10}");

    static AtomRejection Bind(AtomRow atom, OwnerScope owner, BindContext ctx, int? levelReq = null) =>
        BindGate.Check(new[] { atom }, owner, ctx, levelReq);

    static OwnerScope Parse(string text)
    {
        Assert.True(OwnerScope.TryParse(text, out var s).IsOk, text);
        return s;
    }

    // ---- owner-key grammar ------------------------------------------------------------------------

    [Theory]
    [InlineData("match")]
    [InlineData("plant:7")]
    [InlineData("zombie:0")]
    [InlineData("entity:abc")]
    [InlineData("entity:0")]
    [InlineData("player:1")]
    [InlineData("sector:north-ridge")]
    [InlineData("slot:forge-1")]
    [InlineData("unique-actor:abc123")]
    public void Every_legal_owner_key_parses(string text)
    {
        Assert.True(OwnerScope.TryParse(text, out _).IsOk, text);
    }

    [Fact]
    public void UniqueActor_round_trips_through_ToString_and_TryParse()
    {
        // decision 1 (tasks/seed-to-concrete-open-decisions.md): a persistent rpg_unique_actor's own
        // instance_id, durable like Player — never session-scoped like Entity.
        var scope = Parse("unique-actor:c0ffee00");
        Assert.Equal(OwnerKind.UniqueActor, scope.Kind);
        Assert.False(scope.IsSessionScoped);
        Assert.Equal("unique-actor:c0ffee00", scope.ToString());
    }

    [Theory]
    [InlineData("entity:0xABC")]   // both spellings were in circulation; only lowercase-no-0x parses
    [InlineData("entity:ABC")]
    [InlineData("player:0")]       // ids are > 0
    [InlineData("plant:-1")]
    [InlineData("match:1")]        // match takes no key
    [InlineData("wizard:3")]
    [InlineData("plant")]          // missing key
    [InlineData("")]
    public void A_malformed_owner_key_is_rejected(string text)
    {
        Assert.Equal(AtomRejectionReason.BadOwnerKey, OwnerScope.TryParse(text, out _).Reason);
    }

    [Fact]
    public void Match_renders_without_a_colon_and_round_trips()
    {
        Assert.Equal("match", OwnerScope.Match.ToString());
        Assert.Equal("entity:abc", Parse("entity:abc").ToString());
    }

    [Fact]
    public void Entity_bindings_are_session_scoped()
    {
        // The pointer is reused by IL2CPP, so an entity binding cannot outlive the match.
        Assert.True(Parse("entity:abc").IsSessionScoped);
        Assert.False(Parse("player:1").IsSessionScoped);
    }

    [Fact]
    public void A_typeId_for_a_type_that_does_not_exist_is_still_accepted()
    {
        // Type catalogs are game data we do not own; refusing them would make us the authority on
        // someone else's list.
        Assert.True(OwnerScope.TryParse("plant:999999", out _).IsOk);
    }

    // ---- G8: primary defense is side-wide ----------------------------------------------------------

    [Fact]
    public void Stat_modify_on_defense_binds_at_match_scope()
    {
        var r = Bind(StatModify("defense"), OwnerScope.Match, new BindContext(RuntimeId.Lawn));
        Assert.True(r.IsOk, r.ToString());
    }

    [Theory]
    [InlineData("entity:abc")]
    [InlineData("plant:7")]
    [InlineData("zombie:3")]
    [InlineData("player:1")]
    public void Stat_modify_on_defense_is_rejected_at_every_narrower_scope(string owner)
    {
        // G8 corrected: the TakeDamage prefix reads ONE side-wide cached value, so per-type bindings
        // are exactly as dead as per-entity ones. The earlier rule rejected only `entity:` and left
        // plant:N and zombie:N silently doing nothing, which is worse than rejecting all of them.
        var r = Bind(StatModify("defense"), Parse(owner), new BindContext(RuntimeId.Lawn));

        Assert.Equal(AtomRejectionReason.ScopeUnsupported, r.Reason);
    }

    [Fact]
    public void Other_primary_channels_are_unaffected_by_G8()
    {
        var r = Bind(StatModify("maxHp"), Parse("plant:7"), new BindContext(RuntimeId.Lawn));
        Assert.True(r.IsOk, r.ToString());
    }

    // ---- the four-state runtime matrix ---------------------------------------------------------------

    [Fact]
    public void A_kind_with_no_consumer_in_this_runtime_is_rejected()
    {
        // board.action is lawn-only; battle has no consumer at all.
        var r = Bind(Atom("board.action", "{\"action\":\"mow\"}"), OwnerScope.Match,
            new BindContext(RuntimeId.Battle));

        Assert.Equal(AtomRejectionReason.RuntimeUnsupported, r.Reason);
    }

    [Fact]
    public void A_plan_only_kind_binds_only_where_the_host_is_a_planner()
    {
        var atom = StatModify("maxHp");

        // stat.modify is PlanOnly in sim. A non-planner host would accept it and apply nothing.
        Assert.Equal(AtomRejectionReason.RuntimeUnsupported,
            Bind(atom, OwnerScope.Match, new BindContext(RuntimeId.Sim)).Reason);

        Assert.True(Bind(atom, OwnerScope.Match, new BindContext(RuntimeId.Sim, IsPlanner: true)).IsOk);
    }

    [Fact]
    public void A_kind_binds_only_where_a_consumer_exists()
    {
        // stat.derived was None/None/None under D6's quarantine. Each runtime has opened only on the
        // strength of its OWN consumer, never a sibling's — which is the rule this test exists to pin:
        //   battle  — E12 (2026-08-23), `BattleStatComposer` reads bound atoms at squad build
        //   lawn    — decisions.md "Derived-write lawn executor" (2026-08-30), `AtomDerivedSubsystem`
        //   sim     — mechanism-wiring E5 (2026-09-06), `ActorDerivedLookup`'s contribution fold —
        //             opened Partial (plain-sum fold, Replace/Flag silently wrong), sim-hub-parity
        //             (T15, 2026-09-13) closed the gap to Full (op-aware fold via DerivedComposer.
        //             ComposeChannelWithBaseline — EffectOfflineKitTests.
        //             The_four_derived_ops_decide_Full_versus_Partial). BindGate accepts Partial like
        //             Full either way — it only rejects None outright and PlanOnly on a non-planner host.
        var atom = Atom("stat.derived", "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":5}");

        foreach (var runtime in new[] { RuntimeId.Battle, RuntimeId.Lawn, RuntimeId.Sim })
            Assert.True(Bind(atom, OwnerScope.Match, new BindContext(runtime, IsPlanner: true)).IsOk);
    }

    // ---- world scopes, level, staleness ---------------------------------------------------------------

    [Fact]
    public void A_world_scope_without_a_world_host_is_rejected()
    {
        var r = Bind(StatModify("maxHp"), Parse("sector:north-ridge"), new BindContext(RuntimeId.Lawn));

        Assert.Equal(AtomRejectionReason.ScopeUnsupported, r.Reason);
    }

    [Fact]
    public void A_world_scope_binds_where_a_world_host_exists()
    {
        var r = Bind(StatModify("maxHp"), Parse("slot:forge-1"),
            new BindContext(RuntimeId.Lawn, HasWorldHost: true));

        Assert.True(r.IsOk, r.ToString());
    }

    [Fact]
    public void Level_req_above_the_owners_level_is_rejected()
    {
        var r = Bind(StatModify("maxHp"), OwnerScope.Match,
            new BindContext(RuntimeId.Lawn, OwnerLevel: 3), levelReq: 10);

        Assert.Equal(AtomRejectionReason.LevelTooLow, r.Reason);
    }

    [Fact]
    public void Level_req_is_met_or_absent()
    {
        var ctx = new BindContext(RuntimeId.Lawn, OwnerLevel: 10);

        Assert.True(Bind(StatModify("maxHp"), OwnerScope.Match, ctx, levelReq: 10).IsOk);
        Assert.True(Bind(StatModify("maxHp"), OwnerScope.Match, ctx).IsOk);
    }

    [Fact]
    public void A_withdrawn_or_disabled_atom_is_a_stale_instance()
    {
        Assert.Equal(AtomRejectionReason.StaleInstance,
            BindGate.Check(new[] { StatModify("maxHp") }, OwnerScope.Match,
                new BindContext(RuntimeId.Lawn), null, atomIsLive: _ => false).Reason);

        Assert.Equal(AtomRejectionReason.StaleInstance,
            Bind(StatModify("maxHp") with { Enabled = false }, OwnerScope.Match,
                new BindContext(RuntimeId.Lawn)).Reason);
    }

    [Fact]
    public void One_unbindable_atom_rejects_the_whole_instance()
    {
        // A partially-bound instance is the silent no-op this layer exists to remove.
        var r = BindGate.Check(
            new[] { StatModify("maxHp"), Atom("board.action", "{\"action\":\"mow\"}") },
            OwnerScope.Match, new BindContext(RuntimeId.Battle));

        Assert.Equal(AtomRejectionReason.RuntimeUnsupported, r.Reason);
    }
}
