using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

public static class TargetModes
{
    public const string EventTarget = "EventTarget";
    public const string Actor = "Actor";
    public const string Selected = "Selected";
    public const string Single = "Single";
    public const string Multi = "Multi";
    public const string Random = "Random";
    public const string Area = "Area";
    public const string All = "All";
}

public static class AreaShapes
{
    public const string Row = "Row";
    public const string Column = "Column";
    public const string Square = "Square";
    public const string Rectangle = "Rectangle";
}

public static class DeliveryModes
{
    public const string Instant = "Instant";
    public const string OverTime = "OverTime";
    public const string Counter = "Counter";
}

public static class CounterScopes
{
    public const string Target = "Target";
    public const string Actor = "Actor";
}

public static class AnchorOrigins
{
    public const string Corner = "Corner";
    public const string Center = "Center";
}

/// <summary>Who receives an overlay HP change. Orthogonal to <see cref="DeliverySpec"/>.</summary>
public sealed class TargetSpec
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = TargetModes.EventTarget;
    [JsonPropertyName("ptr")] public string? Ptr { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("shape")] public string? Shape { get; set; }
    [JsonPropertyName("size")] public int? Size { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("anchor")] public object? Anchor { get; set; }
    [JsonPropertyName("anchorOrigin")] public string? AnchorOrigin { get; set; }
    [JsonPropertyName("filters")] public Dictionary<string, object?>? Filters { get; set; }
    [JsonPropertyName("maxTargets")] public int? MaxTargets { get; set; }
}

/// <summary>When / how often a <see cref="DamagePacket"/> applies.</summary>
public sealed class DeliverySpec
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = DeliveryModes.Instant;
    [JsonPropertyName("periodMs")] public int? PeriodMs { get; set; }
    [JsonPropertyName("durationMs")] public int? DurationMs { get; set; }
    [JsonPropertyName("tickBudget")] public int? TickBudget { get; set; }
    [JsonPropertyName("everyHits")] public int? EveryHits { get; set; }
    [JsonPropertyName("resetOnBurst")] public bool ResetOnBurst { get; set; } = true;
    [JsonPropertyName("counterScope")] public string? CounterScope { get; set; }
}

/// <summary>One weighted element in a hybrid overlay damage payload.</summary>
public sealed class ElementPayloadComponentDto
{
    [JsonPropertyName("element")] public string Element { get; set; } = "";
    [JsonPropertyName("weight")] public double Weight { get; set; }
}

/// <summary>Debug breakdown from overlay combat calculator — combat-damage-ssot.md §4.2.</summary>
public sealed class OverlayCombatBreakdown
{
    [JsonPropertyName("hit")] public bool Hit { get; init; }
    [JsonPropertyName("crit")] public bool Crit { get; init; }
    [JsonPropertyName("parried")] public bool Parried { get; init; }
    [JsonPropertyName("blocked")] public bool Blocked { get; init; }
    [JsonPropertyName("matchupBonus")] public double MatchupBonus { get; init; }
    [JsonPropertyName("weightedDelta")] public double WeightedDelta { get; init; }
    [JsonPropertyName("powerAdjustedDamage")] public double PowerAdjustedDamage { get; init; }
    [JsonPropertyName("finalSignedDelta")] public long FinalSignedDelta { get; init; }
    [JsonPropertyName("pHitFinal")] public double PHitFinal { get; init; }
    [JsonPropertyName("pCritFinal")] public double PCritFinal { get; init; }
    [JsonPropertyName("critMultiplierFinal")] public double CritMultiplierFinal { get; init; }
}

/// <summary>
/// Planning DTO for overlay HP changes. Signed amount: negative = loss, positive = heal.
/// Runtime still emits Funnel mutations — this is not a second mailbox.
/// </summary>
public sealed class DamagePacket
{
    [JsonPropertyName("packetId")] public string PacketId { get; set; } = "";
    [JsonPropertyName("sourceGrantId")] public string SourceGrantId { get; set; } = "";
    [JsonPropertyName("effectId")] public string? EffectId { get; set; }
    [JsonPropertyName("pluginId")] public string? PluginId { get; set; }
    [JsonPropertyName("actorPtr")] public string? ActorPtr { get; set; }
    [JsonPropertyName("target")] public TargetSpec Target { get; set; } = new();
    [JsonPropertyName("delivery")] public DeliverySpec Delivery { get; set; } = new();
    [JsonPropertyName("signedAmount")] public long SignedAmount { get; set; }
    [JsonPropertyName("chainDepth")] public int ChainDepth { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "hp";
    [JsonPropertyName("fxTag")] public DamageFxTag? FxTag { get; set; }
    [JsonPropertyName("tick")] public long Tick { get; set; }
    [JsonPropertyName("burst")] public DamagePacket? Burst { get; set; }
    [JsonPropertyName("procDepthLimit")] public int? ProcDepthLimit { get; set; }
    [JsonPropertyName("elementPayload")] public List<ElementPayloadComponentDto>? ElementPayload { get; set; }
}
