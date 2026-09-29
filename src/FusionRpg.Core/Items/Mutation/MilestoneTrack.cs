namespace FusionRpg.Core.Items.Mutation;

/// <summary>One authored base-type track entry: at <c>AtLevel</c>, this family.
/// <c>atLevel</c> values are authored ordinals (+4, +12, +20 on a 3-rung track), NOT a stride — the
/// stride only continues the track past its last authored level.</summary>
public sealed record MilestoneTrackEntry(int AtLevel, string Family);

/// <summary>Which milestone family a new level grants (species-gear-chain T13, spec §Design 1):
/// the authored level wins exactly; past the last authored level the stride continues forever,
/// cycling the track's own families by ordinal (+24 grants the track's FIRST family again — the
/// arithmetic, not a new rule). A track is FALLIBLE INPUT, never a closed vocabulary: an empty
/// track grants nothing, levels between authored rungs grant nothing, and non-stride levels past
/// the end grant nothing. Returns the family AND the milestone ordinal (1-based across authored +
/// continued) the tier ladder reads — never the tier itself, which is the tuning's to map.</summary>
public static class MilestoneTrack
{
    public static (string Family, int Ordinal)? FamilyFor(
        IReadOnlyList<MilestoneTrackEntry>? track, int levelAfter, EnhancementTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (track is null || track.Count == 0) return null;

        for (var i = 0; i < track.Count; i++)
            if (track[i].AtLevel == levelAfter)
                return (track[i].Family, i + 1);

        var last = track[^1].AtLevel;
        if (levelAfter <= last) return null;
        if (!EnhancePolicy.IsMilestoneLevel(levelAfter, tuning)) return null;

        var cycleIndex = (levelAfter - last) / tuning.MilestoneStride - 1;
        if (cycleIndex < 0) return null;
        return (track[cycleIndex % track.Count].Family, track.Count + cycleIndex + 1);
    }
}
