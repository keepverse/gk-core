using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects;

public enum RpgEffectFamily
{
    Modifier,
    Mutation
}

/// <summary>Secondary enqueue envelope. Typed EnqueueModifier / EnqueueMutation remain the primary APIs.</summary>
public sealed class RpgEffectEvent
{
    public RpgEffectFamily Family { get; init; }
    public string? GrantId { get; init; }
    public string? EffectId { get; init; }
    public string? PluginId { get; init; }
    public string? OwnerKey { get; init; }
    public string? TargetKey { get; init; }
    public long Amount { get; init; }
    public string Channel { get; init; } = "hp";
    public string? Mode { get; init; }
    public Dictionary<string, object?>? Overlay { get; init; }
}
