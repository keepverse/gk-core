using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>vfx-v3 V2: sustained state machine — start/refresh/end/ttl/evict orderings.</summary>
public class VfxStateTrackerTests
{
    static VfxRecipe Recipe(bool marker = false) => new()
    {
        CueId = "status.test.apply",
        Primitives = marker
            ? new[] { new VfxPrimitiveSpec { Kind = VfxPrimitiveKind.Aura }, new VfxPrimitiveSpec { Kind = VfxPrimitiveKind.Marker } }
            : new[] { new VfxPrimitiveSpec { Kind = VfxPrimitiveKind.Aura } }
    };

    [Fact]
    public void Start_then_reapply_refreshes_without_flicker()
    {
        var t = new VfxStateTracker();
        var started = t.Start("Z1", "wither", "status.wither.apply", Recipe(), 4000, 0.0, out _);
        Assert.NotNull(started);
        Assert.Equal(1, t.LiveCount);
        var again = t.Start("Z1", "wither", "status.wither.apply", Recipe(), 4000, 1.0, out _);
        Assert.Null(again); // refresh, not a new start
        Assert.Equal(1, t.LiveCount);
        // refresh extended the TTL: sweep at old TTL finds nothing
        Assert.Empty(t.SweepTtl(4000 / 1000.0 + VfxSustainedRules.TtlGraceSeconds + 0.5));
        Assert.Single(t.SweepTtl(1.0 + 4.0 + VfxSustainedRules.TtlGraceSeconds + 0.1));
    }

    [Fact]
    public void Ttl_beats_missing_expire_and_infinite_reconfirms()
    {
        var t = new VfxStateTracker();
        t.Start("Z1", "wither", "c", Recipe(), 1000, 0.0, out _);
        t.Start("Z2", "bond", "c", Recipe(), 0, 0.0, out _); // infinite duration
        Assert.Empty(t.SweepTtl(1.0));
        var ended = t.SweepTtl(1.0 + VfxSustainedRules.TtlGraceSeconds + 0.1);
        Assert.Single(ended);
        Assert.Equal("Z1", ended[0].HostPtr);
        // infinite set survives until its reconfirm window lapses
        Assert.Empty(t.SweepTtl(VfxSustainedRules.InfiniteTtlSeconds - 1));
        Assert.Single(t.SweepTtl(VfxSustainedRules.InfiniteTtlSeconds + 0.1));
    }

    [Fact]
    public void End_and_end_host_remove_sets()
    {
        var t = new VfxStateTracker();
        t.Start("Z1", "wither", "c", Recipe(), 4000, 0.0, out _);
        t.Start("Z1", "leech", "c", Recipe(), 4000, 0.0, out _);
        t.Start("Z2", "rally", "c", Recipe(), 4000, 0.0, out _);
        Assert.NotNull(t.End("Z1", "wither"));
        Assert.Null(t.End("Z1", "wither")); // idempotent
        var hostEnded = t.EndHost("Z1");
        Assert.Single(hostEnded);
        Assert.Equal("leech", hostEnded[0].StatusId);
        Assert.Equal(1, t.LiveCount);
        Assert.Single(t.EndAll());
        Assert.Equal(0, t.LiveCount);
    }

    [Fact]
    public void Per_host_cap_evicts_non_marker_oldest_first()
    {
        var t = new VfxStateTracker();
        t.Start("Z1", "wither", "c", Recipe(), 4000, 0.0, out _);            // oldest, no marker
        t.Start("Z1", "pact_mark", "c", Recipe(marker: true), 4000, 1.0, out _); // marker — protected
        var started = t.Start("Z1", "leech", "c", Recipe(), 4000, 2.0, out var evicted);
        Assert.NotNull(started);
        var v = Assert.Single(evicted);
        Assert.Equal("wither", v.StatusId); // non-marker evicted before the marker set
        Assert.Equal(2, t.LiveCount);
    }

    [Fact]
    public void Global_cap_holds_at_24()
    {
        var t = new VfxStateTracker();
        for (var i = 0; i < VfxSustainedRules.GlobalCap; i++)
            t.Start("Z" + i, "wither", "c", Recipe(), 4000, i, out _);
        Assert.Equal(VfxSustainedRules.GlobalCap, t.LiveCount);
        t.Start("ZNEW", "wither", "c", Recipe(), 4000, 100.0, out var evicted);
        Assert.Single(evicted);
        Assert.Equal("Z0", evicted[0].HostPtr); // oldest global victim
        Assert.Equal(VfxSustainedRules.GlobalCap, t.LiveCount);
    }

    [Fact]
    public void Evict_then_reapply_starts_fresh()
    {
        var t = new VfxStateTracker();
        t.Start("Z1", "wither", "c", Recipe(), 4000, 0.0, out _);
        t.Start("Z1", "pact_mark", "c", Recipe(marker: true), 4000, 1.0, out _);
        t.Start("Z1", "leech", "c", Recipe(), 4000, 2.0, out _); // evicts wither
        var restart = t.Start("Z1", "wither", "c", Recipe(), 4000, 3.0, out var evicted2);
        Assert.NotNull(restart); // a genuine new start, not a refresh of the evicted set
        Assert.Single(evicted2); // and it evicts again to respect the cap
    }
}
