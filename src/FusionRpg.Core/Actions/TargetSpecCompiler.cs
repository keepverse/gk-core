using FusionRpg.Contracts;

namespace FusionRpg.Core.Actions;

/// <summary>The two factions a caster may belong to — the index into a compiled per-side spec.</summary>
public enum CasterSide
{
    Plant = 0,
    Zombie = 1,
}

/// <summary>
/// One <see cref="ActionTargetSpec"/>, compiled to what the shipped resolver needs
/// (spec-targeting.md §2, §5). <see cref="IsSelf"/> short-circuits <see cref="PerSide"/> entirely —
/// <c>Self</c> resolves to the caster's own ptr directly and never enters the resolver's pool,
/// because the shipped resolver has no `Self` mode at all.
/// </summary>
public sealed record CompiledTargetSpec(bool IsSelf, TargetSpec[] PerSide);

/// <summary>
/// Compiles the typed authoring contract to the shipped wire DTO — once per caster side, at load,
/// never per resolve call (spec-targeting.md §2). This is the module's highest-value item: one
/// authored row serves both factions because <see cref="ActionRelation"/> is resolved here, not left
/// as the absolute `side` string the shipped resolver's filters actually compare.
/// </summary>
public static class TargetSpecCompiler
{
    public static string SideName(CasterSide side) => side == CasterSide.Plant ? "plant" : "zombie";
    static CasterSide Opposite(CasterSide side) => side == CasterSide.Plant ? CasterSide.Zombie : CasterSide.Plant;

    public static CompiledTargetSpec Compile(ActionTargetSpec spec)
    {
        if (spec.Mode == ActionTargetMode.Self)
            return new CompiledTargetSpec(true, Array.Empty<TargetSpec>());

        return new CompiledTargetSpec(false, new[]
        {
            CompileForSide(spec, CasterSide.Plant),
            CompileForSide(spec, CasterSide.Zombie),
        });
    }

    static TargetSpec CompileForSide(ActionTargetSpec spec, CasterSide caster)
    {
        // Any clears the side filter; Ally and (degenerate) Self read the caster's own side;
        // Enemy reads the opposite side — resolved HERE, once, so the shipped resolver never sees
        // "Enemy", only a concrete "plant" or "zombie".
        string? side = spec.Relation switch
        {
            ActionRelation.Any => null,
            ActionRelation.Enemy => SideName(Opposite(caster)),
            _ => SideName(caster), // Ally, Self
        };

        var filters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (side is not null) filters["side"] = side;

        if (spec.Filters.TypeIds is { Count: > 0 } typeIds)
            filters["typeIdIn"] = typeIds.ToArray();
        if (spec.Filters.ExcludeMindControlled is { } excludeMc)
            filters["excludeMindControlled"] = excludeMc;
        if (spec.Filters.Row is { } row)
            filters["row"] = row;
        if (spec.Filters.ColMin is { } colMin || spec.Filters.ColMax is { } colMax0)
        {
            var colWindow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (spec.Filters.ColMin is { } min) colWindow["min"] = min;
            if (spec.Filters.ColMax is { } max) colWindow["max"] = max;
            filters["col"] = colWindow;
        }

        return new TargetSpec
        {
            Mode = ToWireMode(spec.Mode),
            // The shipped resolver's own "Single" mode means "resolve to a pre-supplied Ptr" — a
            // re-targeting concept this authoring-time spec has no Ptr for. An authored `Single`
            // means "exactly one match from the filtered pool", which is `Multi` with Count forced
            // to 1 regardless of what was authored; `Multi` itself keeps the authored count.
            Count = spec.Mode == ActionTargetMode.Single ? 1 : spec.Count,
            Shape = spec.Shape is { } shape ? ToWireShape(shape) : null,
            Size = spec.Size,
            Width = spec.Width,
            Height = spec.Height,
            AnchorOrigin = null, // rectangle geometry only — this program never authors it
            Filters = filters,
            MaxTargets = spec.MaxTargets,
        };
    }

    static string ToWireMode(ActionTargetMode mode) => mode switch
    {
        ActionTargetMode.Single => TargetModes.Multi,
        ActionTargetMode.Multi => TargetModes.Multi,
        ActionTargetMode.RolledTarget => Combat.TargetModeNames.RolledTarget,
        ActionTargetMode.All => TargetModes.All,
        ActionTargetMode.Area => TargetModes.Area,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Self is handled before reaching the wire mode"),
    };

    static string ToWireShape(ActionAreaShape shape) =>
        Enum.GetName(shape) ?? throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
}
