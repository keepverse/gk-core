using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Loadout;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Objects;

/// <summary>
/// D3.29 (spec-supplies-and-objects.md §3) — "camp actions... are attrition §5's: corpus actions with
/// `useContext: rest`... competing for `LoadoutSet.MaxSize = 5`." No new production code: `LoadoutSet`
/// (T21, action-todo.md) is already a context-agnostic validator over a plain action-id list, so a
/// `rest`-context camp action already competes for the same five slots as any other skill by
/// construction — this file proves that claim against the real, shipped validator rather than leaving
/// it unchecked.
/// </summary>
public class CampActionsLoadoutTests
{
    static bool AlwaysHeld(string _) => true;
    static ActionKind SkillKind(string _) => ActionKind.Skill;
    static bool NotMidRun() => false;

    [Fact]
    public void A_camp_action_occupies_a_slot_like_any_other_skill()
    {
        var actionIds = new[] { "skill.strike", "skill.guard", "camp.forage" }; // camp.forage: useContext rest
        var result = LoadoutSet.Validate(actionIds, AlwaysHeld, SkillKind, NotMidRun);
        Assert.True(result.Ok);
    }

    [Fact]
    public void Five_skills_including_one_camp_action_fill_the_loadout_exactly()
    {
        var actionIds = new[] { "skill.a", "skill.b", "skill.c", "skill.d", "camp.forage" };
        Assert.Equal(5, actionIds.Length);
        Assert.Equal(LoadoutSet.MaxSize, actionIds.Length);
        var result = LoadoutSet.Validate(actionIds, AlwaysHeld, SkillKind, NotMidRun);
        Assert.True(result.Ok);
    }

    [Fact]
    public void A_sixth_action_alongside_a_camp_action_overflows_the_loadout_never_truncates()
    {
        var actionIds = new[] { "skill.a", "skill.b", "skill.c", "skill.d", "skill.e", "camp.forage" };
        var result = LoadoutSet.Validate(actionIds, AlwaysHeld, SkillKind, NotMidRun);
        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.LoadoutFull, result.Reason);
    }

    [Fact]
    public void A_camp_action_still_needs_to_be_held_like_any_other_skill()
    {
        var actionIds = new[] { "skill.a", "camp.forage" };
        bool IsHeld(string id) => id != "camp.forage"; // deliberately not held
        var result = LoadoutSet.Validate(actionIds, IsHeld, SkillKind, NotMidRun);
        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.ActionNotHeld, result.Reason);
        Assert.Equal("camp.forage", result.ActionId);
    }
}
