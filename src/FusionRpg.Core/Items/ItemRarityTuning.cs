using System.Text.Json;

namespace FusionRpg.Core.Items;

/// <summary>One rung's balance-surface numbers (item-ideal.md, `rarity-bands`, module 7).</summary>
public readonly record struct ItemRarityRungTuning(
    string RarityId, int DropWeightPer100k, int EnhanceCapMilli, int PowerCeilingShareMilli);

public sealed class ItemRarityTuningRejection : Exception
{
    public ItemRarityTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser over `gk-core/data/tuning/item-rarity.v1.json` — no file I/O (tunables-ssot.md §7.2: "Core
/// never reads a file. Hosts load and inject."). Carries the three balance tables (drop weight,
/// enhancement cap, power-ceiling share) per rung of <see cref="RarityLadder.RungIds"/> — the
/// structural facts (ids, promote-from, pity guard) stay in <see cref="RarityLadder"/>, never here.
/// </summary>
public static class ItemRarityTuning
{
    public static IReadOnlyDictionary<string, ItemRarityRungTuning> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ItemRarityTuningRejection("item-rarity tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ItemRarityTuningRejection($"item-rarity tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("rungs", out var rungsEl) || rungsEl.ValueKind != JsonValueKind.Object)
                throw new ItemRarityTuningRejection("item-rarity tuning: missing or non-object 'rungs'");

            var result = new Dictionary<string, ItemRarityRungTuning>(StringComparer.Ordinal);
            foreach (var rarityId in RarityLadder.RungIds)
            {
                if (!rungsEl.TryGetProperty(rarityId, out var rungEl) || rungEl.ValueKind != JsonValueKind.Object)
                    throw new ItemRarityTuningRejection($"item-rarity tuning: missing rung '{rarityId}'");

                result[rarityId] = new ItemRarityRungTuning(
                    rarityId,
                    Int(rungEl, "dropWeightPer100k", rarityId),
                    Int(rungEl, "enhanceCapMilli", rarityId),
                    Int(rungEl, "powerCeilingShareMilli", rarityId));
            }

            var dropSum = result.Values.Sum(r => r.DropWeightPer100k);
            if (dropSum != 100_000)
                throw new ItemRarityTuningRejection(
                    $"item-rarity tuning: dropWeightPer100k across all rungs sums to {dropSum}, not 100000");

            return result;
        }
    }

    static int Int(JsonElement parent, string key, string rungId)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ItemRarityTuningRejection($"item-rarity tuning: rung '{rungId}' missing or non-integer '{key}'");
        return v;
    }
}
