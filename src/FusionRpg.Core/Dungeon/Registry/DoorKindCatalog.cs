namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>One door kind — the same flags <c>LaneTypeDef</c> carries (<c>LaneTypeCatalog.cs:23-26</c>),
/// so <c>DoorTypeCatalog</c> rows in <c>delve-scope</c> are <c>LaneTypeDef</c>s as row P2 requires.</summary>
public sealed record DoorKindDef
{
    public string DoorKindId { get; init; } = "";
    public bool Gated { get; init; }
    public bool OneWay { get; init; }
    public bool Hidden { get; init; }
}

public static class DoorKindCatalog
{
    const string File = "door-kinds.v1.json";
    static IReadOnlyList<DoorKindDef>? _all;
    static Dictionary<string, DoorKindDef>? _byId;

    public static IReadOnlyList<DoorKindDef> All => _all
        ?? throw new InvalidOperationException("DoorKindCatalog.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && ByIdMap().ContainsKey(id);

    public static DoorKindDef Get(string id) =>
        ByIdMap().TryGetValue(id, out var def) ? def : throw new ArgumentException($"Unknown door kind id '{id}'.");

    public static void Configure(IReadOnlyList<DoorKindDef> rows)
    {
        _all = Validate(rows);
        _byId = _all.ToDictionary(r => r.DoorKindId, StringComparer.Ordinal);
    }

    static Dictionary<string, DoorKindDef> ByIdMap() =>
        _byId ?? throw new InvalidOperationException("DoorKindCatalog.Configure(...) has not run.");

    public static IReadOnlyList<DoorKindDef> Validate(IReadOnlyList<DoorKindDef> rows)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            DungeonRegistryIds.RequireKebab(r.DoorKindId, "Door kind id", File);
            if (!seenIds.Add(r.DoorKindId))
                throw new DungeonRegistryRejection($"{File}: duplicate id '{r.DoorKindId}'.");
        }
        return rows;
    }

    public static IReadOnlyList<DoorKindDef> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        var members = DungeonRegistryJson.Obj(root, "doorKinds", "$", File);
        var rows = new List<DoorKindDef>();
        foreach (var prop in members.EnumerateObject())
        {
            var id = prop.Name;
            var el = prop.Value;
            rows.Add(new DoorKindDef
            {
                DoorKindId = id,
                Gated = DungeonRegistryJson.Bool(el, "gated", $"doorKinds.{id}", File),
                OneWay = DungeonRegistryJson.Bool(el, "oneWay", $"doorKinds.{id}", File),
                Hidden = DungeonRegistryJson.Bool(el, "hidden", $"doorKinds.{id}", File)
            });
        }
        return rows;
    }
}
