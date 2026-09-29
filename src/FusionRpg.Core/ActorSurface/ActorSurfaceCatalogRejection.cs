using System.Text.Json;

namespace FusionRpg.Core.ActorSurface;

public sealed class ActorSurfaceCatalogRejection : Exception
{
    public ActorSurfaceCatalogRejection(string message) : base(message) { }
}

/// <summary>Shared JSON field readers for actor-surface catalogs (no file I/O).</summary>
internal static class ActorSurfaceJson
{
    public static JsonDocument ParseDocument(string json, string catalog)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ActorSurfaceCatalogRejection($"{catalog}: empty document");

        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw new ActorSurfaceCatalogRejection($"{catalog}: not valid JSON — {ex.Message}");
        }
    }

    public static int Int(JsonElement parent, string key, string path, string catalog)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ActorSurfaceCatalogRejection($"{catalog}: missing or non-integer '{path}.{key}'");
        return v;
    }

    public static bool Bool(JsonElement parent, string key, string path, string catalog)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ActorSurfaceCatalogRejection($"{catalog}: missing or non-boolean '{path}.{key}'");
        return el.GetBoolean();
    }

    public static string Str(JsonElement parent, string key, string path, string catalog)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ActorSurfaceCatalogRejection($"{catalog}: missing or non-string '{path}.{key}'");
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
            throw new ActorSurfaceCatalogRejection($"{catalog}: empty '{path}.{key}'");
        return s.Trim();
    }

    public static string? OptionalStr(JsonElement parent, string key, string path, string catalog)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.String)
            throw new ActorSurfaceCatalogRejection($"{catalog}: non-string '{path}.{key}'");
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return null;
        return s.Trim();
    }

    public static JsonElement Arr(JsonElement parent, string key, string path, string catalog)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new ActorSurfaceCatalogRejection($"{catalog}: missing or non-array '{path}.{key}'");
        return el;
    }

    public static JsonElement Obj(JsonElement parent, string key, string path, string catalog)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new ActorSurfaceCatalogRejection($"{catalog}: missing or non-object '{path}.{key}'");
        return el;
    }

    public static TEnum EnumValue<TEnum>(JsonElement parent, string key, string path, string catalog)
        where TEnum : struct, Enum
    {
        var raw = Str(parent, key, path, catalog);
        if (!Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value))
            throw new ActorSurfaceCatalogRejection($"{catalog}: unknown '{path}.{key}' value '{raw}'");
        return value;
    }

    public static IReadOnlyList<string> StringArray(JsonElement parent, string key, string path, string catalog)
    {
        var arr = Arr(parent, key, path, catalog);
        var list = new List<string>(arr.GetArrayLength());
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                throw new ActorSurfaceCatalogRejection($"{catalog}: non-string '{path}.{key}[{i}]'");
            var s = el.GetString();
            if (string.IsNullOrWhiteSpace(s))
                throw new ActorSurfaceCatalogRejection($"{catalog}: empty '{path}.{key}[{i}]'");
            list.Add(s.Trim());
            i++;
        }
        return list;
    }

    /// <summary>Locale object — every player string requires <c>en</c>.</summary>
    public static LocaleMap Locale(JsonElement parent, string key, string path, string catalog)
    {
        var obj = Obj(parent, key, path, catalog);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                throw new ActorSurfaceCatalogRejection($"{catalog}: non-string '{path}.{key}.{prop.Name}'");
            var s = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(s))
                throw new ActorSurfaceCatalogRejection($"{catalog}: empty '{path}.{key}.{prop.Name}'");
            dict[prop.Name] = s.Trim();
        }

        if (!dict.ContainsKey("en"))
            throw new ActorSurfaceCatalogRejection($"{catalog}: missing required 'en' on '{path}.{key}'");

        return new LocaleMap(dict);
    }
}
