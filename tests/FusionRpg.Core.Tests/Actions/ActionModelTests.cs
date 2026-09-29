using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T1/T2/T3 (action-todo.md, spec-action-model.md §9): the pure validator half, free of I/O. Every
/// rule rejects a planted bad row, naming it — never coerces.
/// </summary>
public class ActionModelTests
{
    static ActionRow Action(string id = "skill.test", ActionKind kind = ActionKind.Skill,
        string containerId = "skill.test", bool grantable = false, bool defaultAttackEligible = false,
        ActionTargetSpec? targeting = null, int minRange = 0, int maxRange = 0) => new()
    {
        ActionId = id,
        Name = "Test",
        Kind = kind,
        ContainerId = containerId,
        Grantable = grantable,
        DefaultAttackEligible = defaultAttackEligible,
        Targeting = targeting ?? new ActionTargetSpec(),
        MinRange = minRange,
        MaxRange = maxRange,
    };

    // ---- rpg_action --------------------------------------------------------------------------------

    [Fact]
    public void An_empty_action_id_is_rejected()
    {
        var result = ActionValidator.ValidateAction(Action(id: ""), new HashSet<string>(), boardAvailable: false);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.BadActionId, result.Reason);
    }

    [Fact]
    public void An_unknown_container_id_is_rejected_naming_the_column()
    {
        var result = ActionValidator.ValidateAction(Action(containerId: "skill.ghost"), null, boardAvailable: false);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.UnknownContainer, result.Reason);
        Assert.Contains("skill.ghost", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Min_range_greater_than_max_range_is_rejected()
    {
        var result = ActionValidator.ValidateAction(
            Action(minRange: 5, maxRange: 2), new HashSet<string>(), false);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.InvalidRange, result.Reason);
    }

    [Fact]
    public void An_area_action_is_rejected_while_no_board_exists()
    {
        var targeting = new ActionTargetSpec { Mode = ActionTargetMode.Area };
        var result = ActionValidator.ValidateAction(Action(targeting: targeting), new HashSet<string>(), boardAvailable: false);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.AreaRequiresBoard, result.Reason);
    }

    [Fact]
    public void An_area_action_is_accepted_once_a_board_exists()
    {
        var targeting = new ActionTargetSpec { Mode = ActionTargetMode.Area };
        var result = ActionValidator.ValidateAction(Action(targeting: targeting), new HashSet<string>(), boardAvailable: true);
        Assert.True(result.IsOk, result.ToString());
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("innate")]
    [InlineData("skill")]
    public void Every_shipped_kind_name_parses(string name)
    {
        Assert.True(ActionKinds.TryParse(name, out _));
    }

    [Fact]
    public void An_unknown_kind_name_is_rejected_never_coerced()
    {
        Assert.False(ActionKinds.TryParse("ultimate", out _));
    }

    [Theory]
    [InlineData("offensive")] [InlineData("defensive")] [InlineData("heal")] [InlineData("buff")]
    [InlineData("debuff")] [InlineData("movement")] [InlineData("summon")] [InlineData("utility")]
    public void Every_one_of_the_eight_shipped_tags_parses(string name)
    {
        Assert.True(ActionTags.TryParse(name, out _));
    }

    [Fact]
    public void An_unknown_tag_is_rejected_never_coerced()
    {
        Assert.False(ActionTags.TryParse("legendary", out _));
    }

    [Theory]
    [InlineData("caster")] [InlineData("primaryTarget")] [InlineData("eachTarget")] [InlineData("casterAllies")]
    public void Every_one_of_the_four_shipped_scopes_parses(string name)
    {
        Assert.True(ActionEffectScopes.TryParse(name, out _));
    }

    [Fact]
    public void An_unknown_scope_is_rejected_never_coerced()
    {
        Assert.False(ActionEffectScopes.TryParse("everyone", out _));
    }

    // ---- rpg_action_cost -----------------------------------------------------------------------------

    [Theory]
    [InlineData("hp")] [InlineData("stamina")] [InlineData("hunger")]
    [InlineData("spirit")] [InlineData("qi")] [InlineData("poise")]
    public void All_six_resources_are_accepted_not_five(string resourceId)
    {
        var cost = new ActionCostRow("skill.test", resourceId, ValueSpec.Of(10), ActionCostTiming.OnCommit);
        var result = ActionValidator.ValidateCost(cost);
        Assert.True(result.IsOk, result.ToString());
    }

    [Fact]
    public void An_unknown_resource_id_is_rejected()
    {
        var cost = new ActionCostRow("skill.test", "mana", ValueSpec.Of(10), ActionCostTiming.OnCommit);
        var result = ActionValidator.ValidateCost(cost);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.UnknownResource, result.Reason);
    }

    [Fact]
    public void A_malformed_value_spec_is_rejected()
    {
        var cost = new ActionCostRow("skill.test", "qi", new ValueSpec(10, 5, RollPolicy.Fixed), ActionCostTiming.OnCommit);
        var result = ActionValidator.ValidateCost(cost);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.BadValueSpec, result.Reason);
    }

    [Theory]
    [InlineData("onCommit")]
    [InlineData("perTick")]
    public void Both_cost_timings_round_trip_their_name(string name)
    {
        Assert.True(ActionCostTimings.TryParse(name, out var timing));
        Assert.Equal(name, ActionCostTimings.Name(timing));
    }

    // ---- rpg_action_effect_scope ----------------------------------------------------------------------

    [Fact]
    public void A_scope_naming_an_atom_the_container_lacks_is_rejected()
    {
        var scope = new ActionScopeRow("skill.test", "atom.ghost", ActionEffectScope.EachTarget);
        var result = ActionValidator.ValidateScope(scope, new HashSet<string> { "atom.real" });
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.ScopeAtomNotInContainer, result.Reason);
    }

    [Fact]
    public void A_scope_naming_an_atom_the_container_holds_is_accepted()
    {
        var scope = new ActionScopeRow("skill.test", "atom.real", ActionEffectScope.CasterAllies);
        var result = ActionValidator.ValidateScope(scope, new HashSet<string> { "atom.real" });
        Assert.True(result.IsOk, result.ToString());
    }

    // ---- rpg_action_grant ------------------------------------------------------------------------------

    [Fact]
    public void A_grant_colliding_with_a_basic_action_is_rejected_never_double_counted()
    {
        var basic = Action(id: "act.attack", kind: ActionKind.Basic);
        var grant = new ActionGrantRow(OwnerKind.Player, "1", "act.attack", "item.sword");
        var result = ActionValidator.ValidateGrant(grant, id => id == "act.attack" ? basic : null);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.BasicCollision, result.Reason);
    }

    [Fact]
    public void An_item_granting_a_non_grantable_action_is_rejected_at_import()
    {
        var notGrantable = Action(id: "act.pass", kind: ActionKind.Skill, grantable: false);
        var grant = new ActionGrantRow(OwnerKind.Player, "1", "act.pass", "item.trinket");
        var result = ActionValidator.ValidateGrant(grant, id => id == "act.pass" ? notGrantable : null);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.ActionNotGrantable, result.Reason);
    }

    [Fact]
    public void The_two_grant_flags_are_independent()
    {
        // Planted row: grantable = 1, default_attack_eligible = 0. Granting it must succeed on
        // `grantable` alone — whether it may REPLACE a default attack is a separate question this
        // validator does not answer (that is A15's, at assembly time).
        var extraAbility = Action(id: "skill.fireball", grantable: true, defaultAttackEligible: false);
        var grant = new ActionGrantRow(OwnerKind.Player, "1", "skill.fireball", "item.tome");
        var result = ActionValidator.ValidateGrant(grant, id => id == "skill.fireball" ? extraAbility : null);
        Assert.True(result.IsOk, result.ToString());
        Assert.True(extraAbility.Grantable);
        Assert.False(extraAbility.DefaultAttackEligible);
    }

    [Fact]
    public void A_grant_with_a_bad_owner_key_is_rejected()
    {
        var action = Action(id: "skill.fireball", grantable: true);
        var grant = new ActionGrantRow(OwnerKind.Player, "not-a-number", "skill.fireball", "item.tome");
        var result = ActionValidator.ValidateGrant(grant, id => id == "skill.fireball" ? action : null);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.BadOwnerKey, result.Reason);
    }

    // ---- species basics --------------------------------------------------------------------------------

    [Fact]
    public void A_species_row_missing_any_basic_is_rejected_naming_the_species()
    {
        var row = new SpeciesBasicsRow("zombie.42", "act.attack", "", "act.move", null);
        var result = ActionValidator.ValidateSpeciesBasics(row, _ => null);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.MissingSpeciesBasic, result.Reason);
        Assert.Contains("zombie.42", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_basic_slot_naming_a_non_basic_action_is_rejected()
    {
        var skill = Action(id: "act.attack", kind: ActionKind.Skill);
        var row = new SpeciesBasicsRow("zombie.42", "act.attack", "act.guard", "act.move", null);
        var result = ActionValidator.ValidateSpeciesBasics(row,
            id => id == "act.attack" ? skill : Action(id, ActionKind.Basic));
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.BasicKindMismatch, result.Reason);
    }

    [Fact]
    public void A_complete_species_row_with_a_matching_innate_is_accepted()
    {
        var row = new SpeciesBasicsRow("zombie.42", "act.attack", "act.guard", "act.move", "innate.rot-burst");
        var result = ActionValidator.ValidateSpeciesBasics(row, id => id switch
        {
            "act.attack" or "act.guard" or "act.move" => Action(id, ActionKind.Basic),
            "innate.rot-burst" => Action(id, ActionKind.Innate),
            _ => null,
        });
        Assert.True(result.IsOk, result.ToString());
    }

    [Fact]
    public void An_innate_slot_naming_a_non_innate_action_is_rejected()
    {
        var row = new SpeciesBasicsRow("zombie.42", "act.attack", "act.guard", "act.move", "skill.fireball");
        var result = ActionValidator.ValidateSpeciesBasics(row, id => id switch
        {
            "act.attack" or "act.guard" or "act.move" => Action(id, ActionKind.Basic),
            "skill.fireball" => Action(id, ActionKind.Skill),
            _ => null,
        });
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.BasicKindMismatch, result.Reason);
    }

    // ---- ActionValueSpecJson ---------------------------------------------------------------------------

    [Fact]
    public void A_fixed_value_spec_round_trips_as_a_bare_number()
    {
        var spec = ValueSpec.Of(50);
        var json = ActionValueSpecJson.Write(spec);
        Assert.Equal("50", json);

        var result = ActionValueSpecJson.TryRead(json, out var read);
        Assert.True(result.IsOk, result.ToString());
        Assert.Equal(spec, read);
    }

    [Fact]
    public void A_ranged_value_spec_round_trips_through_the_object_shape()
    {
        var spec = ValueSpec.Range(500, 1000);
        var json = ActionValueSpecJson.Write(spec);
        var result = ActionValueSpecJson.TryRead(json, out var read);
        Assert.True(result.IsOk, result.ToString());
        Assert.Equal(spec, read);
    }
}
