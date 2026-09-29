namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>One of the six object interaction verbs (ideal §11.5): <c>open · disarm · pray · loot ·
/// destroy · garrison</c>. <see cref="Decision"/> is the base-defense decision number that admits the
/// verb where one exists (destroy = 12, garrison = 15); the other four resolve through a key-supply
/// override or an event-deck draw rather than a base-defense structure decision, so they carry
/// <c>null</c> — a real value, not a missing one (ideal §10 law 6).</summary>
public sealed record InteractionVerbDef
{
    public string VerbId { get; init; } = "";
    public int? Decision { get; init; }
}

public static class InteractionVerbCatalog
{
    const string File = "interaction-verbs.v1.json";
    static IReadOnlyList<InteractionVerbDef>? _all;
    static Dictionary<string, InteractionVerbDef>? _byId;

    public static IReadOnlyList<InteractionVerbDef> All => _all
        ?? throw new InvalidOperationException("InteractionVerbCatalog.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && ByIdMap().ContainsKey(id);

    public static InteractionVerbDef Get(string id) =>
        ByIdMap().TryGetValue(id, out var def) ? def : throw new ArgumentException($"Unknown interaction verb id '{id}'.");

    public static void Configure(IReadOnlyList<InteractionVerbDef> rows)
    {
        _all = Validate(rows);
        _byId = _all.ToDictionary(r => r.VerbId, StringComparer.Ordinal);
    }

    static Dictionary<string, InteractionVerbDef> ByIdMap() =>
        _byId ?? throw new InvalidOperationException("InteractionVerbCatalog.Configure(...) has not run.");

    public static IReadOnlyList<InteractionVerbDef> Validate(IReadOnlyList<InteractionVerbDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            DungeonRegistryIds.RequireKebab(r.VerbId, "Interaction verb id", File);
            if (!seen.Add(r.VerbId))
                throw new DungeonRegistryRejection($"{File}: duplicate id '{r.VerbId}'.");
            if (r.Decision is < 1)
                throw new DungeonRegistryRejection($"{File}: '{r.VerbId}'.decision must be a positive decision number or null.");
        }
        return rows;
    }

    public static IReadOnlyList<InteractionVerbDef> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        var members = DungeonRegistryJson.Obj(root, "interactionVerbs", "$", File);
        var rows = new List<InteractionVerbDef>();
        foreach (var prop in members.EnumerateObject())
        {
            var id = prop.Name;
            var el = prop.Value;
            rows.Add(new InteractionVerbDef
            {
                VerbId = id,
                Decision = DungeonRegistryJson.IntOrNull(el, "decision", $"interactionVerbs.{id}", File)
            });
        }
        return rows;
    }
}
