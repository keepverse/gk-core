using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Creatures.Layers;

/// <summary>
/// species-progression module 6 (`species-layer-delivery`) step 6.2, Transport (SP6.3) — the ONE wire
/// shape a <see cref="ProjectedLayerRow"/> serializes to over HTTP (`GET /api/aptitudes/{playerId}`'s
/// <c>speciesLayers</c> field, `spec-species-layer-delivery.md`'s own "Transport" section) and parses
/// back from (SP6.4, the injector fetch). <c>System.Text.Json</c> has no built-in polymorphic support
/// for <see cref="LayerValue"/>'s two subtypes without a custom converter, so this flat DTO carries an
/// explicit <see cref="Kind"/> discriminator instead of leaning on one — the simplest shape both sides
/// of the wire agree on without either owning a converter the other has to match by hand.
/// </summary>
public sealed record ProjectedLayerRowDto(
    string Channel, string Op, string Kind, double? Amount, long? KMicro, string SourceId);

public static class ProjectedLayerRowJson
{
    public static ProjectedLayerRowDto ToWire(ProjectedLayerRow row) => row.Value switch
    {
        LayerValue.Fixed f => new ProjectedLayerRowDto(row.Channel, OpToken(row.Op), "fixed", f.Amount, null, row.SourceId),
        LayerValue.LadderMicro l => new ProjectedLayerRowDto(row.Channel, OpToken(row.Op), "ladderMicro", null, l.KMicro, row.SourceId),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row.Value, "unknown LayerValue subtype"),
    };

    public static ProjectedLayerRow FromWire(ProjectedLayerRowDto dto)
    {
        if (dto is null) throw new ArgumentNullException(nameof(dto));
        if (!AtomDerivedSubsystem.TryParseOp(dto.Op, out var op))
            throw new ArgumentException($"unknown op '{dto.Op}'", nameof(dto));

        LayerValue value = dto.Kind switch
        {
            "fixed" => new LayerValue.Fixed(
                dto.Amount ?? throw new ArgumentException("a 'fixed' row must carry amount", nameof(dto))),
            "ladderMicro" => new LayerValue.LadderMicro(
                dto.KMicro ?? throw new ArgumentException("a 'ladderMicro' row must carry kMicro", nameof(dto))),
            _ => throw new ArgumentException($"unknown kind '{dto.Kind}'", nameof(dto)),
        };
        return new ProjectedLayerRow(dto.Channel, op, value, dto.SourceId);
    }

    static string OpToken(DerivedModifierOp op) => op switch
    {
        DerivedModifierOp.Flat => "flat",
        DerivedModifierOp.Increased => "increased",
        DerivedModifierOp.Replace => "replace",
        DerivedModifierOp.Flag => "flag",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "unknown DerivedModifierOp"),
    };
}
