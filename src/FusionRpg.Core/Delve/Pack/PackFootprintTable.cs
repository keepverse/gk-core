using System.Text.Json;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Consumables;
using FusionRpg.Core.Items.Materials;

namespace FusionRpg.Core.Delve.Pack;

public sealed class PackRejection : Exception
{
    public PackRejection(string message) : base(message) { }
}

/// <summary>One base-type corpus row, the minimal shape <see cref="PackFootprintTable.Build"/> needs
/// — `gk-data/packs/fusion/data/seed/items/base-types/*.json`'s own `entries[]`, most fields unread here (item-program
/// concerns, not this module's).</summary>
public sealed record BaseTypeEntry(string Id, string Role, IReadOnlyList<string> Tags, bool Enabled = true);

/// <summary>
/// D3.19 (spec-loot-pack.md §2) — builds `footprint(baseTypeId)` once at load from the corpus's own
/// `role` and `tags[]`, and resolves the non-equipment sizes (§2's own "Non-equipment" paragraph).
/// Every refusal is named (`PackRejection`), never silent, matching this module's own §9.
/// </summary>
public static class PackFootprintTable
{
    static readonly IReadOnlySet<string> MassClasses = new HashSet<string>(StringComparer.Ordinal)
        { "light", "medium-light", "medium", "medium-heavy", "heavy" };

    /// <summary>
    /// Resolves `dungeon.v1.json`'s raw `pack.footprint.role`/`.massStep` dictionaries (loaded
    /// vocabulary-agnostic by <c>DungeonTuning.IntTableFree</c> — its own doc comment: "not exactness-
    /// checked... because none owns this vocabulary") into a validated <see cref="PackTuning"/>. This
    /// is where that cross-check happens: every role key must parse via <see cref="ItemRoles.TryParse"/>
    /// and every massStep key must be one of the five real `tags.v1.json` mass-class values — an
    /// unknown key refuses BY NAME (§2: "else refuse at load, naming the key"), not silently ignored.
    /// </summary>
    public static PackTuning ResolveTuning(DungeonTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var roleCells = new Dictionary<ItemRole, int>();
        foreach (var (key, cells) in tuning.PackFootprintRole)
        {
            if (!ItemRoles.TryParse(key, out var role))
                throw new PackRejection($"pack.footprint.role: '{key}' names no known item role");
            roleCells[role] = cells;
        }

        foreach (var key in tuning.PackFootprintMassStep.Keys)
            if (!MassClasses.Contains(key))
                throw new PackRejection($"pack.footprint.massStep: '{key}' is not one of the five mass classes");

        return new PackTuning(roleCells, tuning.PackFootprintMassStep);
    }

    /// <summary>The mass-class tags an entry actually carries, among `tags.v1.json`'s own five —
    /// every OTHER tag on the item (element, flavor, whatever else `tags[]` holds) is irrelevant here.</summary>
    static IReadOnlyList<string> MassClassTagsOf(BaseTypeEntry entry) =>
        entry.Tags.Where(MassClasses.Contains).ToList();

    /// <summary>
    /// footprint(baseTypeId) for every ENABLED entry — a retired (`enabled: false`) row is not live
    /// content and is skipped, matching the corpus's own convention. §9's own refusals: an unparseable
    /// role (`pack.unknown-role`); zero or more than one mass-class tag (`pack.mass-class-count`),
    /// naming the id either way, never a default footprint.
    /// </summary>
    public static IReadOnlyDictionary<string, (int W, int H)> Build(IReadOnlyList<BaseTypeEntry> entries, PackTuning tuning)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var result = new Dictionary<string, (int W, int H)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.Enabled) continue;

            if (!ItemRoles.TryParse(entry.Role, out var role))
                throw new PackRejection($"pack.unknown-role: base type '{entry.Id}' has role '{entry.Role}'");

            var massTags = MassClassTagsOf(entry);
            if (massTags.Count != 1)
                throw new PackRejection(
                    $"pack.mass-class-count: base type '{entry.Id}' carries {massTags.Count} mass-class tags, expected exactly 1");

            try
            {
                result[entry.Id] = Footprint.Derive(role, massTags[0], tuning);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
            {
                throw new PackRejection($"pack.footprint-derive-failed: base type '{entry.Id}' ({ex.Message})");
            }
        }
        return result;
    }

    /// <summary>§2's own "Non-equipment" paragraph: a `Consumable` entry's own cell count and stack cap
    /// read `pack.footprint.consumableClass.*`/`pack.stack.consumableClass.*` by the SAME wire spelling
    /// `ConsumableDef.cs` already uses (<see cref="ConsumableClasses.Wire"/>) — never a private copy.</summary>
    public static (int Cells, int StackCap) ForConsumable(ConsumableClass classId, DungeonTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        var wire = ConsumableClasses.Wire(classId);
        if (!tuning.PackFootprintConsumableClass.TryGetValue(wire, out var cells))
            throw new PackRejection($"pack.footprint.consumableClass: no entry for '{wire}'");
        if (!tuning.PackStackConsumableClass.TryGetValue(wire, out var stackCap))
            throw new PackRejection($"pack.stack.consumableClass: no entry for '{wire}'");
        return (cells, stackCap);
    }

    /// <summary>`Material` (and `Insert`, spec §2) is always a `1×1` stack — only the stack CAP varies
    /// by material class (`pack.stack.materialClass.*`), never the size.</summary>
    public static int ForMaterial(MaterialClass materialClass, DungeonTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (materialClass == MaterialClass.Souls)
            throw new PackRejection("pack.stack.materialClass: Souls is a ledger balance, not a stack (spec §2)");
        var wire = materialClass.ToString().ToLowerInvariant();
        if (!tuning.PackStackMaterialClass.TryGetValue(wire, out var stackCap))
            throw new PackRejection($"pack.stack.materialClass: no entry for '{wire}'");
        return stackCap;
    }

    /// <summary>
    /// The real corpus reader spec §2 names as "this module's own" wiring gap: every `*.json` under
    /// <paramref name="baseTypesDir"/>, each file's own `entries[]` array, `id`/`role`/`tags` and the
    /// optional `enabled` flag (defaults `true` — most rows carry no such key at all).
    /// </summary>
    public static IReadOnlyList<BaseTypeEntry> LoadBaseTypeEntries(string baseTypesDir)
    {
        if (!Directory.Exists(baseTypesDir))
            throw new PackRejection($"pack.base-types-missing: '{baseTypesDir}' does not exist");

        var entries = new List<BaseTypeEntry>();
        foreach (var file in Directory.GetFiles(baseTypesDir, "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("entries", out var list)) continue;
            foreach (var row in list.EnumerateArray())
            {
                var id = row.GetProperty("id").GetString()
                    ?? throw new PackRejection($"pack.base-type-malformed: '{file}' has an entry with no id");
                var role = row.GetProperty("role").GetString()
                    ?? throw new PackRejection($"pack.base-type-malformed: '{id}' in '{file}' has no role");
                var tags = row.TryGetProperty("tags", out var tagsEl)
                    ? tagsEl.EnumerateArray().Select(t => t.GetString() ?? "").ToList()
                    : new List<string>();
                var enabled = !row.TryGetProperty("enabled", out var enabledEl) || enabledEl.GetBoolean();
                entries.Add(new BaseTypeEntry(id, role, tags, enabled));
            }
        }
        return entries;
    }
}
