namespace FusionRpg.Core.Vfx;

/// <summary>Maps aura motion styles to unit-frame anchor kinds — identity batches 1–5.</summary>
public static class VfxAnchorCatalog
{
    public static VfxAnchorKind AnchorKindFor(VfxAuraStyle style) => style switch
    {
        VfxAuraStyle.Drip => VfxAnchorKind.Feet,
        VfxAuraStyle.WispOut => VfxAnchorKind.Feet,
        VfxAuraStyle.BubbleRise => VfxAnchorKind.Feet,
        VfxAuraStyle.ChunkFall => VfxAnchorKind.Feet,
        VfxAuraStyle.StreamOut => VfxAnchorKind.Feet,
        VfxAuraStyle.PactFootPulse => VfxAnchorKind.Feet,
        VfxAuraStyle.Orbit => VfxAnchorKind.Body,
        VfxAuraStyle.CrackleJitter => VfxAnchorKind.Body,
        VfxAuraStyle.SparkStrobe => VfxAnchorKind.Body,
        VfxAuraStyle.ShardGlitter => VfxAnchorKind.Body,
        VfxAuraStyle.SporeDrift => VfxAnchorKind.Body,
        VfxAuraStyle.CharmHeartbeat => VfxAnchorKind.Body,
        VfxAuraStyle.PulseRing => VfxAnchorKind.Body,
        VfxAuraStyle.CommandCrownPulse => VfxAnchorKind.Crown,
        VfxAuraStyle.RiseSparkle => VfxAnchorKind.Crown,
        _ => VfxAnchorKind.Body
    };
}
