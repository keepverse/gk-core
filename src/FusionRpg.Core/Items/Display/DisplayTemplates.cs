using System.Text.Json;
using System.Text.RegularExpressions;

namespace FusionRpg.Core.Items.Display;

/// <summary>
/// N1's one row per family (spec-item-card.md) — a projection over the ALREADY-authored
/// `gk-data/packs/fusion/data/seed/items/display-templates/*.json` (98 rows, one per shipped affix family, authored
/// 2026-08-22 and never wired to a renderer before this module — the same "authored, never
/// consumed" shape `nameWords` had before module 8). `Name`/`PlantOverrideName` carry the template
/// text directly (`{placeholder}` tokens); `gk-content/content/display/en.json` (N2) is generated from these
/// two fields, never a second hand-authored copy.
/// </summary>
public readonly record struct DisplayTemplateRow(
    string RuntimeFamily, string NameKey, string Template, string? PlantOverrideKey,
    string? PlantOverrideTemplate, string GroupId, string Status);

public sealed class DisplayTemplateRejection : Exception
{
    public DisplayTemplateRejection(string message) : base(message) { }
}

public static class DisplayTemplates
{
    /// <summary>Parse one `display-templates/*.json` seed file's `entries` array.</summary>
    public static IReadOnlyList<DisplayTemplateRow> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DisplayTemplateRejection("display-template: empty document");

        using var doc = JsonDocument.Parse(json);
        var rows = new List<DisplayTemplateRow>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var family = e.GetProperty("runtimeFamily").GetString()
                ?? throw new DisplayTemplateRejection("display-template: entry missing 'runtimeFamily'");
            var template = e.GetProperty("name").GetString();
            if (string.IsNullOrEmpty(template))
                throw new DisplayTemplateRejection($"display-template: '{family}' has no template text");

            rows.Add(new DisplayTemplateRow(
                family,
                e.GetProperty("nameKey").GetString() ?? throw new DisplayTemplateRejection($"'{family}': missing nameKey"),
                template,
                e.TryGetProperty("plantOverrideKey", out var pk) && pk.ValueKind == JsonValueKind.String ? pk.GetString() : null,
                e.TryGetProperty("plantOverrideName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null,
                e.GetProperty("groupId").GetString() ?? throw new DisplayTemplateRejection($"'{family}': missing groupId"),
                e.GetProperty("status").GetString() ?? throw new DisplayTemplateRejection($"'{family}': missing status")));
        }

        return rows;
    }

    static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);

    /// <summary>Rule 3: rounding/formatting happens once, at this boundary, and never feeds back —
    /// the caller passes the already-frozen, already-formatted arg strings; this only substitutes.</summary>
    public static string Render(DisplayTemplateRow row, string frame, IReadOnlyDictionary<string, string> args)
    {
        var template = frame == "plant" && row.PlantOverrideTemplate is not null ? row.PlantOverrideTemplate : row.Template;
        return Placeholder.Replace(template, m =>
            args.TryGetValue(m.Groups[1].Value, out var v)
                ? v
                : throw new DisplayTemplateRejection($"'{row.RuntimeFamily}': unresolved placeholder '{{{m.Groups[1].Value}}}'"));
    }

    /// <summary>Every `{placeholder}` a template names, without resolving any of them — used to
    /// validate a template renders with no leftover token, at Min/mid/Max, before ever reaching a player.</summary>
    public static IReadOnlyList<string> PlaceholdersOf(string template) =>
        Placeholder.Matches(template).Select(m => m.Groups[1].Value).Distinct().ToList();
}

/// <summary>
/// N2's string catalog (`ssot-presentation.md` §5.3) read back: a flat map from display key to
/// <c>{ template, plural? }</c>. Pure — the caller supplies the JSON text; Core never opens a file.
///
/// <para>A key with no row resolves to <c>null</c>, never to the key itself and never to a
/// synthesised sentence. That absence is <see cref="DisplayRules.MissingDisplayKey"/>'s question to
/// answer, and papering over it here is exactly the "a placement, never a translation" defect this
/// reader exists to remove.</para>
/// </summary>
public static class DisplayStringCatalog
{
    /// <summary>The catalog as the lookup <c>ItemCardInput</c> takes.</summary>
    public static Func<string, string?> Parse(string json)
    {
        var byKey = ParseMap(json);
        return key => byKey.TryGetValue(key, out var text) ? text : null;
    }

    /// <summary>The same read, as a map — for a caller that needs the whole key set.</summary>
    public static IReadOnlyDictionary<string, string> ParseMap(string json)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return byKey;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return byKey;

        foreach (var row in doc.RootElement.EnumerateObject())
        {
            if (row.Value.ValueKind != JsonValueKind.Object) continue;
            if (row.Value.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.String)
                byKey[row.Name] = t.GetString()!;
        }

        return byKey;
    }
}
