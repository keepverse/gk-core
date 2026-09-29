namespace FusionRpg.Core.Narrative.Vocabulary;

/// <summary>`spec-narrative-vocabulary.md` §1, `narrative-seed/spec-character-vocab.md` §3: a character's
/// place in the world. <see cref="Allegiance"/> is the closed attribute (`player · ally · independent ·
/// antagonist`) the seed's §6 rules read; the `none` row carries none, because it means "no role fits".</summary>
public sealed record NarrativeRoleDef(string Id, string? Allegiance, string Description, string Negative);

/// <summary>`§1`, seed §3: the nine roles plus `none`, and the `leads` block keyed by lead token. The
/// member list is narrative-seed's, so it is compared against the file, never pinned here.</summary>
public static class NarrativeRoleCatalog
{
    const string File = "roles.v1.json";
    static readonly string[] Allegiances = { "player", "ally", "independent", "antagonist" };
    static IReadOnlyList<NarrativeRoleDef>? _all;
    static Dictionary<string, NarrativeRoleDef>? _byId;
    static IReadOnlyDictionary<string, string>? _leads;

    public static IReadOnlyList<NarrativeRoleDef> All => _all ?? throw NotConfigured();

    /// <summary>The lead tokens (`lead_summoner` → `player`, …): a lead is NOT one of the nine roles, so
    /// its allegiance comes from this block.</summary>
    public static IReadOnlyDictionary<string, string> Leads => _leads ?? throw NotConfigured();

    public static bool IsKnown(string? id) => id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static NarrativeRoleDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown narrative role '{id}'.");

    public static void Configure(IReadOnlyList<NarrativeRoleDef> rows, IReadOnlyDictionary<string, string> leads)
    {
        Validate(rows, leads);
        _all = rows;
        _byId = rows.ToDictionary(x => x.Id, StringComparer.Ordinal);
        _leads = leads;
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(NarrativeRoleCatalog)}.Configure(...) has not run.");

    public static void Validate(IReadOnlyList<NarrativeRoleDef> rows, IReadOnlyDictionary<string, string> leads)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "role id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            if (row.Id == "none")
            {
                if (row.Allegiance is not null)
                    throw new NarrativeVocabularyRejection($"{File}: 'none' must not carry an allegiance (no role fits).");
            }
            else if (row.Allegiance is null || !Allegiances.Contains(row.Allegiance))
            {
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{row.Id}'.allegiance '{row.Allegiance}' is not one of {string.Join(", ", Allegiances)}.");
            }
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
        if (!seen.Contains("none"))
            throw new NarrativeVocabularyRejection($"{File}: roles must carry the 'none' member (role is a voted field).");

        foreach (var lead in leads)
        {
            NarrativeRegistryIds.RequireLeadToken(lead.Key, "lead token", File);
            if (!Allegiances.Contains(lead.Value))
                throw new NarrativeVocabularyRejection(
                    $"{File}: lead '{lead.Key}'.allegiance '{lead.Value}' is not one of {string.Join(", ", Allegiances)}.");
        }
    }

    public static (IReadOnlyList<NarrativeRoleDef> Rows, IReadOnlyDictionary<string, string> Leads) Parse(string json)
    {
        // This file carries TWO top-level blocks — the `roles` map and the `leads` block (seed §3: a lead is
        // NOT one of the nine roles) — so it is parsed from the root, not from a single vocabulary wrapper.
        var root = NarrativeRegistryJson.Root(json, File);
        NarrativeRegistryJson.RejectUnknownKeys(
            root, new[] { "schemaVersion", "registryVersion", "_meta", "roles", "leads" }, "$", File);

        var rows = new List<NarrativeRoleDef>();
        if (!root.TryGetProperty("roles", out var roleRows) || roleRows.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new NarrativeVocabularyRejection($"{File}: missing 'roles' object.");
        foreach (var (id, row) in NarrativeRegistryJson.Rows(roleRows, File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(row, new[] { "description", "negative", "allegiance" }, "roles." + id, File);
            var text = NarrativeRegistryJson.Text(row, "roles." + id, File);
            rows.Add(new NarrativeRoleDef(id,
                NarrativeRegistryJson.OptionalString(row, "allegiance", "roles." + id, File), text.Description, text.Negative));
        }

        var leads = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("leads", out var leadRows) || leadRows.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new NarrativeVocabularyRejection($"{File}: missing 'leads' object.");
        foreach (var lead in leadRows.EnumerateObject())
        {
            if (lead.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                throw new NarrativeVocabularyRejection($"{File}: leads.{lead.Name} must be an allegiance string.");
            leads[lead.Name] = lead.Value.GetString()!;
        }
        return (rows, leads);
    }
}

/// <summary>`spec-narrative-vocabulary.md` §1, seed §5: what picks a line at runtime. `KeyedOn` is the
/// closed set `relation · personal-history · world-fact · outing`; `none` means no context fits.</summary>
public sealed record LineContextDef(string Id, string? KeyedOn, string Description, string Negative);

/// <summary>`§1`, seed §5.</summary>
public static class LineContextCatalog
{
    // The seed's FILE for this vocabulary is `line-contexts.v1.json`; the runtime spec's §1 table names
    // the same file, so only the voices file name differs between the two specs (see NR-F2).
    const string File = "line-contexts.v1.json";
    const string Key = "lineContexts";
    static readonly string[] KeyedOnValues = { "relation", "personal-history", "world-fact", "outing" };
    static IReadOnlyList<LineContextDef>? _all;
    static Dictionary<string, LineContextDef>? _byId;

    public static IReadOnlyList<LineContextDef> All => _all ?? throw NotConfigured();

    public static bool IsKnown(string? id) => id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static LineContextDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown line context '{id}'.");

    public static void Configure(IReadOnlyList<LineContextDef> rows)
    {
        Validate(rows);
        _all = rows;
        _byId = rows.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(LineContextCatalog)}.Configure(...) has not run.");

    public static void Validate(IReadOnlyList<LineContextDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "line context id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            if (row.Id == "none")
            {
                if (row.KeyedOn is not null)
                    throw new NarrativeVocabularyRejection($"{File}: 'none' must not carry a keyedOn (no context fits).");
            }
            else if (row.KeyedOn is null || !KeyedOnValues.Contains(row.KeyedOn))
            {
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{row.Id}'.keyedOn '{row.KeyedOn}' is not one of {string.Join(", ", KeyedOnValues)}.");
            }
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
        if (!seen.Contains("none"))
            throw new NarrativeVocabularyRejection($"{File}: line contexts must carry the 'none' member (the line call is voted).");
    }

    public static IReadOnlyList<LineContextDef> Parse(string json)
    {
        var rows = new List<LineContextDef>();
        foreach (var (id, row) in NarrativeRegistryJson.Rows(NarrativeRegistryJson.Vocabulary(json, Key, File), File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(row, new[] { "description", "negative", "keyedOn" }, id, File);
            var text = NarrativeRegistryJson.Text(row, id, File);
            rows.Add(new LineContextDef(id,
                NarrativeRegistryJson.OptionalString(row, "keyedOn", id, File), text.Description, text.Negative));
        }
        return rows;
    }
}

/// <summary>`spec-narrative-vocabulary.md` §1, seed §4: how a character talks. <see cref="Exemplar"/> is the
/// authored style reference the seed side names per register (optional, because `none` has none).</summary>
public sealed record VoiceRegisterDef(string Id, string? Exemplar, string Description, string Negative);

/// <summary>`§1`, seed §4. <b>File name:</b> the runtime spec's §1 table calls this file
/// `voice-registers.v1.json` while the seed spec that authors it (its Project structure) calls it
/// `voices.v1.json`. The reader reads the file the seed side writes and the discrepancy is filed as NR-F2
/// rather than silently resolved; the vocabulary key is `voices` either way.</summary>
public static class VoiceRegisterCatalog
{
    const string File = "voices.v1.json";
    const string Key = "voices";
    static IReadOnlyList<VoiceRegisterDef>? _all;
    static Dictionary<string, VoiceRegisterDef>? _byId;

    public static IReadOnlyList<VoiceRegisterDef> All => _all ?? throw NotConfigured();

    public static bool IsKnown(string? id) => id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static VoiceRegisterDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown voice register '{id}'.");

    public static void Configure(IReadOnlyList<VoiceRegisterDef> rows)
    {
        Validate(rows);
        _all = rows;
        _byId = rows.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(VoiceRegisterCatalog)}.Configure(...) has not run.");

    public static void Validate(IReadOnlyList<VoiceRegisterDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "voice register id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            if (row.Exemplar is not null && !row.Exemplar.EndsWith(".json", StringComparison.Ordinal))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}'.exemplar '{row.Exemplar}' must name a .json file.");
            if (row.Id == "none" && row.Exemplar is not null)
                throw new NarrativeVocabularyRejection($"{File}: 'none' must not name an exemplar (no register fits).");
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
        if (!seen.Contains("none"))
            throw new NarrativeVocabularyRejection($"{File}: voices must carry the 'none' member (voice is a voted field).");
    }

    public static IReadOnlyList<VoiceRegisterDef> Parse(string json)
    {
        var rows = new List<VoiceRegisterDef>();
        foreach (var (id, row) in NarrativeRegistryJson.Rows(NarrativeRegistryJson.Vocabulary(json, Key, File), File))
        {
            NarrativeRegistryJson.RejectUnknownKeys(row, new[] { "description", "negative", "exemplar" }, id, File);
            var text = NarrativeRegistryJson.Text(row, id, File);
            rows.Add(new VoiceRegisterDef(id,
                NarrativeRegistryJson.OptionalString(row, "exemplar", id, File), text.Description, text.Negative));
        }
        return rows;
    }
}
