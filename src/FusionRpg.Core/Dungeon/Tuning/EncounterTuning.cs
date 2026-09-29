using System.Text.Json;
using FusionRpg.Core.Dungeon.Registry;

namespace FusionRpg.Core.Dungeon.Tuning;

public sealed record SlotCountBandTuning(int Min, int Max);

public sealed record FormationPartyTuning(int SlotsMin, int SlotsMax, int MaxRepeatedPosture);

public sealed record AffixKitTierTuning(int AffixCount, string FloorRung, string CeilRung);

/// <summary>
/// The whole of <c>gk-core/data/tuning/encounter.v1.json</c> — the encounter generator's balance surface.
/// Every key is required (T5); see <see cref="DungeonTuning"/> for the sibling file.
/// </summary>
public sealed record EncounterTuning(
    int SchemaVersion, int Version,
    IReadOnlyDictionary<string, SlotCountBandTuning> SlotCountBand,
    string ThreatWindowBossFloorRung,
    IReadOnlyDictionary<string, long> SpreadOffClimateMilli,
    int FormationPackW, int FormationPartyW, FormationPartyTuning FormationParty, int FormationBossRankSpan,
    int BossFightLengthTargetRoundsMin, int BossFightLengthTargetRoundsMax,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, AffixKitTierTuning>> AffixKitTier,
    IReadOnlyList<long> PhaseBreakpointHpThresholdMilli, IReadOnlyList<long> PhaseEscalatingHpThresholdMilli,
    int SummonCapPerBoss, long PackSameSpeciesMaxMilli);

public sealed class EncounterTuningRejection : Exception
{
    public EncounterTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser (tunables-ssot.md §7.2). Takes the loaded <see cref="DungeonRegistries"/>
/// so <c>slot.countBand.*</c> is checked against the registry's own countBand member set.</summary>
public static class EncounterTuningLoader
{
    const string File = "encounter.v1.json";

    /// <summary>The threat ladder is READ, never restated here (tier-propagation-contract
    /// T-2): <paramref name="threatRungIds"/> must be <c>CreatureThreatTuning.RungIds</c> parsed
    /// from the real <c>gk-core/data/tuning/creature-threat.v1.json</c> — a different vocabulary from the
    /// dungeon difficulty rungs; threatWindow.bossFloorRung picks from this list, never the other.</summary>
    public static EncounterTuning Parse(string json, DungeonRegistries registries, IReadOnlyList<string> threatRungIds)
    {
        var root = Root(json);
        var countBandMembers = registries.Bands["countBand"].Members;
        var elementSpreadMembers = registries.Bands["elementSpread"].Members;

        var slotEl = Obj(root, "slot", "$");
        var countBandEl = Obj(slotEl, "countBand", "slot");
        var slotCountBand = new Dictionary<string, SlotCountBandTuning>(StringComparer.Ordinal);
        foreach (var m in countBandMembers)
        {
            var c = Obj(countBandEl, m, "slot.countBand");
            slotCountBand[m] = new SlotCountBandTuning(Int(c, "min", $"slot.countBand.{m}"), Int(c, "max", $"slot.countBand.{m}"));
        }
        RejectExtraKeys(countBandEl, countBandMembers, "slot.countBand");

        var threatWindowEl = Obj(root, "threatWindow", "$");
        var bossFloorRung = Str(threatWindowEl, "bossFloorRung", "threatWindow");
        if (!threatRungIds.Contains(bossFloorRung, StringComparer.Ordinal))
            throw new EncounterTuningRejection($"{File}: threatWindow.bossFloorRung '{bossFloorRung}' is not one of creature-threat.v1.json's rung ids.");

        var spreadEl = Obj(root, "spread", "$");
        var spread = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var m in elementSpreadMembers)
            spread[m] = Long(Obj(spreadEl, m, "spread"), "offClimateMilli", $"spread.{m}");
        RejectExtraKeys(spreadEl, elementSpreadMembers, "spread");

        var formationEl = Obj(root, "formation", "$");
        var packEl = Obj(formationEl, "pack", "formation");
        var partyEl = Obj(formationEl, "party", "formation");
        var bossEl = Obj(formationEl, "boss", "formation");
        var slotsEl = Obj(partyEl, "slots", "formation.party");
        var formationParty = new FormationPartyTuning(
            SlotsMin: Int(slotsEl, "min", "formation.party.slots"), SlotsMax: Int(slotsEl, "max", "formation.party.slots"),
            MaxRepeatedPosture: Int(partyEl, "maxRepeatedPosture", "formation.party"));

        var bossFightLength = Obj(root, "boss", "$");
        var fightLengthEl = Obj(bossFightLength, "fightLengthTargetRounds", "boss");

        var affixEl = Obj(root, "affix", "$");
        var affixKitTier = new Dictionary<string, IReadOnlyDictionary<string, AffixKitTierTuning>>(StringComparer.Ordinal);
        foreach (var role in new[] { "elite", "boss" })
        {
            var roleEl = Obj(affixEl, $"{role}KitTier", "affix");
            var tiers = new Dictionary<string, AffixKitTierTuning>(StringComparer.Ordinal);
            foreach (var tier in new[] { "t1", "t2", "t3" })
            {
                var t = Obj(roleEl, tier, $"affix.{role}KitTier");
                var path = $"affix.{role}KitTier.{tier}";
                tiers[tier] = new AffixKitTierTuning(Int(t, "affixCount", path), Str(t, "floorRung", path), Str(t, "ceilRung", path));
            }
            affixKitTier[role] = tiers;
        }

        var phaseEl = Obj(root, "phase", "$");
        var breakpointEl = Obj(phaseEl, "breakpoint", "phase");
        var escalatingEl = Obj(phaseEl, "escalating", "phase");
        var breakpointThresholds = LongArray(breakpointEl, "hpThresholdMilli", "phase.breakpoint");
        if (breakpointThresholds.Count != 1)
            throw new EncounterTuningRejection($"{File}: phase.breakpoint.hpThresholdMilli must have exactly 1 entry, found {breakpointThresholds.Count}.");
        var escalatingThresholds = LongArray(escalatingEl, "hpThresholdMilli", "phase.escalating");
        if (escalatingThresholds.Count != 2)
            throw new EncounterTuningRejection($"{File}: phase.escalating.hpThresholdMilli must have exactly 2 entries, found {escalatingThresholds.Count}.");

        var summonEl = Obj(root, "summon", "$");
        var packRootEl = Obj(root, "pack", "$");

        return new EncounterTuning(
            Int(root, "schemaVersion", "$"), Int(root, "version", "$"),
            slotCountBand, bossFloorRung, spread,
            Int(packEl, "w", "formation.pack"), Int(partyEl, "w", "formation.party"), formationParty, Int(bossEl, "rankSpan", "formation.boss"),
            Int(fightLengthEl, "min", "boss.fightLengthTargetRounds"), Int(fightLengthEl, "max", "boss.fightLengthTargetRounds"),
            affixKitTier, breakpointThresholds, escalatingThresholds,
            Int(summonEl, "capPerBoss", "summon"), Long(packRootEl, "sameSpeciesMaxMilli", "pack"));
    }

    static JsonElement Root(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new EncounterTuningRejection($"{File}: empty document");
        try { return JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new EncounterTuningRejection($"{File}: not valid JSON — {ex.Message}"); }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new EncounterTuningRejection($"{File}: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new EncounterTuningRejection($"{File}: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new EncounterTuningRejection($"{File}: missing or non-integer '{path}.{key}'");
        return v;
    }

    static string Str(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new EncounterTuningRejection($"{File}: missing or non-string '{path}.{key}'");
        return el.GetString()!;
    }

    static IReadOnlyList<long> LongArray(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new EncounterTuningRejection($"{File}: missing or non-array '{path}.{key}'");
        var list = new List<long>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var v))
                throw new EncounterTuningRejection($"{File}: '{path}.{key}' must be an array of integers");
            list.Add(v);
        }
        return list;
    }

    static void RejectExtraKeys(JsonElement obj, IReadOnlyList<string> requiredMembers, string path)
    {
        var required = new HashSet<string>(requiredMembers, StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            if (!required.Contains(prop.Name))
                throw new EncounterTuningRejection($"{File}: '{path}.{prop.Name}' is not a known member — extra key.");
    }
}

/// <summary>Single configuration point for <see cref="EncounterTuning"/>.</summary>
public static class EncounterTuningHub
{
    static EncounterTuning? _tuning;

    public static void Configure(EncounterTuning tuning) => _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static EncounterTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "EncounterTuningHub.Configure(...) has not run. Every encounter rule reads data/tuning/" +
        "encounter.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
