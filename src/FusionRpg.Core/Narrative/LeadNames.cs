using System.Text.Json;
using System.Text.RegularExpressions;

namespace FusionRpg.Core.Narrative;

/// <summary>
/// The closed <c>article</c> tag of a names-registry row: whether the name reads with "the". It is
/// never folded into <see cref="LeadNameRow.Display"/> — a message selects on this tag instead
/// (<c>docs/architecture/narrative-seed-ideal.md</c> §6.5b), so "the Garden Keeper" at the start of a
/// sentence and the vocative "Keeper" both come out of one row.
/// </summary>
public enum NameArticle
{
    /// <summary>"the Garden Keeper" — the name is a title.</summary>
    Definite = 0,

    /// <summary>"Hourbloom" — no article at all.</summary>
    None = 1,
}

/// <summary>The closed <c>gender</c> tag: what pronouns and verb agreement a name takes. Grammar only,
/// never a statement about the entity.</summary>
public enum NameGender
{
    Male = 0,
    Female = 1,
    Neuter = 2,

    /// <summary>Agreement is not defined for this name; a message must not branch on gender for it.</summary>
    None = 3,
}

/// <summary>The closed <c>number</c> tag: whether a name reads as one entity or a group.</summary>
public enum NameNumber
{
    Singular = 0,

    /// <summary>Reserved for group tokens; no English v1 row uses it.</summary>
    Plural = 1,

    /// <summary>Agreement is not defined for this name.</summary>
    None = 2,
}

/// <summary>
/// The three lead tokens of the story grammar, as a closed vocabulary
/// (<c>docs/architecture/narrative-seed-ideal.md</c> §6.5b). A fourth lead is a token-grammar change
/// (narrative-seed module 8 <c>token-grammar</c>), never a row added here — which is why
/// <see cref="All"/> is pinned literally by test rather than derived.
/// </summary>
public static class LeadTokens
{
    public const string Summoner = "lead_summoner";
    public const string Companion = "lead_companion";
    public const string Antagonist = "lead_antagonist";

    /// <summary>The lead family, in the order §6.5b lists it.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Summoner, Companion, Antagonist };
}

/// <summary>Raised when a token has no row in the configured set. Named rather than a bare
/// <see cref="KeyNotFoundException"/> so a caller can tell a missing row from any other lookup
/// failure, and so the message names the token.</summary>
public sealed class UnknownLeadNameException : KeyNotFoundException
{
    public UnknownLeadNameException(string token)
        : base($"lead-names: no row for token '{token}'") => Token = token;

    public string Token { get; }
}

/// <summary>One authored row of <c>data/seed/narrative/_registry/names.{locale}.v{n}.json</c>:
/// <see cref="Display"/> is the bare name and the three tags are closed enums.</summary>
public sealed record LeadNameRow(
    string Token,
    string Display,
    NameArticle Article,
    NameGender Gender,
    NameNumber Number,
    string Ruling);

/// <summary>
/// The parsed names registry: token → display string plus the closed grammatical tags. <see cref="Parse"/>
/// is pure JSON-in/rows-out; the host reads the file and configures <see cref="LeadNamesHub"/>, because
/// Core never touches a path (<c>tunables-ssot.md</c> §7.2, the same rule
/// <c>DataCommanderDirectory</c> follows).
///
/// <para>Only the three lead tokens are <b>required</b>. A fourth row is carried, not refused:
/// narrative-seed's <c>names-registry</c> module unions character names into the same set later, and
/// its own file may grow the rows. What is pinned is that the lead family is present and that the
/// tags are closed.</para>
/// </summary>
public sealed class LeadNames
{
    /// <summary>The only registry schema this build reads. A newer file is a build that cannot read
    /// it — refused rather than half-read.</summary>
    public const int SupportedSchemaVersion = 1;

    static readonly Regex TokenShape = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    readonly IReadOnlyDictionary<string, LeadNameRow> _byToken;

    LeadNames(int schemaVersion, int? registryVersion, string locale, IReadOnlyList<LeadNameRow> rows)
    {
        SchemaVersion = schemaVersion;
        RegistryVersion = registryVersion;
        Locale = locale;
        Rows = rows;

        var byToken = new Dictionary<string, LeadNameRow>(StringComparer.Ordinal);
        foreach (var row in rows) byToken[row.Token] = row;
        _byToken = byToken;
    }

    public int SchemaVersion { get; }

    /// <summary>The authored registry's own version, when the file carries one (plan D1's minimal
    /// contract does not, the written `names-registry` spec does).</summary>
    public int? RegistryVersion { get; }

    public string Locale { get; }

    /// <summary>Every row, ordered by token so two parses of the same text enumerate identically.</summary>
    public IReadOnlyList<LeadNameRow> Rows { get; }

    public bool TryGet(string token, out LeadNameRow row)
    {
        if (token is not null && _byToken.TryGetValue(token, out var found))
        {
            row = found;
            return true;
        }

        row = null!;
        return false;
    }

    /// <summary>The row for a token. An unknown token throws: a silently defaulted name would ship a
    /// wrong identity into player-facing text.</summary>
    public LeadNameRow Row(string token) =>
        token is not null && _byToken.TryGetValue(token, out var row)
            ? row
            : throw new UnknownLeadNameException(token ?? "(null)");

    /// <summary>The bare display string for a token — never article-bearing.</summary>
    public string Display(string token) => Row(token).Display;

    /// <summary>Pure parser. A missing field, an unknown tag value or a missing lead token throws; an
    /// authored file is never guessed at.</summary>
    public static LeadNames Parse(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("lead-names: the registry is not valid JSON", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("lead-names: the registry root must be an object");

            var schemaVersion = RequiredInt(root, "schemaVersion", "registry");
            if (schemaVersion != SupportedSchemaVersion)
                throw new FormatException(
                    $"lead-names: unsupported schemaVersion {schemaVersion} (this build reads {SupportedSchemaVersion})");

            var locale = RequiredString(root, "locale", "registry");
            var registryVersion = OptionalInt(root, "registryVersion", "registry");

            if (!root.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Object)
                throw new FormatException("lead-names: the registry is missing the object 'names'");

            var rows = new List<LeadNameRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in names.EnumerateObject())
            {
                var token = property.Name;
                var where = $"row '{token}'";
                if (!TokenShape.IsMatch(token))
                    throw new FormatException(
                        $"lead-names: '{token}' is not a token (lowercase letter first, then letters, digits or underscores)");
                if (!seen.Add(token))
                    throw new FormatException($"lead-names: duplicate row '{token}'");
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new FormatException($"lead-names: {where} must be an object");

                rows.Add(new LeadNameRow(
                    token,
                    DisplayOf(property.Value, token),
                    Article(RequiredString(property.Value, "article", where), where),
                    Gender(RequiredString(property.Value, "gender", where), where),
                    Number(RequiredString(property.Value, "number", where), where),
                    RequiredString(property.Value, "ruling", where)));
            }

            foreach (var lead in LeadTokens.All)
            {
                if (!seen.Contains(lead))
                    throw new FormatException($"lead-names: the registry is missing the lead token '{lead}'");
            }

            return new LeadNames(
                schemaVersion,
                registryVersion,
                locale,
                rows.OrderBy(r => r.Token, StringComparer.Ordinal).ToArray());
        }
    }

    static string DisplayOf(JsonElement row, string token)
    {
        var where = $"row '{token}'";
        var display = RequiredString(row, "display", where);
        if (display != display.Trim())
            throw new FormatException($"lead-names: {where} display has surrounding whitespace");

        foreach (var ch in display)
        {
            if (char.IsDigit(ch) || ch is '{' or '}' or '<' or '>')
                throw new FormatException(
                    $"lead-names: {where} display must be a bare name (no digit, brace or markup)");
        }

        var firstWord = display.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (firstWord.Equals("the", StringComparison.OrdinalIgnoreCase) ||
            firstWord.Equals("a", StringComparison.OrdinalIgnoreCase) ||
            firstWord.Equals("an", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"lead-names: {where} display starts with an article — the article is the 'article' tag, " +
                "never part of the display");
        }

        return display;
    }

    static NameArticle Article(string raw, string where) => raw switch
    {
        "definite" => NameArticle.Definite,
        "none" => NameArticle.None,
        _ => throw new FormatException(
            $"lead-names: {where} has unknown article '{raw}' (definite | none)"),
    };

    static NameGender Gender(string raw, string where) => raw switch
    {
        "male" => NameGender.Male,
        "female" => NameGender.Female,
        "neuter" => NameGender.Neuter,
        "none" => NameGender.None,
        _ => throw new FormatException(
            $"lead-names: {where} has unknown gender '{raw}' (male | female | neuter | none)"),
    };

    static NameNumber Number(string raw, string where) => raw switch
    {
        "singular" => NameNumber.Singular,
        "plural" => NameNumber.Plural,
        "none" => NameNumber.None,
        _ => throw new FormatException(
            $"lead-names: {where} has unknown number '{raw}' (singular | plural | none)"),
    };

    static string RequiredString(JsonElement obj, string field, string where)
    {
        if (!obj.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String)
            throw new FormatException($"lead-names: {where} is missing string field '{field}'");

        var value = el.GetString()!;
        if (value.Length == 0)
            throw new FormatException($"lead-names: {where} field '{field}' is empty");
        return value;
    }

    static int RequiredInt(JsonElement obj, string field, string where)
    {
        if (!obj.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Number ||
            !el.TryGetInt32(out var value))
        {
            throw new FormatException($"lead-names: {where} is missing integer field '{field}'");
        }

        return value;
    }

    static int? OptionalInt(JsonElement obj, string field, string where)
    {
        if (!obj.TryGetProperty(field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var value))
            throw new FormatException($"lead-names: {where} field '{field}' must be an integer");
        return value;
    }
}

/// <summary>
/// The one process-wide lead-names set, configured by the host that read the authored registry — the
/// same shape as <c>CommanderDirectoryHub</c>. Readers that cannot take an injected instance (the
/// Data layer, the injector's session cache) resolve through here.
///
/// <para>A reader with nothing configured throws rather than invent a name: a guessed display string
/// would be written into player-facing text and, at T13, into persisted faction rows.</para>
/// </summary>
public static class LeadNamesHub
{
    static LeadNames? _current;

    public static void Configure(LeadNames names) =>
        _current = names ?? throw new ArgumentNullException(nameof(names));

    /// <summary>Reads, parses and configures the hub from the authored registry at
    /// <paramref name="path"/>. Throws — naming the path — when the file is not there, and never
    /// configures a fallback.</summary>
    /// <remarks>
    /// This is the one implementation of "turn the authored registry into the process-wide hub".
    /// It lives here, in Core, rather than only in the server's boot, because **two hosts need it**:
    /// the server and the Injector already did, and the seed importer did not — so a cold import
    /// against an empty data dir reached <c>RpgStore</c>'s reader with the hub unconfigured and
    /// threw <c>LeadNamesHub.Configure(...) has not run</c>, rolling the whole import back. That is
    /// the shape of a first-time deploy into a fresh live-probe slot: the pool's slots have empty
    /// data dirs, so every one of them hit it.
    ///
    /// The PATH stays each host's business, which is why this takes a path and reads nothing from
    /// the environment: the server resolves it beside its own published content, the Injector beside
    /// its tuning dir, and the importer beside the seed root it was handed.
    /// </remarks>
    public static void ConfigureFromFile(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"lead-names hub: the names registry is not at '{path}'. There is no default lead name, "
                + "because the same hub feeds player-facing text and a new world's persisted faction "
                + "label — a guessed string is an identity no later reader could tell from an "
                + "authored one.",
                path);
        }

        Configure(LeadNames.Parse(File.ReadAllText(path)));
    }

    public static LeadNames Current =>
        _current ?? throw new InvalidOperationException(
            "LeadNamesHub.Configure(...) has not run — no lead names registry is configured");

    public static bool IsConfigured => _current is not null;

    /// <summary>Test-only: the hub is process-wide, so a test that pins the unconfigured refusal has
    /// to be able to put the process back into that state.</summary>
    internal static void Reset() => _current = null;
}
