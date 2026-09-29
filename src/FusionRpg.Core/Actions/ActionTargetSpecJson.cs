using System.Text;
using System.Text.Json;

namespace FusionRpg.Core.Actions;

/// <summary>
/// Reads and writes <see cref="ActionTargetSpec"/> in its canonical JSON form. A hand-rolled reader,
/// not a blind <c>JsonSerializer.Deserialize</c> — an unrecognized key is rejected at authoring, not
/// silently ignored, matching the atom program's closed-leaf discipline (spec-targeting.md §3).
/// </summary>
public static class ActionTargetSpecJson
{
    static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "mode", "relation", "count", "shape", "size", "width", "height",
        "anchorSource", "filters", "maxTargets", "ordering",
    };

    static readonly HashSet<string> FilterKeys = new(StringComparer.Ordinal)
    {
        "typeIds", "excludeMindControlled", "row", "colMin", "colMax",
    };

    public static ActionRejection TryRead(string? json, out ActionTargetSpec spec)
    {
        spec = new ActionTargetSpec();
        if (string.IsNullOrWhiteSpace(json)) return ActionRejection.Ok; // absent means the default (Single/Enemy)

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json!); }
        catch (JsonException ex)
        {
            return ActionRejection.Fail(ActionRejectionReason.BadActionId, $"target spec: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ActionRejection.Fail(ActionRejectionReason.BadActionId, "target spec must be an object");

            foreach (var prop in root.EnumerateObject())
                if (!TopKeys.Contains(prop.Name))
                    return ActionRejection.Fail(ActionRejectionReason.BadActionId,
                        $"target spec: unknown key '{prop.Name}'");

            var mode = ActionTargetMode.Single;
            if (root.TryGetProperty("mode", out var modeEl))
            {
                var text = modeEl.GetString();
                if (!ActionTargetModes.TryParse(text, out mode))
                    return ActionRejection.Fail(ActionRejectionReason.BadActionId, $"target spec: unknown mode '{text}'");
            }

            var relation = ActionRelation.Enemy;
            if (root.TryGetProperty("relation", out var relEl))
            {
                var text = relEl.GetString();
                if (!ActionRelations.TryParse(text, out relation))
                    return ActionRejection.Fail(ActionRejectionReason.BadActionId, $"target spec: unknown relation '{text}'");
            }

            int? count = TryInt(root, "count");

            ActionAreaShape? shape = null;
            if (root.TryGetProperty("shape", out var shapeEl))
            {
                var text = shapeEl.GetString();
                if (!ActionAreaShapes.TryParse(text, out var parsedShape))
                    return ActionRejection.Fail(ActionRejectionReason.BadActionId, $"target spec: unknown shape '{text}'");
                shape = parsedShape;
            }

            var size = TryInt(root, "size");
            var width = TryInt(root, "width");
            var height = TryInt(root, "height");

            var anchorSource = ActionAnchorSource.Caster;
            if (root.TryGetProperty("anchorSource", out var anchorEl))
            {
                var text = anchorEl.GetString();
                if (!ActionAnchorSources.TryParse(text, out anchorSource))
                    return ActionRejection.Fail(ActionRejectionReason.BadActionId, $"target spec: unknown anchorSource '{text}'");
            }

            var filters = new ActionTargetFilters();
            if (root.TryGetProperty("filters", out var filtersEl))
            {
                if (filtersEl.ValueKind != JsonValueKind.Object)
                    return ActionRejection.Fail(ActionRejectionReason.BadActionId, "target spec: 'filters' must be an object");

                foreach (var prop in filtersEl.EnumerateObject())
                    if (!FilterKeys.Contains(prop.Name))
                        return ActionRejection.Fail(ActionRejectionReason.BadActionId,
                            $"target spec: unknown filter key '{prop.Name}'");

                List<int>? typeIds = null;
                if (filtersEl.TryGetProperty("typeIds", out var typeIdsEl))
                {
                    if (typeIdsEl.ValueKind != JsonValueKind.Array)
                        return ActionRejection.Fail(ActionRejectionReason.BadActionId, "target spec: 'filters.typeIds' must be an array");
                    typeIds = new List<int>();
                    foreach (var e in typeIdsEl.EnumerateArray())
                        typeIds.Add(e.GetInt32());
                }

                bool? excludeMc = filtersEl.TryGetProperty("excludeMindControlled", out var mcEl)
                    ? mcEl.GetBoolean() : null;

                filters = new ActionTargetFilters
                {
                    TypeIds = typeIds,
                    ExcludeMindControlled = excludeMc,
                    Row = TryInt(filtersEl, "row"),
                    ColMin = TryInt(filtersEl, "colMin"),
                    ColMax = TryInt(filtersEl, "colMax"),
                };
            }

            var maxTargets = TryInt(root, "maxTargets");

            var ordering = ActionTargetOrdering.OrdinalPtr;
            if (root.TryGetProperty("ordering", out var orderEl))
            {
                var text = orderEl.GetString();
                if (!ActionTargetOrderings.TryParse(text, out ordering))
                    return ActionRejection.Fail(ActionRejectionReason.BadActionId, $"target spec: unknown ordering '{text}'");
            }

            spec = new ActionTargetSpec
            {
                Mode = mode, Relation = relation, Count = count,
                Shape = shape, Size = size, Width = width, Height = height,
                AnchorSource = anchorSource, Filters = filters,
                MaxTargets = maxTargets, Ordering = ordering,
            };
            return ActionRejection.Ok;
        }
    }

    static int? TryInt(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;

    public static string Write(ActionTargetSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"mode\":\"{ActionTargetModes.Name(spec.Mode)}\",");
        sb.Append($"\"relation\":\"{ActionRelations.Name(spec.Relation)}\",");
        if (spec.Count is { } count) sb.Append($"\"count\":{count},");
        if (spec.Shape is { } shape) sb.Append($"\"shape\":\"{ActionAreaShapes.Name(shape)}\",");
        if (spec.Size is { } size) sb.Append($"\"size\":{size},");
        if (spec.Width is { } width) sb.Append($"\"width\":{width},");
        if (spec.Height is { } height) sb.Append($"\"height\":{height},");
        sb.Append($"\"anchorSource\":\"{ActionAnchorSources.Name(spec.AnchorSource)}\",");
        sb.Append("\"filters\":{");
        var wroteFilterField = false;
        if (spec.Filters.TypeIds is { Count: > 0 } typeIds)
        {
            sb.Append($"\"typeIds\":[{string.Join(",", typeIds)}]");
            wroteFilterField = true;
        }
        if (spec.Filters.ExcludeMindControlled is { } excludeMc)
        {
            if (wroteFilterField) sb.Append(',');
            sb.Append($"\"excludeMindControlled\":{(excludeMc ? "true" : "false")}");
            wroteFilterField = true;
        }
        if (spec.Filters.Row is { } row)
        {
            if (wroteFilterField) sb.Append(',');
            sb.Append($"\"row\":{row}");
            wroteFilterField = true;
        }
        if (spec.Filters.ColMin is { } colMin)
        {
            if (wroteFilterField) sb.Append(',');
            sb.Append($"\"colMin\":{colMin}");
            wroteFilterField = true;
        }
        if (spec.Filters.ColMax is { } colMax)
        {
            if (wroteFilterField) sb.Append(',');
            sb.Append($"\"colMax\":{colMax}");
        }
        sb.Append("},");
        if (spec.MaxTargets is { } maxTargets) sb.Append($"\"maxTargets\":{maxTargets},");
        sb.Append($"\"ordering\":\"{ActionTargetOrderings.Name(spec.Ordering)}\"");
        sb.Append('}');
        return sb.ToString();
    }
}
