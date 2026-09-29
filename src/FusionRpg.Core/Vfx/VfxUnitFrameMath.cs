namespace FusionRpg.Core.Vfx;

/// <summary>Pure world anchor pick from a resolved unit frame snapshot — no Unity types.</summary>
public static class VfxUnitFrameMath
{
    public static float WorldX(float pivotX, float boundsCenterX, bool hasBounds) =>
        hasBounds ? boundsCenterX : pivotX;

    public static float WorldY(
        float laneY, float boundsCenterY, float halfCell, float boundsHalfHeight,
        bool hasBounds, VfxAnchorKind kind) =>
        kind switch
        {
            VfxAnchorKind.Feet => laneY,
            VfxAnchorKind.Body => hasBounds ? boundsCenterY : laneY + halfCell,
            VfxAnchorKind.Crown => hasBounds
                ? boundsCenterY + boundsHalfHeight * 0.65f
                : laneY + halfCell * 1.2f,
            VfxAnchorKind.Cell => boundsCenterY,
            _ => laneY
        };
}
