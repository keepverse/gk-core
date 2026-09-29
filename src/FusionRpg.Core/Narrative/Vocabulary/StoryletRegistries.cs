using System.Text.Json;

namespace FusionRpg.Core.Narrative.Vocabulary;

/// <summary>
/// Thrown by every narrative registry parser and validator. One type, named per file — the
/// `DungeonRegistryRejection` shape (`gk-core/src/FusionRpg.Core/Dungeon/Registry/DungeonRegistries.cs:23-29`),
/// which is itself tunables-ssot T5's "a missing tunable is a load rejection, never a default" applied
/// to registries.
/// </summary>
public sealed class NarrativeVocabularyRejection : Exception
{
    public NarrativeVocabularyRejection(string message) : base(message) { }
}

/// <summary>Kebab/dotted wire-id checks shared by every narrative registry parser. Copied rather than
/// referenced from `DungeonRegistryIds` for the same reason that file copies `WorldIds`: a vocabulary
/// module must not depend on a sibling's string check.</summary>
public static class NarrativeRegistryIds
{
    /// <summary>A requirement FAMILY id is camelCase, not kebab: a requirement string is
    /// `&lt;family&gt;:&lt;value&gt;` and the families are `source`, `side`, `element`, `characterRole`
    /// (`narrative-seed/spec-storylet-vocab.md` §3.6). The VALUES stay wire ids.
    /// </summary>
    public static void RequireFamilyId(string? id, string label, string file)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new NarrativeVocabularyRejection($"{file}: {label} is empty.");
        var s = id!;
        if (s != s.Trim())
            throw new NarrativeVocabularyRejection($"{file}: {label} '{id}' must not have leading or trailing whitespace.");
        if (s[0] < 'a' || s[0] > 'z')
            throw new NarrativeVocabularyRejection($"{file}: {label} '{id}' must start with a lower-case letter.");
        foreach (var c in s)
            if (!(c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9'))
                throw new NarrativeVocabularyRejection(
                    $"{file}: {label} '{id}' must be camelCase (letters and digits only).");
    }

    /// <summary>A C# leaf NAME, not a wire id: `conditions.v1.json` names the runtime's `LeafId` member
    /// (`BandIs`) or a leaf it proposes (`RelationBandAtMost`), and the file's `proposedLeaves` block uses
    /// the same spelling — which is what lets a test tell "built" from "proposed" by name.</summary>
    public static void RequireLeafName(string? name, string label, string file)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new NarrativeVocabularyRejection($"{file}: {label} is empty.");
        var s = name!;
        if (s != s.Trim())
            throw new NarrativeVocabularyRejection($"{file}: {label} '{name}' must not have leading or trailing whitespace.");
        if (s[0] < 'A' || s[0] > 'Z')
            throw new NarrativeVocabularyRejection($"{file}: {label} '{name}' must start with an upper-case letter (a LeafId member name).");
        foreach (var c in s)
            if (!(c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9'))
                throw new NarrativeVocabularyRejection(
                    $"{file}: {label} '{name}' must be a C# identifier (letters and digits only).");
    }

    /// <summary>Lowercase letters, digits, hyphens, and dots as family separators — the wire-id grammar
    /// of `spec-narrative-vocabulary.md` §3 (`flag.set`, `character-state-is`). A registry id is stored,
    /// so it may never be an ordinal or a C# name.</summary>
    public static void RequireWireId(string? id, string label, string file) => RequireToken(id, label, file, allowUnderscore: false);

    /// <summary>A LEAD token (`lead_summoner`, `lead_companion`, `lead_antagonist`) is snake_case, not
    /// kebab: it is the names registry's own token spelling (seed §3, R11).</summary>
    public static void RequireLeadToken(string? id, string label, string file) => RequireToken(id, label, file, allowUnderscore: true);

    static void RequireToken(string? id, string label, string file, bool allowUnderscore)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new NarrativeVocabularyRejection($"{file}: {label} is empty.");
        var s = id!;
        if (s != s.Trim())
            throw new NarrativeVocabularyRejection($"{file}: {label} '{id}' must not have leading or trailing whitespace.");
        if (s[0] == '.' || s[0] == '-' || s[0] == '_' || s[^1] == '.' || s[^1] == '-' || s[^1] == '_')
            throw new NarrativeVocabularyRejection($"{file}: {label} '{id}' must not start or end with a separator.");
        var previous = '\0';
        foreach (var c in s)
        {
            var separator = c is '-' or '.' || (allowUnderscore && c == '_');
            var ok = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || separator;
            if (!ok)
                throw new NarrativeVocabularyRejection(
                    $"{file}: {label} '{id}' must be a lowercase wire id (letters, digits, '-', '.'{ (allowUnderscore ? ", '_'" : string.Empty) }).");
            if (separator && previous is '-' or '.' or '_')
                throw new NarrativeVocabularyRejection($"{file}: {label} '{id}' has an empty segment.");
            previous = c;
        }
    }
}

/// <summary>
/// The common registry-file shape of `narrative-seed/spec-storylet-vocab.md` §2:
/// <c>{schemaVersion, registryVersion, "&lt;vocabulary&gt;": {"&lt;value&gt;": {description, negative, ...}}}</c>.
/// The `&lt;vocabulary&gt;` key is the camelCase of the file stem, the `disposition.v1.json` /
/// <c>"disposition"</c> precedent; an unknown key, a missing row key or a wrong-typed value rejects
/// naming the file and the JSON path, never a silent default.
/// </summary>
static class NarrativeRegistryJson
{
    const int SupportedSchemaVersion = 1;

    public static JsonElement Root(string json, string file)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            throw new NarrativeVocabularyRejection($"{file}: not valid JSON — {ex.Message}");
        }

        // JsonDocument is IDisposable and the element outlives it only while the document does, so the
        // caller owns the document: this helper clones the root, which is a small one-off cost at process
        // start and the only shape that lets every catalog be a plain static Configure/read pair.
        var root = doc.RootElement.Clone();
        doc.Dispose();
        if (root.ValueKind != JsonValueKind.Object)
            throw new NarrativeVocabularyRejection($"{file}: root must be an object.");
        var version = RequireInt(root, "schemaVersion", "$", file);
        if (version != SupportedSchemaVersion)
            throw new NarrativeVocabularyRejection(
                $"{file}: schemaVersion {version} is not supported (this reader understands {SupportedSchemaVersion}).");
        return root;
    }

    public static JsonElement Vocabulary(string json, string key, string file)
    {
        var root = Root(json, file);
        if (!root.TryGetProperty(key, out var vocab))
            throw new NarrativeVocabularyRejection($"{file}: missing '{key}'.");
        if (vocab.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new NarrativeVocabularyRejection($"{file}: '{key}' must be an object or an array of rows.");
        return vocab;
    }

    public static void RejectUnknownKeys(JsonElement row, IReadOnlyCollection<string> allowed, string path, string file)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (allowed.Contains(property.Name)) continue;
            throw new NarrativeVocabularyRejection(
                $"{file}: {path} has unknown key '{property.Name}' (allowed: {string.Join(", ", allowed)}).");
        }
    }

    public static int RequireInt(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var i))
            throw new NarrativeVocabularyRejection($"{file}: {path}.{key} must be an integer.");
        return i;
    }

    public static string RequireString(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            throw new NarrativeVocabularyRejection($"{file}: {path}.{key} must be a string.");
        return value.GetString()!;
    }

    public static string? OptionalString(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new NarrativeVocabularyRejection($"{file}: {path}.{key} must be a string or null.");
        return value.GetString();
    }

    public static bool RequireBool(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new NarrativeVocabularyRejection($"{file}: {path}.{key} must be a boolean.");
        return value.GetBoolean();
    }

    public static IReadOnlyList<string> StringArray(JsonElement parent, string key, string path, string file)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new NarrativeVocabularyRejection($"{file}: {path}.{key} must be an array.");
        var rows = new List<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new NarrativeVocabularyRejection($"{file}: {path}.{key}[{index}] must be a string.");
            rows.Add(item.GetString()!);
            index++;
        }
        return rows;
    }

    /// <summary>Every row of a vocabulary object, in FILE order — `teaches.v1.json`'s array order IS the
    /// teaching order (`narrative-seed/spec-storylet-vocab.md` §3.8), and System.Text.Json preserves an
    /// object's declared order.</summary>
    /// <summary>
    /// The vocabulary's rows, in either shape the two sides of this contract use: the KEYED OBJECT
    /// `narrative-seed/spec-storylet-vocab.md` §2 specifies (`"&lt;value&gt;": {...}`, the
    /// `disposition.v1.json` precedent, which `gk-core/tests/fixtures/narrative/_registry/**` and
    /// `doctrines.v1.json` carry) or an ARRAY of rows each carrying its own `id` — the shape
    /// narrative-seed's `storylet_vocab` adapter actually emitted into
    /// `gk-data/packs/fusion/data/seed/narrative/_registry/**` (8 of the 9 files). The array form is normalised here by removing
    /// `id`, so every catalog's own unknown-key check sees exactly the row keys its spec names, whichever
    /// shape the file used.
    ///
    /// <para><b>Reconciliation, not a widened rule:</b> both shapes carry the same rows and the same keys,
    /// and the corpus-vs-spec drift is FILED (the files do not conform to §2; the adapter's prose is
    /// model-authored, so "fix the generator and regenerate" is narrative-seed's re-authoring, not a
    /// mechanical regeneration). A file that is neither shape still refuses.</para>
    /// </summary>
    public static IEnumerable<(string Id, JsonElement Row)> Rows(JsonElement vocabulary, string file)
    {
        if (vocabulary.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in vocabulary.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new NarrativeVocabularyRejection($"{file}: every row in the array must be an object.");
                if (!element.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(idElement.GetString()))
                    throw new NarrativeVocabularyRejection($"{file}: every row in the array must carry a non-empty string 'id'.");

                var normalized = new System.Text.Json.Nodes.JsonObject();
                foreach (var property in element.EnumerateObject())
                    if (property.Name != "id")
                        normalized[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());

                yield return (idElement.GetString()!, System.Text.Json.JsonSerializer.SerializeToElement(normalized));
            }

            yield break;
        }

        foreach (var property in vocabulary.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                throw new NarrativeVocabularyRejection($"{file}: '{property.Name}' must be an object.");
            yield return (property.Name, property.Value);
        }
    }

    /// <summary>The `description`/`negative` pair every row carries (§2): separate keys, so a test can
    /// prove the negative clause exists rather than trusting a comment.</summary>
    public static (string Description, string Negative) Text(JsonElement row, string path, string file) =>
        (RequireString(row, "description", path, file), RequireString(row, "negative", path, file));

    public static void RequireDistinct(IReadOnlyList<string> values, string label, string path, string file)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!seen.Add(value))
                throw new NarrativeVocabularyRejection($"{file}: {path}.{label} repeats '{value}'.");
    }
}

/// <summary>`spec-narrative-vocabulary.md` §1, `narrative-seed/spec-storylet-vocab.md` §3.2: a choice kind —
/// what the player can do, and the machinery it resolves through. `Gate` is the intrinsic predicate gate
/// (`none`, or `intrinsic` for a kind gated by built machinery); `ArgFamily` is the closed argument family
/// when the kind takes one.</summary>
public sealed record ChoiceKindDef(string Id, string Gate, string? ArgFamily, string Description, string Negative);

/// <summary>`§1`, seed §3.2. Seven closed families: `supplyTag`, `stock`, `element`, plus the `none` that a
/// kind without an argument carries.</summary>
public static class ChoiceKindCatalog
{
    const string File = "choice-kinds.v1.json";
    const string Key = "choiceKinds";
    static readonly string[] Gates = { "none", "intrinsic" };
    static readonly string[] ArgFamilies = { "none", "supplyTag", "stock", "element" };
    static IReadOnlyList<ChoiceKindDef>? _all;
    static Dictionary<string, ChoiceKindDef>? _byId;

    public static IReadOnlyList<ChoiceKindDef> All => _all ?? throw NotConfigured();

    public static bool IsKnown(string? id) =>
        id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static ChoiceKindDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown choice kind '{id}'.");

    public static void Configure(IReadOnlyList<ChoiceKindDef> rows)
    {
        var validated = Validate(rows);
        _all = validated;
        _byId = validated.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(ChoiceKindCatalog)}.Configure(...) has not run.");

    public static IReadOnlyList<ChoiceKindDef> Validate(IReadOnlyList<ChoiceKindDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "choice kind id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            if (!Gates.Contains(row.Gate))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}'.gate '{row.Gate}' is not one of {string.Join(", ", Gates)}.");
            if (row.ArgFamily is null || !ArgFamilies.Contains(row.ArgFamily))
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{row.Id}'.argFamily '{row.ArgFamily}' is not one of {string.Join(", ", ArgFamilies)}.");
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
        return rows;
    }

    public static IReadOnlyList<ChoiceKindDef> Parse(string json)
    {
        var rows = new List<ChoiceKindDef>();
        foreach (var (id, row) in NarrativeRegistryJson.Rows(NarrativeRegistryJson.Vocabulary(json, Key, File), File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(row, new[] { "description", "negative", "gate", "argFamily" }, id, File);
            var text = NarrativeRegistryJson.Text(row, id, File);
            rows.Add(new ChoiceKindDef(id, NarrativeRegistryJson.RequireString(row, "gate", id, File),
                NarrativeRegistryJson.RequireString(row, "argFamily", id, File), text.Description, text.Negative));
        }
        return rows;
    }
}

/// <summary>`spec-narrative-vocabulary.md` §1, seed §3.5: what an outcome does beyond its effects. The
/// kind declares whether `ref` is required and in which prefixed forms (`RefForms`) and which closed
/// `param` values it allows (`Params`) — the round-3 owner ruling's `{kind, ref, param}` object.</summary>
public sealed record ConsequenceKindDef(
    string Id,
    IReadOnlyList<string> RefForms,
    IReadOnlyList<string> Params,
    string RoutesTo,
    string Description,
    string Negative);

/// <summary>`§1`, seed §3.5: the four that exist today plus the seven the storylet engine routes.</summary>
public static class ConsequenceKindCatalog
{
    const string File = "consequence-kinds.v1.json";
    const string Key = "consequenceKinds";

    /// <summary>The five relation fact kinds `relation.shift` may carry — the ledger's own vocabulary,
    /// closed here so a storylet cannot name a shift the ledger cannot derive a band from.</summary>
    public static readonly IReadOnlyList<string> RelationShiftParams =
        new[] { "met", "helped", "refused", "betrayed", "spared" };

    static IReadOnlyList<ConsequenceKindDef>? _all;
    static Dictionary<string, ConsequenceKindDef>? _byId;

    public static IReadOnlyList<ConsequenceKindDef> All => _all ?? throw NotConfigured();

    public static bool IsKnown(string? id) => id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static ConsequenceKindDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown consequence kind '{id}'.");

    public static void Configure(IReadOnlyList<ConsequenceKindDef> rows)
    {
        var validated = Validate(rows);
        _all = validated;
        _byId = validated.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(ConsequenceKindCatalog)}.Configure(...) has not run.");

    public static IReadOnlyList<ConsequenceKindDef> Validate(IReadOnlyList<ConsequenceKindDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "consequence kind id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            foreach (var form in row.RefForms)
            {
                var colon = form.IndexOf(':');
                if (colon <= 0 || colon == form.Length - 1)
                    throw new NarrativeVocabularyRejection(
                        $"{File}: '{row.Id}'.refForms entry '{form}' must be a '<prefix>:<value>' form.");
            }
            if (row.Params.Count == 0)
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}'.params must name a value ('none' when it takes no param).");
            NarrativeRegistryJson.RequireDistinct(row.Params, "params", row.Id, File);
            foreach (var param in row.Params)
                NarrativeRegistryIds.RequireWireId(param, $"{row.Id}.param", File);
            if (row.Id == "relation.shift" && !row.Params.OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(RelationShiftParams.OrderBy(x => x, StringComparer.Ordinal)))
                throw new NarrativeVocabularyRejection(
                    $"{File}: 'relation.shift'.params must be exactly {string.Join(", ", RelationShiftParams)}.");
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
        return rows;
    }

    public static IReadOnlyList<ConsequenceKindDef> Parse(string json)
    {
        var rows = new List<ConsequenceKindDef>();
        foreach (var (id, row) in NarrativeRegistryJson.Rows(NarrativeRegistryJson.Vocabulary(json, Key, File), File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(
                row, new[] { "description", "negative", "refForms", "params", "routesTo" }, id, File);
            var text = NarrativeRegistryJson.Text(row, id, File);
            rows.Add(new ConsequenceKindDef(id,
                NarrativeRegistryJson.StringArray(row, "refForms", id, File),
                NarrativeRegistryJson.StringArray(row, "params", id, File),
                NarrativeRegistryJson.RequireString(row, "routesTo", id, File),
                text.Description, text.Negative));
        }
        return rows;
    }
}

/// <summary>`spec-narrative-vocabulary.md` §1, seed §3.4: an extra gate on a conditioned slot.
/// <see cref="Leaf"/> is the `LeafId` the condition compiles to, or null for a condition resolved by
/// casting (`role-cast`) rather than by a predicate leaf.</summary>
public sealed record ConditionDef(
    string Id,
    string? ArgFamily,
    IReadOnlyList<string> UsableIn,
    string? Leaf,
    string Description,
    string Negative);

/// <summary>`§1`, seed §3.4. The leaf half is validated against the built `LeafId` enum
/// (`gk-core/src/FusionRpg.Core/Effects/Atoms/PredicateNode.cs:28-46`) plus the file's own `proposedLeaves`
/// block: a leaf the runtime has landed may NOT still be listed as proposed, which is what tells the
/// seed file to move the day the runtime ships it.</summary>
public static class ConditionCatalog
{
    const string File = "conditions.v1.json";
    const string Key = "conditions";
    static readonly string[] UsableInValues = { "slot", "eligibility" };
    static readonly string[] ArgFamilies =
    {
        "none", "dangerBand", "disposition", "characterState", "storyFlag", "levelGate", "roleId",
    };

    static IReadOnlyList<ConditionDef>? _all;
    static Dictionary<string, ConditionDef>? _byId;
    static IReadOnlyList<string>? _proposedLeaves;

    public static IReadOnlyList<ConditionDef> All => _all ?? throw NotConfigured();

    /// <summary>The leaves the file declares as NOT yet built, in file order. Read by
    /// `narrative-predicates` when it lands one: the file must then drop it from here.</summary>
    public static IReadOnlyList<string> ProposedLeaves => _proposedLeaves ?? throw NotConfigured();

    public static bool IsKnown(string? id) => id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static ConditionDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown condition id '{id}'.");

    public static void Configure(IReadOnlyList<ConditionDef> rows, IReadOnlyList<string> proposedLeaves)
    {
        var validated = Validate(rows, proposedLeaves);
        _all = validated;
        _byId = validated.ToDictionary(x => x.Id, StringComparer.Ordinal);
        _proposedLeaves = proposedLeaves;
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(ConditionCatalog)}.Configure(...) has not run.");

    public static IReadOnlyList<ConditionDef> Validate(IReadOnlyList<ConditionDef> rows, IReadOnlyList<string> proposedLeaves)
    {
        var built = Enum.GetNames(typeof(FusionRpg.Core.Effects.Atoms.LeafId)).ToHashSet(StringComparer.Ordinal);
        var proposed = proposedLeaves.ToHashSet(StringComparer.Ordinal);
        if (proposed.Count != proposedLeaves.Count)
            throw new NarrativeVocabularyRejection($"{File}: proposedLeaves repeats a leaf.");
        foreach (var leaf in proposedLeaves)
        {
            NarrativeRegistryIds.RequireLeafName(leaf, "proposedLeaves entry", File);
            if (built.Contains(leaf))
                throw new NarrativeVocabularyRejection(
                    $"{File}: proposedLeaves lists '{leaf}', but the runtime has landed that LeafId — remove it " +
                    "from proposedLeaves and let the condition compile to the built leaf.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "condition id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            if (row.ArgFamily is null || !ArgFamilies.Contains(row.ArgFamily))
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{row.Id}'.argFamily '{row.ArgFamily}' is not one of {string.Join(", ", ArgFamilies)}.");
            if (row.UsableIn.Count == 0)
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}'.usableIn must name at least one of {string.Join(", ", UsableInValues)}.");
            NarrativeRegistryJson.RequireDistinct(row.UsableIn, "usableIn", row.Id, File);
            foreach (var usable in row.UsableIn)
                if (!UsableInValues.Contains(usable))
                    throw new NarrativeVocabularyRejection(
                        $"{File}: '{row.Id}'.usableIn '{usable}' is not one of {string.Join(", ", UsableInValues)}.");
            if (row.Leaf is not null)
            {
                NarrativeRegistryIds.RequireLeafName(row.Leaf, $"{row.Id}.leaf", File);
                if (!built.Contains(row.Leaf) && !proposed.Contains(row.Leaf))
                    throw new NarrativeVocabularyRejection(
                        $"{File}: '{row.Id}'.leaf '{row.Leaf}' is neither a built LeafId nor listed in proposedLeaves.");
            }
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
        return rows;
    }

    public static (IReadOnlyList<ConditionDef> Rows, IReadOnlyList<string> ProposedLeaves) ParseDocument(string json)
    {
        var root = NarrativeRegistryJson.Root(json, File);
        if (!root.TryGetProperty("proposedLeaves", out var proposedElement) || proposedElement.ValueKind != JsonValueKind.Array)
            throw new NarrativeVocabularyRejection($"{File}: missing 'proposedLeaves' array.");
        var proposed = new List<string>();
        foreach (var item in proposedElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new NarrativeVocabularyRejection($"{File}: proposedLeaves entry must be a string.");
            proposed.Add(item.GetString()!);
        }

        var rows = new List<ConditionDef>();
        foreach (var (id, row) in NarrativeRegistryJson.Rows(NarrativeRegistryJson.Vocabulary(json, Key, File), File))
        {
            // `compilesTo` is the SEED side's own key name (spec-storylet-vocab.md §3.4's table, and what the
            // committed corpus writes); `leaf` is the spelling this reader's fixtures used before the corpus
            // landed. Both name the same thing, so both are read and the consumer sees one value.
            NarrativeRegistryJson.RejectUnknownKeys(
                row, new[] { "description", "negative", "argFamily", "usableIn", "compilesTo", "leaf" }, id, File);
            var text = NarrativeRegistryJson.Text(row, id, File);
            var rawLeaf = NarrativeRegistryJson.OptionalString(row, "compilesTo", id, File)
                ?? NarrativeRegistryJson.OptionalString(row, "leaf", id, File);
            rows.Add(new ConditionDef(id,
                NarrativeRegistryJson.RequireString(row, "argFamily", id, File),
                NarrativeRegistryJson.StringArray(row, "usableIn", id, File),
                HostKindCatalog.NormaliseCompiledLeaf(rawLeaf),
                text.Description, text.Negative));
        }
        return (rows, proposed);
    }

    public static IReadOnlyList<ConditionDef> Parse(string json) => ParseDocument(json).Rows;
}

/// <summary>`spec-narrative-vocabulary.md` §1 (Audit 2026-09-19), seed §3.6: who can fill a storylet role.
/// Two closed blocks in one file — the role kinds, and the requirement families a requirement string
/// `&lt;family&gt;:&lt;value&gt;` may name.</summary>
public sealed record RoleKindDef(string Id, string Description, string Negative);

public sealed record RequireFamilyDef(string Id, IReadOnlyList<string> Values, string? ValuesFrom, string Description, string Negative);

public static class RoleTagCatalog
{
    const string File = "role-tags.v1.json";
    const string Key = "roleTags";
    static IReadOnlyList<RoleKindDef>? _roleKinds;
    static IReadOnlyList<RequireFamilyDef>? _families;

    public static IReadOnlyList<RoleKindDef> RoleKinds => _roleKinds ?? throw NotConfigured();

    public static IReadOnlyList<RequireFamilyDef> RequireFamilies => _families ?? throw NotConfigured();

    public static bool IsKnownRoleKind(string? id) =>
        id != null && (_roleKinds ?? throw NotConfigured()).Any(x => x.Id == id);

    public static bool IsKnownFamily(string? id) =>
        id != null && (_families ?? throw NotConfigured()).Any(x => x.Id == id);

    public static void Configure(IReadOnlyList<RoleKindDef> roleKinds, IReadOnlyList<RequireFamilyDef> families)
    {
        Validate(roleKinds, families);
        _roleKinds = roleKinds;
        _families = families;
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(RoleTagCatalog)}.Configure(...) has not run.");

    public static void Validate(IReadOnlyList<RoleKindDef> roleKinds, IReadOnlyList<RequireFamilyDef> families)
    {
        // `none` is a member on purpose: the structure call picks a role kind, so a model-facing list
        // must be able to say "no role kind fits" (map principle 4, seed §3.6).
        var seenKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in roleKinds)
        {
            NarrativeRegistryIds.RequireWireId(kind.Id, "role kind id", File);
            if (!seenKinds.Add(kind.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate role kind '{kind.Id}'.");
            if (string.IsNullOrWhiteSpace(kind.Description) || string.IsNullOrWhiteSpace(kind.Negative))
                throw new NarrativeVocabularyRejection($"{File}: role kind '{kind.Id}' must carry a description and a negative clause.");
        }
        if (!seenKinds.Contains("none"))
            throw new NarrativeVocabularyRejection($"{File}: roleKinds must carry the 'none' member (the structure call names it).");

        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in families)
        {
            NarrativeRegistryIds.RequireFamilyId(family.Id, "requireFamily id", File);
            if (!seenFamilies.Add(family.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate requireFamily '{family.Id}'.");
            if (family.Values.Count == 0 && family.ValuesFrom is null)
                throw new NarrativeVocabularyRejection(
                    $"{File}: requireFamily '{family.Id}' must name values or a valuesFrom registry.");
            NarrativeRegistryJson.RequireDistinct(family.Values, "values", family.Id, File);
            foreach (var value in family.Values)
                NarrativeRegistryIds.RequireWireId(value, $"{family.Id}.value", File);
            if (string.IsNullOrWhiteSpace(family.Description) || string.IsNullOrWhiteSpace(family.Negative))
                throw new NarrativeVocabularyRejection($"{File}: requireFamily '{family.Id}' must carry a description and a negative clause.");
        }
        return;
    }

    public static (IReadOnlyList<RoleKindDef> RoleKinds, IReadOnlyList<RequireFamilyDef> Families) Parse(string json)
    {
        // The seed side writes this file FLAT — `roleKinds`, `requireFamilies` and `requirementShape` at the
        // ROOT (narrative-seed/spec-storylet-vocab.md §3.6, and what the committed corpus carries) — while the
        // fixtures wrap the two blocks under `roleTags`. Both are read. `requirementShape` is the seed side's
        // own declaration of the `requires` string shape; this reader does not consume it, because the value
        // grammar is enforced below by RequireFamilyId/RequireWireId.
        var root = NarrativeRegistryJson.Root(json, File);
        var isWrapped = root.TryGetProperty(Key, out var wrapped) && wrapped.ValueKind == JsonValueKind.Object;
        var vocabulary = isWrapped ? wrapped : root;
        NarrativeRegistryJson.RejectUnknownKeys(
            vocabulary,
            isWrapped
                ? new[] { "roleKinds", "requireFamilies" }
                : new[] { "roleKinds", "requireFamilies", "requirementShape", "schemaVersion", "registryVersion", "_meta" },
            "$", File);

        var kinds = new List<RoleKindDef>();
        var kindsElement = vocabulary;
        if (!kindsElement.TryGetProperty("roleKinds", out var kindRows)
            || kindRows.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new NarrativeVocabularyRejection($"{File}: missing 'roleKinds' block (object or array of rows).");
        foreach (var (id, row) in NarrativeRegistryJson.Rows(kindRows, File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(row, new[] { "description", "negative" }, "roleKinds." + id, File);
            var text = NarrativeRegistryJson.Text(row, "roleKinds." + id, File);
            kinds.Add(new RoleKindDef(id, text.Description, text.Negative));
        }

        var families = new List<RequireFamilyDef>();
        if (!kindsElement.TryGetProperty("requireFamilies", out var familyRows)
            || familyRows.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new NarrativeVocabularyRejection($"{File}: missing 'requireFamilies' block (object or array of rows).");
        foreach (var (id, row) in NarrativeRegistryJson.Rows(familyRows, File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(
                row, new[] { "description", "negative", "values", "valuesFrom" }, "requireFamilies." + id, File);
            var text = NarrativeRegistryJson.Text(row, "requireFamilies." + id, File);
            var values = row.TryGetProperty("values", out _)
                ? NarrativeRegistryJson.StringArray(row, "values", "requireFamilies." + id, File)
                : Array.Empty<string>();
            families.Add(new RequireFamilyDef(id, values,
                NarrativeRegistryJson.OptionalString(row, "valuesFrom", "requireFamilies." + id, File),
                text.Description, text.Negative));
        }
        return (kinds, families);
    }
}

/// <summary>`spec-narrative-vocabulary.md` §1 (owner ruling 2026-09-20: the story is also the tutorial),
/// seed §3.8: a mechanic or loop a piece of story may teach.</summary>
public sealed record TeachesDef(
    string Id,
    string Loop,
    IReadOnlyList<string> Carriers,
    IReadOnlyList<string> Requires,
    string TeachingLine,
    string Description,
    string Negative);

/// <summary>`§1`, seed §3.8. `All` is in FILE order — that order IS the teaching order `arc-shapes` §5
/// uses to order the spine's chapters, so it is part of the contract, not a presentation detail.
/// Planner-only: the list carries no `none` (an empty `teaches` means the content teaches nothing).</summary>
public static class TeachesCatalog
{
    const string File = "teaches.v1.json";
    const string Key = "teaches";
    static readonly string[] Carriers = { "spine", "storylet" };
    static IReadOnlyList<TeachesDef>? _all;

    public static IReadOnlyList<TeachesDef> All => _all ?? throw new InvalidOperationException(
        $"{nameof(TeachesCatalog)}.Configure(...) has not run.");

    public static bool IsKnown(string? id) => id != null && All.Any(x => x.Id == id);

    public static TeachesDef Get(string id) =>
        All.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException($"Unknown teaches id '{id}'.");

    public static void Configure(IReadOnlyList<TeachesDef> rows) => _all = Validate(rows);

    public static IReadOnlyList<TeachesDef> Validate(IReadOnlyList<TeachesDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "teaches id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            if (row.Carriers.Count == 0)
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}'.carriers must name at least one of {string.Join(", ", Carriers)}.");
            NarrativeRegistryJson.RequireDistinct(row.Carriers, "carriers", row.Id, File);
            foreach (var carrier in row.Carriers)
                if (!Carriers.Contains(carrier))
                    throw new NarrativeVocabularyRejection(
                        $"{File}: '{row.Id}'.carriers entry '{carrier}' is not one of {string.Join(", ", Carriers)}.");
            // The teaching sentence is AUTHORED here and never model-written: it is a rules claim, and a
            // model that paraphrases it can get the rules wrong (seed §3.8).
            if (string.IsNullOrWhiteSpace(row.TeachingLine))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry an authored teachingLine.");
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
        return rows;
    }

    public static IReadOnlyList<TeachesDef> Parse(string json)
    {
        var rows = new List<TeachesDef>();
        foreach (var (id, row) in NarrativeRegistryJson.Rows(NarrativeRegistryJson.Vocabulary(json, Key, File), File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(
                row, new[] { "description", "negative", "loop", "carriers", "requires", "teachingLine" }, id, File);
            var text = NarrativeRegistryJson.Text(row, id, File);
            rows.Add(new TeachesDef(id,
                NarrativeRegistryJson.RequireString(row, "loop", id, File),
                NarrativeRegistryJson.StringArray(row, "carriers", id, File),
                row.TryGetProperty("requires", out _)
                    ? NarrativeRegistryJson.StringArray(row, "requires", id, File)
                    : Array.Empty<string>(),
                NarrativeRegistryJson.RequireString(row, "teachingLine", id, File),
                text.Description, text.Negative));
        }
        return rows;
    }
}

/// <summary>`spec-narrative-vocabulary.md` §2 (members defined by the runtime), seed §3.1 (the file):
/// a place that can show a storylet. `Clock` is the host clock kind that place counts on; it is optional
/// in the file because the runtime owns the clock table (§2) and the seed spec's own row shape has no
/// clock column, so <see cref="HostKindCatalog.ClockOf"/> falls back to that table.</summary>
public sealed record HostKindDef(
    string Id,
    string Place,
    string? RoomKind,
    IReadOnlyList<string> Admits,
    bool ClimateNeutral,
    string ClimateSource,
    IReadOnlyList<string> Climates,
    HostClockKind? Clock,
    string Description,
    string Negative);

/// <summary>`§1`/`§2`: the 16 host kinds. The member set is the runtime's own declaration — each member is
/// a place that exists in code — so the count is pinned by a test rather than read from the file, and a new
/// place is a reviewed change here.</summary>
public static class HostKindCatalog
{
    const string File = "host-kinds.v1.json";
    const string Key = "hostKinds";
    static readonly string[] Places = { "delve", "homeworld", "world", "expedition" };
    static readonly string[] ClimateSources = { "none", "room", "sector" };
    static readonly string[] Climates =
    {
        "none", "earth", "fire", "ice", "air", "light", "dark",
    };

    /// <summary>§2's clock column, verbatim: the Delve's room kinds count on the room clock, every world
    /// host on the world turn clock, the expedition return on its collect, the homeworld on its return.</summary>
    static readonly Dictionary<string, HostClockKind> ClockTable = new(StringComparer.Ordinal)
    {
        ["delve.curio"] = HostClockKind.DelveRoom,
        ["delve.shrine"] = HostClockKind.DelveRoom,
        ["delve.trap"] = HostClockKind.DelveRoom,
        ["delve.wild"] = HostClockKind.DelveRoom,
        ["delve.merchant"] = HostClockKind.DelveRoom,
        ["delve.unknown"] = HostClockKind.DelveRoom,
        ["delve.rest"] = HostClockKind.DelveRoom,
        ["world.shrine"] = HostClockKind.WorldTurn,
        ["world.anomaly"] = HostClockKind.WorldTurn,
        ["world.tear"] = HostClockKind.WorldTurn,
        ["world.vault"] = HostClockKind.WorldTurn,
        ["world.market"] = HostClockKind.WorldTurn,
        ["world.wildland"] = HostClockKind.WorldTurn,
        ["world.petition"] = HostClockKind.WorldTurn,
        ["expedition.return"] = HostClockKind.ExpeditionCollect,
        ["sanctum.hub"] = HostClockKind.SanctumReturn,
    };

    static IReadOnlyList<HostKindDef>? _all;
    static Dictionary<string, HostKindDef>? _byId;

    public static IReadOnlyList<HostKindDef> All => _all ?? throw NotConfigured();

    public static bool IsKnown(string? id) => id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static HostKindDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown host kind '{id}'.");

    /// <summary>The clock this host counts on. The file's own `clock` wins when it carries one; otherwise
    /// §2's table does. An unknown host kind throws — never a default clock, because a storylet counted on
    /// the wrong clock fires at the wrong time.</summary>
    public static HostClockKind ClockOf(string hostKindId)
    {
        var row = Get(hostKindId);
        if (row.Clock is { } fromFile) return fromFile;
        return ClockTable.TryGetValue(hostKindId, out var clock)
            ? clock
            : throw new NarrativeVocabularyRejection(
                $"{File}: host kind '{hostKindId}' has no clock row (add it to §2's table and to this catalog).");
    }

    public static void Configure(IReadOnlyList<HostKindDef> rows)
    {
        var validated = Validate(rows);
        _all = validated;
        _byId = validated.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(HostKindCatalog)}.Configure(...) has not run.");

    public static IReadOnlyList<HostKindDef> Validate(IReadOnlyList<HostKindDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "host kind id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            if (!Places.Contains(row.Place))
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{row.Id}'.place '{row.Place}' is not one of {string.Join(", ", Places)}.");
            if (row.RoomKind is null && row.Place == "delve")
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' is a Delve host, so it must name a roomKind.");
            if (row.RoomKind is not null && row.Place != "delve")
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{row.Id}' is not a Delve host, so its roomKind must be null (§3.1).");
            if (row.Admits.Count == 0)
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}'.admits must name at least one storylet kind.");
            NarrativeRegistryJson.RequireDistinct(row.Admits, "admits", row.Id, File);
            if (!ClimateSources.Contains(row.ClimateSource))
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{row.Id}'.climateSource '{row.ClimateSource}' is not one of {string.Join(", ", ClimateSources)}.");
            if (row.ClimateNeutral && row.ClimateSource != "none")
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{row.Id}' is climateNeutral, so its climateSource must be 'none'.");
            // An EMPTY `climates` is legal and meaningful, not a defect: narrative-seed's own spec §3.1 says
            // "an empty `climates` (`world.anomaly`) means the host fires nowhere today and the planner
            // declares no cell for it". Requiring a non-empty list here rejected the corpus the runtime must
            // read (`gk-data/packs/fusion/data/seed/narrative/_registry/host-kinds.v1.json` ships `climates: []` on the several
            // rows that have no map cell yet) — the rule was over-tightened on this side, and the seed side's
            // words are the contract for what a host may declare.
            NarrativeRegistryJson.RequireDistinct(row.Climates, "climates", row.Id, File);
            foreach (var climate in row.Climates)
                if (!Climates.Contains(climate))
                    throw new NarrativeVocabularyRejection(
                        $"{File}: '{row.Id}'.climates entry '{climate}' is not one of {string.Join(", ", Climates)}.");
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
        return rows;
    }

    public static IReadOnlyList<HostKindDef> Parse(string json)
    {
        var rows = new List<HostKindDef>();
        foreach (var (id, row) in NarrativeRegistryJson.Rows(NarrativeRegistryJson.Vocabulary(json, Key, File), File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(
                row,
                new[] { "description", "negative", "place", "roomKind", "admits", "climateNeutral", "climateSource", "climates", "clock" },
                id, File);
            var text = NarrativeRegistryJson.Text(row, id, File);
            HostClockKind? clock = null;
            var clockId = NarrativeRegistryJson.OptionalString(row, "clock", id, File);
            if (clockId is not null)
            {
                if (!HostClockKindIds.TryParse(clockId, out var parsed))
                    throw new NarrativeVocabularyRejection(
                        $"{File}: '{id}'.clock '{clockId}' is not a HostClockKind ({string.Join(", ", Enum.GetNames(typeof(HostClockKind)))}).");
                clock = parsed;
            }
            rows.Add(new HostKindDef(id,
                NarrativeRegistryJson.RequireString(row, "place", id, File),
                // `"none"` and JSON null both mean "this row names no room kind" — the seed side's spec §3.1
                // spells the sentinel as the word `none` ("`roomKind` is `none` on every non-Delve row") and
                // the committed corpus writes exactly that, while the fixture files wrote null. Normalised to
                // null here so a consumer never has to know which spelling a file used.
                NormaliseSentinel(NarrativeRegistryJson.OptionalString(row, "roomKind", id, File)),
                NarrativeRegistryJson.StringArray(row, "admits", id, File),
                NarrativeRegistryJson.RequireBool(row, "climateNeutral", id, File),
                NarrativeRegistryJson.RequireString(row, "climateSource", id, File),
                NarrativeRegistryJson.StringArray(row, "climates", id, File),
                clock,
                text.Description, text.Negative));
        }
        return rows;
    }

    /// <summary>The registry's own sentinel words, read as absence: `none` (the same word
    /// `climateSource: "none"` uses) and `nothing` (`spec-storylet-vocab.md` §3.4's `compilesTo` value for the
    /// `none` condition row). A consumer reads null either way, so the spellings cannot drift into different
    /// behaviours.</summary>
    internal static string? NormaliseSentinel(string? value) =>
        value is "none" or "nothing" ? null : value;

    /// <summary>
    /// The seed side's own marker for "casting resolves this, so no leaf": its validator and its test both
    /// skip a `compilesTo` that STARTS WITH `role requirement`
    /// (`gk-forge/tools/seedsmith/seedsmith/adapters/narrative/storylet_vocab.py:309-310`, the comment there reads
    /// "casting resolves a role requirement"; `gk-forge/tools/seedsmith/tests/test_narrative_storylet_vocab.py:193-194`).
    /// That convention is honoured as ABSENCE — the same value the `nothing` sentinel produces — and only that
    /// prefix: any other text still refuses, so a mistyped leaf name cannot become a silent "no leaf".
    /// </summary>
    internal static string? NormaliseCompiledLeaf(string? value) =>
        value is not null && value.StartsWith("role requirement", StringComparison.Ordinal)
            ? null
            : NormaliseSentinel(value);
}
