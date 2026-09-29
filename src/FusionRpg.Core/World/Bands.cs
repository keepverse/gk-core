namespace FusionRpg.Core.World;

/// <summary>
/// base-defense `structure-catalog-import` (module 25, spec-structure-catalog-import.md §2). The
/// ONE place an ordinal becomes a number for structure content — narrower than the spec's own
/// literal text (see that spec's Correction 1): only `strengthBand` has a real, needed resolution
/// today. `reach`/`footprint`/`coverTier`/`costProfile`/`tempo` are real, validated ordinals with no
/// consuming `StructureDef` field yet, so resolving them now would be a converter with no reader.
/// </summary>
public static class Bands
{
    /// <summary>
    /// `strengthBand` (structure-schema's own 3-rung vocabulary) to `StructureDef.MaterialTier`
    /// (decision 32's existing int ladder, 1..3) — feeding straight into the ALREADY-SHIPPED
    /// <see cref="StructurePolicy.TierMultiplierMilli"/>, never a second, parallel string-keyed
    /// multiplier table (that would be a sixth instance of this codebase's own recurring "N synced
    /// lists" bug class). An unauthored ordinal throws — loud over silent, matching every other
    /// catalog rule in this program.
    /// </summary>
    public static int MaterialTierOf(string strengthBand) => strengthBand switch
    {
        "rubble" => 1,
        "timber" => 2,
        "stone" => 3,
        _ => throw new InvalidOperationException(
            $"structure-seed: strengthBand '{strengthBand}' has no material-tier mapping — the "
            + "structure anchor's own vocabulary is rubble/timber/stone, nothing else."),
    };
}
