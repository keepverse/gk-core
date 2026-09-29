using System.Text.Json;

namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>
/// All nine dungeon registries, loaded once from <c>gk-data/packs/fusion/data/seed/dungeon/_registry/*.json</c>
/// (spec-dungeon-registries.md "Registry files") and handed to their catalogs by
/// <see cref="DungeonRegistryHub"/>. A registry row never carries a magnitude — numbers a row
/// needs (a room kind's weight, a rung's deltas) are joined from <c>DungeonTuningHub.Tuning</c> at
/// first read, exactly as <c>LaneTypeCatalog</c> joins <c>WorldTuningHub</c>.
/// </summary>
public sealed record DungeonRegistries(
    IReadOnlyList<RoomKindDef> RoomKinds,
    IReadOnlyList<DoorKindDef> DoorKinds,
    IReadOnlyList<string> OverrideTags,
    IReadOnlyList<ObjectiveTemplateDef> ObjectiveTemplates,
    IReadOnlyList<DifficultyRungDef> DifficultyRungs,
    IReadOnlyList<string> Disposition,
    IReadOnlyList<InteractionVerbDef> InteractionVerbs,
    IReadOnlyList<string> RaidModes,
    IReadOnlyDictionary<string, BandDef> Bands);

/// <summary>Thrown by every dungeon registry parser and validator. One type, named per file
/// (tunables-ssot.md T5 — "a missing tunable is a load rejection, never a default", the same
/// discipline applied to registries).</summary>
public sealed class DungeonRegistryRejection : Exception
{
    public DungeonRegistryRejection(string message) : base(message) { }
}

/// <summary>Stable kebab-case id checks and the S2-12 spelled-number ban, shared by every dungeon
/// registry parser. Copied from <c>WorldIds.cs:6-14</c> rather than referenced — Dungeon must not
/// depend on the World module for a string check.</summary>
public static class DungeonRegistryIds
{
    static readonly string[] SpelledNumbers =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"
    };

    public static void RequireKebab(string? id, string label, string file)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new DungeonRegistryRejection($"{file}: {label} is empty.");

        var s = id!;
        if (s != s.Trim())
            throw new DungeonRegistryRejection($"{file}: {label} '{id}' must not have leading or trailing whitespace.");

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= 'a' and <= 'z') continue;
            if (c is >= '0' and <= '9') continue;
            if (c == '-') continue;
            throw new DungeonRegistryRejection($"{file}: {label} '{id}' must be kebab-case (lowercase letters, digits, hyphens).");
        }

        if (s[0] == '-' || s[^1] == '-')
            throw new DungeonRegistryRejection($"{file}: {label} '{id}' must not start or end with '-'.");
    }

    /// <summary>S2-12: "no member may be a spelled number — countBand is lone·few·several·many,
    /// phasing is none·breakpoint·escalating, never one·two·three."</summary>
    public static void RejectSpelledNumber(string member, string bandName, string file)
    {
        if (Array.IndexOf(SpelledNumbers, member) >= 0)
            throw new DungeonRegistryRejection(
                $"{file}: band '{bandName}' member '{member}' is a spelled number — use a true band with a " +
                "{min,max} tuning row instead (spec-dungeon-registries.md S2-12).");
    }
}

/// <summary>Pure JSON helpers shared by every registry parser — no file I/O here
/// (tunables-ssot.md §7.2); <see cref="DungeonRegistryLoader"/> is the one place that reads a path.</summary>
public static class DungeonRegistryJson
{
    public static JsonElement Root(string json, string file)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DungeonRegistryRejection($"{file}: empty document");
        try { return JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new DungeonRegistryRejection($"{file}: not valid JSON — {ex.Message}"); }
    }

    public static JsonElement Obj(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DungeonRegistryRejection($"{file}: missing or non-object '{path}.{key}'");
        return el;
    }

    public static bool Bool(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DungeonRegistryRejection($"{file}: missing or non-boolean '{path}.{key}'");
        return el.GetBoolean();
    }

    public static int Int(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new DungeonRegistryRejection($"{file}: missing or non-integer '{path}.{key}'");
        return v;
    }

    /// <summary>An explicit JSON <c>null</c> is a legal value here — the key itself must still be
    /// present (T5: absence is a defect; <c>none</c>/<c>null</c> is a real value — ideal §10 law 6).</summary>
    public static int? IntOrNull(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var el))
            throw new DungeonRegistryRejection($"{file}: missing '{path}.{key}' (write JSON null explicitly, never omit the key)");
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new DungeonRegistryRejection($"{file}: '{path}.{key}' must be an integer or null");
        return v;
    }

    public static string Str(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new DungeonRegistryRejection($"{file}: missing or non-string '{path}.{key}'");
        return el.GetString()!;
    }

    public static IReadOnlyList<string> StringArray(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new DungeonRegistryRejection($"{file}: missing or non-array '{path}.{key}'");
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new DungeonRegistryRejection($"{file}: '{path}.{key}' must be an array of strings");
            list.Add(item.GetString()!);
        }
        return list;
    }
}

/// <summary>
/// Loads the nine <c>gk-data/packs/fusion/data/seed/dungeon/_registry/*.json</c> files. Pure over a directory path —
/// Core never touches a path elsewhere (tunables-ssot.md §7.2); the only <c>File.</c> calls under
/// <c>Core/Dungeon/</c> are the nine reads in this one method (success criterion 6).
/// </summary>
public static class DungeonRegistryLoader
{
    public static DungeonRegistries LoadAll(string registryDir)
    {
        string Read(string name)
        {
            var path = Path.Combine(registryDir, name);
            if (!File.Exists(path))
                throw new DungeonRegistryRejection($"{name}: file not found at '{path}'");
            return File.ReadAllText(path);
        }

        return new DungeonRegistries(
            RoomKinds: RoomKindCatalog.Parse(Read("room-kinds.v1.json")),
            DoorKinds: DoorKindCatalog.Parse(Read("door-kinds.v1.json")),
            OverrideTags: OverrideTagCatalog.Parse(Read("override-tags.v1.json")),
            ObjectiveTemplates: ObjectiveTemplateCatalog.Parse(Read("objective-templates.v1.json")),
            DifficultyRungs: DifficultyRungCatalog.Parse(Read("difficulty-rungs.v1.json")),
            Disposition: DispositionCatalog.Parse(Read("disposition.v1.json")),
            InteractionVerbs: InteractionVerbCatalog.Parse(Read("interaction-verbs.v1.json")),
            RaidModes: RaidModeCatalog.Parse(Read("raid-modes.v1.json")),
            Bands: BandCatalog.Parse(Read("bands.v1.json")));
    }
}

/// <summary>
/// Wires every one of the nine catalogs from one loaded <see cref="DungeonRegistries"/> — the
/// module's single Configure point. Call <c>DungeonTuningHub.Configure</c> first: <see cref="RoomKindDef"/>
/// and <see cref="DifficultyRungDef"/> join tuning at first property read, not at Configure time.
/// </summary>
public static class DungeonRegistryHub
{
    public static void Configure(DungeonRegistries registries)
    {
        RoomKindCatalog.Configure(registries.RoomKinds);
        DoorKindCatalog.Configure(registries.DoorKinds);
        OverrideTagCatalog.Configure(registries.OverrideTags);
        ObjectiveTemplateCatalog.Configure(registries.ObjectiveTemplates);
        DifficultyRungCatalog.Configure(registries.DifficultyRungs);
        DispositionCatalog.Configure(registries.Disposition);
        InteractionVerbCatalog.Configure(registries.InteractionVerbs);
        RaidModeCatalog.Configure(registries.RaidModes);
        BandCatalog.Configure(registries.Bands);
    }
}
