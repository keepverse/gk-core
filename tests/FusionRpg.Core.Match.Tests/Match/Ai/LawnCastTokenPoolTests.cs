using System;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.7, spec-lawn-cast-trigger.md §8) — the cast-token
/// pool. Spec test row 8, plus the token half of rows 9 and 10.
/// </summary>
public class LawnCastTokenPoolTests
{
    const long Timeout = LawnCastTokenPool.CastTokenTimeoutTicks;

    [Fact]
    public void A_lease_is_refused_once_every_token_is_out()
    {
        var pool = new LawnCastTokenPool(capacity: 2);
        Assert.True(pool.TryLease("ptr.a", 0));
        Assert.True(pool.TryLease("ptr.b", 0));
        Assert.False(pool.TryLease("ptr.c", 0));
        Assert.Equal(2, pool.InUse);
    }

    [Fact]
    public void Leasing_twice_for_one_actor_is_the_same_cast_and_consumes_no_second_token()
    {
        var pool = new LawnCastTokenPool(capacity: 1);
        Assert.True(pool.TryLease("ptr.a", 0));
        Assert.True(pool.TryLease("ptr.a", 1));
        Assert.Equal(1, pool.InUse);
        // Consuming a second token for one cast would be a leak nothing would ever release.
        Assert.False(pool.TryLease("ptr.b", 2));
    }

    [Fact]
    public void Release_is_idempotent_and_a_second_release_never_goes_negative()
    {
        var pool = new LawnCastTokenPool(capacity: 2);
        Assert.True(pool.TryLease("ptr.a", 0));

        Assert.True(pool.Release("ptr.a"));
        Assert.False(pool.Release("ptr.a"));
        Assert.False(pool.Release("ptr.never-leased"));
        Assert.Equal(0, pool.InUse);

        // And the released token is genuinely available again.
        Assert.True(pool.TryLease("ptr.b", 0));
        Assert.Equal(1, pool.InUse);
    }

    [Fact]
    public void A_token_never_released_is_reclaimed_at_the_timeout_and_not_before()
    {
        var pool = new LawnCastTokenPool(capacity: 1);
        Assert.True(pool.TryLease("ptr.a", 100));

        // Reclaims AT the timeout: leased at 100 with a 20-tick timeout, reclaimed on the tick-120 call.
        Assert.Equal(0, pool.ReclaimExpired(100 + Timeout - 1));
        Assert.True(pool.IsLeased("ptr.a"));

        Assert.Equal(1, pool.ReclaimExpired(100 + Timeout));
        Assert.False(pool.IsLeased("ptr.a"));
        Assert.Equal(0, pool.InUse);

        // The reclaimed token is available, so a leak self-heals rather than shrinking the pool.
        Assert.True(pool.TryLease("ptr.b", 100 + Timeout));
    }

    [Fact]
    public void Reclaim_expired_leaves_a_lease_that_is_still_within_its_window()
    {
        var pool = new LawnCastTokenPool(capacity: 2);
        Assert.True(pool.TryLease("ptr.old", 0));
        Assert.True(pool.TryLease("ptr.fresh", 50));

        Assert.Equal(1, pool.ReclaimExpired(50));
        Assert.False(pool.IsLeased("ptr.old"));
        Assert.True(pool.IsLeased("ptr.fresh"));
    }

    [Fact]
    public void Death_releases_the_token_and_a_reused_ptr_finds_the_pool_clean()
    {
        var pool = new LawnCastTokenPool(capacity: 1);
        Assert.True(pool.TryLease("ptr.a", 0));

        // The death path (Hot rule 4) releases the lease, exactly as it drops the trigger state.
        Assert.True(pool.Release("ptr.a"));

        Assert.False(pool.IsLeased("ptr.a"));
        Assert.True(pool.TryLease("ptr.a", 1));
        Assert.Equal(1, pool.InUse);
    }

    [Fact]
    public void Clear_releases_every_lease_at_once()
    {
        var pool = new LawnCastTokenPool(capacity: 4);
        Assert.True(pool.TryLease("ptr.a", 0));
        Assert.True(pool.TryLease("ptr.b", 0));

        pool.Clear();

        Assert.Equal(0, pool.InUse);
        Assert.False(pool.IsLeased("ptr.a"));
        Assert.False(pool.IsLeased("ptr.b"));
    }

    [Fact]
    public void The_structural_defaults_are_the_pool_count_and_the_timeout_backstop()
    {
        // Closed, code-owned structural constants: a concurrency lease count and a leak backstop.
        // A balance pass has no reason to touch either (tunables-ssot.md §1).
        Assert.Equal(4, LawnCastTokenPool.CastTokens);
        Assert.Equal(20, LawnCastTokenPool.CastTokenTimeoutTicks);
        Assert.Equal(LawnCastTokenPool.CastTokens, new LawnCastTokenPool().Capacity);
    }

    [Fact]
    public void A_zero_capacity_or_a_zero_timeout_is_rejected()
    {
        // A zero-capacity pool is a permanently disabled cast path (the kill switch's job, not a token
        // count's), and a zero timeout reclaims a legitimate cast's lease at the tick it was taken.
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnCastTokenPool(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LawnCastTokenPool(timeoutTicks: 0));
        Assert.Throws<ArgumentException>(() => new LawnCastTokenPool().TryLease("", 0));
        Assert.Throws<ArgumentException>(() => new LawnCastTokenPool().Release(""));
    }
}
