using System.Collections.Concurrent;
using FusionRpg.Contracts;

namespace FusionRpg.Server;

/// <summary>
/// Hot-session snapshot for one UniqueActor: live statuses + shield layers (drain order).
/// Filled by Injector via <c>POST /api/internal/actors/{id}/live-state</c>; read by
/// <see cref="UniqueActorHubCompose.ProjectSheet"/>. Cold actors have no bag entry.
/// </summary>
public sealed class ActorLiveState
{
    public IReadOnlyList<ActorStatusGlyphDto> LiveStatuses { get; init; } =
        Array.Empty<ActorStatusGlyphDto>();

    public IReadOnlyList<ActorShieldLayerDto> ShieldLayers { get; init; } =
        Array.Empty<ActorShieldLayerDto>();
}

public interface IActorLiveStateStore
{
    ActorLiveState? Get(string instanceId);
    void Upsert(string instanceId, ActorLiveState state);
    void Clear(string instanceId);
}

/// <summary>Thread-safe in-memory Hot live bag keyed by UniqueActor instanceId.</summary>
public sealed class ActorLiveStateStore : IActorLiveStateStore
{
    readonly ConcurrentDictionary<string, ActorLiveState> _byInstance =
        new(StringComparer.OrdinalIgnoreCase);

    public ActorLiveState? Get(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        return _byInstance.TryGetValue(instanceId.Trim(), out var state) ? state : null;
    }

    public void Upsert(string instanceId, ActorLiveState state)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("instanceId required", nameof(instanceId));
        ArgumentNullException.ThrowIfNull(state);
        _byInstance[instanceId.Trim()] = state;
    }

    public void Clear(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return;
        _byInstance.TryRemove(instanceId.Trim(), out _);
    }
}
