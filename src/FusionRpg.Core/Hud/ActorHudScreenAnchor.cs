namespace FusionRpg.Core.Hud;

/// <summary>Visible actor footprint in Unity screen pixels, using Unity's bottom-left screen origin.</summary>
public readonly record struct ActorHudScreenRect(float Left, float Bottom, float Width, float Height)
{
    public float CenterX => Left + Width * 0.5f;
    public bool IsVisual => Width > 0f && Height > 0f;
}

/// <summary>
/// Top-centred HUD root in screen pixels. Children lay out toward negative local Y, so the complete
/// HUD remains below the actor rather than growing back into its silhouette.
/// </summary>
public readonly record struct ActorHudScreenAnchor(float CenterX, float TopY, float Width);

/// <summary>Pure presentation geometry; Unity projects the actor footprint, this type places its HUD.</summary>
public static class ActorHudScreenAnchorMath
{
    public static bool TryResolve(
        ActorHudScreenRect visual,
        float gapPixels,
        float widthFactor,
        float minWidthPixels,
        float maxWidthPixels,
        out ActorHudScreenAnchor anchor)
    {
        anchor = default;
        if (!visual.IsVisual || gapPixels < 0f || widthFactor <= 0f || minWidthPixels <= 0f || maxWidthPixels < minWidthPixels)
            return false;

        var width = Math.Clamp(visual.Width * widthFactor, minWidthPixels, maxWidthPixels);
        anchor = new ActorHudScreenAnchor(visual.CenterX, visual.Bottom - gapPixels, width);
        return true;
    }
}

/// <summary>
/// Pure local-space row geometry for the screen HUD. Every renderer uses this to centre the
/// complete visible group, rather than centring one child and appending the remaining children to
/// its right.
/// </summary>
public static class ActorHudScreenLayoutMath
{
    public static float CenteredPackedStart(float itemWidths, int itemCount, float itemGap)
    {
        if (itemWidths <= 0f) throw new ArgumentOutOfRangeException(nameof(itemWidths));
        if (itemCount <= 0) throw new ArgumentOutOfRangeException(nameof(itemCount));
        if (itemGap < 0f) throw new ArgumentOutOfRangeException(nameof(itemGap));

        return -(itemWidths + (itemCount - 1) * itemGap) * 0.5f;
    }

    public static float TakeItemCenter(ref float left, float width, float itemGap)
    {
        if (width <= 0f) throw new ArgumentOutOfRangeException(nameof(width));
        if (itemGap < 0f) throw new ArgumentOutOfRangeException(nameof(itemGap));

        var center = left + width * 0.5f;
        left += width + itemGap;
        return center;
    }
}
