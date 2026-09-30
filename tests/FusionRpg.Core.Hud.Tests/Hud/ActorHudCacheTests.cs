using FusionRpg.Core.Hud;
using FusionRpg.Bridge.Hud;
using Xunit;

namespace FusionRpg.Core.Tests.Hud;

public sealed class ActorHudCacheTests : IDisposable
{
    public ActorHudCacheTests()
    {
        ActorHudCache.Clear();
        ActorHudCache.Build = ptr => new ActorHudSnapshot(
            new ActorHudIdentity(ActorHudTier.Normal, "vanilla", 1, Array.Empty<string>()),
            null,
            Array.Empty<ActorHudStatusToken>(),
            new ActorHudOverflow(0));
    }

    public void Dispose()
    {
        ActorHudCache.Clear();
        ActorHudCache.Build = null;
        ActorHudCache.DeltaEmit = null;
    }

    /// <summary>
    /// ⚡ This test used to be <c>GetOrBuild_always_rebuilds_from_build_delegate</c> and asserted
    /// <c>calls == 2</c> — it pinned a rebuild-per-read that was harmless while observe was the only
    /// reader and cost 42% of wall once the per-frame world HUD walk became one
    /// (<see cref="ActorHudCache"/>'s own note has the measurement). The contract is now the one the
    /// class was named for: a clean ptr is served from the cache, and only a dirty one rebuilds.
    /// </summary>
    [Fact]
    public void GetOrBuild_serves_a_clean_ptr_from_cache_without_rebuilding()
    {
        var calls = 0;
        ActorHudCache.Build = _ =>
        {
            calls++;
            return new ActorHudSnapshot(
                new ActorHudIdentity(ActorHudTier.Normal, "vanilla", calls, Array.Empty<string>()),
                null,
                Array.Empty<ActorHudStatusToken>(),
                new ActorHudOverflow(0));
        };

        var first = ActorHudCache.GetOrBuild("ABC");
        var second = ActorHudCache.GetOrBuild("ABC");

        Assert.Equal(1, calls);
        Assert.Same(first, second);
        Assert.Equal(1, second!.Identity.LevelBand);
    }

    /// <summary>The cache is per ptr — one ptr being warm must not serve another ptr's snapshot.</summary>
    [Fact]
    public void Each_ptr_caches_independently()
    {
        var calls = 0;
        ActorHudCache.Build = _ =>
        {
            calls++;
            return new ActorHudSnapshot(
                new ActorHudIdentity(ActorHudTier.Normal, "vanilla", calls, Array.Empty<string>()),
                null,
                Array.Empty<ActorHudStatusToken>(),
                new ActorHudOverflow(0));
        };

        Assert.Equal(1, ActorHudCache.GetOrBuild("ABC")!.Identity.LevelBand);
        Assert.Equal(2, ActorHudCache.GetOrBuild("DEF")!.Identity.LevelBand);
        Assert.Equal(1, ActorHudCache.GetOrBuild("ABC")!.Identity.LevelBand);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void GetOrBuild_emits_delta_only_when_dirty()
    {
        var deltaCalls = 0;
        ActorHudCache.DeltaEmit = (_, _) => deltaCalls++;

        ActorHudCache.GetOrBuild("ABC");
        Assert.Equal(0, deltaCalls);

        ActorHudCache.MarkDirty("ABC");
        ActorHudCache.GetOrBuild("ABC");
        Assert.Equal(1, deltaCalls);

        ActorHudCache.GetOrBuild("ABC");
        Assert.Equal(1, deltaCalls);
    }

    [Fact]
    public void Cache_invalidates_on_mark_dirty_rebuild()
    {
        var calls = 0;
        ActorHudCache.Build = _ =>
        {
            calls++;
            return new ActorHudSnapshot(
                new ActorHudIdentity(ActorHudTier.Normal, "vanilla", calls, Array.Empty<string>()),
                null,
                Array.Empty<ActorHudStatusToken>(),
                new ActorHudOverflow(0));
        };

        ActorHudCache.GetOrBuild("ABC");
        ActorHudCache.MarkDirty("ABC");
        var rebuilt = ActorHudCache.GetOrBuild("ABC");

        Assert.Equal(2, calls);
        Assert.Equal(2, rebuilt!.Identity.LevelBand);
    }

    [Fact]
    public void Cache_remove_clears_entry()
    {
        ActorHudCache.GetOrBuild("ABC");
        ActorHudCache.Remove("ABC");
        ActorHudCache.MarkDirty("ABC");

        var calls = 0;
        ActorHudCache.Build = _ =>
        {
            calls++;
            return new ActorHudSnapshot(
                new ActorHudIdentity(ActorHudTier.Normal, "vanilla", null, Array.Empty<string>()),
                null,
                Array.Empty<ActorHudStatusToken>(),
                new ActorHudOverflow(0));
        };

        ActorHudCache.GetOrBuild("ABC");
        Assert.Equal(1, calls);
    }
}
