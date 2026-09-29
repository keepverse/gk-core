using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Scope;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// The lawn executor for `stat.derived` (decisions.md "Derived-write lawn executor", 2026-08-30).
/// Covers spec-derived-write-lawn.md acceptance A1 (per-entity scoping), A2 (match scope), A3
/// (withdraw), A4 (the G8 unlock — per-actor `combat.defense.*`) and A8 (sim stays refused).
/// </summary>
public class AtomDerivedSubsystemTests
{
    static StatContext Ctx(string entityKey) => new()
    {
        Side = StatSide.Plant,
        EntityKey = entityKey,
        Baseline = new EntityBaseline { Hp = 300, MaxHp = 300, Atk = 20 }
    };

    static FusionRpg.Core.Stats.Derived.ActorHub HubWith(
        Func<StatContext, IReadOnlyList<BoundDerivedAtom>> boundFor)
    {
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(boundFor));
        return hub;
    }

    [Fact] // A1 — the aura reaches the bound entity and nobody else
    public void A_bound_atom_moves_only_the_entity_it_is_bound_to()
    {
        var hub = HubWith(ctx => ctx.EntityKey == "abc"
            ? new[] { new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 250, "aura.might") }
            : Array.Empty<BoundDerivedAtom>());

        Assert.Equal(250, hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni));
        Assert.Equal(0, hub.ResolveDerived(Ctx("def")).Get(DerivedStatChannels.CombatPowerOmni));
    }

    [Fact] // A2 — match scope reaches every entity
    public void A_match_scoped_atom_moves_every_entity()
    {
        var hub = HubWith(_ => new[]
        {
            new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 100, "aura.match")
        });

        Assert.Equal(100, hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni));
        Assert.Equal(100, hub.ResolveDerived(Ctx("def")).Get(DerivedStatChannels.CombatPowerOmni));
    }

    [Fact] // A3 — withdraw is just an empty bound set; nothing lingers, and ptr reuse inherits nothing
    public void Withdrawing_the_binding_returns_the_channel_to_its_prior_value()
    {
        var granted = true;
        var hub = HubWith(_ => granted
            ? new[] { new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 400, "aura.x") }
            : Array.Empty<BoundDerivedAtom>());

        Assert.Equal(400, hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni));
        granted = false;
        Assert.Equal(0, hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni));
    }

    [Fact] // A4 — the G8 unlock: per-actor mitigation, which `stat.modify` may not express off `match`
    public void Per_actor_combat_defense_composes_which_is_the_whole_G8_unlock()
    {
        var hub = HubWith(ctx => ctx.EntityKey == "tanky"
            ? new[] { new BoundDerivedAtom(DerivedStatChannels.CombatDefenseOmni, DerivedModifierOp.Flat, 175, "aura.bulwark") }
            : Array.Empty<BoundDerivedAtom>());

        Assert.Equal(175, hub.ResolveDerived(Ctx("tanky")).Get(DerivedStatChannels.CombatDefenseOmni));
        Assert.Equal(0, hub.ResolveDerived(Ctx("squishy")).Get(DerivedStatChannels.CombatDefenseOmni));
    }

    [Fact]
    public void Two_atoms_on_one_channel_sum_like_any_other_derived_contribution()
    {
        var hub = HubWith(_ => new[]
        {
            new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 100, "aura.a"),
            new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 60, "aura.b")
        });

        Assert.Equal(160, hub.ResolveDerived(Ctx("abc")).Get(DerivedStatChannels.CombatPowerOmni));
    }

    [Fact]
    public void No_bindings_contributes_nothing_and_is_not_a_zero_valued_modifier()
    {
        var hub = HubWith(_ => Array.Empty<BoundDerivedAtom>());
        var (_, contributions) = hub.ResolveDerivedWithContributions(Ctx("abc"));
        Assert.Empty(contributions.ContributionsFor(DerivedStatChannels.CombatPowerOmni));
    }

    [Fact]
    public void Contributions_are_attributed_to_the_atom_source_so_why_did_my_stat_change_is_answerable()
    {
        var hub = HubWith(_ => new[]
        {
            new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 90, "aura.vigor")
        });

        var (_, contributions) = hub.ResolveDerivedWithContributions(Ctx("abc"));
        var one = Assert.Single(contributions.ContributionsFor(DerivedStatChannels.CombatPowerOmni));
        Assert.Equal("aura.vigor", one.SourceId);
        Assert.Equal(90, one.Value);
    }

    [Fact]
    public void Empty_SourceId_is_skipped_never_minted_as_blank_contribution()
    {
        var hub = HubWith(_ => new[]
        {
            new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 40, ""),
            new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 60, "   "),
            new BoundDerivedAtom(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, 70, "aura.kept")
        });

        var (snap, contributions) = hub.ResolveDerivedWithContributions(Ctx("abc"));
        Assert.Equal(70, snap.Get(DerivedStatChannels.CombatPowerOmni));
        var one = Assert.Single(contributions.ContributionsFor(DerivedStatChannels.CombatPowerOmni));
        Assert.Equal("aura.kept", one.SourceId);
    }

    [Theory]
    [InlineData("flat", DerivedModifierOp.Flat)]
    [InlineData("increased", DerivedModifierOp.Increased)]
    [InlineData("replace", DerivedModifierOp.Replace)]
    [InlineData("flag", DerivedModifierOp.Flag)]
    public void The_four_legal_derived_ops_parse(string op, DerivedModifierOp expected)
    {
        Assert.True(AtomDerivedSubsystem.TryParseOp(op, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    // `more` is NOT a derived op (definitions.md) — coercing it to Flat would ship a wrong number
    // looking right, so it must be refused like any unknown string.
    [InlineData("more")]
    [InlineData("override")]
    [InlineData("")]
    [InlineData(null)]
    public void An_op_the_derived_side_does_not_have_is_refused_never_coerced(string? op)
    {
        Assert.False(AtomDerivedSubsystem.TryParseOp(op, out _));
    }

    [Fact] // A8 — the lawn opened; E5 (2026-09-06) opened sim too, then T15 finished it
    public void Lawn_battle_and_sim_are_all_fully_supported()
    {
        // Sim's fold (ActorDerivedLookup, reached by SimEffectHost/FoundationHarness) was a plain sum
        // that honoured Flat/Increased and not Replace/Flag -- proven empirically by
        // EffectOfflineKitTests.The_four_derived_ops_decide_Full_versus_Partial -- so the cell read
        // Partial, not Full, from E5 (2026-09-06) until sim-hub-parity (T15, 2026-09-13) routed the
        // fold through DerivedComposer.ComposeChannelWithBaseline. Renamed from
        // "..._and_sim_stays_refused", then from "..._and_sim_opens_partially", neither of which
        // stayed true.
        var kind = AtomKindRegistry.Get("stat.derived");
        Assert.NotNull(kind);
        Assert.Equal(RuntimeState.Full, kind!.Support.Lawn);
        Assert.Equal(RuntimeState.Full, kind.Support.Battle);
        Assert.Equal(RuntimeState.Full, kind.Support.Sim);
    }

    [Fact] // A8 — and the scope table agrees with the kind matrix on both hosts
    public void Scope_table_lists_stat_derived_as_per_entity_on_both_battlefield_hosts()
    {
        foreach (var host in new[] { ScopeHost.Live, ScopeHost.Sim })
        {
            var support = ScopeCompatibility.Resolve(
                "stat.derived", WhereScope.Battlefield, WhoKind.Relation, host);
            Assert.Equal(ScopeSupportLevel.Full, support.Level);
            Assert.Equal(ScopeDeliveryShape.PerEntityGrant, support.Shape);
        }
    }
}
