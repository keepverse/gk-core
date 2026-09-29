using System.Text.Json;

namespace FusionRpg.Core.World.StructureSeed;

/// <summary>
/// base-defense `structure-catalog-import` (module 25, spec-structure-catalog-import.md). The C#
/// reader for the committed JSON corpus `structure-corpus` (module 24) authored under
/// `gk-data/packs/fusion/data/seed/structures/&lt;role&gt;/&lt;id&gt;.json`, in the shared
/// `{"kind","_meta","entries":[{"id","anchor","_provenance","magnitudes"?}]}` shape
/// `seedsmith.corpus.Corpus.load`'s own Python-side loader already defines (Law 1: one shape, not
/// a second ad-hoc one per language).
///
/// <para><b>Only a row with a real <c>magnitudes</c> block is catalog-loadable</b> — see this
/// module's own spec correction 1 for why: the anchor is identity/ordinals only (structure-schema's
/// own "no numbers at all" rule), and a row's real per-structure numbers (cost, yield, etc.) live in
/// the sibling <c>magnitudes</c> key instead. A row with no <c>magnitudes</c> is registered here
/// (so callers can still see its identity/ordinals exist) but <see cref="StructureCorpusRow.IsCatalogLoadable"/>
/// is false for it, and `StructureCatalog`'s own `Configure` skips it when building `StructureDef`s.</para>
/// </summary>
public sealed record StructureMagnitudes(
    string StructureKind,
    long Cost,
    int YieldMultiplierMilli,
    int BuildTurns,
    long CapacityBonus,
    long ItemStorageCapacityBonus,
    long FlatYieldPerTurn,
    long ConstructRubbleCost,
    long ConstructIronworkCost,
    int MaterialTier,
    bool BlocksMovement,
    bool BlocksLineOfFire,
    string ObstacleKind,
    int CoverPowerMilli,
    int CoverRadius,
    int EntryStaminaMultiplierMilli,
    int? VisionRangeTiles,
    string? ContainerId,
    // loam-relics-and-wonders `wonder-structure`: genuinely optional at parse time (TryGetProperty,
    // default null) — matching ContainerId's own precedent, NOT the Require* discipline every older
    // field uses — so the shipped rows load byte-identical with no Wonder keys present.
    string? WonderScope = null,
    string? WonderRarity = null,
    IReadOnlyList<WonderEffectMagnitude>? WonderEffects = null,
    // loam-relics-and-wonders `wonder-build-flow` §Design 2: how many DISTINCT relic instances a
    // Wonder-tier row requires. Nullable-optional like WonderScope above — missing/null reads as
    // null (mapped to 0 by StructureCatalog), so the 25 shipped rows load byte-identical.
    long? RelicCost = null);

/// <summary>Wire shape of one <c>wonderEffects</c> entry — enum strings must match the C# member
/// spelling exactly (no <c>ignoreCase</c>), the same convention <c>structureKind</c>/<c>obstacleKind</c>
/// already follow.</summary>
public sealed record WonderEffectMagnitude(string Kind, string Scope, long ValueMilli);

public sealed record StructureCorpusRow(
    string StructureId,
    string Name,
    string Role,
    string RequiredSlotKind,
    string StrengthBand,
    IReadOnlyList<string> AcquisitionPaths,
    bool ControlPoint,
    StructureMagnitudes? Magnitudes)
{
    public bool IsCatalogLoadable => Magnitudes is not null;
}

/// <summary>Thrown for a corpus file that fails to parse or is missing a field this reader
/// requires — a startup error, never a silent skip, matching `StructureCatalog.Validate`'s own
/// loud-over-silent stance for every other catalog rule.</summary>
public sealed class StructureCorpusLoadException : Exception
{
    public StructureCorpusLoadException(string path, string reason)
        : base($"{path}: {reason}") { }
}

public sealed class StructureCorpus
{
    public IReadOnlyList<StructureCorpusRow> Rows { get; }

    StructureCorpus(IReadOnlyList<StructureCorpusRow> rows) => Rows = rows;

    /// <summary>Walks every `*.json` file under `root`, treating each as one seed file with a
    /// single-entry `entries` array (`structure-corpus`'s own one-row-per-file convention). A file
    /// whose top level is not `{"kind": "structure-anchor", ...}` is silently not corpus content —
    /// the same "not every JSON file under a seed root is a seed file" rule
    /// `seedsmith.corpus.Corpus.load`'s own Python sibling already applies.</summary>
    public static StructureCorpus Load(string root)
    {
        var rows = new List<StructureCorpusRow>();
        if (!Directory.Exists(root)) return FromRows(rows);

        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            AddRowsFromDocument(rows, File.ReadAllText(path), path);

        return FromRows(rows);
    }

    /// <summary>
    /// Rows from **in-memory** corpus JSON — the same document shape <see cref="Load"/> reads out of a
    /// directory, for a caller that already holds the text (a fixture authoring one row in a string literal).
    /// Same rules as <see cref="Load"/>: a document whose top level is not
    /// <c>{"kind": "structure-anchor", ...}</c> contributes nothing, and a row missing a required field
    /// throws <see cref="StructureCorpusLoadException"/>. One parser serves both entrances, so a literal and
    /// a file cannot drift apart (Law 1: one shape).
    /// </summary>
    public static StructureCorpus FromJson(string json, string source = "<inline>")
    {
        // net6.0 target: no ArgumentException.ThrowIfNullOrWhiteSpace here.
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("corpus JSON is empty", nameof(json));

        var rows = new List<StructureCorpusRow>();
        AddRowsFromDocument(rows, json, source);
        return FromRows(rows);
    }

    /// <summary>The one document parser <see cref="Load"/> and <see cref="FromJson"/> share.</summary>
    static void AddRowsFromDocument(List<StructureCorpusRow> rows, string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var documentRoot = doc.RootElement;
        if (!documentRoot.TryGetProperty("kind", out var kindEl) || kindEl.GetString() != "structure-anchor")
            return;
        if (!documentRoot.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in entries.EnumerateArray())
            rows.Add(ParseRow(entry, source));
    }

    /// <summary>
    /// A corpus over rows the caller already holds — <b>the in-memory entrance</b>, beside
    /// <see cref="Load"/>'s directory walk. The corpus is data; a caller that needs the shipped rows
    /// plus one row of its own (a fixture's depot or wonder) has no reason to materialise a directory
    /// to say so, and until this existed it had no other way to build a corpus at all (the ctor is
    /// private), so fixtures copied `gk-data/packs/fusion/data/seed/**` to a temp path and loaded the copy.
    ///
    /// <para>The rows are copied and exposed as a read-only view: a caller holding a corpus cannot be
    /// surprised by a writer reaching through <see cref="Rows"/>, so the only way to add rows is
    /// <see cref="WithRows"/>, which returns a new corpus.</para>
    /// </summary>
    public static StructureCorpus FromRows(IEnumerable<StructureCorpusRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new StructureCorpus(rows.ToList().AsReadOnly());
    }

    /// <summary>
    /// This corpus plus <paramref name="extraRows"/>, as a **new** corpus — the receiver is untouched.
    /// The superset property callers rely on is explicit here: every row already present stays present,
    /// so a suite that replaces the static catalog with the result cannot blank a row another suite
    /// reading that catalog concurrently depends on.
    /// </summary>
    public StructureCorpus WithRows(params StructureCorpusRow[] extraRows)
    {
        ArgumentNullException.ThrowIfNull(extraRows);
        return FromRows(Rows.Concat(extraRows));
    }

    static StructureCorpusRow ParseRow(JsonElement entry, string path)
    {
        if (!entry.TryGetProperty("id", out var idEl))
            throw new StructureCorpusLoadException(path, "an entry has no 'id'");
        var id = idEl.GetString() ?? throw new StructureCorpusLoadException(path, "'id' is null");

        if (!entry.TryGetProperty("anchor", out var anchor))
            throw new StructureCorpusLoadException(path, $"{id}: no 'anchor' object");

        var name = RequireString(entry, "name", path, id);
        var role = RequireString(anchor, "role", path, id);
        var requiredSlotKind = RequireString(anchor, "requiredSlotKind", path, id);
        var strengthBand = RequireString(anchor, "strengthBand", path, id);
        var controlPoint = RequireBool(anchor, "controlPoint", path, id);
        var acquisitionPaths = anchor.GetProperty("acquisitionPaths").EnumerateArray()
            .Select(e => e.GetString() ?? "").ToList();

        StructureMagnitudes? magnitudes = null;
        if (entry.TryGetProperty("magnitudes", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            magnitudes = new StructureMagnitudes(
                StructureKind: RequireString(m, "structureKind", path, id),
                Cost: RequireLong(m, "cost", path, id),
                YieldMultiplierMilli: RequireInt(m, "yieldMultiplierMilli", path, id),
                BuildTurns: RequireInt(m, "buildTurns", path, id),
                CapacityBonus: RequireLong(m, "capacityBonus", path, id),
                // scoped-inventory `sector-storage`: optional at parse time — every row shipped
                // before ItemStorage defaults to 0, so existing seed rows load byte-identical.
                ItemStorageCapacityBonus: OptionalLong(m, "itemStorageCapacityBonus"),
                FlatYieldPerTurn: RequireLong(m, "flatYieldPerTurn", path, id),
                ConstructRubbleCost: RequireLong(m, "constructRubbleCost", path, id),
                ConstructIronworkCost: RequireLong(m, "constructIronworkCost", path, id),
                MaterialTier: RequireInt(m, "materialTier", path, id),
                BlocksMovement: RequireBool(m, "blocksMovement", path, id),
                BlocksLineOfFire: RequireBool(m, "blocksLineOfFire", path, id),
                ObstacleKind: RequireString(m, "obstacleKind", path, id),
                CoverPowerMilli: RequireInt(m, "coverPowerMilli", path, id),
                CoverRadius: RequireInt(m, "coverRadius", path, id),
                EntryStaminaMultiplierMilli: RequireInt(m, "entryStaminaMultiplierMilli", path, id),
                VisionRangeTiles: m.GetProperty("visionRangeTiles").ValueKind == JsonValueKind.Null
                    ? null
                    : m.GetProperty("visionRangeTiles").GetInt32(),
                ContainerId: m.TryGetProperty("containerId", out var cid) && cid.ValueKind == JsonValueKind.String
                    ? cid.GetString()
                    : null,
                WonderScope: m.TryGetProperty("wonderScope", out var ws) && ws.ValueKind == JsonValueKind.String
                    ? ws.GetString()
                    : null,
                WonderRarity: m.TryGetProperty("wonderRarity", out var wr) && wr.ValueKind == JsonValueKind.String
                    ? wr.GetString()
                    : null,
                WonderEffects: m.TryGetProperty("wonderEffects", out var we) && we.ValueKind == JsonValueKind.Array
                    ? we.EnumerateArray().Select(e => new WonderEffectMagnitude(
                        e.GetProperty("kind").GetString()!, e.GetProperty("scope").GetString()!,
                        e.GetProperty("valueMilli").GetInt64())).ToList()
                    : null,
                RelicCost: m.TryGetProperty("relicCost", out var rc) && rc.ValueKind == JsonValueKind.Number
                    ? rc.GetInt64()
                    : null);
        }

        return new StructureCorpusRow(id, name, role, requiredSlotKind, strengthBand, acquisitionPaths, controlPoint, magnitudes);
    }

    static string RequireString(JsonElement obj, string key, string path, string id) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new StructureCorpusLoadException(path, $"{id}: '{key}' is missing or not a string");

    static bool RequireBool(JsonElement obj, string key, string path, string id) =>
        obj.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : throw new StructureCorpusLoadException(path, $"{id}: '{key}' is missing or not a bool");

    static int RequireInt(JsonElement obj, string key, string path, string id) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : throw new StructureCorpusLoadException(path, $"{id}: '{key}' is missing or not a number");

    static long RequireLong(JsonElement obj, string key, string path, string id) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : throw new StructureCorpusLoadException(path, $"{id}: '{key}' is missing or not a number");

    /// <summary>Optional long wire field — missing or null reads as 0, never a load error.</summary>
    static long OptionalLong(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;
}
