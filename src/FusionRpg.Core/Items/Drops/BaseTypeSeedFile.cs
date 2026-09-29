using System.Text.Json;

namespace FusionRpg.Core.Items.Drops;

/// <summary>One `gk-data/packs/fusion/data/seed/items/base-types/**/*.json` entry's `(id, frame, role)` — the three fields
/// <see cref="LootContentView.BaseTypesFor"/>'s forward query (`(frame, role) -&gt; [ids]`) and an
/// `item_generation` stamp's reverse one (`id -&gt; (frame, role)`) both need. Every other authored field
/// (`class`, `band`, `implicit`, `socketMax`, `tags`, `flavor*`, `iconKey`) already has its own reader
/// for its own job (`FusionRpg.Server.ItemBaseTypeCorpus` for the item-card display shape,
/// `BaseTypeSocketMaxCorpus` for module 16's socket cap) — this type carries only what THIS reader's own
/// two callers use, not a second copy of either.</summary>
public sealed record BaseTypeSeedRow(string Id, string Frame, string Role);

/// <summary>
/// Reads `gk-data/packs/fusion/data/seed/items/base-types/**/*.json` into <see cref="BaseTypeSeedRow"/> rows.
///
/// <para>⚠ <b>Confirmed a real, already-shipped reader of this exact content already exists —
/// `FusionRpg.Server.ItemBaseTypeCorpus.Load` (`ItemCardEndpoints.cs`) — but it lives in
/// `FusionRpg.Server`, which `FusionRpg.Data`/`RpgStore.BuildLiveLootContentView` cannot depend on
/// (wrong dependency direction), and it returns DISPLAY-shaped rows (`role.&lt;id&gt;` prefixed keys,
/// for `gk-content/content/display/en.json` lookups), not the raw `(frame, role)` pair a drop-table draw or an
/// `item_generation` stamp needs. This is a separate, minimal, Core-side reader for a separate job —
/// not a duplicate of that one, matching this program's own established "row's own adapter, not the
/// pure primitive" pattern.</b> Both readers' own boot comments already name the real target state —
/// "Deleted the day that table exists" (`item_base_type`, `Program.cs:470-475,480-483`) — this reader
/// feeds exactly that table's own importer (`RpgStore.ImportBaseTypes`).</para>
///
/// <para>Recursive, matching `ItemBaseTypeCorpus.Load`'s own established reasoning: "the corpus is
/// partitioned into subdirectories (`footing/`, `girdle/`, …) as well as files at the root, and a
/// non-recursive walk silently loses those partitions."</para>
/// </summary>
public static class BaseTypeSeedFile
{
    public static IReadOnlyList<BaseTypeSeedRow> LoadAll(string baseTypesDir)
    {
        if (baseTypesDir is null) throw new ArgumentNullException(nameof(baseTypesDir));
        if (!Directory.Exists(baseTypesDir)) return Array.Empty<BaseTypeSeedRow>();

        var rows = new List<BaseTypeSeedRow>();
        foreach (var path in Directory
                     .EnumerateFiles(baseTypesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in entries.EnumerateArray())
            {
                var id = Str(entry, "id");
                var frame = Str(entry, "frame");
                var role = Str(entry, "role");
                if (id is not { Length: > 0 } || frame is not { Length: > 0 } || role is not { Length: > 0 })
                    continue;
                rows.Add(new BaseTypeSeedRow(id, frame, role));
            }
        }
        return rows;
    }

    static string? Str(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
