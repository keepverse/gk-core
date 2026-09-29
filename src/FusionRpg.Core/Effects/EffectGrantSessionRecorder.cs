using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects;

/// <summary>
/// Keeps <see cref="EffectGrantSession"/> in sync with debug commands and match lifecycle (W0-E audit).
/// </summary>
public static class EffectGrantSessionRecorder
{
    public static bool IsMatchLifecycleKind(string? kind) =>
        string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "board.end", StringComparison.OrdinalIgnoreCase);

    public static void NoteMatchLifecycle(EffectGrantSession session, string? kind)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (IsMatchLifecycleKind(kind))
            session.Clear();
    }

    /// <summary>Apply a debug / effects command to the session store (HTTP or scenario step).</summary>
    public static void ApplyDebugCommand(EffectGrantSession session, string name, JsonElement payload)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrWhiteSpace(name)) return;

        var n = name.Trim();
        if (n.Equals("debug.effect.grant", StringComparison.OrdinalIgnoreCase))
        {
            var dto = TryParseGrant(payload);
            if (dto == null || string.IsNullOrWhiteSpace(dto.EffectId)) return;
            NormalizeGrantDefaults(dto);
            session.Upsert(dto);
            return;
        }

        if (n.Equals("debug.effect.withdraw", StringComparison.OrdinalIgnoreCase))
        {
            var grantId = Str(payload, "grantId");
            if (!string.IsNullOrWhiteSpace(grantId))
                session.Remove(grantId!);
            return;
        }

        if (n.Equals("debug.effect.clear", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("effects.reload", StringComparison.OrdinalIgnoreCase))
        {
            session.Clear();
        }
    }

    public static void ApplyDebugCommand(EffectGrantSession session, string name, object? payload)
    {
        var el = ToElement(payload);
        ApplyDebugCommand(session, name, el);
    }

    public static void ApplyDebugSteps(EffectGrantSession session, IEnumerable<(string Name, object? Payload)> steps)
    {
        foreach (var (name, payload) in steps)
            ApplyDebugCommand(session, name, payload);
    }

    /// <summary>True when Hot grant requires a stable grantId (no Guid mint on injector).</summary>
    public static bool TryValidateHotGrantId(string? grantId, out string error)
    {
        if (string.IsNullOrWhiteSpace(grantId))
        {
            error = "grantId required";
            return false;
        }
        error = "";
        return true;
    }

    public static void NormalizeGrantDefaults(EffectGrantDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.GrantId))
            dto.GrantId = Guid.NewGuid().ToString("N")[..12];
        if (string.IsNullOrWhiteSpace(dto.OwnerKey))
            dto.OwnerKey = EffectOwnerKeys.Match;
        if (string.IsNullOrWhiteSpace(dto.OwnerKind))
            dto.OwnerKind = "match";
        if (string.IsNullOrWhiteSpace(dto.PluginId))
            dto.PluginId = "debug";
    }

    public static EffectGrantDto? TryParseGrant(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return JsonSerializer.Deserialize<EffectGrantDto>(b.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    static JsonElement ToElement(object? payload)
    {
        if (payload is JsonElement el) return el;
        if (payload == null) return JsonSerializer.SerializeToElement(new { });
        try
        {
            return JsonSerializer.SerializeToElement(payload);
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
