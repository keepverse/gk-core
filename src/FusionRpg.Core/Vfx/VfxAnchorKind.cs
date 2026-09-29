namespace FusionRpg.Core.Vfx;

/// <summary>Vertical anchor baseline for unit-attached VFX — spec-unit-frame.md.</summary>
public enum VfxAnchorKind
{
    /// <summary>Lane ground line — feet rings, drip clusters, ground debuffs.</summary>
    Feet = 0,
    /// <summary>Sprite bounds center — orbit, crackle, mid-body reads.</summary>
    Body = 1,
    /// <summary>Upper sprite band; particle grammar adds further upward bias.</summary>
    Crown = 2,
    /// <summary>Cell center — non-unit cues (bursts at col/row).</summary>
    Cell = 3
}
