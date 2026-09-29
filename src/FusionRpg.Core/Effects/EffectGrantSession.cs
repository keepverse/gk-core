using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects;

/// <summary>
/// Server-side session snapshot of Hot Effect grants (W0-E).
/// Cold SSOT for reconnect rehydrate until ActiveBound loadouts (W5+).
/// </summary>
public sealed class EffectGrantSession
{
    readonly object _gate = new();
    readonly Dictionary<string, EffectGrantDto> _byId = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    public void Upsert(EffectGrantDto dto)
    {
        if (dto == null) throw new ArgumentNullException(nameof(dto));
        if (string.IsNullOrWhiteSpace(dto.GrantId))
            throw new ArgumentException("grantId required", nameof(dto));
        if (string.IsNullOrWhiteSpace(dto.EffectId))
            throw new ArgumentException("effectId required", nameof(dto));

        lock (_gate)
            _byId[dto.GrantId.Trim()] = Clone(dto);
    }

    public bool Remove(string grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId)) return false;
        lock (_gate)
            return _byId.Remove(grantId.Trim());
    }

    public void Clear()
    {
        lock (_gate)
            _byId.Clear();
    }

    public IReadOnlyList<EffectGrantDto> Snapshot()
    {
        lock (_gate)
            return _byId.Values.Select(Clone).ToList();
    }

    static EffectGrantDto Clone(EffectGrantDto dto) => new()
    {
        GrantId = dto.GrantId,
        EffectId = dto.EffectId,
        OwnerKind = dto.OwnerKind,
        OwnerKey = dto.OwnerKey,
        PluginId = dto.PluginId,
        Priority = dto.Priority,
        Overlay = dto.Overlay == null
            ? null
            : new Dictionary<string, object?>(dto.Overlay, StringComparer.OrdinalIgnoreCase)
    };
}

/// <summary>Builds injector <c>effects.grants.apply</c> payload for Hello rehydrate.</summary>
public static class EffectGrantRehydrate
{
    public const string ApplyCommandName = "effects.grants.apply";

    /// <returns>Payload object, or null when there is nothing to push.</returns>
    public static object? TryBuildApplyPayload(IReadOnlyList<EffectGrantDto> grants)
    {
        if (grants == null || grants.Count == 0) return null;
        return new { grants };
    }

    public static CommandDto? TryBuildApplyCommand(IReadOnlyList<EffectGrantDto> grants, string? cmdId = null)
    {
        var payload = TryBuildApplyPayload(grants);
        if (payload == null) return null;
        return new CommandDto
        {
            Name = ApplyCommandName,
            Payload = payload,
            Id = cmdId ?? Guid.NewGuid().ToString("N")
        };
    }
}
