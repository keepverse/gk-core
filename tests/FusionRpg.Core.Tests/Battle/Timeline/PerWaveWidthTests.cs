using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// battle-timeline T15/B33 (spec-profile-migration.md §3) — a wave may override the resolved profile's
/// concurrency width, and ships without doing so.
///
/// <para>Two halves, and the second matters as much as the first: the mechanism must WORK
/// (a wave that sets `W = 1` on a wide profile really does serialize) and it must be INERT
/// (no wave sets one today, so nothing whatsoever changes). The inert half is what keeps the profile
/// migration's own delta attributable to the profile switch alone.</para>
/// </summary>
public class PerWaveWidthTests
{
    /// <summary>Inert, and proven by REFERENCE identity rather than equality — "the wave did not
    /// override" must be indistinguishable from "the mechanism does not exist", not merely equal
    /// to it.</summary>
    [Theory]
    [InlineData("rift-skirmish")]
    [InlineData("rift-warband")]
    [InlineData("rift-onslaught")]
    [InlineData("rift-tyrant")]
    public void EveryShippedWaveOverridesNothing(string waveId)
    {
        Assert.Null(WaveCatalog.Get(waveId).W);
        Assert.Same(BattleModeProfileCatalog.Resolve(WaveCatalog.Get(waveId).Profile),
                    WaveCatalog.ProfileFor(waveId));
    }

    [Fact]
    public void AWaveOverrideReplacesTheProfileWidth_andChangesNothingElse()
    {
        var baseline = BattleModeProfileCatalog.HybridAtb;
        var wave = WaveCatalog.Get("rift-tyrant") with
        {
            Profile = BattleModeProfileCatalog.HybridAtbId,
            W = 1
        };

        var resolved = wave.W is { } w ? BattleModeProfileCatalog.Resolve(wave.Profile) with { W = w } : baseline;

        Assert.Equal(1, resolved.W);
        Assert.NotEqual(baseline.W, resolved.W);
        // Everything else is untouched — an override of the width is not a different mode.
        Assert.Equal(baseline.ProfileId, resolved.ProfileId);
        Assert.Equal(baseline.AdvancePolicy, resolved.AdvancePolicy);
        Assert.Equal(baseline.WScope, resolved.WScope);
        Assert.Equal(baseline.DefaultCommitment, resolved.DefaultCommitment);
        Assert.Equal(baseline.PassQuantum, resolved.PassQuantum);
        Assert.Same(baseline.NewEconomy, resolved.NewEconomy);   // the factory carries over untouched
    }

    /// <summary>The mechanism proven by CONTRAST at the slot layer — the same shape B12 uses for
    /// `W=1` vs `W=2`. A width that does not actually gate acquisition is a field, not a lever.</summary>
    [Fact]
    public void WidthOneSerializesWhereTheProfileDefaultOverlaps()
    {
        var p = BattleModeProfileCatalog.HybridAtb;
        var wide = new ActionSlots(p.W, p.WScope);                              // W = 4
        Assert.True(wide.TryAcquire("a", "left"));
        Assert.True(wide.TryAcquire("b", "left"));
        Assert.True(wide.TryAcquire("c", "left"));
        Assert.True(wide.TryAcquire("d", "left"));
        Assert.False(wide.TryAcquire("e", "left"));                             // the 5th exceeds W = 4

        var narrowed = p with { W = 1 };
        var serialized = new ActionSlots(narrowed.W, narrowed.WScope);
        Assert.True(serialized.TryAcquire("a", "left"));
        Assert.False(serialized.TryAcquire("b", "left"));                       // the 2nd exceeds W = 1
    }

    /// <summary>A width of 0 is an encounter in which nobody may ever act. Refused at the slot layer,
    /// which is where it is actually enforced; `WaveCatalog.ProfileFor` refuses it a layer earlier so
    /// the error names the offending wave rather than surfacing as an opaque slot failure.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveWidthIsRefused(int bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActionSlots(bad));
    }
}
