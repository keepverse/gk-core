namespace FusionRpg.Core.Vfx;

/// <summary>Pure span basis for world VFX — max(cell, sprite bounds) × tunables. spec-unit-frame.md.</summary>
public static class VfxSpanMath
{
    // Structural, not tunable (tunables-ssot T2): a presentation floor so a VFX on a sub-cell sprite is
    // still visible. The per-recipe size knobs a designer turns (spanScale, recipeSizeScale) are the
    // parameters of ComputeSpan; this only stops the basis collapsing to nothing. Not gameplay.
    public const float MinSpan = 0.35f;

    public static float ComputeSpan(
        float cellSpan, float boundsWidth, float boundsHeight, bool hasBounds,
        float spanScale, float recipeSizeScale)
    {
        var basis = cellSpan < MinSpan ? MinSpan : cellSpan;
        if (hasBounds)
        {
            var boundsMax = MathF.Max(boundsWidth, boundsHeight);
            if (boundsMax > basis) basis = boundsMax;
        }

        if (spanScale <= 0f) spanScale = 1f;
        if (recipeSizeScale <= 0f) recipeSizeScale = 1f;
        return basis * spanScale * recipeSizeScale;
    }
}
