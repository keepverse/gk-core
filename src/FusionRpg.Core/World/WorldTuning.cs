using System.Text.Json;

namespace FusionRpg.Core.World;

public sealed record WorldSizeNodeRange(int Min, int Max);

public sealed record StrengthBandTuning(long Floor, long Ceiling, long Midpoint);

public sealed record WorldCalendarTuning(
    int DaysPerWeek, int WeeksPerMonth,
    int SpecialWeekChanceMilli, int SpecialMonthChanceMilli, int PlagueChanceMilli);

/// <summary>
/// world-stage W30: how much of a turn's march a `dowse` stance leaves. A balance number, not a
/// structural one — a dowser sees four lanes out (<see cref="Intel.Prospecting.DowserSightLanes"/>)
/// against a scout's two, so it is priced against <see cref="Movement.MovementPolicy.ScoutPointsPerTurn"/>
/// the same way, never a `const` in <see cref="Movement.MovementPolicy"/> itself.
/// </summary>
public sealed record MovementTuning(
    int DowseBudgetMilli,
    int ClaimCostMilli,
    int DepositCostMilli,
    int WithdrawCostMilli,
    int LoadCostMilli,
    int UnloadCostMilli,
    int HoldAllowanceMilli);

/// <summary>
/// world-map W42 (spec-sector-development.md §1): the calibration target read by the acceptance
/// harness (W59), never by the engine — a legion count the engine enforced would be a hard
/// progression ceiling (AGENTS.md).
/// </summary>
public sealed record LegionTargetTuning(int Min, int Max, int ByTurn);

/// <summary>
/// world-map W42: the seated weekly recruit pulse (spec-sector-development.md §1) — a held Seat
/// contributes <see cref="SeatPulsePerWeek"/> and a cleared lair multiplies its sector's pulse by
/// <see cref="LairMultiplierMilli"/>; a special week scales it by <see cref="SpecialWeekMultiplierMilli"/>
/// (a plague month suppresses it outright, `TurnCalendar.cs:52-54`'s own rule, no tunable needed).
/// Ships with the pulse at 0 and both multipliers at identity (1000‰) — `RecruitPolicy` (W43) reads
/// these, but nothing downstream moves a hash until the pulse itself is turned on alongside W58.
///
/// <see cref="RaiseMemberHp"/> (world-map W51): a raised legion's one founding member's starting Hp
/// — its own tunable, not a reuse of <c>LoamPolicy.UnmadeMemberHp</c>, because a barbarian's
/// difficulty and a player's own legion strength are different balance surfaces a pass would want
/// to move independently.
/// </summary>
public sealed record WorldGrowthTuning(
    long SeatPulsePerWeek, int LairMultiplierMilli, int SpecialWeekMultiplierMilli,
    long RaiseCostPoints, long RaiseMemberHp, LegionTargetTuning LegionTarget);

/// <summary>
/// world-map W42/W47: a season is a pure function of the turn (spec-sector-development.md §2),
/// `season(turn) = (turn / (DaysPerWeek*WeeksPerMonth*MonthsPerSeason)) % Count`. Each per-season
/// multiplier array is indexed by that result and ships at identity (1000‰) until W58.
/// </summary>
public sealed record WorldSeasonsTuning(
    int Count, int MonthsPerSeason,
    IReadOnlyList<int> YieldMilli, IReadOnlyList<int> UpkeepMilli, IReadOnlyList<int> MovementMilli);

/// <summary>World balance surface (tunables-ssot.md T1) — loaded, not hard-coded. Row ids/names/
/// structural flags stay in their C# catalogs; only the numeric fields live here. See
/// <see cref="WorldTuningHub.Configure"/> and <see cref="WorldTuningLoader"/>.</summary>
public sealed record WorldTuning(
    int SchemaVersion, int Version,
    IReadOnlyDictionary<string, int> LaneCostMultiplierMilli,
    IReadOnlyDictionary<string, WorldSizeNodeRange> WorldSizeNodes,
    IReadOnlyList<StrengthBandTuning> StrengthBands,
    WorldCalendarTuning Calendar,
    MovementTuning Movement,
    WorldGrowthTuning Growth,
    WorldSeasonsTuning Seasons);

public sealed class WorldTuningRejection : Exception
{
    public WorldTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class WorldTuningLoader
{
    public static WorldTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new WorldTuningRejection("world tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new WorldTuningRejection($"world tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            var laneEl = Obj(root, "laneCostMultiplierMilli", "$");
            var lanes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var prop in laneEl.EnumerateObject())
                lanes[prop.Name] = RequireInt(prop.Value, $"laneCostMultiplierMilli.{prop.Name}");

            var sizeEl = Obj(root, "worldSizeNodes", "$");
            var sizes = new Dictionary<string, WorldSizeNodeRange>(StringComparer.Ordinal);
            foreach (var prop in sizeEl.EnumerateObject())
            {
                var path = $"worldSizeNodes.{prop.Name}";
                sizes[prop.Name] = new WorldSizeNodeRange(
                    Int(prop.Value, "min", path), Int(prop.Value, "max", path));
            }

            var bandsEl = Require(root, "strengthBands", "$");
            if (bandsEl.ValueKind != JsonValueKind.Array)
                throw new WorldTuningRejection("world tuning: 'strengthBands' must be an array");
            var bands = new List<StrengthBandTuning>();
            var i = 0;
            foreach (var el in bandsEl.EnumerateArray())
            {
                var path = $"strengthBands[{i}]";
                bands.Add(new StrengthBandTuning(
                    Long(el, "floor", path), Long(el, "ceiling", path), Long(el, "midpoint", path)));
                i++;
            }

            var cal = Obj(root, "calendar", "$");
            var calendar = new WorldCalendarTuning(
                DaysPerWeek: Int(cal, "daysPerWeek", "calendar"),
                WeeksPerMonth: Int(cal, "weeksPerMonth", "calendar"),
                SpecialWeekChanceMilli: Int(cal, "specialWeekChanceMilli", "calendar"),
                SpecialMonthChanceMilli: Int(cal, "specialMonthChanceMilli", "calendar"),
                PlagueChanceMilli: Int(cal, "plagueChanceMilli", "calendar"));

            var mv = Obj(root, "movement", "$");
            var movement = new MovementTuning(
                DowseBudgetMilli: Int(mv, "dowseBudgetMilli", "movement"),
                ClaimCostMilli: NonNegativeInt(mv, "claimCostMilli", "movement"),
                DepositCostMilli: NonNegativeInt(mv, "depositCostMilli", "movement"),
                WithdrawCostMilli: NonNegativeInt(mv, "withdrawCostMilli", "movement"),
                LoadCostMilli: NonNegativeInt(mv, "loadCostMilli", "movement"),
                UnloadCostMilli: NonNegativeInt(mv, "unloadCostMilli", "movement"),
                HoldAllowanceMilli: NonNegativeInt(mv, "holdAllowanceMilli", "movement"));

            var gr = Obj(root, "growth", "$");
            var legionTargetEl = Obj(gr, "legionTarget", "growth");
            var growth = new WorldGrowthTuning(
                SeatPulsePerWeek: Long(gr, "seatPulsePerWeek", "growth"),
                LairMultiplierMilli: Int(gr, "lairMultiplierMilli", "growth"),
                SpecialWeekMultiplierMilli: Int(gr, "specialWeekMultiplierMilli", "growth"),
                RaiseCostPoints: Long(gr, "raiseCostPoints", "growth"),
                RaiseMemberHp: Long(gr, "raiseMemberHp", "growth"),
                LegionTarget: new LegionTargetTuning(
                    Min: Int(legionTargetEl, "min", "growth.legionTarget"),
                    Max: Int(legionTargetEl, "max", "growth.legionTarget"),
                    ByTurn: Int(legionTargetEl, "byTurn", "growth.legionTarget")));

            var se = Obj(root, "seasons", "$");
            var seasonCount = Int(se, "count", "seasons");
            var yieldMilli = IntArray(se, "yieldMilli", "seasons");
            var upkeepMilli = IntArray(se, "upkeepMilli", "seasons");
            var movementMilli = IntArray(se, "movementMilli", "seasons");
            // Read by index (`TurnCalendar.SeasonOf(turn) % Count`, world-map W47/W48) — a mismatched
            // array length is a config error that must fail loudly at boot, not throw an
            // IndexOutOfRangeException the first time a turn lands on the missing entry.
            if (yieldMilli.Count != seasonCount)
                throw new WorldTuningRejection($"world tuning: 'seasons.yieldMilli' has {yieldMilli.Count} entries, expected 'seasons.count' ({seasonCount})");
            if (upkeepMilli.Count != seasonCount)
                throw new WorldTuningRejection($"world tuning: 'seasons.upkeepMilli' has {upkeepMilli.Count} entries, expected 'seasons.count' ({seasonCount})");
            if (movementMilli.Count != seasonCount)
                throw new WorldTuningRejection($"world tuning: 'seasons.movementMilli' has {movementMilli.Count} entries, expected 'seasons.count' ({seasonCount})");

            var seasons = new WorldSeasonsTuning(
                Count: seasonCount,
                MonthsPerSeason: Int(se, "monthsPerSeason", "seasons"),
                YieldMilli: yieldMilli,
                UpkeepMilli: upkeepMilli,
                MovementMilli: movementMilli);

            return new WorldTuning(
                schemaVersion, version, lanes, sizes, bands, calendar, movement,
                growth, seasons);
        }
    }

    static JsonElement Require(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el))
            throw new WorldTuningRejection($"world tuning: missing '{path}.{key}'");
        return el;
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        var el = Require(parent, key, path);
        if (el.ValueKind != JsonValueKind.Object)
            throw new WorldTuningRejection($"world tuning: '{path}.{key}' must be an object");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el))
            throw new WorldTuningRejection($"world tuning: missing '{path}.{key}'");
        return RequireInt(el, $"{path}.{key}");
    }

    static int RequireInt(JsonElement el, string path)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new WorldTuningRejection($"world tuning: '{path}' must be an integer");
        return v;
    }

    static int NonNegativeInt(JsonElement parent, string key, string path)
    {
        var v = Int(parent, key, path);
        if (v < 0)
            throw new WorldTuningRejection($"world tuning: '{path}.{key}' must be non-negative");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new WorldTuningRejection($"world tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static IReadOnlyList<int> IntArray(JsonElement parent, string key, string path)
    {
        var el = Require(parent, key, path);
        if (el.ValueKind != JsonValueKind.Array)
            throw new WorldTuningRejection($"world tuning: '{path}.{key}' must be an array");
        var result = new List<int>();
        foreach (var item in el.EnumerateArray())
            result.Add(RequireInt(item, $"{path}.{key}[{result.Count}]"));
        return result;
    }
}

/// <summary>
/// Single configuration point for every world catalog/policy this tuning file feeds — mirrors
/// <c>ContractPolicy.Configure</c> / <c>LoamPolicy.Configure</c>, but one call covers several files
/// (<see cref="LaneTypeCatalog"/>, <see cref="WorldSizeCatalog"/>, <see cref="Intel.StrengthBandCatalog"/>,
/// <see cref="Turn.TurnCalendar"/>) since they all read one <c>world.v{n}.json</c>.
/// </summary>
public static class WorldTuningHub
{
    static WorldTuning? _tuning;

    public static void Configure(WorldTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static WorldTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "WorldTuningHub.Configure(...) has not run. Every world catalog/policy number reads " +
        "data/tuning/world.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
