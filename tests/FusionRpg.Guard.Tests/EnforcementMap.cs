using System.Text.Json;
using System.Text.RegularExpressions;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The typed view of <c>gk-core/scripts/enforcement-registry.v1.json</c> (solid-enforcement
/// <c>enforcement-registry</c>) and the one parser of the map's module table. Both are shared so the
/// meta-test and any later module read the same shape instead of re-parsing.
/// </summary>
sealed class EnforcementRegistry
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, RegistryGuard> Guards { get; set; } = new(StringComparer.Ordinal);
    public List<RegistryInvariant> Invariants { get; set; } = new();

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static EnforcementRegistry Load(string repoRoot) =>
        FromJson(File.ReadAllText(Path.Combine(repoRoot, "scripts", "enforcement-registry.v1.json")));

    /// <summary>Used by the falsifiers to build a deliberately broken registry in memory — never on disk.</summary>
    public static EnforcementRegistry FromJson(string json) =>
        JsonSerializer.Deserialize<EnforcementRegistry>(json, Options)
        ?? throw new InvalidOperationException("enforcement-registry JSON deserialized to null");
}

sealed class RegistryGuard
{
    public string Script { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Status { get; set; } = "";
    public string? BacklogModule { get; set; }
    public string? LocalReason { get; set; }
    public string? CiEntry { get; set; }
    public string? CiEntryReason { get; set; }
}

sealed class RegistryInvariant
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public List<string> Guards { get; set; } = new();
    public string? UnguardableReason { get; set; }
}

/// <summary>
/// Parses <c>docs/architecture/solid-enforcement-map.md</c>'s module table — the program's own list of
/// module ids. The registry's R3 checks a backlog guard against this set, and a JSON copy would be a
/// DRY defect, so the table is the source. The parser fails LOUDLY if the table cannot be found rather
/// than returning an empty set, which would make R3 pass vacuously.
/// </summary>
static class EnforcementMap
{
    public static IReadOnlySet<string> ModuleIds(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "docs", "architecture", "solid-enforcement-map.md");
        if (!File.Exists(path))
            throw new InvalidOperationException($"cannot find the capability map at {path}");

        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, l => l.Trim().Equals("## Modules", StringComparison.Ordinal));
        if (start < 0)
            throw new InvalidOperationException(
                "cannot find the '## Modules' table in solid-enforcement-map.md — the map's shape changed; " +
                "fix the parser rather than letting R3 pass vacuously");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = start + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!trimmed.StartsWith("|", StringComparison.Ordinal)) continue;
            var match = Regex.Match(trimmed, @"^\|\s*`([a-z][a-z0-9-]+)`");
            if (match.Success) ids.Add(match.Groups[1].Value);
        }

        if (ids.Count == 0)
            throw new InvalidOperationException(
                "the '## Modules' table parsed to zero module ids — its shape changed; fix the parser");
        return ids;
    }
}
