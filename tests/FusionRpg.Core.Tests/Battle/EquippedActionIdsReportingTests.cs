using System.Text.Json;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// T22 (action-todo.md, Checkpoint 5): "the auto-equipped set appears in the battle report."
/// **Reopened 2026-08-28 (A17, action-map.md §12):** the doc comment this file originally shipped
/// with claimed "`BattleEngine` itself has no notion of actions/skills at all" as a settled fact —
/// which a completeness audit found was true only because nothing had ever wired it, not because it
/// was structurally inert. A17's own module spec makes equipped loadouts meaningful; this file's
/// tests now supply a real (if synthetic) <see cref="ActionCatalog"/> so the loud "an equipped id
/// must resolve against a real catalog" validation A17 added doesn't refuse them. The
/// "fight identically" test below is still TRUE today — A17's own build order (T35/T36 land the
/// plumbing; T37 wires actual consumption) means nothing reads `HeldActionsOf` for real behavior
/// yet — but its premise is explicitly scheduled to flip once T37 lands, and its own evidence there
/// must update this file rather than leave it stale.
/// </summary>
public class EquippedActionIdsReportingTests
{
    static BattleActorSetup Actor(string key, string side, IReadOnlyList<string>? equipped = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = 3,
        MaxHp = BattleRuleset.BaseHp(3),
        Atk = BattleRuleset.BaseAtk(3),
        Defense = BattleRuleset.BaseDefense(3),
        EquippedActionIds = equipped,
    };

    /// <summary>A minimal, degenerate compiled action for an arbitrary id — enough to satisfy A17's
    /// loud loadout validation, carrying no real atoms/costs (this file tests reporting, not
    /// resolution).</summary>
    static CompiledAction Dummy(string actionId) => new(
        // AE1.4: a SKILL must land on a real rung row (rung 0 is the basic attack's own, and a skill
        // whose effective rung has no row throws by design). Rung 1 is this fixture's own placeholder.
        ActionId: actionId, Kind: ActionKind.Skill, Rung: 1, Tags: Array.Empty<ActionTag>(),
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: FusionRpg.Core.Battle.Timeline.ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always, Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());

    static ActionCatalog CatalogFor(params string[] actionIds) =>
        ActionCatalog.Build(actionIds.Select(Dummy).ToList());

    [Fact]
    public void A_populated_EquippedActionIds_rides_from_setup_to_the_final_result()
    {
        var setup = new BattleSetup
        {
            WaveId = "test-wave",
            Squad = new[] { Actor("squad:0", "squad", new[] { "skill.fireball", "skill.heal" }) },
            Wave = new[] { Actor("wave:0", "wave") },
        };

        var report = BattleEngine.Resolve(setup, seed: 1, actionCatalog: CatalogFor("skill.fireball", "skill.heal"));

        var squadActor = report.Actors.Single(a => a.Key == "squad:0");
        Assert.Equal(new[] { "skill.fireball", "skill.heal" }, squadActor.EquippedActionIds);
    }

    [Fact]
    public void An_unset_EquippedActionIds_reaches_the_result_as_null_not_an_empty_list()
    {
        var setup = new BattleSetup
        {
            WaveId = "test-wave",
            Squad = new[] { Actor("squad:0", "squad") },
            Wave = new[] { Actor("wave:0", "wave") },
        };

        var report = BattleEngine.Resolve(setup, seed: 1);

        Assert.Null(report.Actors.Single(a => a.Key == "squad:0").EquippedActionIds);
    }

    [Fact]
    public void Nothing_in_the_round_loop_reads_it_two_actors_with_different_sets_fight_identically()
    {
        // Pure observability, proven by the strongest test available: swap what each actor carries
        // and the resulting combat outcome (damage, kills, hp) must not change at all -- if anything
        // in BattleEngine ever started reading this field, this is the test that would catch it.
        BattleSetup SetupWith(IReadOnlyList<string>? squadEquip) => new()
        {
            WaveId = "test-wave",
            Squad = new[] { Actor("squad:0", "squad", squadEquip) },
            Wave = new[] { Actor("wave:0", "wave") },
        };

        // action-enrich AE1.4: the hit's base IS the action's, so "no loadout" (the hand-built basic
        // attack, its own tuning base) and "a loadout" now deliberately resolve different damage.
        // The field's own claim — that what it CARRIES is reporting, not behaviour — is therefore
        // proven the way it can still be true: two DIFFERENT sets whose actions are equivalent.
        var withA = BattleEngine.Resolve(SetupWith(new[] { "skill.anything" }), seed: 7,
            actionCatalog: CatalogFor("skill.anything"));
        var withB = BattleEngine.Resolve(SetupWith(new[] { "skill.other" }), seed: 7,
            actionCatalog: CatalogFor("skill.other"));

        // Strip the field itself before comparing everything else byte-for-byte.
        string Canonical(BattleReport r) => JsonSerializer.Serialize(r with
        {
            Actors = r.Actors.Select(a => a with { EquippedActionIds = null }).ToList(),
        });

        Assert.Equal(Canonical(withA), Canonical(withB));
    }

    [Fact]
    public void An_unset_field_serializes_as_absent_not_as_a_null_or_empty_key()
    {
        // The exact hazard this field could reintroduce (spec-value-spec-and-curve.md's own sibling
        // lesson, BattleReport.ContentHash's doc comment): a golden that never populates this field
        // must see the SAME bytes as before it existed. Proven directly against the raw JSON, not just
        // "the object round-trips."
        var setup = new BattleSetup
        {
            WaveId = "test-wave",
            Squad = new[] { Actor("squad:0", "squad") },
            Wave = new[] { Actor("wave:0", "wave") },
        };

        var json = JsonSerializer.Serialize(BattleEngine.Resolve(setup, seed: 1));

        Assert.DoesNotContain("EquippedActionIds", json, StringComparison.Ordinal);
    }
}
