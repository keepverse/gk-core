using System.Text.Json;

namespace FusionRpg.Core.Items;

/// <summary>
/// Reads <c>gk-data/packs/fusion/data/seed/items/affix-families/*.json</c> into <see cref="AffixFamilySource"/> rows — the
/// production counterpart of <c>RoleFamilyTableTests.LoadFamilies</c>, which has read this exact
/// content since `affix-legality` (item module 8) landed 2026-09-04, but only ever inside that one
/// test file. <see cref="RoleFamilyTable.Derive"/> and <see cref="AffixFilters"/> are real, pure, and
/// already tested against the real shipped corpus — confirmed by a dedicated search that neither has a
/// single production caller anywhere in <c>src/</c> — the ONE missing piece was this reader, mirroring
/// <see cref="FusionRpg.Core.Items.Drops.BaseTypeSeedFile"/>'s own identical "row's own adapter, not
/// the pure primitive" shape for the sibling <c>base-types/**</c> corpus (party-dungeon-todo.md
/// D4.12/D3.11, `mintAt`'s Equipment-kind arm, 2026-09-07).
///
/// <para>Deliberately skips a malformed entry rather than throwing — <see cref="BaseTypeSeedFile"/>'s
/// own established convention for this exact class of reader — since a single bad row in a 100+-file
/// corpus should narrow the derived legality table, never crash the whole read.</para>
/// </summary>
public static class AffixFamilySeedFile
{
    public static IReadOnlyList<AffixFamilySource> LoadAll(string affixFamiliesDir)
    {
        if (affixFamiliesDir is null) throw new ArgumentNullException(nameof(affixFamiliesDir));
        if (!Directory.Exists(affixFamiliesDir)) return Array.Empty<AffixFamilySource>();

        var rows = new List<AffixFamilySource>();
        foreach (var path in Directory
                     .EnumerateFiles(affixFamiliesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in entries.EnumerateArray())
            {
                var id = Str(entry, "id");
                var side = Str(entry, "side");
                var kindId = Str(entry, "kindId");
                if (id is not { Length: > 0 } || side is not { Length: > 0 } || kindId is not { Length: > 0 })
                    continue;

                var roles = StrArray(entry, "roles");
                var frames = StrArray(entry, "frames");
                if (roles.Count == 0 || frames.Count == 0) continue;

                rows.Add(new AffixFamilySource(id, roles, frames, side, kindId));
            }
        }
        return rows;
    }

    static string? Str(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static List<string> StrArray(JsonElement entry, string name)
    {
        var result = new List<string>();
        if (!entry.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                result.Add(s);
        return result;
    }
}
