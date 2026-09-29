namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>What an objective template's target ref names — a category, never a specific id
/// (ideal §11.3: "targetRef (a kind, never a number, or none)").</summary>
public enum ObjectiveTargetKind
{
    RoomKind,
    CurioKind,
    ItemKind,
    Boss,
    None
}

/// <summary>One of the nine quest objective templates (ideal §11.3), Doran &amp; Parberry's
/// structural strategies with the noun replaced by a kind ref and the count by a band.</summary>
public sealed record ObjectiveTemplateDef
{
    public string ObjectiveTemplateId { get; init; } = "";
    public ObjectiveTargetKind TargetKind { get; init; }

    /// <summary>D14 (audit §4): eligible only at rung ≥ hard or paired with a risk objective —
    /// true for finish-under-hunger · survive-no-downed · spend-no-provision.</summary>
    public bool SinkAvoidance { get; init; }
}

public static class ObjectiveTemplateCatalog
{
    const string File = "objective-templates.v1.json";
    static readonly Dictionary<string, ObjectiveTargetKind> TargetKindNames = new(StringComparer.Ordinal)
    {
        ["room-kind"] = ObjectiveTargetKind.RoomKind,
        ["curio-kind"] = ObjectiveTargetKind.CurioKind,
        ["item-kind"] = ObjectiveTargetKind.ItemKind,
        ["boss"] = ObjectiveTargetKind.Boss,
        ["none"] = ObjectiveTargetKind.None
    };

    static IReadOnlyList<ObjectiveTemplateDef>? _all;
    static Dictionary<string, ObjectiveTemplateDef>? _byId;

    public static IReadOnlyList<ObjectiveTemplateDef> All => _all
        ?? throw new InvalidOperationException("ObjectiveTemplateCatalog.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && ByIdMap().ContainsKey(id);

    public static ObjectiveTemplateDef Get(string id) =>
        ByIdMap().TryGetValue(id, out var def) ? def : throw new ArgumentException($"Unknown objective template id '{id}'.");

    public static void Configure(IReadOnlyList<ObjectiveTemplateDef> rows)
    {
        _all = Validate(rows);
        _byId = _all.ToDictionary(r => r.ObjectiveTemplateId, StringComparer.Ordinal);
    }

    static Dictionary<string, ObjectiveTemplateDef> ByIdMap() =>
        _byId ?? throw new InvalidOperationException("ObjectiveTemplateCatalog.Configure(...) has not run.");

    public static IReadOnlyList<ObjectiveTemplateDef> Validate(IReadOnlyList<ObjectiveTemplateDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            DungeonRegistryIds.RequireKebab(r.ObjectiveTemplateId, "Objective template id", File);
            if (!seen.Add(r.ObjectiveTemplateId))
                throw new DungeonRegistryRejection($"{File}: duplicate id '{r.ObjectiveTemplateId}'.");
        }
        return rows;
    }

    public static IReadOnlyList<ObjectiveTemplateDef> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        var members = DungeonRegistryJson.Obj(root, "objectiveTemplates", "$", File);
        var rows = new List<ObjectiveTemplateDef>();
        foreach (var prop in members.EnumerateObject())
        {
            var id = prop.Name;
            var el = prop.Value;
            var targetKindStr = DungeonRegistryJson.Str(el, "targetKind", $"objectiveTemplates.{id}", File);
            if (!TargetKindNames.TryGetValue(targetKindStr, out var targetKind))
                throw new DungeonRegistryRejection(
                    $"{File}: objectiveTemplates.{id}.targetKind '{targetKindStr}' is not one of " +
                    "room-kind · curio-kind · item-kind · boss · none.");
            rows.Add(new ObjectiveTemplateDef
            {
                ObjectiveTemplateId = id,
                TargetKind = targetKind,
                SinkAvoidance = DungeonRegistryJson.Bool(el, "sinkAvoidance", $"objectiveTemplates.{id}", File)
            });
        }
        return rows;
    }
}
