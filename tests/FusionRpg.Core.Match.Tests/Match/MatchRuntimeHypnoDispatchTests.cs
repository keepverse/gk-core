using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Match;

/// <summary>
/// T6 (buff-debuff-scope-todo.md Phase 2) — the real new piece: `MatchRuntime.cs:110` was a
/// placeholder comment, never working handling. Tests against `MatchRuntime.Apply` directly rather
/// than `SimEngine.Hypno` — checked, that helper hard-codes `isMindControlled = true` and cannot
/// produce the release direction, so it can't exercise both sides of this dispatch case.
/// </summary>
public class MatchRuntimeHypnoDispatchTests
{
    static MatchRuntime InMatch()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-test" });
        return rt;
    }

    static Dictionary<string, object> HypnoPayload(string ptr, bool mc) => new()
    {
        ["ptr"] = ptr,
        ["controlLevel"] = mc ? 1 : 0,
        ["isMindControlled"] = mc,
    };

    [Fact]
    public void A_real_hypno_on_event_is_tracked_and_raises_MindControlToggled()
    {
        var rt = InMatch();
        var events = new List<ScopeMembershipEvent>();
        rt.MembershipChanged += events.Add;
        var revisionBefore = rt.Revision;

        rt.Apply("zombie.hypno", HypnoPayload("0xAAA", mc: true));

        var e = Assert.Single(events);
        Assert.Equal("AAA", e.Ptr);
        Assert.Equal(ScopeMembershipTransition.MindControlToggled, e.Transition);
        Assert.True(e.MindControlledNow);
        Assert.True(rt.Revision > revisionBefore, "a real state change must bump the revision");
    }

    [Fact]
    public void A_real_hypno_off_event_is_tracked_and_raises_MindControlToggled_false()
    {
        var rt = InMatch();
        rt.Apply("zombie.hypno", HypnoPayload("0xBBB", mc: true));

        var events = new List<ScopeMembershipEvent>();
        rt.MembershipChanged += events.Add;
        rt.Apply("zombie.hypno", HypnoPayload("0xBBB", mc: false));

        var e = Assert.Single(events);
        Assert.Equal("BBB", e.Ptr);
        Assert.False(e.MindControlledNow);
    }

    [Fact]
    public void A_redundant_repeat_event_does_not_double_bump_but_still_raises_the_signal()
    {
        var rt = InMatch();
        rt.Apply("zombie.hypno", HypnoPayload("0xCCC", mc: true));
        var revisionAfterFirst = rt.Revision;

        var events = new List<ScopeMembershipEvent>();
        rt.MembershipChanged += events.Add;
        rt.Apply("zombie.hypno", HypnoPayload("0xCCC", mc: true)); // redundant repeat, same state

        Assert.Equal(revisionAfterFirst, rt.Revision);
        Assert.Single(events); // the signal still fires; only the Bump is suppressed
    }

    [Fact]
    public void Existing_dispatch_cases_are_unmoved_by_the_new_hypno_case()
    {
        var rt = InMatch();
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1", ["typeId"] = 5 });
        rt.Apply("zombie.spawn", new Dictionary<string, object> { ["ptr"] = "0xZ1", ["typeId"] = 9 });
        Assert.True(rt.Revision > 0);

        rt.Apply("plant.die", new Dictionary<string, object> { ["ptr"] = "0xP1" });
        rt.Apply("zombie.die", new Dictionary<string, object> { ["ptr"] = "0xZ1" });
        // No exception, no crash, existing paths still resolve — the regression bar this task named.
    }

    [Fact]
    public void MembershipChanged_survives_a_second_match_after_the_first_ends()
    {
        // The facet-swap finding: `_state.UniqueBindings` (and now `MindControl`) reset on every
        // match, but MatchRuntime's own event must not go silently stale after the first reset.
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-1" });

        var events = new List<ScopeMembershipEvent>();
        rt.MembershipChanged += events.Add;

        rt.Apply("board.end");
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-2" });
        rt.Apply("zombie.hypno", HypnoPayload("0xDDD", mc: true));

        var e = Assert.Single(events);
        Assert.Equal("DDD", e.Ptr);
    }
}
