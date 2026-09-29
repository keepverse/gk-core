using System.Globalization;
using System.Text.Json;
using FusionRpg.Core.Saves;

namespace FusionRpg.Core.Commanders;

/// <summary>One authored row of <c>data/seed/commanders/_registry/default-commanders.v{n}.json</c>.
/// <see cref="ScopeKeyTemplate"/> carries the scope enum's literal shape with an <c>{id}</c> hole.</summary>
public sealed record CommanderRow(
    string StableId,
    EmpireId Empire,
    string DisplayName,
    bool DisplayFromPlayer,
    string ScopeKeyTemplate);

/// <summary>
/// The shipped <see cref="ICommanderDirectory"/>: two authored rows, byte-identical to the retired
/// <c>CommanderId</c> helpers except for the one ruled change (the player's own commander shows the
/// owner's name). <see cref="Parse"/> is pure JSON-in/rows-out; the host reads the file
/// (<c>tunables-ssot.md</c> §7.2 — Core never touches a path). <see cref="DisplayName"/> takes the
/// caller's player name rather than a player id: the directory owns the rule (the player's own
/// commander shows the player's name), and the caller — which already holds the name — owns the read.
/// </summary>
public sealed class DataCommanderDirectory : ICommanderDirectory, ICommanderRoster
{
    readonly IReadOnlyList<CommanderRow> _rows;
    readonly UniqueCommanderSource? _source;

    public DataCommanderDirectory(IReadOnlyList<CommanderRow> rows, UniqueCommanderSource? source = null)
    {
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _source = source;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (string.IsNullOrWhiteSpace(row.StableId))
                throw new ArgumentException("a commander row needs a stable id", nameof(rows));
            if (!seen.Add(row.StableId))
                throw new ArgumentException($"duplicate commander stable id '{row.StableId}'", nameof(rows));
            if (row.ScopeKeyTemplate is null || !row.ScopeKeyTemplate.Contains("{id}", StringComparison.Ordinal))
                throw new ArgumentException(
                    $"commander '{row.StableId}' scopeKeyTemplate must contain '{{id}}'", nameof(rows));
        }
    }

    /// <summary>Pure parser for the authored registry. A missing or mistyped field throws; an authored
    /// file is never guessed at.</summary>
    public static DataCommanderDirectory Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("commanders", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new FormatException("default-commanders: missing array 'commanders'");

        var rows = new List<CommanderRow>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new FormatException("default-commanders: every 'commanders' entry must be an object");
            rows.Add(new CommanderRow(
                Str(el, "stableId"),
                new EmpireId(Str(el, "empireId")),
                Str(el, "displayName"),
                Bool(el, "displayFromPlayer"),
                Str(el, "scopeKeyTemplate")));
        }

        return new DataCommanderDirectory(rows);
    }

    public bool TryResolve(string? stableId, out CommanderRef commander)
    {
        commander = default;
        if (string.IsNullOrWhiteSpace(stableId)) return false;
        var trimmed = stableId.Trim();
        foreach (var row in _rows)
        {
            if (string.Equals(row.StableId, trimmed, StringComparison.Ordinal))
            {
                commander = new CommanderRef(row.StableId);
                return true;
            }
        }

        // The composed source (EP3.2): a creature that holds the role is a commander. The authored rows
        // are checked FIRST — the shipped rows stay the defaults, and a source can never shadow one.
        return _source is not null && _source.TryResolve(trimmed, out commander);
    }

    public EmpireId EmpireOf(CommanderRef commander) =>
        TryRow(commander, out var row) ? row.Empire : SourceOrThrow().EmpireOf(commander);

    public string DisplayName(CommanderRef commander, string? playerName)
    {
        if (!TryRow(commander, out var row)) return SourceOrThrow().DisplayName(commander, playerName);
        if (row.DisplayFromPlayer && !string.IsNullOrWhiteSpace(playerName)) return playerName!;
        return ResolveDisplay(row.DisplayName);
    }

    /// <summary>
    /// A row may name a lead by TOKEN — <c>"{lead_antagonist}"</c> — so its display name is resolved
    /// through the process-wide names registry (identity-rename T12). A literal (<c>"Wild"</c>) passes
    /// through unchanged, which is what the rows that name no lead keep doing. An unknown token THROWS
    /// (the hub's own <c>UnknownLeadNameException</c>) rather than rendering the braces to a player.
    /// </summary>
    static string ResolveDisplay(string displayName) =>
        displayName.Length > 2 && displayName[0] == '{' && displayName[^1] == '}'
            ? FusionRpg.Core.Narrative.LeadNamesHub.Current.Display(displayName[1..^1])
            : displayName;

    public string AllocationScopeKey(CommanderRef commander, long playerId) =>
        TryRow(commander, out var row)
            ? row.ScopeKeyTemplate.Replace("{id}", playerId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            : SourceOrThrow().AllocationScopeKey(commander, playerId);

    /// <summary>The authored commanders for the empire, ordinal by stable id — the first half of a
    /// roster. The composed source appends the role-holding creatures (EP3.2), so this stays the
    /// directory's own answer for its own rows and the source composes rather than replaces.</summary>
    public IReadOnlyList<CommanderRef> ForEmpire(EmpireRef empire)
    {
        var roster = _rows.Where(r => r.Empire == empire.Empire)
            .Select(r => new CommanderRef(r.StableId))
            .ToList();
        if (_source is not null)
        {
            foreach (var holder in _source.RoleHoldersForEmpire(empire))
            {
                if (roster.All(r => !string.Equals(r.StableId, holder.StableId, StringComparison.Ordinal)))
                    roster.Add(holder);
            }
        }
        return roster.OrderBy(r => r.StableId, StringComparer.Ordinal).ToList();
    }

    /// <summary>The same directory with a <see cref="UniqueCommanderSource"/> composed in — one
    /// directory class, one place the answers are decided, and the authored rows still first.</summary>
    public DataCommanderDirectory WithSource(UniqueCommanderSource source) =>
        new(_rows, source ?? throw new ArgumentNullException(nameof(source)));

    public CommanderRef DefaultFor(EmpireId empire)
    {
        foreach (var row in _rows)
        {
            if (row.Empire == empire) return new CommanderRef(row.StableId);
        }
        throw new InvalidOperationException($"no default commander is registered for empire '{empire.Value}'");
    }

    CommanderRow Row(CommanderRef commander)
    {
        if (TryRow(commander, out var row)) return row;
        throw new ArgumentException($"unknown commander stable id '{commander.StableId}'", nameof(commander));
    }

    bool TryRow(CommanderRef commander, out CommanderRow row)
    {
        foreach (var candidate in _rows)
        {
            if (string.Equals(candidate.StableId, commander.StableId, StringComparison.Ordinal))
            {
                row = candidate;
                return true;
            }
        }
        row = null!;
        return false;
    }

    UniqueCommanderSource SourceOrThrow() => _source ?? throw new ArgumentException(
        "unknown commander stable id — no role source is composed into this directory");

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new FormatException($"default-commanders: missing string '{key}'");
        var v = el.GetString();
        if (string.IsNullOrEmpty(v))
            throw new FormatException($"default-commanders: '{key}' must not be empty");
        return v;
    }

    static bool Bool(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el)
            || (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
            throw new FormatException($"default-commanders: missing boolean '{key}'");
        return el.GetBoolean();
    }
}
