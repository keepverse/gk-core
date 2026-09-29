using System.Globalization;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Match;

/// <summary>
/// Minimal Absolute + grant-template loadout for Bound unique (W5-C).
/// Not the W8 gear shop — JSON/deploy stub only.
/// </summary>
public sealed class UniqueLoadoutSpec
{
    public IReadOnlyDictionary<string, int> Absolutes { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EffectGrantDto> Grants { get; init; } = Array.Empty<EffectGrantDto>();

    public bool IsEmpty => Absolutes.Count == 0 && Grants.Count == 0;

    /// <summary>
    /// Parse deploy / <c>rpg_unique_stat_mods.mods_json</c> shape:
    /// <c>{ "absolutes": { "hp": 500, "maxHp": 500, "atk": 40 }, "grants": [ EffectGrantDto... ] }</c>
    /// Empty / missing → empty spec (no-op).
    /// </summary>
    public static UniqueLoadoutSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement);
        }
        catch
        {
            return Empty;
        }
    }

    public static UniqueLoadoutSpec Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return Empty;

        var abs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("absolutes", out var absEl) && absEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in absEl.EnumerateObject())
            {
                if (TryReadInt(p.Value, out var n))
                    abs[p.Name] = n;
            }
        }

        // Flat stub: mods_json may put hp/atk at root.
        foreach (var key in new[] { "hp", "maxHp", "atk", "HP", "MaxHp", "ATK" })
        {
            if (root.TryGetProperty(key, out var flat) && TryReadInt(flat, out var n) && !abs.ContainsKey(key))
                abs[key] = n;
        }

        var grants = new List<EffectGrantDto>();
        if (root.TryGetProperty("grants", out var grantsEl) && grantsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in grantsEl.EnumerateArray())
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<EffectGrantDto>(g.GetRawText(), JsonOpts);
                    if (dto != null && !string.IsNullOrWhiteSpace(dto.EffectId))
                        grants.Add(dto);
                }
                catch
                {
                    /* skip bad grant */
                }
            }
        }

        if (abs.Count == 0 && grants.Count == 0) return Empty;
        return new UniqueLoadoutSpec { Absolutes = abs, Grants = grants };
    }

    /// <summary>
    /// Rewrite grant templates to <c>entity:{ptr}</c>. Absolutes unchanged (ptr-only Writer applies them).
    /// </summary>
    public UniqueLoadoutSpec BindToPtr(string ptr)
    {
        if (IsEmpty) return this;
        var bound = new EffectGrantDto[Grants.Count];
        for (var i = 0; i < Grants.Count; i++)
            bound[i] = UniqueOwnerBinder.BindGrant(Grants[i], ptr);
        return new UniqueLoadoutSpec { Absolutes = Absolutes, Grants = bound };
    }

    /// <summary>
    /// Build cheat-absolute map for <see cref="StatContext.CheatAbsolute"/> scoped by caller to one entity.
    /// Keys: hp, maxHp, atk (case-insensitive input normalized).
    /// </summary>
    public IReadOnlyDictionary<string, int> ToCheatAbsoluteMap()
    {
        if (Absolutes.Count == 0)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Absolutes)
        {
            var k = kv.Key.Trim().ToLowerInvariant();
            if (k is "hp" or "health") map["hp"] = kv.Value;
            else if (k is "maxhp" or "max_hp" or "maxhealth") map["maxHp"] = kv.Value;
            else if (k is "atk" or "attack" or "damage") map["atk"] = kv.Value;
            else map[kv.Key] = kv.Value;
        }
        return map;
    }

    /// <summary>
    /// Prove sibling isolation in Core: entity-scoped modifier matches only that ptr.
    /// </summary>
    public static bool AbsoluteWouldApplyToEntity(
        string entityPtr,
        string? applyOwnerKey,
        StatSide side,
        int typeId) =>
        StatApplyScope.Matches(applyOwnerKey, side, typeId, entityPtr);

    public static UniqueLoadoutSpec Empty { get; } = new();

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static bool TryReadInt(JsonElement el, out int n)
    {
        n = 0;
        try
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out n)) return true;
            if (el.ValueKind == JsonValueKind.String &&
                int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return true;
        }
        catch
        {
            return false;
        }
        return false;
    }
}

/// <summary>
/// Deploy Intent loadout vs <c>rpg_unique_stat_mods.mods_json</c>.
/// Empty-ish deploy (Parse.IsEmpty) falls back to mods; non-empty deploy wins.
/// </summary>
public static class UniqueLoadoutMerge
{
    public static string Merge(string? deployJson, string? modsJson)
    {
        var deploy = string.IsNullOrWhiteSpace(deployJson) ? "{}" : deployJson.Trim();
        var mods = string.IsNullOrWhiteSpace(modsJson) ? "{}" : modsJson.Trim();
        if (UniqueLoadoutSpec.Parse(deploy).IsEmpty)
            return UniqueLoadoutSpec.Parse(mods).IsEmpty ? "{}" : mods;
        return deploy;
    }
}
