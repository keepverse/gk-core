using FusionRpg.Core.Diagnostics;
using Xunit;

namespace FusionRpg.Core.Tests.Diagnostics;

// PerfProbe is process-global and other test classes run in parallel (StatSystem/EffectBag
// tests record stats.resolve / grants.scan concurrently). These tests therefore only assert
// on sections no other Core test records: loop.tick, board.capture, fx.show, takeDamage.prefix.
[Collection("PerfProbe")]
public class PerfProbeTests
{
    static Dictionary<string, object>? Section(Dictionary<string, object> window, string name)
    {
        var sections = Assert.IsType<Dictionary<string, object>>(window["sections"]);
        return sections.TryGetValue(name, out var s) ? (Dictionary<string, object>)s : null;
    }

    [Fact]
    public void Measure_records_count_and_time()
    {
        PerfProbe.ResetAll();
        using (PerfProbe.Measure(PerfSection.BoardCapture)) { }
        using (PerfProbe.Measure(PerfSection.BoardCapture)) { }

        var window = PerfProbe.SnapshotAndReset();
        var s = Section(window, "board.capture");
        Assert.NotNull(s);
        Assert.Equal(2L, s!["count"]);
        Assert.True((double)s["totalMs"] >= 0);
        Assert.True((double)s["maxMs"] >= 0);
    }

    [Fact]
    public void Snapshot_resets_counters()
    {
        PerfProbe.ResetAll();
        using (PerfProbe.Measure(PerfSection.TakeDamagePrefix)) { }
        var first = PerfProbe.SnapshotAndReset();
        Assert.NotNull(Section(first, "takeDamage.prefix"));

        var second = PerfProbe.SnapshotAndReset();
        Assert.Null(Section(second, "takeDamage.prefix"));
    }

    [Fact]
    public void Sections_with_no_activity_are_omitted()
    {
        PerfProbe.ResetAll();
        using (PerfProbe.Measure(PerfSection.FxShow)) { }

        var window = PerfProbe.SnapshotAndReset();
        Assert.NotNull(Section(window, "fx.show"));
        Assert.Null(Section(window, "loop.tick"));
        Assert.Null(Section(window, "board.capture"));
    }

    [Fact]
    public void CountEmit_classifies_kinds()
    {
        PerfProbe.ResetAll();
        PerfProbe.CountEmit("combat.hit");
        PerfProbe.CountEmit("combat.hitland");
        PerfProbe.CountEmit("debug.combat.overlay");
        PerfProbe.CountEmit("zombie.damage");
        PerfProbe.CountEmit("bullet.init");
        PerfProbe.CountEmit(null);

        var window = PerfProbe.SnapshotAndReset();
        var emits = Assert.IsType<Dictionary<string, object>>(window["emits"]);
        Assert.Equal(2L, emits["combat"]);
        Assert.Equal(1L, emits["debug"]);
        Assert.Equal(1L, emits["damage"]);
        Assert.Equal(2L, emits["other"]);
    }

    [Fact]
    public void RecordFrame_buckets_by_duration()
    {
        PerfProbe.ResetAll();
        PerfProbe.RecordFrame(0.008f);  // 120fps-class
        PerfProbe.RecordFrame(0.016f);  // 60fps-class
        PerfProbe.RecordFrame(0.030f);  // slow
        PerfProbe.RecordFrame(0.050f);  // spike
        PerfProbe.RecordFrame(0f);      // ignored
        PerfProbe.RecordFrame(-1f);     // ignored

        var window = PerfProbe.SnapshotAndReset();
        var frames = Assert.IsType<Dictionary<string, object>>(window["frames"]);
        Assert.Equal(4L, frames["total"]);
        Assert.Equal(1L, frames["lt8ms"]);
        Assert.Equal(1L, frames["lt17ms"]);
        Assert.Equal(1L, frames["lt33ms"]);
        Assert.Equal(1L, frames["gte33ms"]);
        Assert.True((double)frames["maxMs"] >= 49.0);
    }

    [Fact]
    public void Disabled_probe_records_nothing()
    {
        PerfProbe.ResetAll();
        var was = PerfProbe.Enabled;
        try
        {
            PerfProbe.Enabled = false;
            using (PerfProbe.Measure(PerfSection.LoopTick)) { }
            PerfProbe.CountEmit("combat.hit");
            PerfProbe.RecordFrame(0.016f);
        }
        finally
        {
            PerfProbe.Enabled = was;
        }

        var window = PerfProbe.SnapshotAndReset();
        Assert.Null(Section(window, "loop.tick"));
        var emits = Assert.IsType<Dictionary<string, object>>(window["emits"]);
        Assert.Empty(emits);
        var frames = Assert.IsType<Dictionary<string, object>>(window["frames"]);
        Assert.Equal(0L, frames["total"]);
    }

    [Fact]
    public void Window_reports_gc_and_time()
    {
        PerfProbe.ResetAll();
        var window = PerfProbe.SnapshotAndReset();
        Assert.True(window.ContainsKey("t"));
        Assert.True(window.ContainsKey("windowMs"));
        var gc = Assert.IsType<Dictionary<string, object>>(window["gc"]);
        Assert.True(gc.ContainsKey("gen0"));
        Assert.True(gc.ContainsKey("allocKb"));
    }

    /// <summary>
    /// The three-way agreement the `SectionCount` comment asserts in prose: the enum's members, the
    /// `SectionNames` array and the structural bound must all describe the SAME set. It is a CONTRACT,
    /// not a population count — `PerfSection` is a closed vocabulary this code owns, and a member added
    /// without its name (or past the bound, which `PerfProbe.Measure` silently drops) is the defect.
    ///
    /// <para>Recorded rather than read: `SnapshotAndReset` omits a section with no count, so the key set
    /// is only complete once every member has been measured once. Parallel writers in this collection
    /// are serialized by the attribute and can only ADD counts to sections already in this set.</para>
    /// </summary>
    [Fact]
    public void PerfSections_match_the_enum_the_names_and_this_bound()
    {
        PerfProbe.ResetAll();

        var members = Enum.GetValues<PerfSection>();
        foreach (var member in members)
            using (PerfProbe.Measure(member)) { }

        var window = PerfProbe.SnapshotAndReset();
        var sections = Assert.IsType<Dictionary<string, object>>(window["sections"]);

        // One name per member, and no member silently past the bound (Measure returns early past it).
        Assert.Equal(members.Length, sections.Count);

        // combat-ai CAI1.14's residue and CAI4.7's line, named rather than counted.
        Assert.NotNull(Section(window, "ai.decide"));
        Assert.NotNull(Section(window, "lawn.ai.decide"));
    }
}
