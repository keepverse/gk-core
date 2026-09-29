using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Consumables;

/// <summary>
/// One row of <c>gk-data/packs/fusion/data/seed/items/consumables/*.json</c> — the authored <b>seed</b>, not the shipped
/// container. Per the binding seed-to-concrete rule the corpus emits a family and a <c>powerBand</c>,
/// never a magnitude (seed-contract.md §3); a runtime generator rolls the concrete atom. This module
/// validates the seed, grades it and gates it; it does not roll it.
/// </summary>
/// <param name="ContainerId">
/// ⭐ The seed id verbatim. <c>naming.v1.json idNamespaces.consumables</c>'s template is
/// <c>consumable.k{slot}-{seq:03}</c> and §4.6 fixes the kind's container prefix as
/// <c>consumable.</c> — so unlike a unique's <c>unique.</c> tracking id, this one is <b>already</b> a
/// legal container id and needs a grammar check rather than a derivation.
/// </param>
/// <param name="Family">
/// The affix family the core atom comes from. Resolved against the shipped affix-family corpus to get
/// its <c>kindId</c>, which is what makes §6.3's runtime-legality check real rather than declarative.
/// </param>
/// <param name="Element">The family's variant, where it has one — the second half of the exclusion key.</param>
/// <param name="PowerBand">One of `bands.v1.json`'s five. Resolves to the grade through the tuning's
/// mirrored <c>gradeTierMap</c>.</param>
public sealed record ConsumableSeed(
    string ContainerId,
    string NameKey,
    string Name,
    ConsumableClass ClassId,
    IReadOnlyList<UseContext> UseContexts,
    string Family,
    string? Element,
    string PowerBand,
    int ManifestCost,
    IReadOnlyList<string> Tags,
    string? GrantsActionId,
    string? CooldownKey,
    string Partition)
{
    /// <summary>
    /// §5.2: "defaults to the container's dominant <c>(family_id, variant)</c>, which is the shipped
    /// <c>group</c> default (definitions §4)". Spelled exactly as the lane's own worked examples do —
    /// <c>atom.vitality|</c> for an elementless row, <c>atom.elemental-power|fire</c> for a variant —
    /// so a reader of §7.1/§7.2 recognises the string.
    /// </summary>
    public string ExclusionGroup => $"{Family}|{Element ?? ""}";
}

public sealed class ConsumableCorpusRejection : Exception
{
    public AtomRejection Rejection { get; }

    public ConsumableCorpusRejection(string ruleId, string detail) : base($"{ruleId}: {detail}")
    {
        Rejection = ConsumableRules.Fail(ruleId, detail);
    }
}

/// <summary>
/// <c>gk-data/packs/fusion/data/seed/items/consumables/*.json</c>, parsed. Pure — the caller supplies the JSON text; Core
/// never opens a file.
///
/// <para>⭐ <b>The 60-row corpus was AUTHORED 2026-08-22 AND NEVER WIRED</b> — the same pattern as
/// <c>item_role_family</c>, <c>nameWords</c>, <c>displayTemplate</c>, <c>UnitClass</c>, the 144
/// uniques and the 30-set corpus before it. Three partitions of twenty; every row carries a class, a
/// use context, a family and a power band, and not one line of Core read a single row until this
/// module. So this is a wiring pass plus the validators, not a from-scratch build — checked before
/// assuming, exactly as modules 6/7/8/10/17 were.</para>
/// </summary>
public static class ConsumableCorpus
{
    public static IReadOnlyList<ConsumableSeed> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed, "empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed, $"not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed, "no 'entries' array");

            var partition = "";
            if (root.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("partition", out var pEl) && pEl.ValueKind == JsonValueKind.String)
                partition = pEl.GetString()!;

            var result = new List<ConsumableSeed>();
            foreach (var e in entries.EnumerateArray()) result.Add(ReadEntry(e, partition));
            return result;
        }
    }

    static ConsumableSeed ReadEntry(JsonElement e, string partition)
    {
        var id = Str(e, "id");
        if (!ConsumableContainerIds.IsWellFormed(id))
            throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed,
                $"consumable '{id}' is not a legal container id — §4.6 fixes the prefix as " +
                $"'{ConsumableContainerIds.Prefix}' and definitions §1 fixes the body as a kebab-case slug");

        var classId = Str(e, "classId");
        if (!ConsumableClasses.TryParse(classId, out var cls))
            throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed,
                $"consumable '{id}' declares classId '{classId}', which is not one of ssot-consumables.md " +
                "§3.1's six classes");

        if (!e.TryGetProperty("useContext", out var ucEl) || ucEl.ValueKind != JsonValueKind.Array)
            throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed,
                $"consumable '{id}' declares no useContext array");

        var contexts = new List<UseContext>();
        foreach (var item in ucEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                !UseContexts.TryParse(item.GetString(), out var u))
                throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed,
                    $"consumable '{id}' declares a useContext that is not one of the closed four");
            if (!contexts.Contains(u)) contexts.Add(u);
        }
        if (contexts.Count == 0)
            throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed,
                $"consumable '{id}' names no use context, so it is usable nowhere");

        var manifestCost = e.TryGetProperty("manifestCost", out var mcEl) && mcEl.ValueKind == JsonValueKind.Number
            ? mcEl.GetInt32()
            : 1;   // §5.2's own column default; the floor is checked by the validator, not defaulted away.

        var tags = new List<string>();
        if (e.TryGetProperty("tags", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            foreach (var t in tEl.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString()!);

        return new ConsumableSeed(
            id, Str(e, "nameKey"), Str(e, "name"), cls, contexts,
            Str(e, "family"), OptStr(e, "element"), Str(e, "powerBand"),
            manifestCost, tags, OptStr(e, "grantsActionId"), OptStr(e, "cooldownKey"), partition);
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ConsumableCorpusRejection(ConsumableRules.CorpusMalformed, $"missing or non-string '{key}'");
        return el.GetString()!;
    }

    static string? OptStr(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
