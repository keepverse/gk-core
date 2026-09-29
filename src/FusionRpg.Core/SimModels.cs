namespace FusionRpg.Core;

/// <summary>Config-backed magnitudes (tunables-ssot.md T1) — gk-core/data/tuning/sim.v1.json. Name strings
/// stay structural (not magnitudes).</summary>
public static class SimDefaults
{
    static SimTuning? _tuning;

    public static void Configure(SimTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static SimTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "SimDefaults.Configure(...) has not run. SimEngine's fallback stats read " +
        "data/tuning/sim.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    public static long PlantHp => Tuning.PlantHp;
    public static long PlantAttack => Tuning.PlantAttack;
    public static long ZombieHp => Tuning.ZombieHp;
    public static long ZombieAttack => Tuning.ZombieAttack;
    public static long HitDamage => Tuning.HitDamage;
    public const string LevelName = "Sim";
    public const string PlantTypeName = "Peashooter";
    public const string ZombieTypeName = "Zombie";
}

public sealed class SimEntity
{
    public string Side { get; set; } = "";
    public string Ptr { get; set; } = "";
    public int Type { get; set; }
    public string TypeName { get; set; } = "";
    public long HpBase { get; set; }
    public long Hp { get; set; }
    public long MaxHpBase { get; set; }
    public long MaxHp { get; set; }
    public long AttackBase { get; set; }
    public long Attack { get; set; }
    public long ArmorBase { get; set; }
    public long Armor { get; set; }
    public long ArmorMax { get; set; }
    public int Col { get; set; }
    public int Row { get; set; }
}

public sealed class SimMower
{
    public string Ptr { get; set; } = "";
    public int Type { get; set; }
    public string TypeName { get; set; } = "LawnMower";
    public int Row { get; set; }
    public bool Started { get; set; }
    public bool Dead { get; set; }
}

public sealed class SimSpawnPlantRequest
{
    public int? Type { get; set; }
    public string? TypeName { get; set; }
    public long? Hp { get; set; }
    public long? MaxHp { get; set; }
    public long? Attack { get; set; }
    public int? Col { get; set; }
    public int? Row { get; set; }
    public string? Ptr { get; set; }
}

public sealed class SimSpawnZombieRequest
{
    public int? Type { get; set; }
    public string? TypeName { get; set; }
    public long? Hp { get; set; }
    public long? MaxHp { get; set; }
    public long? Attack { get; set; }
    public long? Armor { get; set; }
    public long? ArmorMax { get; set; }
    public string? Ptr { get; set; }
    /// <summary>Capture dump source tag (e.g. extra for PvzIntent).</summary>
    public string? Source { get; set; }
    /// <summary>Optional typed progression claim carried by simulator spawn events.</summary>
    public string? SourceKind { get; set; }
    public string? SourceId { get; set; }
}

public sealed class SimDamageRequest
{
    public string Ptr { get; set; } = "";
    public long? Damage { get; set; }
}

/// <summary>Sim shield-grant probe (sim-adoption) — durations unsupported (no sim tick host).</summary>
public sealed class SimShieldGrantRequest
{
    public string Ptr { get; set; } = "";
    public long Amount { get; set; }
    public string? Element { get; set; }
    public int? Priority { get; set; }
    public string? SourceId { get; set; }
}

public sealed class SimDieRequest
{
    public string Ptr { get; set; } = "";
    public int? Reason { get; set; }
    public string? ReasonName { get; set; }
}

public sealed class SimBoardStartRequest
{
    public string? LevelName { get; set; }
    public string? MatchKey { get; set; }
    public string? LevelType { get; set; }
    public int? BoardLevel { get; set; }
    public float? ZombieHealthMultiplier { get; set; }
}

public sealed class SimBoardEndRequest
{
    public object? Summary { get; set; }
    public string? LevelName { get; set; }
}

public sealed class SimMatchResultRequest
{
    public string Result { get; set; } = "victory";
}

public sealed class SimSnapshotRequest
{
    public int? Sun { get; set; }
    public int? Wave { get; set; }
    public int? MaxWave { get; set; }
    public int? MowerUsedCount { get; set; }
    public int? PlantsPlanted { get; set; }
    public int? PlantsDied { get; set; }
    public int? ZombiesKilled { get; set; }
    public double? Duration { get; set; }
    public string? GameResult { get; set; }
    public int? SunProduced { get; set; }
    public int? SunConsumed { get; set; }
    public float? TotalZombieDamage { get; set; }
    public int? PlantsShoveled { get; set; }
    public int? ZombiesMindControlled { get; set; }
    public int? MoneyEarned { get; set; }
}

public sealed class SimWaveRequest
{
    public int Wave { get; set; }
    public int? MaxWave { get; set; }
}

public sealed class SimPlacePlantRequest
{
    public int? Type { get; set; }
    public string? TypeName { get; set; }
    public int? Col { get; set; }
    public int? Row { get; set; }
    public string? Ptr { get; set; }
}

public sealed class SimPlaceZombieRequest
{
    public int? Type { get; set; }
    public string? TypeName { get; set; }
    public int? Row { get; set; }
    public string? Ptr { get; set; }
    public bool MindControlled { get; set; }
}

public sealed class SimMixRequest
{
    public int? UsedType { get; set; }
    public string? UsedTypeName { get; set; }
    public string? PlantPtr { get; set; }
    public int? Row { get; set; }
}

public sealed class SimEconomyRequest
{
    public int? Sun { get; set; }
    public int? Money { get; set; }
    public int? Points { get; set; }
    public int? PlantedCount { get; set; }
}

public sealed class SimSunSpendRequest
{
    public float Count { get; set; } = 25;
}

public sealed class SimLevelNameRequest
{
    public string? LevelName { get; set; }
}

public sealed class SimEntityStatsRequest
{
    public string Ptr { get; set; } = "";
    public string? Side { get; set; }
    public long? Hp { get; set; }
    public string? Source { get; set; }
}

public sealed class SimCardUseRequest
{
    public int? PlantType { get; set; }
    public string? TypeName { get; set; }
    public int? Cost { get; set; }
    public int? PlantLevel { get; set; }
}

public sealed class SimPetRequest
{
    public int? PetType { get; set; }
    public string? TypeName { get; set; }
    public string? Ptr { get; set; }
}

public sealed class SimGridRequest
{
    public int? Type { get; set; }
    public string? TypeName { get; set; }
    public int? Col { get; set; }
    public int? Row { get; set; }
    public string? Ptr { get; set; }
}

public sealed class SimMowerRequest
{
    public string? Ptr { get; set; }
    public int? Type { get; set; }
    public string? TypeName { get; set; }
    public int? Row { get; set; }
}

public sealed class SimResult
{
    public List<FusionRpg.Contracts.EventEnvelope> Events { get; } = new();
    public string? Error { get; set; }
    public bool Skipped { get; set; }
    public string? MatchKey { get; set; }
}
