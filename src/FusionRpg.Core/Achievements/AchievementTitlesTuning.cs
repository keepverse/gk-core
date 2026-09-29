using System.Text.Json;

namespace FusionRpg.Core.Achievements;

// Pure parser over an achievement-titles tuning JSON string — no file I/O
// (tunables-ssot.md §7.2: Core never reads a file; hosts load and inject).
// Missing keys reject naming the key, never a silent default.
public sealed record AchievementTitlesTuning(
    int Version,
    int HallSlots,
    int[] TierNeedCounts,
    Dictionary<string, long> PoolWeightsMilli,
    Dictionary<string, long> EquipShareMilli,
    Dictionary<string, long> UpkeepShareMilli,
    string StackRule,
    Dictionary<string, long> ValidTurns,
    long RitualSouls,
    long RitualEssence,
    Dictionary<string, long> SeasonTurnWindows,
    long HiddenShareCapMilli,
    string WornRule,
    long HallYieldCapMilli,
    long HallUpkeepCapMilli);

public static class AchievementTitlesTuningLoader
{
    public static AchievementTitlesTuning Parse(string json, string source = "achievement-titles tuning")
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new RegistryLoadException($"{source}: empty document");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new RegistryLoadException($"{source}: not valid JSON — {ex.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            Str(root, "kind", "$", source);
            return new AchievementTitlesTuning(
                Int(root, "version", "$", source),
                Int(root, "hallSlots", "$", source),
                IntArray(root, "tierNeedCounts", "$", source),
                MilliMap(root, "poolWeightsMilli", "$", source),
                MilliMap(root, "equipShareMilli", "$", source),
                MilliMap(root, "upkeepShareMilli", "$", source),
                Str(root, "stackRule", "$", source),
                MilliMap(root, "validTurns", "$", source),
                Long(Obj(root, "titleRitualPrice", "$", source), "souls", "titleRitualPrice", source),
                Long(Obj(root, "titleRitualPrice", "$", source), "essence", "titleRitualPrice", source),
                MilliMap(root, "seasonTurnWindows", "$", source),
                Long(root, "hiddenShareCapMilli", "$", source),
                Str(root, "wornRule", "$", source),
                Long(root, "hallYieldCapMilli", "$", source),
                Long(root, "hallUpkeepCapMilli", "$", source));
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string path, string source)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new RegistryLoadException($"{source}: missing or non-object '{path}.{key}'");
        return el;
    }

    static string Str(JsonElement parent, string key, string path, string source)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new RegistryLoadException($"{source}: missing or non-string '{path}.{key}'");
        return el.GetString()!;
    }

    static int Int(JsonElement parent, string key, string path, string source)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new RegistryLoadException($"{source}: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path, string source)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new RegistryLoadException($"{source}: missing or non-integer '{path}.{key}'");
        return v;
    }

    static int[] IntArray(JsonElement parent, string key, string path, string source)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new RegistryLoadException($"{source}: missing or non-array '{path}.{key}'");
        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var v))
                throw new RegistryLoadException($"{source}: non-integer entry in '{path}.{key}'");
            list.Add(v);
        }
        return list.ToArray();
    }

    static Dictionary<string, long> MilliMap(JsonElement parent, string key, string path, string source)
    {
        var obj = Obj(parent, key, path, source);
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            map[prop.Name] = Long(obj, prop.Name, $"{path}.{key}", source);
        return map;
    }
}
