using System.Text.Json;
using FusionRpg.Core.Delve.Pack;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.9's own real bridging gap (party-dungeon-todo.md, 2026-09-07): spec-event-deck.md
/// §9's `OverrideTagUnsupplied` rule ("every `supplyOverride` tag is carried by ≥ 1 supply") needs the
/// UNION of every real `supplies-and-objects` extension anchor's own `overrideTags` array — no reader
/// exposed this anywhere (`supplies-and-objects`' own shipped code, `SupplyClassMap`/`SupplyInstantiation`/
/// `SupplyUse`, is runtime mechanics over an already-resolved supply, never a raw seed-content reader).
/// This is the smallest reader that closes the gap, living beside the ONE real consumer (`event-deck`,
/// not `supplies-and-objects`), matching <see cref="RoomEventPoolSeedFile"/>'s own identical
/// "reader lives with its consumer, not its source" placement.
/// </summary>
public static class SupplyOverrideTagSeedFile
{
    /// <summary>The flat union of every real supply-extension anchor's own `overrideTags` — existence
    /// only ("is this tag carried by ANYTHING"), never which specific supply, matching the rule's own
    /// "carried by &gt;= 1 supply" wording exactly.</summary>
    public static IReadOnlySet<string> LoadAllOverrideTags(string suppliesDir)
    {
        if (suppliesDir is null) throw new ArgumentNullException(nameof(suppliesDir));
        if (!Directory.Exists(suppliesDir)) return new HashSet<string>(StringComparer.Ordinal);

        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(suppliesDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            foreach (var tag in root.GetProperty("overrideTags").EnumerateArray())
                tags.Add(tag.GetString()!);
        }
        return tags;
    }

    /// <summary>
    /// 2026-09-08 — the per-supply half `LoadAllOverrideTags` deliberately never needed: which SPECIFIC
    /// `consumableRef` carries which tags, keyed the same way a pack cell's own `PackItem.RefId`
    /// names an item (confirmed by reading a real shipped file directly — the extension anchor's own
    /// `consumableRef` field is exactly the same string its filename encodes, e.g.
    /// `consumable.k1-001.json`'s `"consumableRef": "consumable.k1-001"`). This is the missing half of
    /// D3.5/D3.3's own remaining gap: `HoldsOverrideStock` (below) needs to know WHICH pack item
    /// satisfies a tag, not just that the tag exists somewhere in the corpus.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> LoadOverrideTagsByConsumableRef(string suppliesDir)
    {
        if (suppliesDir is null) throw new ArgumentNullException(nameof(suppliesDir));
        var byRef = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        if (!Directory.Exists(suppliesDir)) return byRef;

        foreach (var path in Directory.EnumerateFiles(suppliesDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var consumableRef = root.GetProperty("consumableRef").GetString()!;
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in root.GetProperty("overrideTags").EnumerateArray())
                tags.Add(tag.GetString()!);
            byRef[consumableRef] = tags;
        }
        return byRef;
    }
}

/// <summary>
/// `event-deck` D3.3/D3.5's own last-remaining named gap (party-dungeon-todo.md, 2026-09-08): a real
/// `holdsOverrideStock` fact, sourced from the party's own pack, for `EventChoices.IsEligible`/
/// `.Autopilot` and `OutcomeResolver.TryForcedOutcome`/`.Resolve` — both of which already take this
/// exact boolean as a caller-supplied parameter (matching this whole program's "Core decides on a
/// caller-supplied fact" posture for every other similarly-blocked function), so the missing piece was
/// never their own signature, only a real function to compute it from.
///
/// <para><b>Confirmed, not assumed: zero real content exercises this path today.</b> All 31 real
/// `gk-data/packs/fusion/data/seed/dungeon/supplies/*.json` anchors carry an EMPTY `overrideTags` array, and all 54 real
/// event anchors carry `supplyOverride: "none"` (D3.9's own already-confirmed finding) — so this
/// bridge is provably correct against real content structurally, but every real call today would
/// return `false` on any tag, the same "provably correct, zero production exercise" posture this
/// program already ships for `ConsumableCatalog.Load` and several other pieces.</para>
/// </summary>
public static class HoldsOverrideStockBridge
{
    /// <summary>Does ANY cell in this pack carry an item whose own `consumableRef` is tagged with
    /// <paramref name="overrideTag"/>? A pack cell only ever exists when its own `Qty` is positive
    /// (`PackGrid`'s own invariant — an emptied stack is removed, never left at zero), so mere presence
    /// in <paramref name="cells"/> already IS "holds ≥ 1", matching spec §6's own literal
    /// "HoldsStock(tag-bearing supply) &gt;= 1" threshold exactly — no further quantity check needed
    /// here.</summary>
    public static bool HoldsOverrideStock(
        IReadOnlyList<PackCell> cells, string overrideTag,
        IReadOnlyDictionary<string, IReadOnlySet<string>> tagsByConsumableRef)
    {
        if (cells is null) throw new ArgumentNullException(nameof(cells));
        if (string.IsNullOrEmpty(overrideTag)) throw new ArgumentException("overrideTag must be non-empty", nameof(overrideTag));
        if (tagsByConsumableRef is null) throw new ArgumentNullException(nameof(tagsByConsumableRef));

        foreach (var cell in cells)
        {
            if (tagsByConsumableRef.TryGetValue(cell.Item.RefId, out var tags)
                && tags.Contains(overrideTag))
                return true;
        }
        return false;
    }
}
