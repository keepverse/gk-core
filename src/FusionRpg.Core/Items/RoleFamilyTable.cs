using System.Text.Json;

namespace FusionRpg.Core.Items;

/// <summary>One legal (role, frame, family) cell, with its effective ceiling tier.</summary>
public readonly record struct RoleFamilyCell(string RoleId, string ItemFrame, string FamilyId, int MaxTier);

public sealed class RoleFamilyRejection : Exception
{
    public RoleFamilyRejection(string message) : base(message) { }
}

/// <summary>One shipped affix-family entry, the shape `item_role_family` derives from — never a
/// second, hand-authored home for the same fact (item-ideal.md, `affix-legality` module 8).</summary>
public sealed record AffixFamilySource(string FamilyId, IReadOnlyList<string> Roles, IReadOnlyList<string> Frames, string Side, string KindId);

/// <summary>
/// `item_role_family` — DERIVED from the 98 shipped affix families' own `roles`/`frames`, never
/// authored (spec-affix-legality.md "item_role_family is DERIVED"). The default ceiling is the top of
/// D29's ladder (tier 5); the only two ways it narrows are <see cref="FamilyOverrides"/> (the minor
/// jewels' role-wide cap and the bulwark/savagery removal) and <see cref="RoleRelocation"/> (D3's
/// three dropped roles — a family stays legal on its surviving hybrid-core host, at a reduced tier,
/// rather than the budget-price refunding itself as free breadth).
/// </summary>
public static class RoleFamilyTable
{
    /// <summary>Balance surface (tunables-ssot.md T1) — reads through <see cref="ItemsTuningHub"/>,
    /// not a bare const.</summary>
    public static int DefaultMaxTier => ItemsTuningHub.Tuning.DefaultMaxTier;

    public static IReadOnlyList<RoleFamilyCell> Derive(
        IReadOnlyList<AffixFamilySource> families, FamilyOverrides overrides, RoleRelocationTable relocation)
    {
        var byRoleFamily = new Dictionary<(string Role, string Family), int>();
        var frameByRoleFamily = new Dictionary<(string Role, string Family), List<string>>();

        foreach (var f in families)
        {
            foreach (var role in f.Roles)
            {
                if (overrides.IsRemoved(role, f.FamilyId)) continue;

                var ceiling = DefaultMaxTier;
                if (overrides.RoleCap(role) is { } cap) ceiling = Math.Min(ceiling, cap);
                if (relocation.ReducedMaxTier(role, f.FamilyId) is { } reduced) ceiling = Math.Min(ceiling, reduced);

                var key = (role, f.FamilyId);
                byRoleFamily[key] = ceiling;
                if (!frameByRoleFamily.TryGetValue(key, out var frames)) frameByRoleFamily[key] = frames = new List<string>();
                frames.AddRange(f.Frames);
            }
        }

        var cells = new List<RoleFamilyCell>();
        foreach (var ((role, family), maxTier) in byRoleFamily)
            foreach (var frame in frameByRoleFamily[(role, family)].Distinct(StringComparer.Ordinal))
                cells.Add(new RoleFamilyCell(role, frame, family, maxTier));

        return cells;
    }
}

/// <summary>Pure parser over `family-overrides.v1.json` — the ONLY per-(role,family) granularity
/// this module ships; enumerated and small, never open-ended.</summary>
public sealed class FamilyOverrides
{
    readonly Dictionary<string, int> _roleCaps;
    readonly HashSet<(string Role, string FamilyId)> _removed;

    FamilyOverrides(Dictionary<string, int> roleCaps, HashSet<(string, string)> removed)
    {
        _roleCaps = roleCaps;
        _removed = removed;
    }

    public int? RoleCap(string role) => _roleCaps.TryGetValue(role, out var c) ? c : null;

    public bool IsRemoved(string role, string familyId) => _removed.Contains((role, familyId));

    public static FamilyOverrides Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new RoleFamilyRejection("family-overrides: empty document");

        using var doc = JsonDocument.Parse(json);
        var caps = new Dictionary<string, int>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("roleCaps", out var capsEl))
            foreach (var c in capsEl.EnumerateArray())
                caps[c.GetProperty("role").GetString()!] = c.GetProperty("maxTier").GetInt32();

        var removed = new HashSet<(string, string)>();
        if (doc.RootElement.TryGetProperty("removedFamilies", out var remEl))
            foreach (var r in remEl.EnumerateArray())
                removed.Add((r.GetProperty("role").GetString()!, r.GetProperty("familyId").GetString()!));

        return new FamilyOverrides(caps, removed);
    }
}

/// <summary>Pure parser over `role-relocation.v1.json` — D3's relocation, module 3's artefact to
/// author (this module applies it). Missing entirely is itself a finding, not a silent pass.</summary>
public sealed class RoleRelocationTable
{
    readonly Dictionary<(string HostRole, string FamilyId), int> _reduced;
    public int RowCount { get; }
    public IReadOnlyList<string> DroppedRoles { get; }

    RoleRelocationTable(Dictionary<(string, string), int> reduced, int rowCount, IReadOnlyList<string> droppedRoles)
    {
        _reduced = reduced;
        RowCount = rowCount;
        DroppedRoles = droppedRoles;
    }

    public int? ReducedMaxTier(string hostRole, string familyId) =>
        _reduced.TryGetValue((hostRole, familyId), out var t) ? t : null;

    public static readonly RoleRelocationTable Empty = new(new Dictionary<(string, string), int>(), 0, Array.Empty<string>());

    public static RoleRelocationTable Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new RoleFamilyRejection("role-relocation: empty document");

        using var doc = JsonDocument.Parse(json);
        var reduced = new Dictionary<(string, string), int>();
        var rows = doc.RootElement.GetProperty("relocations");
        foreach (var r in rows.EnumerateArray())
        {
            var host = r.GetProperty("hostRole").GetString()!;
            var family = r.GetProperty("familyId").GetString()!;
            var maxTier = r.GetProperty("maxTier").GetInt32();
            var key = (host, family);
            // The smallest reduction wins if a family relocates from more than one dropped role
            // onto the same host.
            reduced[key] = reduced.TryGetValue(key, out var existing) ? Math.Min(existing, maxTier) : maxTier;
        }

        var droppedRoles = doc.RootElement.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("droppedRoles", out var dr)
            ? dr.EnumerateArray().Select(e => e.GetString()!).ToList()
            : new List<string>();

        return new RoleRelocationTable(reduced, rows.GetArrayLength(), droppedRoles);
    }
}
