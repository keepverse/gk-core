namespace FusionRpg.Contracts;

/// <summary>One GG-49 contribution on a derived (or primary) channel — wire shape for sheet/derived.</summary>
public sealed class ActorContributionDto
{
    public string SourceId { get; init; } = "";
    public string Label { get; init; } = "";
    public string Op { get; init; } = "";
    public double Value { get; init; }
}

/// <summary>One derived channel on the actor sheet projection.</summary>
public sealed class ActorSheetChannelDto
{
    public string ChannelId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Reading { get; init; } = "";
    public string ComposeKind { get; init; } = "";
    /// <summary>
    /// D6 exempt: sheet channel <see cref="Value"/> stays <c>double</c> for now (overflow widen is a
    /// separate Core ticket). Shield HP magnitudes elsewhere stay <c>long</c> — not this DTO.
    /// </summary>
    public double Value { get; init; }
    public IReadOnlyList<ActorContributionDto> Contributions { get; init; } = Array.Empty<ActorContributionDto>();
    /// <summary>UnitClass ledger id from registry/surface (magnitude SSOT).</summary>
    public string UnitClass { get; init; } = "";
    public double DefaultValue { get; init; }
    /// <summary>Null when uncapped (e.g. <c>status.resist.omni</c>).</summary>
    public double? Cap { get; init; }
    /// <summary>D2 authoritative six-state: active|default|capped|stub|no-producer|unregistered.</summary>
    public string RenderState { get; init; } = "";
}

public sealed class ActorElementTypingDto
{
    public string Primary { get; init; } = "";
    public string? Secondary { get; init; }
}

public sealed class ActorStatusGlyphDto
{
    public string StatusId { get; init; } = "";
    public int? RemainingPermille { get; init; }
}

public sealed class ActorResourcePoolDto
{
    public string ResourceId { get; init; } = "";
    public long? Current { get; init; }
    public long? Max { get; init; }
}

public sealed class ActorShieldSummaryDto
{
    public string? ElementId { get; init; }
    public long? Current { get; init; }
    public long? Max { get; init; }
    /// <summary>Layer count when Hot layers are projected (S2); omit/null when no summary.</summary>
    public int? Stacks { get; init; }
}

/// <summary>
/// One drain-order shield layer on the Hot sheet (S1). Order = priority DESC, CreatedSeq ASC.
/// </summary>
public sealed class ActorShieldLayerDto
{
    public string ShieldId { get; init; } = "";
    public string? ElementId { get; init; }
    public long Current { get; init; }
    public long Max { get; init; }
    public int Priority { get; init; }
    public string SourceId { get; init; } = "";
    public bool IsInnate { get; init; }
    public long? RegenPerSecond { get; init; }
    public bool Broken { get; init; }
}

/// <summary>
/// Standing five-axis vector (<c>PowerVector</c> / definitions.md §7) — offense · survivability ·
/// control · utility · economy. Projected on cold UniqueActor sheet via <c>ActorPowerCache.Compose</c>.
/// </summary>
public sealed class ActorStandingDto
{
    public int Offense { get; init; }
    public int Survivability { get; init; }
    public int Control { get; init; }
    public int Utility { get; init; }
    public int Economy { get; init; }
}

/// <summary>
/// Cold UniqueActor identity + Hot Hub resolve with FULL contribution tracing (GG-49).
/// Hub compose only — never a second private fold.
/// </summary>
public sealed class ActorSheetDto
{
    public string InstanceId { get; init; } = "";
    public long PlayerId { get; init; }
    public string Side { get; init; } = "";
    public int TypeId { get; init; }
    public string? DisplayName { get; init; }
    public string? SpeciesId { get; init; }
    public string? SpeciesName { get; init; }
    public string Phase { get; init; } = "";
    public string? RoleLabel { get; init; }
    public long Level { get; init; }
    public long Xp { get; init; }
    public long? XpToNext { get; init; }
    public ActorElementTypingDto? ElementTyping { get; init; }
    public ActorStandingDto? Standing { get; init; }
    public IReadOnlyList<ActorStatusGlyphDto> LiveStatuses { get; init; } = Array.Empty<ActorStatusGlyphDto>();
    public IReadOnlyList<ActorResourcePoolDto> ResourcePools { get; init; } = Array.Empty<ActorResourcePoolDto>();
    public ActorShieldSummaryDto? ShieldSummary { get; init; }
    /// <summary>Hot drain-order layers (S1); cold empty. Filled with <see cref="ShieldSummary"/> in one ProjectSheet call.</summary>
    public IReadOnlyList<ActorShieldLayerDto> ShieldLayers { get; init; } = Array.Empty<ActorShieldLayerDto>();
    public IReadOnlyList<ActorSheetChannelDto> Derived { get; init; } = Array.Empty<ActorSheetChannelDto>();
    public IReadOnlyList<ActorContributionDto> Primary { get; init; } = Array.Empty<ActorContributionDto>();
}
