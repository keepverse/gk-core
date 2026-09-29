using System.Text.Json;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// Reads `gk-data/packs/fusion/data/seed/dungeon/domains/*.json` into <see cref="DomainRow"/> rows — the same
/// direct-file-read shape `Events.EventSeedFile`/`Roll.LayoutSeedFile` already use for committed
/// dungeon seed content. This is a Core-side reader only: it feeds `DomainCatalog.Load` and the
/// D4.17 row-4 bridge (`Roll.DomainAnchorBuilder`) for testing against real content — it is NOT the
/// SQL import path (`RpgStore.Import.cs`'s own domain-kind write arm, D4.16, still unbuilt).
///
/// <para>`PermadeathFromRung` uses this program's own `"none"` sentinel, converted to a real C#
/// `null` here — the same convention `EventSeedFile`'s `NoneToNull` already applies to its own
/// nullable string fields. `RoomPalette`/`QuestPool`/`LootBinding` are read but not carried on
/// `DomainRow` itself (D4.15's own deliberate scope: pools are storage-only, `dungeon_domain_pool`'s
/// own concern) — <see cref="LoadPools"/> reads them separately, keyed by domain id, for a caller
/// (the row-4 bridge) that needs the room palette specifically.</para>
/// </summary>
public static class DomainSeedFile
{
    static string? NoneToNull(string? value) => value is null or "none" ? null : value;

    public static IReadOnlyList<DomainRow> LoadAll(string domainsDir)
    {
        if (domainsDir is null) throw new ArgumentNullException(nameof(domainsDir));
        if (!Directory.Exists(domainsDir)) return Array.Empty<DomainRow>();

        var rows = new List<DomainRow>();
        foreach (var path in Directory.EnumerateFiles(domainsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            rows.Add(new DomainRow(
                DomainId: root.GetProperty("domainId").GetString()!,
                Name: root.GetProperty("name").GetString()!,
                Flavor: root.GetProperty("flavor").GetString()!,
                Theme: root.GetProperty("theme").GetString()!,
                Climate: root.GetProperty("climate").GetString()!,
                DangerBand: root.GetProperty("dangerBand").GetString()!,
                Entry: root.GetProperty("entry").GetString()!,
                LayoutTemplateId: root.GetProperty("layoutTemplateId").GetString()!,
                BossSpeciesRef: root.GetProperty("bossSpeciesRef").GetString()!,
                RetinueFamily: NoneToNull(root.TryGetProperty("retinueFamily", out var rf) ? rf.GetString() : null),
                EntranceHint: root.GetProperty("entranceHint").GetString()!,
                PermadeathFromRung: NoneToNull(root.TryGetProperty("permadeathFromRung", out var pfr) ? pfr.GetString() : null),
                FirstClearRef: NoneToNull(root.TryGetProperty("firstClearRef", out var fcr) ? fcr.GetString() : null)));
        }
        return rows;
    }

    /// <summary>`roomPalette` per domain id — the one pool this program's row-4 bridge needs;
    /// `questPool`/`lootBinding` are read by <see cref="LoadQuestPools"/>/<see cref="LoadLootBindings"/>
    /// below, for D4.16's own eventual SQL-import arm and D4.17's own quest/loot preflight bridges.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadRoomPalettes(string domainsDir)
    {
        if (domainsDir is null) throw new ArgumentNullException(nameof(domainsDir));
        if (!Directory.Exists(domainsDir)) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var byId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(domainsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var domainId = root.GetProperty("domainId").GetString()!;
            var palette = root.GetProperty("roomPalette").EnumerateArray().Select(e => e.GetString()!).ToList();
            byId[domainId] = palette;
        }
        return byId;
    }

    /// <summary>`questPool` per domain id — D4.16's own SQL-import arm own `dungeon_domain_pool`
    /// (`pool='quest'`) rows, and a future quest-preflight (row 8) bridge.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadQuestPools(string domainsDir)
    {
        if (domainsDir is null) throw new ArgumentNullException(nameof(domainsDir));
        if (!Directory.Exists(domainsDir)) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var byId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(domainsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var domainId = root.GetProperty("domainId").GetString()!;
            var pool = root.GetProperty("questPool").EnumerateArray().Select(e => e.GetString()!).ToList();
            byId[domainId] = pool;
        }
        return byId;
    }

    /// <summary>`lootBinding` per domain id (room kind -&gt; `drop_table` id) — row 9's own already-
    /// wired `LootBindingFor` need, and D4.16's own `dungeon_domain_pool` (`pool='loot'`, `key`=room
    /// kind, `ref_id`=table id per spec §1's own schema comment) rows.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadLootBindings(string domainsDir)
    {
        if (domainsDir is null) throw new ArgumentNullException(nameof(domainsDir));
        if (!Directory.Exists(domainsDir)) return new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        var byId = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(domainsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var domainId = root.GetProperty("domainId").GetString()!;
            var binding = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in root.GetProperty("lootBinding").EnumerateObject())
                binding[prop.Name] = prop.Value.GetString()!;
            byId[domainId] = binding;
        }
        return byId;
    }

    /// <summary>The anchor's own `_provenance` block verbatim, canonicalised — or a well-formed,
    /// honestly-EMPTY provenance object (every field default/blank) for content that predates
    /// tracking, never a fabricated one. Matches seedsmith's own already-shipped
    /// `gk-forge/tools/seedsmith/seedsmith/adapters/dungeon/provenance.py` `stale_ids()` doc comment verbatim:
    /// "An entry with no `_provenance` predates tracking and is reported stale (cannot be proven
    /// current)" — the empty object here is the C#-side, NOT-NULL-column equivalent of that same
    /// "absent, never faked" rule (D4.16, spec-domain-catalog.md §3). Confirmed via `grep` that
    /// `DungeonProvenance`/`stale_ids` (D1.11) have ZERO callers anywhere in the dungeon adapter today
    /// — this is a real, pre-existing wiring gap across every dungeon content kind, not domain-specific,
    /// named here rather than silently worked around.</summary>
    public static string LoadProvenanceJson(string domainPath)
    {
        if (domainPath is null) throw new ArgumentNullException(nameof(domainPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(domainPath));
        var root = doc.RootElement;
        return root.TryGetProperty("_provenance", out var prov)
            ? prov.GetRawText()
            : EmptyProvenanceJson;
    }

    /// <summary>The exact shape `DungeonProvenance().to_dict()` (Python, all-default fields) would
    /// serialize to — every key spec §3 names, all empty, never omitted (a caller checking for a
    /// specific key must see it present-but-blank, not throw on a missing one).</summary>
    public const string EmptyProvenanceJson =
        "{\"planHash\":\"\",\"briefHash\":\"\",\"promptVersions\":{},\"registryVersions\":{},\"motifSubsetHash\":\"\",\"attempts\":{},\"confidence\":{},\"minorityValues\":{}}";
}
