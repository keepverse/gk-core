namespace FusionRpg.Contracts;

/// <summary>
/// Semantic VFX cue — presentation only, never writes gameplay state (vfx-ssot.md §5).
/// Carries what happened, never how it looks; visuals live in the Core VfxCatalog.
/// </summary>
public sealed class VfxCueDto
{
    public string CueId { get; set; } = "";

    // Primary target anchor: precedence TargetPtr > Cell > World (vfx-ssot.md §5).
    public string? TargetPtr { get; set; }
    /// <summary>Optional semantic origin for source-attached/travel presentation. It never changes gameplay.</summary>
    public string? SourcePtr { get; set; }
    public int? Col { get; set; }
    public int? Row { get; set; }
    public float? WorldX { get; set; }
    public float? WorldY { get; set; }

    public long Amount { get; set; }
    public DamageFxTag? Tag { get; set; }

    /// <summary>Element coloring payload (vfx-ssot.md §16); reuses the combat contract type.</summary>
    public List<ElementPayloadComponentDto>? Elements { get; set; }

    public float ScaleMul { get; set; } = 1f;
    public float LifeMul { get; set; } = 1f;

    /// <summary>Sustained-visual TTL basis (SPEC vfx-v3): status duration in ms; 0 = unknown/infinite.</summary>
    public int DurationMs { get; set; }
}
