using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Loadout;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T21 (action-todo.md, spec-loadout.md §2): the equipped-skill set. "Rejects, never truncates" is
/// the load-bearing property — every rejection test below asserts the WHOLE attempt is refused, not
/// that the first N valid entries got applied anyway.
/// </summary>
public class LoadoutTests
{
    static Func<string, bool> AllHeld() => _ => true;
    static Func<string, ActionKind> AllSkills() => _ => ActionKind.Skill;
    static Func<bool> NotMidRun() => () => false;

    [Fact]
    public void FiveHeldSkillsAreValid()
    {
        var ids = new[] { "s1", "s2", "s3", "s4", "s5" };
        var result = LoadoutSet.Validate(ids, AllHeld(), AllSkills(), NotMidRun());
        Assert.True(result.Ok);
    }

    [Fact]
    public void FewerThanFiveIsLegalNotPadded()
    {
        var ids = new[] { "s1", "s2" };
        var result = LoadoutSet.Validate(ids, AllHeld(), AllSkills(), NotMidRun());
        Assert.True(result.Ok);
    }

    [Fact]
    public void ZeroEntriesIsLegal()
    {
        var result = LoadoutSet.Validate(Array.Empty<string>(), AllHeld(), AllSkills(), NotMidRun());
        Assert.True(result.Ok);
    }

    [Fact]
    public void ASixthEntryRejectsTheWholeAttemptAndTruncatesNothing()
    {
        var ids = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var result = LoadoutSet.Validate(ids, AllHeld(), AllSkills(), NotMidRun());

        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.LoadoutFull, result.Reason);
        // "Truncates nothing" is a property of the CALLER (a real persist call would see Ok=false
        // and write zero rows) -- structurally guaranteed here since Validate performs no writes at
        // all and has no partial-success return shape to misuse.
    }

    [Fact]
    public void AnEntryTheActorDoesNotHoldRejects()
    {
        var ids = new[] { "s1", "s.not-held" };
        Func<string, bool> isHeld = id => id != "s.not-held";

        var result = LoadoutSet.Validate(ids, isHeld, AllSkills(), NotMidRun());

        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.ActionNotHeld, result.Reason);
        Assert.Equal("s.not-held", result.ActionId);
    }

    [Theory]
    [InlineData(ActionKind.Basic)]
    [InlineData(ActionKind.Innate)]
    public void AnIntrinsicEntryRejectsAsACategoryErrorNotAFullSlotError(ActionKind intrinsicKind)
    {
        var ids = new[] { "act.attack" };
        Func<string, ActionKind> kindOf = _ => intrinsicKind;

        var result = LoadoutSet.Validate(ids, AllHeld(), kindOf, NotMidRun());

        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.IntrinsicNotEquippable, result.Reason);
        Assert.Equal("act.attack", result.ActionId);
    }

    [Fact]
    public void AnIntrinsicHeldEntryStillRejectsNotSlipsThroughAsHeld()
    {
        // The ordering property named directly in the implementation: an actor's own basic IS
        // "held" in the sense of always-present, so this proves Kind is checked BEFORE isHeld,
        // rather than an intrinsic slipping through because it happens to also be "held".
        var ids = new[] { "act.attack" };
        var result = LoadoutSet.Validate(ids, isHeld: _ => true, kindOf: _ => ActionKind.Basic, NotMidRun());

        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.IntrinsicNotEquippable, result.Reason);
    }

    [Fact]
    public void ADuplicateActionIdRejects()
    {
        var ids = new[] { "s1", "s2", "s1" };
        var result = LoadoutSet.Validate(ids, AllHeld(), AllSkills(), NotMidRun());

        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.DuplicateInLoadout, result.Reason);
        Assert.Equal("s1", result.ActionId);
    }

    [Fact]
    public void MidRunRejectsBeforeConsultingAnyOtherDelegate()
    {
        var ids = new[] { "s1" };
        var result = LoadoutSet.Validate(
            ids,
            isHeld: _ => throw new InvalidOperationException("isHeld consulted mid-run"),
            kindOf: _ => throw new InvalidOperationException("kindOf consulted mid-run"),
            isMidRun: () => true);

        Assert.False(result.Ok);
        Assert.Equal(LoadoutRejectionReason.MidRun, result.Reason);
    }

    [Fact]
    public void OrdinalIsPositionOnlyTwoAttemptsWithTheSameSetInDifferentOrderBothValidate()
    {
        // spec §1: "ordinal is the display and tie-break order, not a priority." Proven the only way
        // this pure validator can: the SAME set, reordered, produces the SAME Ok result.
        var forward = new[] { "s1", "s2", "s3" };
        var reversed = new[] { "s3", "s2", "s1" };

        var a = LoadoutSet.Validate(forward, AllHeld(), AllSkills(), NotMidRun());
        var b = LoadoutSet.Validate(reversed, AllHeld(), AllSkills(), NotMidRun());

        Assert.True(a.Ok);
        Assert.True(b.Ok);
    }
}
