using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// A17 (spec-action-selection-adoption.md) — T35/T36: the `IBattleView` adapter and loadout
/// compilation, proven from outside `BattleEngine` the only way possible (both are private, nested
/// per B13's own deviation note) — through `BattleEngine.Resolve`'s public surface and its
/// observable effects (the report, including `Warnings` for a loadout that could not resolve —
/// aura-skill T3/audit D3 replaced the old thrown exception with this degrade-and-warn path).
/// </summary>
public class ActionSelectionAdoptionTests
{
    static BattleActorSetup Actor(string key, string side, IReadOnlyList<string>? equipped = null) => new()
    {
        Key = key, Side = side, SpeciesId = "t35-species", TypeId = 10_003, Level = 3,
        MaxHp = BattleRuleset.BaseHp(3), Atk = BattleRuleset.BaseAtk(3), Defense = BattleRuleset.BaseDefense(3),
        EquippedActionIds = equipped,
    };

    static CompiledAction Dummy(string actionId, params ActionTag[] tags) => new(
        // AE1.4: a SKILL must land on a real rung row (a skill whose effective rung has no row throws
        // by design); rung 1 is this fixture's own placeholder.
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: tags,
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always, Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());

    static BattleSetup Setup(BattleActorSetup squad) => new()
    {
        WaveId = "t35-wave",
        Squad = new[] { squad },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    [Fact]
    public void An_actor_with_no_loadout_still_resolves_a_battle_with_no_catalog_supplied()
    {
        // The fallback case (§2): null/empty EquippedActionIds needs no ActionCatalog at all.
        var report = BattleEngine.Resolve(Setup(Actor("squad:0", "squad")), seed: 1);
        Assert.NotNull(report);
    }

    [Fact]
    public void A_nonempty_loadout_with_no_catalog_degrades_with_a_named_warning()
    {
        // aura-skill T3 (audit D3): this used to throw ArgumentException and fail the whole battle —
        // which meant the first authored Skill grant broke every web battle, and re-threw on every
        // replay of an already-stored setup. It now degrades to the basic-attack fallback and names
        // the dropped actor/reason in BattleReport.Warnings instead.
        var report = BattleEngine.Resolve(Setup(Actor("squad:0", "squad", new[] { "skill.x" })), seed: 1);

        Assert.NotNull(report.Warnings);
        Assert.Contains(report.Warnings!, w =>
            w.Contains("squad:0", StringComparison.Ordinal) && w.Contains("ActionCatalog", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_equipped_id_against_a_real_catalog_degrades_with_a_named_warning()
    {
        // Same D3 fix, the second throw site: an id absent from a real (non-null) catalog.
        var catalog = ActionCatalog.Build(new[] { Dummy("skill.known") });
        var report = BattleEngine.Resolve(
            Setup(Actor("squad:0", "squad", new[] { "skill.unknown" })), seed: 1, actionCatalog: catalog);

        Assert.NotNull(report.Warnings);
        Assert.Contains(report.Warnings!, w => w.Contains("skill.unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void A_known_multi_action_loadout_resolves_without_throwing()
    {
        var catalog = ActionCatalog.Build(new[]
        {
            Dummy("skill.utility", ActionTag.Utility),
            Dummy("skill.offense", ActionTag.Offensive),
        });
        var report = BattleEngine.Resolve(
            Setup(Actor("squad:0", "squad", new[] { "skill.utility", "skill.offense" })), seed: 1, actionCatalog: catalog);

        Assert.Equal(new[] { "skill.utility", "skill.offense" },
            report.Actors.Single(a => a.Key == "squad:0").EquippedActionIds);
    }

    [Fact]
    public void Multiple_battles_against_the_same_catalog_are_independent_and_deterministic()
    {
        // Guards against any hidden shared-mutable-state bug in loadout compilation — two
        // back-to-back resolves against the SAME ActionCatalog instance must not interfere.
        var catalog = ActionCatalog.Build(new[] { Dummy("skill.a"), Dummy("skill.b") });
        var setup = Setup(Actor("squad:0", "squad", new[] { "skill.a", "skill.b" }));

        var first = BattleEngine.Resolve(setup, seed: 42, actionCatalog: catalog);
        var second = BattleEngine.Resolve(setup, seed: 42, actionCatalog: catalog);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Rounds, second.Rounds);
    }

    /// <summary>
    /// T37 (spec-action-selection-adoption.md §3/§4, success criterion 2): two different loadouts
    /// on otherwise-identical actors must produce a MEASURABLY different outcome, attributable to a
    /// specific CompiledAction — not to engine code that merely reads EquippedActionIds as a label.
    /// Range/cooldown gates are provably inert today (no board exists, so casterPos/targetPos are
    /// always null and gate 4 always skips; nothing before this test has ever started a cooldown),
    /// and this module's own scope keeps every chosen action's RESOLUTION basic-attack-shaped
    /// regardless of which CompiledAction was picked (A18's job, not this one's) — so a Condition
    /// leaf is the one lever that can observably diverge outcomes today. `HpAboveMilli(Target) &gt;
    /// 1000` per-mille can never hold (HpMilli is clamped to [0,1000] in BattleRunState.FactsOf), so
    /// the gated actor's only held action is never usable: `ActionIntent.None`, which hazard 3 (§4)
    /// maps to Break — the attacker deals no damage all battle, while an ungated actor of otherwise
    /// identical setup attacks normally.
    /// </summary>
    [Fact]
    public void A_condition_gated_loadout_breaks_the_attack_while_an_ungated_one_still_lands()
    {
        var neverUsable = PredicateCompiler.TryCompile(
            new PredicateNode.Leaf(LeafId.HpAboveMilli, Subject.Target, Value: 1000),
            statusBit: null, out var gatedCondition);
        Assert.True(neverUsable.IsOk);

        var catalog = ActionCatalog.Build(new[] { Dummy("skill.never-usable") with { Condition = gatedCondition } });

        var gated = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "t37-gated",
            Squad = new[] { Actor("squad:0", "squad", new[] { "skill.never-usable" }) },
            Wave = new[] { Actor("wave:0", "wave") },
        }, seed: 3, actionCatalog: catalog);

        var ungated = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "t37-ungated",
            Squad = new[] { Actor("squad:0", "squad") }, // empty loadout -> falls back to the basic attack
            Wave = new[] { Actor("wave:0", "wave") },
        }, seed: 3);

        Assert.Equal(0, gated.Actors.Single(a => a.Key == "squad:0").DamageDealt);
        Assert.True(ungated.Actors.Single(a => a.Key == "squad:0").DamageDealt > 0);
    }
}
