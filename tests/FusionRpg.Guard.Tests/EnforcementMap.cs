using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// The typed view of <c>gk-core/scripts/enforcement-registry.v1.json</c> (solid-enforcement
/// <c>enforcement-registry</c>) and the one parser of the map's module table. Both are shared so the
/// meta-test and any later module read the same shape instead of re-parsing.
///
/// <para><b>The root is resolved here, not passed in, and that is the fix.</b> This used to take a
/// single <c>repoRoot</c> argument, which was true before the Keepverse split, when one repository
/// held both this registry and the capability map. After the split they are in DIFFERENT
/// repositories - <c>scripts/enforcement-registry.v1.json</c> was placed in gk-core and
/// <c>docs/architecture/solid-enforcement-map.md</c> in gk-workflow, confirmed against the staging
/// report rather than assumed - so one argument could only ever be right for one of them. Callers
/// dutifully passed their own repository root and the other read failed on a path that
/// demonstrably exists elsewhere. The parameter is KEPT, as an explicit override, because the
/// falsifiers need to aim it at a directory that does not hold the file; it is simply no longer
/// how a normal caller resolves a root.</para>
/// </summary>
sealed class EnforcementRegistry
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, RegistryGuard> Guards { get; set; } = new(StringComparer.Ordinal);
    public List<RegistryInvariant> Invariants { get; set; } = new();

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Loads gk-core's own registry, which is where the ownership rules placed it.</summary>
    public static EnforcementRegistry Load() => Load(KeepverseRoots.Core());

    /// <summary>Explicit-root overload, for a falsifier that must read a registry from a fixture.</summary>
    public static EnforcementRegistry Load(string coreRoot) =>
        FromJson(File.ReadAllText(Path.Combine(coreRoot, "scripts", "enforcement-registry.v1.json")));

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
///
/// <para><b>This file lives in gk-workflow, not gk-core</b> — developer documentation, which the
/// workspace ownership rule gives to gk-workflow — while <see cref="EnforcementRegistry"/> in this
/// same file reads a gk-core path. See that type for why each accessor resolves its own root instead
/// of taking one. The parameter is retained so the falsifier can aim it at a directory with no map
/// and assert the loud failure.</para>
/// </summary>
static class EnforcementMap
{
    /// <summary>Reads the capability map from gk-workflow, where the ownership rules placed it.</summary>
    public static IReadOnlySet<string> ModuleIds() => ModuleIds(KeepverseRoots.Workspace());

    /// <summary>Explicit-root overload, for the falsifier that must fail on a directory with no map.</summary>
    public static IReadOnlySet<string> ModuleIds(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, "docs", "architecture", "solid-enforcement-map.md");
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
