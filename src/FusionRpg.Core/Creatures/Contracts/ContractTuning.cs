using System.Text.Json;

namespace FusionRpg.Core.Creatures.Contracts;

/// <summary>One personality's rate multipliers (tunables-ssot.md, `contracts.v{n}.json`).</summary>
public sealed record PersonalityRateTuning(int GainPct, int DecayPct, int UpkeepPct);

public sealed record ContractLoyaltyTuning(
    int Max, int DeployFloor, int BindLoyalty,
    int SwornThreshold, int TrustedThreshold, int DevotedThreshold,
    int WinGain, int LossPenalty, int DailyGainCap, int DecayPerDay, int RitualGain,
    int RankBonusSwornMilli, int RankBonusTrustedMilli, int RankBonusDevotedMilli);

public sealed record ContractSlotsTuning(int BaseSlots, int SlotPriceStep);

public sealed record ContractSettlementTuning(int MaxSettleDays);

/// <summary>
/// Contract balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="ContractPolicy.Configure"/> and <see cref="ContractTuningLoader"/>.
/// </summary>
public sealed record ContractTuning(
    int SchemaVersion,
    int Version,
    ContractLoyaltyTuning Loyalty,
    ContractSlotsTuning Slots,
    ContractSettlementTuning Settlement,
    IReadOnlyDictionary<CreaturePersonality, PersonalityRateTuning> PersonalityRates,
    IReadOnlyDictionary<CreatureRarity, int> BaseUpkeepPerDay,
    IReadOnlyDictionary<CreatureRarity, long> RitualPriceSouls);

/// <summary>
/// A missing or malformed tunable — thrown, never defaulted (tunables-ssot.md T5: "a missing tunable
/// is a load rejection naming it, never a built-in default").
/// </summary>
public sealed class ContractTuningRejection : Exception
{
    public ContractTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser over a tuning JSON string — no file I/O (tunables-ssot.md §7.2: "Core never reads a
/// file. Hosts load and inject."). The host reads `data/tuning/contracts.v{n}.json` and calls
/// <see cref="Parse"/>; tests construct a JSON string inline.
/// </summary>
public static class ContractTuningLoader
{
    public static ContractTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ContractTuningRejection("contracts tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ContractTuningRejection($"contracts tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            var loyaltyEl = Obj(root, "loyalty", "$");
            var loyalty = new ContractLoyaltyTuning(
                Max: Int(loyaltyEl, "max", "loyalty"),
                DeployFloor: Int(loyaltyEl, "deployFloor", "loyalty"),
                BindLoyalty: Int(loyaltyEl, "bindLoyalty", "loyalty"),
                SwornThreshold: Int(loyaltyEl, "swornThreshold", "loyalty"),
                TrustedThreshold: Int(loyaltyEl, "trustedThreshold", "loyalty"),
                DevotedThreshold: Int(loyaltyEl, "devotedThreshold", "loyalty"),
                WinGain: Int(loyaltyEl, "winGain", "loyalty"),
                LossPenalty: Int(loyaltyEl, "lossPenalty", "loyalty"),
                DailyGainCap: Int(loyaltyEl, "dailyGainCap", "loyalty"),
                DecayPerDay: Int(loyaltyEl, "decayPerDay", "loyalty"),
                RitualGain: Int(loyaltyEl, "ritualGain", "loyalty"),
                RankBonusSwornMilli: Int(loyaltyEl, "rankBonusSwornMilli", "loyalty"),
                RankBonusTrustedMilli: Int(loyaltyEl, "rankBonusTrustedMilli", "loyalty"),
                RankBonusDevotedMilli: Int(loyaltyEl, "rankBonusDevotedMilli", "loyalty"));

            var slotsEl = Obj(root, "slots", "$");
            var slots = new ContractSlotsTuning(
                BaseSlots: Int(slotsEl, "baseSlots", "slots"),
                SlotPriceStep: Int(slotsEl, "slotPriceStep", "slots"));

            var settlementEl = Obj(root, "settlement", "$");
            var settlement = new ContractSettlementTuning(
                MaxSettleDays: Int(settlementEl, "maxSettleDays", "settlement"));

            var ratesEl = Obj(root, "personalityRates", "$");
            var rates = new Dictionary<CreaturePersonality, PersonalityRateTuning>();
            foreach (var personality in Enum.GetValues<CreaturePersonality>())
            {
                var key = ToJsonKey(personality);
                var rateEl = Obj(ratesEl, key, "personalityRates");
                rates[personality] = new PersonalityRateTuning(
                    GainPct: Int(rateEl, "gainPct", $"personalityRates.{key}"),
                    DecayPct: Int(rateEl, "decayPct", $"personalityRates.{key}"),
                    UpkeepPct: Int(rateEl, "upkeepPct", $"personalityRates.{key}"));
            }

            var upkeepEl = Obj(root, "baseUpkeepPerDay", "$");
            var upkeep = new Dictionary<CreatureRarity, int>();
            foreach (var rarity in Enum.GetValues<CreatureRarity>())
                upkeep[rarity] = Int(upkeepEl, ToJsonKey(rarity), "baseUpkeepPerDay");

            var ritualEl = Obj(root, "ritualPriceSouls", "$");
            var ritual = new Dictionary<CreatureRarity, long>();
            foreach (var rarity in Enum.GetValues<CreatureRarity>())
                ritual[rarity] = Long(ritualEl, ToJsonKey(rarity), "ritualPriceSouls");

            return new ContractTuning(schemaVersion, version, loyalty, slots, settlement,
                rates, upkeep, ritual);
        }
    }

    static string ToJsonKey(CreaturePersonality p) => p.ToString().ToLowerInvariant();
    static string ToJsonKey(CreatureRarity r) => r.ToString().ToLowerInvariant();

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new ContractTuningRejection($"contracts tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ContractTuningRejection($"contracts tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new ContractTuningRejection($"contracts tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}
