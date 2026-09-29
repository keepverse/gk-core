namespace FusionRpg.Core.Overlay;

/// <summary>A screen-space rectangle for the Rift menu art. Kept on its own when the old percentage
/// layout (RiftMenuOverlayLayout, the retired IMGUI painter's placement) was deleted as dead code on
/// 2026-09-18: <see cref="RiftMenuPlacement"/> still returns this shape, and the live layout numbers
/// are config (`riftMenu` in gk-core/data/tuning/overlay.v4.json).</summary>
public readonly struct RiftOverlayRect
{
    public RiftOverlayRect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public float X { get; }
    public float Y { get; }
    public float Width { get; }
    public float Height { get; }
}
