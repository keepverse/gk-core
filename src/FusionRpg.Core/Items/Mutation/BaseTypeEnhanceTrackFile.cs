using System.Text.Json;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// Reads the authored <c>enhanceTrack</c> off <c>gk-data/packs/fusion/data/seed/items/base-types/**/*.json</c> — the
/// per-base-type <c>[{atLevel, family}]</c> list species-gear-chain T13's milestone append reads, and
/// the one input <c>ItemWorkbench</c>'s <c>baseTypeEnhanceTrack</c> delegate needs.
///
/// <para>⚠ <b>A separate, minimal reader for a separate job</b>, matching
/// <see cref="Drops.BaseTypeSeedFile"/>'s own established reasoning: that one carries the raw
/// <c>(id, frame, role)</c> triple a drop-table draw and an <c>item_generation</c> stamp need, this one
/// carries only the track the enhance path needs. Neither is a second copy of the other. Both walk the
/// corpus <b>recursively</b>, because it is partitioned into subdirectories
/// (<c>footing/humanoid/</c>, <c>girdle/plant/</c>, …) as well as files at the root — a non-recursive
/// walk silently loses those partitions.</para>
///
/// <para>Entries are ordered by <c>atLevel</c> and then by family ordinal, so a re-ordered file is not a
/// different track. A track is <b>fallible input</b> (see <see cref="MilestoneTrack.FamilyFor"/>): a
/// file with no usable track is simply absent from the map, which the workbench's own <c>null</c> arm
/// already reads as "no milestone append" rather than as a refusal.</para>
/// </summary>
public static class BaseTypeEnhanceTrackFile
{
    /// <summary>Base-type id → its authored track, ordinal-ordered. Empty for an absent directory.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<MilestoneTrackEntry>> LoadAll(string baseTypesDir)
    {
        if (baseTypesDir is null) throw new ArgumentNullException(nameof(baseTypesDir));

        var tracks = new Dictionary<string, IReadOnlyList<MilestoneTrackEntry>>(StringComparer.Ordinal);
        if (!Directory.Exists(baseTypesDir)) return tracks;

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
                if (id is not { Length: > 0 }) continue;
                if (!entry.TryGetProperty("enhanceTrack", out var track) ||
                    track.ValueKind != JsonValueKind.Array)
                    continue;

                var rows = new List<MilestoneTrackEntry>();
                foreach (var pair in track.EnumerateArray())
                {
                    var family = Str(pair, "family");
                    if (family is not { Length: > 0 } ||
                        !pair.TryGetProperty("atLevel", out var level) ||
                        level.ValueKind != JsonValueKind.Number ||
                        !level.TryGetInt32(out var atLevel))
                        continue;
                    rows.Add(new MilestoneTrackEntry(atLevel, family));
                }

                if (rows.Count == 0) continue;
                tracks[id] = rows
                    .OrderBy(r => r.AtLevel)
                    .ThenBy(r => r.Family, StringComparer.Ordinal)
                    .ToList();
            }
        }

        return tracks;
    }

    static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
