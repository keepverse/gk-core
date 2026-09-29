using System.Text.Json;

namespace FusionRpg.Core.Creatures.Fusion;

public sealed record FusionCostTuning(long Souls, int ShardCount, int EssenceCount);

public sealed record RecipeCostTuning(long Souls, CreatureRarity ShardRarity, int ShardCount, int EssenceCount);

/// <summary>Fusion balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="StarPolicy.Configure"/> and <see cref="FusionTuningLoader"/>.</summary>
public sealed record FusionTuning(
    int SchemaVersion, int Version,
    int PerStarPowerMilli, int PerStarDefenseMilli,
    IReadOnlyDictionary<CreatureRarity, int> StarCap,
    FusionCostTuning StarMergeCost, FusionCostTuning PromotionCost,
    IReadOnlyDictionary<CreatureRarity, FusionCostTuning> PromotionCostByRarity,
    IReadOnlyDictionary<CreatureRarity, RecipeCostTuning> RecipeCost,
    IReadOnlyDictionary<CreatureRarity, int> SlotsByRarity,
    IReadOnlyDictionary<CreatureRarity, long> InheritCostByRarity);

public sealed class FusionTuningRejection : Exception
{
    public FusionTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class FusionTuningLoader
{
    public static FusionTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FusionTuningRejection("fusion tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FusionTuningRejection($"fusion tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");
            var perStarPowerMilli = Int(root, "perStarPowerMilli", "$");
            var perStarDefenseMilli = Int(root, "perStarDefenseMilli", "$");

            var capEl = Obj(root, "starCap", "$");
            var starCap = new Dictionary<CreatureRarity, int>();
            foreach (var rarity in Enum.GetValues<CreatureRarity>())
                starCap[rarity] = Int(capEl, rarity.ToString().ToLowerInvariant(), "starCap");

            var starMergeCost = Cost(root, "starMergeCost");
            var promotionCost = Cost(root, "promotionCost");

            // Per-rung promotion price (effort-power M5, 2026-09-05). Every rung must be present;
            // `promotionCost` above stays as the shape the flat price used to have, and is the
            // fallback a rung falls back to only if this table ever loses one.
            var promoEl = Obj(root, "promotionCostByRarity", "$");
            var promotionCostByRarity = new Dictionary<CreatureRarity, FusionCostTuning>();
            foreach (var rarity in CreatureRarityLadder.All)
            {
                var key = rarity.ToString().ToLowerInvariant();
                var el = Obj(promoEl, key, "promotionCostByRarity");
                promotionCostByRarity[rarity] = new FusionCostTuning(
                    Souls: Long(el, "souls", $"promotionCostByRarity.{key}"),
                    ShardCount: Int(el, "shardCount", $"promotionCostByRarity.{key}"),
                    EssenceCount: Int(el, "essenceCount", $"promotionCostByRarity.{key}"));
            }

            // Eligible-for-recipes is CreatureRecipeCatalog's own eligibility floor (Cultivated and
            // up, spec-rarity-migration.md §3's translation of the old ">= Rare") — one source of
            // truth, never a second hardcoded list here.
            var recipeEl = Obj(root, "recipeCost", "$");
            var recipeCost = new Dictionary<CreatureRarity, RecipeCostTuning>();
            foreach (var rarity in CreatureRarityLadder.All)
            {
                if (!CreatureRarityLadder.AtLeast(rarity, CreatureRecipeCatalog.OutputEligibilityFloor)) continue;
                var key = rarity.ToString().ToLowerInvariant();
                var el = Obj(recipeEl, key, "recipeCost");
                recipeCost[rarity] = new RecipeCostTuning(
                    Souls: Long(el, "souls", $"recipeCost.{key}"),
                    ShardRarity: ParseRarity(Str(el, "shardRarity", $"recipeCost.{key}"), $"recipeCost.{key}.shardRarity"),
                    ShardCount: Int(el, "shardCount", $"recipeCost.{key}"),
                    EssenceCount: Int(el, "essenceCount", $"recipeCost.{key}"));
            }

            var slotsEl = Obj(root, "slotsByRarity", "$");
            var slotsByRarity = new Dictionary<CreatureRarity, int>();
            foreach (var rarity in CreatureRarityLadder.All)
                slotsByRarity[rarity] = Int(slotsEl, rarity.ToString().ToLowerInvariant(), "slotsByRarity");

            // WAVE F2.3 (creature-standalone, 2026-09-07): a fusion pick's cost is read from the PICK'S
            // OWN source rarity, never the fusion output's — the same rung set recipeCost covers
            // (Cultivated and up), a different lookup key, hence its own table rather than a second
            // read of recipeCost.
            var inheritEl = Obj(root, "inheritCostByRarity", "$");
            var inheritCostByRarity = new Dictionary<CreatureRarity, long>();
            foreach (var rarity in CreatureRarityLadder.All)
            {
                if (!CreatureRarityLadder.AtLeast(rarity, CreatureRecipeCatalog.OutputEligibilityFloor)) continue;
                var key = rarity.ToString().ToLowerInvariant();
                inheritCostByRarity[rarity] = Long(inheritEl, key, "inheritCostByRarity");
            }

            return new FusionTuning(schemaVersion, version, perStarPowerMilli, perStarDefenseMilli,
                starCap, starMergeCost, promotionCost, promotionCostByRarity, recipeCost, slotsByRarity,
                inheritCostByRarity);
        }
    }

    static FusionCostTuning Cost(JsonElement root, string key)
    {
        var el = Obj(root, key, "$");
        return new FusionCostTuning(
            Souls: Long(el, "souls", key),
            ShardCount: Int(el, "shardCount", key),
            EssenceCount: Int(el, "essenceCount", key));
    }

    static CreatureRarity ParseRarity(string value, string path)
    {
        foreach (var rarity in Enum.GetValues<CreatureRarity>())
            if (string.Equals(rarity.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return rarity;
        throw new FusionTuningRejection($"fusion tuning: '{path}' is not a known rarity: '{value}'");
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new FusionTuningRejection($"fusion tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static string Str(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new FusionTuningRejection($"fusion tuning: missing or non-string '{path}.{key}'");
        return el.GetString()!;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new FusionTuningRejection($"fusion tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new FusionTuningRejection($"fusion tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}
