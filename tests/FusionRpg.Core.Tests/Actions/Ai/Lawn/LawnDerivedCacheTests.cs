using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions.Ai.Lawn;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai.Lawn;

/// <summary>
/// combat-ai `lawn-actor-view` (CAI4.1, spec-lawn-actor-view.md "The derived memo"): the per-frame,
/// revision-keyed memo. Its two claims are counted, never timed.
/// </summary>
public class LawnDerivedCacheTests
{
    /// <summary>A resolver that counts its calls and hands back a distinct snapshot per call, so a
    /// memo HIT is visible as "the same instance came back" rather than only as a call count.</summary>
    sealed class CountingResolver
    {
        public int Calls;
        public ActorDerivedSnapshot Resolve(string ptr) { Calls++; return ActorDerivedSnapshot.StubNeutral(); }
    }

    [Fact]
    public void One_resolve_per_actor_per_frame_across_many_reads()
    {
        var resolver = new CountingResolver();
        var cache = new LawnDerivedCache(resolver.Resolve, _ => 0L);

        for (var i = 0; i < 20; i++) cache.Get("plant:1");

        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void It_returns_the_very_instance_the_resolver_produced()
    {
        var resolver = new CountingResolver();
        var cache = new LawnDerivedCache(resolver.Resolve, _ => 0L);

        var first = cache.Get("plant:1");
        var second = cache.Get("plant:1");

        Assert.Same(first, second); // Hub's own answer, never a copy or a re-fold
    }

    /// <summary>The row's own acceptance line: a mid-frame revision bump costs exactly one further
    /// resolve for THAT actor, and none for others. The revision guard is what honours an invalidation
    /// arriving inside a frame — frame scope alone would miss it until the next frame.</summary>
    [Fact]
    public void A_mid_frame_revision_bump_costs_exactly_one_further_resolve_for_that_actor_only()
    {
        var resolver = new CountingResolver();
        var revisions = new Dictionary<string, long>(StringComparer.Ordinal) { ["bumped:1"] = 7, ["steady:1"] = 3 };
        var cache = new LawnDerivedCache(resolver.Resolve, ptr => revisions[ptr]);

        cache.Get("bumped:1");
        cache.Get("steady:1");
        Assert.Equal(2, resolver.Calls);

        revisions["bumped:1"] = 8;   // the liveness edge, mid-frame
        cache.Get("bumped:1");
        cache.Get("steady:1");
        cache.Get("steady:1");

        Assert.Equal(3, resolver.Calls); // exactly one further resolve, for the bumped actor alone
    }

    [Fact]
    public void BeginFrame_drops_the_memo_so_the_next_read_resolves_again()
    {
        var resolver = new CountingResolver();
        var cache = new LawnDerivedCache(resolver.Resolve, _ => 0L);

        cache.Get("plant:1");
        cache.Get("plant:1");
        Assert.Equal(1, resolver.Calls);

        cache.BeginFrame();
        cache.Get("plant:1");

        Assert.Equal(2, resolver.Calls);
    }

    /// <summary>The pre-`actor-liveness-refresh` behaviour, asserted rather than left implicit: a
    /// CONSTANT revision seam makes this memo frame-scoped only. Correct, and colder than it will be
    /// once the real counter lands.</summary>
    [Fact]
    public void A_constant_revision_seam_makes_the_memo_frame_scoped_only()
    {
        var resolver = new CountingResolver();
        var cache = new LawnDerivedCache(resolver.Resolve, _ => 42L);

        for (var i = 0; i < 5; i++) cache.Get("plant:1");
        Assert.Equal(1, resolver.Calls);

        cache.BeginFrame();
        cache.Get("plant:1");
        Assert.Equal(2, resolver.Calls); // the frame boundary is the ONLY invalidation a constant seam gives
    }

    [Fact]
    public void Both_seams_are_required()
    {
        var resolver = new CountingResolver();
        Assert.Throws<ArgumentNullException>(() => new LawnDerivedCache(null!, _ => 0L));
        Assert.Throws<ArgumentNullException>(() => new LawnDerivedCache(resolver.Resolve, null!));
    }
}
