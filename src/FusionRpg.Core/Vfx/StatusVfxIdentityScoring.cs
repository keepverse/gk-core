namespace FusionRpg.Core.Vfx;

/// <summary>
/// Static predicted distinguishability for custom status VFX audits.
/// Human LIVE trials override these scores when available.
/// </summary>
public static class StatusVfxIdentityScoring
{
    public enum GlanceVerdict { Pass, Conditional, Fail }

    public sealed record StatusScore(
        string StatusId,
        GlanceVerdict ApplyMoment,
        GlanceVerdict SustainIdle,
        GlanceVerdict SustainGlance,
        GlanceVerdict UnderStress,
        string Rationale);

    public static StatusScore Score(string statusId)
    {
        var sig = StatusVfxIdentity.Signature(statusId);
        var grammarPeers = StatusVfxIdentity.AllCustomSignatures()
            .Count(s => s.MotionGrammarKey == sig.MotionGrammarKey) - 1;

        var hasMarker = sig.MarkerShape.HasValue;
        var uniqueMotion = grammarPeers == 0;

        var apply = sig.ApplyBurstKey != StatusVfxIdentity.DefaultApplyBurstKey
            ? GlanceVerdict.Conditional
            : GlanceVerdict.Fail; // default template is color-only
        var idle = uniqueMotion ? GlanceVerdict.Pass
            : hasMarker ? GlanceVerdict.Pass
            : grammarPeers >= 2 ? GlanceVerdict.Conditional
            : GlanceVerdict.Conditional;
        var glance = uniqueMotion ? GlanceVerdict.Pass
            : hasMarker ? GlanceVerdict.Pass
            : GlanceVerdict.Fail;
        var stress = hasMarker ? GlanceVerdict.Pass
            : uniqueMotion ? GlanceVerdict.Pass
            : GlanceVerdict.Conditional;

        if (statusId is "pact_mark" or "command" or "expose" or "bond")
        {
            idle = GlanceVerdict.Pass;
            glance = GlanceVerdict.Pass;
        }

        var rationale = uniqueMotion
            ? "Unique motion grammar (" + sig.AuraStyle + ")."
            : hasMarker
                ? "Motion cluster peer but marker shape breaks confusion."
                : "Shares " + sig.MotionGrammarKey + " with " + grammarPeers + " others — color/tint only.";

        return new StatusScore(statusId, apply, idle, glance, stress, rationale);
    }

    public static IReadOnlyList<StatusScore> AllScores() =>
        StatusVfxIdentity.CustomIds.Select(Score).ToList();

    /// <summary>Pairwise predicted confusion risk for forced-choice prioritization.</summary>
    public static string PairRisk(string a, string b)
    {
        var sa = StatusVfxIdentity.Signature(a);
        var sb = StatusVfxIdentity.Signature(b);
        if (sa.FullKey == sb.FullKey) return "critical";
        if (sa.StructuralKey == sb.StructuralKey) return "critical";
        if (sa.MotionGrammarKey != sb.MotionGrammarKey) return "low";
        if (sa.MarkerShape != sb.MarkerShape &&
            (sa.MarkerShape.HasValue || sb.MarkerShape.HasValue)) return "medium";
        if (sa.MotionGrammarKey is "Drip" or "CrackleJitter" or "Orbit") return "high";
        return "medium"; // same grammar, no marker differentiation
    }
}
