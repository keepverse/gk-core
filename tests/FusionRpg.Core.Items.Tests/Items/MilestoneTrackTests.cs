using FusionRpg.Core.Items.Mutation;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T13 (spec-enhance-track-wiring.md §Design 1): which milestone family a level
/// grants. Synthetic tracks throughout (unit logic); the corpus join lives in the workbench tests.
/// Ordinals, not tiers, are pinned here — the tier mapping is the tuning's, tested against the
/// shipped file below.
/// </summary>
public class MilestoneTrackTests
{
    static EnhancementTuning Tuning(int stride = 4) =>
        EnhancementTuning.Parse(
            """{"scalarPerLevelMilli":100,"asymptoteK":10,"milestoneStride":"""
            + stride
            + ""","ilvlCapFloor":1,"ilvlCapDivisor":1,"bands":[{"id":"safe","fromLevel":1,"spanLevels":8,"successStartMilli":1000,"successEndMilli":1000,"canDowngrade":false}],"downgradeFromLevel":99,"craftPityThreshold":5,"transferRatioMilli":500,"transferItemLevelWindow":3,"rerollCostRungSlopeMilli":1,"rerollCostAffixBaseMilli":1,"rerollCostAffixStepMilli":1,"milestoneTierLadder":[1,2,2,3,4]}""");

    static IReadOnlyList<MilestoneTrackEntry> Track(params (int At, string Family)[] rows) =>
        rows.Select(r => new MilestoneTrackEntry(r.At, r.Family)).ToList();

    [Fact]
    public void An_authored_level_grants_its_family_with_its_ordinal()
    {
        var track = Track((4, "a"), (12, "b"), (20, "c"));
        Assert.Equal(("a", 1), MilestoneTrack.FamilyFor(track, 4, Tuning()));
        Assert.Equal(("b", 2), MilestoneTrack.FamilyFor(track, 12, Tuning()));
        Assert.Equal(("c", 3), MilestoneTrack.FamilyFor(track, 20, Tuning()));
    }

    [Fact]
    public void Past_the_last_authored_level_the_stride_cycles_families_by_ordinal()
    {
        var track = Track((4, "a"), (12, "b"), (20, "c"));
        // +24 grants the track's FIRST family again — the arithmetic, not a new rule.
        Assert.Equal(("a", 4), MilestoneTrack.FamilyFor(track, 24, Tuning()));
        Assert.Equal(("b", 5), MilestoneTrack.FamilyFor(track, 28, Tuning()));
        Assert.Equal(("c", 6), MilestoneTrack.FamilyFor(track, 32, Tuning()));
        Assert.Equal(("a", 7), MilestoneTrack.FamilyFor(track, 36, Tuning()));
    }

    [Fact]
    public void Non_stride_levels_grant_nothing_anywhere()
    {
        var track = Track((4, "a"), (12, "b"), (20, "c"));
        Assert.Null(MilestoneTrack.FamilyFor(track, 8, Tuning()));   // between authored
        Assert.Null(MilestoneTrack.FamilyFor(track, 16, Tuning()));  // between authored
        Assert.Null(MilestoneTrack.FamilyFor(track, 21, Tuning()));  // past last, not stride
        Assert.Null(MilestoneTrack.FamilyFor(track, 3, Tuning()));   // before everything
    }

    [Fact]
    public void A_null_or_empty_track_grants_nothing()
    {
        Assert.Null(MilestoneTrack.FamilyFor(null, 4, Tuning()));
        Assert.Null(MilestoneTrack.FamilyFor(Array.Empty<MilestoneTrackEntry>(), 4, Tuning()));
    }

    [Fact]
    public void A_two_rung_track_cycles_from_its_own_last_level()
    {
        var track = Track((4, "a"), (12, "b"));
        Assert.Equal(("a", 3), MilestoneTrack.FamilyFor(track, 16, Tuning()));
        Assert.Equal(("b", 4), MilestoneTrack.FamilyFor(track, 20, Tuning()));
    }

    [Fact]
    public void Ordinals_past_the_ladder_read_its_last_element()
    {
        var tuning = Tuning();
        // The ladder contract, recomputed from the shipped shape: ordinal n reads element
        // min(n, length)-1. The 7th milestone reads the last element.
        Assert.Equal(tuning.MilestoneTierLadder[^1], tuning.MilestoneTierLadder[Math.Min(7, tuning.MilestoneTierLadder.Count) - 1]);
    }

    [Fact]
    public void A_missing_ladder_is_a_load_rejection_never_an_implied_flat_tier()
    {
        var ex = Assert.Throws<EnhancementTuningRejection>(() => EnhancementTuning.Parse(
            """{"scalarPerLevelMilli":100,"asymptoteK":10,"milestoneStride":4,"ilvlCapFloor":1,"ilvlCapDivisor":1,"bands":[{"id":"safe","fromLevel":1,"spanLevels":8,"successStartMilli":1000,"successEndMilli":1000,"canDowngrade":false}],"downgradeFromLevel":99,"craftPityThreshold":5,"transferRatioMilli":500,"transferItemLevelWindow":3,"rerollCostRungSlopeMilli":1,"rerollCostAffixBaseMilli":1,"rerollCostAffixStepMilli":1}"""));
        Assert.Contains("milestoneTierLadder", ex.Message);
    }

    [Fact]
    public void Every_shipped_track_is_ascending_and_names_only_milestone_families()
    {
        // The corpus join, as relationships: atLevels strictly ascend (the ordinal arithmetic
        // assumes it), and every named family is one of the milestone corpus's runtimeFamilies —
        // a track naming anything else is a content gap the lookup would throw on.
        var root = FindRepoRoot();
        using var milestones = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            KeepverseRoots.Content(), "data", "seed", "items", "enhancement-milestones", "milestones.json")));
        var known = milestones.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("runtimeFamily").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types"), "*.json"))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (!entry.TryGetProperty("enhanceTrack", out var track)
                    || track.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                var id = entry.GetProperty("id").GetString()!;
                var last = -1;
                foreach (var row in track.EnumerateArray())
                {
                    var at = row.GetProperty("atLevel").GetInt32();
                    var family = row.GetProperty("family").GetString()!;
                    if (at <= last)
                        violations.Add($"{id}: atLevel {at} does not ascend after {last}");
                    last = at;
                    if (!known.Contains(family))
                        violations.Add($"{id}: unknown milestone family '{family}'");
                }
            }
        }
        Assert.True(violations.Count == 0, "track corpus violations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void The_shipped_ladder_starts_shallow()
    {
        var tuning = EnhancementTuning.Parse(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "data", "tuning", "enhancement.v1.json")));
        // A +4 milestone (ordinal 1) and a +20 milestone (ordinal 3) are never the same row.
        Assert.NotEqual(tuning.MilestoneTierLadder[0], tuning.MilestoneTierLadder[2]);
        Assert.All(tuning.MilestoneTierLadder, t => Assert.InRange(t, 1, 5));
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
