using FusionRpg.Contracts;
using FusionRpg.Core.Vfx;
using Xunit;

namespace FusionRpg.Core.Tests.Vfx;

/// <summary>Locks vfx-ssot.md §7 rate-limit grouping and §8 admission steps.</summary>
public class VfxAdmissionTests
{
    static VfxAdmission NewAdmission()
    {
        var catalog = new VfxCatalog();
        catalog.ReplaceAll(VfxSeedCatalog.CreateAll());
        return new VfxAdmission(catalog);
    }

    static VfxCueDto Hit(string ptr = "0xAAA", int? col = null, int? row = null) => new()
    {
        CueId = VfxCueIds.CombatHit,
        TargetPtr = ptr,
        Col = col,
        Row = row,
        Amount = -10
    };

    [Fact]
    public void Master_off_skips_disabled()
    {
        var a = NewAdmission();
        a.BeginTick(0);
        var d = a.Decide(Hit(), 0, masterOn: false);
        Assert.False(d.Admitted);
        Assert.Equal(VfxSkipReasons.Disabled, d.Reason);
    }

    [Fact]
    public void Unknown_cue_and_empty_cue_skip()
    {
        var a = NewAdmission();
        a.BeginTick(0);
        Assert.Equal(VfxSkipReasons.UnknownCue,
            a.Decide(new VfxCueDto { CueId = "status.nope.apply", TargetPtr = "0xA" }, 0, true).Reason);
        Assert.Equal(VfxSkipReasons.UnknownCue,
            a.Decide(new VfxCueDto { CueId = "" }, 0, true).Reason);
    }

    [Fact]
    public void Muted_cue_skips_muted()
    {
        var a = NewAdmission();
        a.BeginTick(0);
        var d = a.Decide(Hit(), 0, true, id => id == VfxCueIds.CombatHit);
        Assert.Equal(VfxSkipReasons.Muted, d.Reason);
    }

    [Fact]
    public void Same_unit_within_floater_interval_rate_limits_all_specs()
    {
        var a = NewAdmission();
        a.BeginTick(0);
        Assert.True(a.Decide(Hit("0xAAA"), 0.00, true).Admitted);
        var d = a.Decide(Hit("0xAAA"), 0.02, true);
        Assert.False(d.Admitted);
        Assert.Equal(VfxSkipReasons.RateLimited, d.Reason);
        // after the floater interval the floater re-admits (burst + flash still limited)
        var later = a.Decide(Hit("0xAAA"), 0.08, true);
        Assert.True(later.Admitted);
        Assert.Contains(0, later.SpecIndices);   // floater spec
        Assert.DoesNotContain(1, later.SpecIndices); // burst still inside 0.15s
        Assert.DoesNotContain(2, later.SpecIndices); // flash grouped with bursts
    }

    [Fact]
    public void Distinct_units_never_collapse()
    {
        var a = NewAdmission();
        a.BeginTick(0);
        var d1 = a.Decide(Hit("0xAAA"), 0, true);
        var d2 = a.Decide(Hit("0xBBB"), 0, true);
        Assert.True(d1.Admitted);
        Assert.True(d2.Admitted);
        Assert.Equal(6, d1.SpecIndices.Count);
        Assert.Equal(6, d2.SpecIndices.Count);
    }

    [Fact]
    public void Same_cell_bursts_collapse_but_floaters_stay_per_unit()
    {
        var a = NewAdmission();
        a.BeginTick(0);
        // two different units standing on the same cell, same frame (AoE)
        var d1 = a.Decide(Hit("0xAAA", col: 3, row: 2), 0, true);
        var d2 = a.Decide(Hit("0xBBB", col: 3, row: 2), 0, true);
        Assert.True(d1.Admitted);
        Assert.True(d2.Admitted);
        Assert.Equal(6, d1.SpecIndices.Count);           // floater + element-capable phase primitives
        Assert.Single(d2.SpecIndices);                   // floater only — same-cell accents collapse
        Assert.Equal(0, d2.SpecIndices[0]);
    }

    [Fact]
    public void Global_per_tick_cap_drops_the_33rd_cue()
    {
        var a = NewAdmission();
        a.BeginTick(0);
        for (var i = 0; i < VfxRules.GlobalCuePerTickCap; i++)
            Assert.True(a.Decide(Hit("0x" + i), 0, true).Admitted);
        var d = a.Decide(Hit("0xLAST"), 0, true);
        Assert.False(d.Admitted);
        Assert.Equal(VfxSkipReasons.Cap, d.Reason);
        // next tick resets the counter
        a.BeginTick(1);
        Assert.True(a.Decide(Hit("0xNEXT"), 1, true).Admitted);
    }

    [Fact]
    public void Cell_anchored_probe_rate_limits_per_cell()
    {
        var a = NewAdmission();
        a.BeginTick(0);
        var probe = new VfxCueDto { CueId = VfxCueIds.DebugProbe, Col = 1, Row = 1 };
        Assert.True(a.Decide(probe, 0.00, true).Admitted);
        Assert.False(a.Decide(probe, 0.10, true).Admitted);
        Assert.True(a.Decide(probe, 0.20, true).Admitted);
        var otherCell = new VfxCueDto { CueId = VfxCueIds.DebugProbe, Col = 2, Row = 1 };
        Assert.True(a.Decide(otherCell, 0.20, true).Admitted);
    }
}
