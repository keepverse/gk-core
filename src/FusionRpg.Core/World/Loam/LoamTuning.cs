using System.Text.Json;

namespace FusionRpg.Core.World.Loam;

public sealed record LoamUpkeepTuning(
    long SeepPerTurn, long LoamCapacity, long BaseUpkeepPerSector,
    long GarrisonUpkeepPerMember, long DevelopmentUpkeepPerLevel, long DangerUpkeepPerBand,
    long EmpireWonderUpkeepRateMilli);

/// <summary>
/// world-map W55 (spec-sector-development.md §3, empire-economy-ssot.md A8): the yield half of the
/// invariant "development must raise yield faster than it raises upkeep" — living beside
/// <see cref="LoamUpkeepTuning.DevelopmentUpkeepPerLevel"/> in the same tuning file on purpose, so
/// the comparison A8 makes is readable by inspection, not split across two files.
/// </summary>
public sealed record LoamDevelopmentTuning(long YieldPerLevel);

public sealed record LoamFadeTuning(
    int RecoveryMilli, int BaseDecayMilli, int DecayPerDeficitUnitMilli,
    long DecayScaleDivisor, int MaxDecayMilli, int AbandonmentHorizonTurns);

/// <summary>
/// base-defense siege-supply, decision 42: <paramref name="BesiegedRationMilli"/> — a garrison
/// topping up inside a BESIEGED sector draws at this per-mille of normal. `1000` = no rationing, the
/// shipped default, so the F1/F1b defect fix and this balance dial stay separable: at the neutral
/// default the ration is a no-op, and setting it below 1000 is a pure balance-pass change that touches
/// nothing else. Bounded ratio (0..1000), exempt from AGENTS.md's no-hard-ceilings rule, stated here
/// as that rule requires.
/// </summary>
public sealed record LoamLegionSupplyTuning(long CarryPerBearer, long BurnPerMember, int BesiegedRationMilli);

// world-map W57: the six `*Cost` fields below were `*CostMilli` — every one is a whole loam unit
// compared directly against a legion's CarriedLoam or a sector's LoamStock, never a per-mille. The
// `Milli` suffix lied; renamed, no value changed (see BuildResolver.cs / StructureCatalog.cs).
public sealed record LoamStructuresTuning(
    int WellYieldMultiplierMilli, long WellCost, long WaystationCost,
    int WellBuildTurns, int WaystationBuildTurns, int WaystationRangeHops,
    long GranaryCost, long GranaryCapacityBonus, int GranaryBuildTurns,
    // world-map W56 (spec-sector-development.md §3) — the yield kinds.
    long SoulConduitCost, long SoulConduitFlatYieldPerTurn, int SoulConduitBuildTurns,
    long ExtractorCost, long ExtractorFlatYieldPerTurn, int ExtractorBuildTurns,
    long HatcheryCost, int HatcheryYieldMultiplierMilli, int HatcheryBuildTurns);

public sealed record LoamTextureTuning(
    int ContagionPressurePerTurn, int MaxPressureMilli, int PressureDecayPerTurn,
    int SurgeDecayMultiplierMilli, int UnmadeSpawnAfterTurns, long UnmadeMemberHp, int UnmadeMemberCount);

/// <summary>Loam balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="LoamPolicy.Configure"/> and <see cref="LoamTuningLoader"/>.</summary>
public sealed record LoamTuning(
    int SchemaVersion, int Version,
    LoamUpkeepTuning Upkeep, LoamDevelopmentTuning Development, LoamFadeTuning Fade,
    LoamLegionSupplyTuning LegionSupply, LoamStructuresTuning Structures, LoamTextureTuning Texture);

public sealed class LoamTuningRejection : Exception
{
    public LoamTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2) — the host reads
/// <c>data/tuning/loam.v{n}.json</c> and calls <see cref="Parse"/>.</summary>
public static class LoamTuningLoader
{
    public static LoamTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new LoamTuningRejection("loam tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new LoamTuningRejection($"loam tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            var u = Obj(root, "upkeep", "$");
            var upkeep = new LoamUpkeepTuning(
                SeepPerTurn: Long(u, "seepPerTurn", "upkeep"),
                LoamCapacity: Long(u, "loamCapacity", "upkeep"),
                BaseUpkeepPerSector: Long(u, "baseUpkeepPerSector", "upkeep"),
                GarrisonUpkeepPerMember: Long(u, "garrisonUpkeepPerMember", "upkeep"),
                DevelopmentUpkeepPerLevel: Long(u, "developmentUpkeepPerLevel", "upkeep"),
                DangerUpkeepPerBand: Long(u, "dangerUpkeepPerBand", "upkeep"),
                EmpireWonderUpkeepRateMilli: Long(u, "empireWonderUpkeepRateMilli", "upkeep"));

            var d = Obj(root, "development", "$");
            var development = new LoamDevelopmentTuning(
                YieldPerLevel: Long(d, "yieldPerLevel", "development"));

            var f = Obj(root, "fade", "$");
            var fade = new LoamFadeTuning(
                RecoveryMilli: Int(f, "recoveryMilli", "fade"),
                BaseDecayMilli: Int(f, "baseDecayMilli", "fade"),
                DecayPerDeficitUnitMilli: Int(f, "decayPerDeficitUnitMilli", "fade"),
                DecayScaleDivisor: Long(f, "decayScaleDivisor", "fade"),
                MaxDecayMilli: Int(f, "maxDecayMilli", "fade"),
                AbandonmentHorizonTurns: Int(f, "abandonmentHorizonTurns", "fade"));

            var ls = Obj(root, "legionSupply", "$");
            var besiegedRationMilli = Int(ls, "besiegedRationMilli", "legionSupply");
            if (besiegedRationMilli < 0 || besiegedRationMilli > 1000)
                throw new LoamTuningRejection(
                    $"loam tuning: legionSupply.besiegedRationMilli must be within 0..1000 (it is a " +
                    $"draw-rate ratio); got {besiegedRationMilli}");
            var legionSupply = new LoamLegionSupplyTuning(
                CarryPerBearer: Long(ls, "carryPerBearer", "legionSupply"),
                BurnPerMember: Long(ls, "burnPerMember", "legionSupply"),
                BesiegedRationMilli: besiegedRationMilli);

            var s = Obj(root, "structures", "$");
            var structures = new LoamStructuresTuning(
                WellYieldMultiplierMilli: Int(s, "wellYieldMultiplierMilli", "structures"),
                WellCost: Long(s, "wellCost", "structures"),
                WaystationCost: Long(s, "waystationCost", "structures"),
                WellBuildTurns: Int(s, "wellBuildTurns", "structures"),
                WaystationBuildTurns: Int(s, "waystationBuildTurns", "structures"),
                WaystationRangeHops: Int(s, "waystationRangeHops", "structures"),
                GranaryCost: Long(s, "granaryCost", "structures"),
                GranaryCapacityBonus: Long(s, "granaryCapacityBonus", "structures"),
                GranaryBuildTurns: Int(s, "granaryBuildTurns", "structures"),
                SoulConduitCost: Long(s, "soulConduitCost", "structures"),
                SoulConduitFlatYieldPerTurn: Long(s, "soulConduitFlatYieldPerTurn", "structures"),
                SoulConduitBuildTurns: Int(s, "soulConduitBuildTurns", "structures"),
                ExtractorCost: Long(s, "extractorCost", "structures"),
                ExtractorFlatYieldPerTurn: Long(s, "extractorFlatYieldPerTurn", "structures"),
                ExtractorBuildTurns: Int(s, "extractorBuildTurns", "structures"),
                HatcheryCost: Long(s, "hatcheryCost", "structures"),
                HatcheryYieldMultiplierMilli: Int(s, "hatcheryYieldMultiplierMilli", "structures"),
                HatcheryBuildTurns: Int(s, "hatcheryBuildTurns", "structures"));

            var t = Obj(root, "texture", "$");
            var texture = new LoamTextureTuning(
                ContagionPressurePerTurn: Int(t, "contagionPressurePerTurn", "texture"),
                MaxPressureMilli: Int(t, "maxPressureMilli", "texture"),
                PressureDecayPerTurn: Int(t, "pressureDecayPerTurn", "texture"),
                SurgeDecayMultiplierMilli: Int(t, "surgeDecayMultiplierMilli", "texture"),
                UnmadeSpawnAfterTurns: Int(t, "unmadeSpawnAfterTurns", "texture"),
                UnmadeMemberHp: Long(t, "unmadeMemberHp", "texture"),
                UnmadeMemberCount: Int(t, "unmadeMemberCount", "texture"));

            return new LoamTuning(schemaVersion, version, upkeep, development, fade, legionSupply, structures, texture);
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new LoamTuningRejection($"loam tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new LoamTuningRejection($"loam tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new LoamTuningRejection($"loam tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}
