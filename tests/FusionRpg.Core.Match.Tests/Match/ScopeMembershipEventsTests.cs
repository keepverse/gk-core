using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests.Match;

/// <summary>
/// T5 (buff-debuff-scope-todo.md Phase 2): Bound/Cleared raised alongside
/// <c>MatchUniqueBindingsFacet</c>'s own already-correct transitions — never a new detection path.
/// </summary>
public class ScopeMembershipEventsTests
{
    [Fact]
    public void Binding_on_spawn_raises_Bound_exactly_once_with_the_real_ptr()
    {
        var facet = new MatchUniqueBindingsFacet();
        var events = new List<ScopeMembershipEvent>();
        facet.MembershipChanged += events.Add;

        facet.TryBeginPending("inst-1", "corr-1", "plant", typeId: 42);
        var ok = facet.TryBindOnSpawn("corr-1", instanceId: null, ptr: "0xABC", out var bound);

        Assert.True(ok);
        Assert.Equal("ABC", bound!.Ptr);
        var e = Assert.Single(events);
        Assert.Equal("ABC", e.Ptr);
        Assert.Equal(ScopeMembershipTransition.Bound, e.Transition);
    }

    [Fact]
    public void Clearing_a_bound_instance_raises_Cleared_with_the_ptr_it_held()
    {
        var facet = new MatchUniqueBindingsFacet();
        facet.TryBeginPending("inst-2", "corr-2", "zombie", typeId: 7);
        facet.TryBindOnSpawn("corr-2", null, "0xDEF", out _);

        var events = new List<ScopeMembershipEvent>();
        facet.MembershipChanged += events.Add;
        var cleared = facet.TryClearByPtr("0xDEF");

        Assert.True(cleared);
        var e = Assert.Single(events);
        Assert.Equal("DEF", e.Ptr);
        Assert.Equal(ScopeMembershipTransition.Cleared, e.Transition);
    }

    [Fact]
    public void Clearing_a_still_pending_never_bound_instance_raises_nothing()
    {
        // No live ptr ever existed, so there is nothing for a scope's own-side population to have
        // included in the first place — correctly a no-signal case, not a bug.
        var facet = new MatchUniqueBindingsFacet();
        facet.TryBeginPending("inst-3", "corr-3", "plant", typeId: 1);

        var events = new List<ScopeMembershipEvent>();
        facet.MembershipChanged += events.Add;
        var cleared = facet.TryClearByInstance("inst-3");

        Assert.True(cleared);
        Assert.Empty(events);
    }

    [Fact]
    public void UniqueBindings_own_existing_behaviour_is_unchanged_by_the_new_event()
    {
        // Regression proof: T5 must not alter *when* or *whether* a transition fires, only add a
        // signal alongside it — re-run a slice of UniqueBindings.cs's own established contract with
        // no subscriber attached at all.
        var facet = new MatchUniqueBindingsFacet();
        Assert.True(facet.TryBeginPending("inst-4", "corr-4", "plant", typeId: 9));
        Assert.True(facet.TryBindOnSpawn("corr-4", null, "0x111", out var bound));
        Assert.Equal(UniqueBindingPhase.Bound, bound!.Phase);
        Assert.Equal(1, facet.Count);
        Assert.True(facet.TryClearByPtr("0x111"));
        Assert.Equal(0, facet.Count);
        Assert.False(facet.TryClearByPtr("0x111"), "clearing twice must still refuse the second time");
    }
}
