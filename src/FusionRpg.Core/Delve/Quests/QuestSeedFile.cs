using System.Text.Json;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>
/// D4.30's real prerequisite chain (2026-09-07): reads `gk-data/packs/fusion/data/seed/dungeon/quests/*.json` into
/// `QuestRow` rows -- the same direct-file-read shape `Delve.Roll.LayoutSeedFile`/
/// `DungeonRegistryLoader` already use for committed seed content (Core reads it directly; this is
/// not player data, so `guard-dal.ps1`'s SQL-only boundary does not apply). This is genuinely NOT
/// the seed-import wiring D4.16 names as its own still-unbuilt job (`RpgStore.Import.cs`'s
/// per-player validate-then-write path, gated on a real database) — it is the narrower, read-only
/// bridge this task needs to prove real seedsmith-authored content against the real
/// <see cref="QuestCatalog"/> validator, matching `LayoutTemplateCatalog`'s own identical posture.
///
/// <para>The JSON string `"none"` (the seed contract's own spelled convention for "no value" on a
/// nullable field, `schema.py`'s `_enum(..., nullable=True)`) is converted to a real C# `null` here
/// -- `QuestRow.TargetRef`/`.CountBand` are `string?`, never the literal string `"none"`.</para>
/// </summary>
public static class QuestSeedFile
{
    static string? NoneToNull(string value) => value == "none" ? null : value;

    public static IReadOnlyList<QuestRow> LoadAll(string questsDir)
    {
        if (questsDir is null) throw new ArgumentNullException(nameof(questsDir));
        if (!Directory.Exists(questsDir)) return Array.Empty<QuestRow>();

        var rows = new List<QuestRow>();
        foreach (var path in Directory.EnumerateFiles(questsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            rows.Add(new QuestRow(
                root.GetProperty("questId").GetString()!,
                root.GetProperty("objectiveTemplate").GetString()!,
                NoneToNull(root.GetProperty("targetRef").GetString()!),
                NoneToNull(root.GetProperty("countBand").GetString()!),
                root.GetProperty("rewardBand").GetString()!,
                root.GetProperty("scope").GetString()!,
                Predicate: null));
        }
        return rows;
    }
}
