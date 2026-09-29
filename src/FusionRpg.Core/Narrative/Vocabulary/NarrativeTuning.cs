namespace FusionRpg.Core.Narrative.Vocabulary;

/// <summary>`spec-narrative-vocabulary.md` §4: one host kind's firing odds, per-mille, as `min(1000,
/// baseMilli + n × stepMilli)` where `n` is the misses since that host last fired.</summary>
public sealed record FiringRow(long BaseMilli, long StepMilli);

/// <summary>
/// `spec-narrative-vocabulary.md` §4: the narrative domain's whole balance surface, parsed once. Numeric
/// types are the spec's own table — every `*Milli`, clock tick and score is `long` (a rate grows with `n`
/// and `int` per-mille exceeds its range at Θ=3,213), a band step is `int` (bounded by the four-member
/// ladder), a gate threshold is `int` (a summoner level), and a count is `int`.
/// </summary>
public sealed record NarrativeTuning(
    int SchemaVersion,
    int Version,
    IReadOnlyDictionary<string, string> KindFrequencyBand,
    long SpecificityStepMilli,
    bool UnplayedFirst,
    IReadOnlyDictionary<string, FiringRow> Firing,
    IReadOnlyDictionary<string, long> CooldownPerStorylet,
    IReadOnlyDictionary<string, long> CooldownPerKind,
    int MaxNegativeInARow,
    IReadOnlyDictionary<string, int> ShiftByFactKind,
    IReadOnlyDictionary<string, string> BaseBandByRole,
    IReadOnlyDictionary<string, string> BaseBandByFactionKind,
    bool TopBandStoryGated,
    IReadOnlyDictionary<string, long> CastingScore,
    IReadOnlyDictionary<string, long> ResidentChanceMilliBySlot,
    IReadOnlyDictionary<string, long> SaveCastPerRole,
    IReadOnlyDictionary<string, long> GatesLeadLevel,
    IReadOnlyDictionary<string, long> DelveResidentsPerRole,
    IReadOnlyDictionary<string, long> QuestExpiry,
    IReadOnlyDictionary<string, long> QuestLogRecentClosed,
    long WorldOfferLifetimeTurns,
    IReadOnlyDictionary<string, long> FailurePriorityWindow,
    IReadOnlyDictionary<string, long> Doctrine,
    IReadOnlyDictionary<string, long> Readings);

/// <summary>
/// `spec-narrative-vocabulary.md` §4 and §5: the pure parser for `data/tuning/narrative.v{n}.json`. Every
/// key is required — a missing key is a rejection naming its full path (tunables-ssot T5: a missing
/// tunable is a load rejection, never a default) — and every cross-reference is a JOIN against the
/// registry that owns the vocabulary, never a second copy of it:
/// `firing` is one row per `HostKindCatalog` member, `cooldown` names `HostClockKind` wire ids,
/// `relation.baseBandByRole` is one row per non-`none` `NarrativeRoleCatalog` role,
/// `relation.shiftByFactKind` is `ConsequenceKindCatalog.RelationShiftParams`, and every band value is a
/// member of the ONE relation ladder (`DispositionCatalog`, the dungeon registry read here, never
/// re-declared).
///
/// <para><b>Prerequisites:</b> the registries (`NarrativeRegistryHub.Configure`) and the dungeon
/// disposition registry must be configured before <see cref="Parse"/>. A read before Configure throws —
/// loudly, never a default — which is why the host wires registries first (NR1.5).</para>
/// </summary>
public static class NarrativeTuningLoader
{
    const string File = "narrative.v1.json";

    /// <summary>The six storylet kinds a host can admit (seed §3.3's `fitsKinds` / the dungeon
    /// `eventKind` vocabulary). Pinned here because `selection.kindFrequencyBand` must cover exactly them
    /// and this loader reads no dungeon registry.</summary>
    public static readonly IReadOnlyList<string> StoryletKinds =
        new[] { "curio", "shrine", "trap", "bargain", "story", "encounter-event" };

    /// <summary>The three clocks a cooldown may be counted on. `delve.room` is ABSENT on purpose: the
    /// Delve's own `events.noRepeatRooms` recent-cell filter IS that cooldown (§4).</summary>
    public static readonly IReadOnlyList<string> CooldownClocks =
        new[] { "world.turn", "expedition.collect", "sanctum.return" };

    /// <summary>The six world slot names a resident chance is declared for (§4).</summary>
    public static readonly IReadOnlyList<string> ResidentSlots =
        new[] { "Market", "Shrine", "Wildland", "Vault", "Anomaly", "Tear" };

    /// <summary>The four faction kinds (§4).</summary>
    public static readonly IReadOnlyList<string> FactionKinds = new[] { "Clan", "Rival", "Zomboss", "Wild" };

    /// <summary>The roles with a save-cast limit. Pinned because the loader requires every key (§4's key
    /// table names `companion` at first ship); adding a role is a reviewed change here and in the file.
    /// </summary>
    public static readonly IReadOnlyList<string> SaveCastRoles = new[] { "companion" };

    /// <summary>`casting.delveResidentsPerRole` — the three roles delve-host §Data shapes declares.
    /// Pinned because the wave-0 file declares the union (plan D4) and the loader requires every key.</summary>
    public static readonly IReadOnlyList<string> DelveResidentRoles = new[] { "trader", "chronicler", "captive" };

    /// <summary>`quest.expiry` clocks (quest-sources §Data shapes). `delve.room` is absent: a quest does not
    /// lapse inside a room.</summary>
    public static readonly IReadOnlyList<string> QuestExpiryClocks = new[] { "world.turn", "expedition.collect", "sanctum.return" };

    /// <summary>`questLog.recentClosed` — one per `HostClockKind` (quest-log-contract §2), `delve.room`
    /// included because the window is declared per clock kind.</summary>
    public static readonly IReadOnlyList<string> QuestLogClocks = new[] { "world.turn", "expedition.collect", "sanctum.return", "delve.room" };

    /// <summary>`failure.priorityWindow` clocks (failure-branches §Data shapes).</summary>
    public static readonly IReadOnlyList<string> FailureWindowClocks = new[] { "world.turn", "sanctum.return", "delve.room" };

    /// <summary>`doctrine.*` — the six keys counter-doctrine §6 declares.</summary>
    public static readonly IReadOnlyList<string> DoctrineKeys =
        new[] { "leanThresholdMilli", "studyRatePerTurnMilli", "windowTurns", "cooldownTurns", "raidSetbackMilli", "raidShortenTurns" };

    /// <summary>`readings.*` — the three keys narrative-readings §Data shapes declares.</summary>
    public static readonly IReadOnlyList<string> ReadingsKeys = new[] { "minAnswers", "pickFloorMilli", "pickCeilMilli" };

    static readonly string[] CastingScoreKeys = { "metBefore", "perBandFriendlier", "notCastRecentlyPulses" };
    static readonly string[] DropBands = { "staple", "frequent", "occasional", "seldom", "exceptional" };

    public static NarrativeTuning Parse(string json)
    {
        var root = NarrativeRegistryJson.Root(json, File);
        NarrativeRegistryJson.RejectUnknownKeys(
            root,
            new[] { "schemaVersion", "version", "_meta", "selection", "firing", "cooldown", "fairness", "relation", "casting", "gates",
                    "quest", "questLog", "world", "failure", "doctrine", "readings" },
            "$", File);

        var version = NarrativeRegistryJson.RequireInt(root, "version", "$", File);
        if (version < 1)
            throw new NarrativeVocabularyRejection($"{File}: version {version} is not a published version (>= 1).");

        var selection = Object(root, "selection", "selection");
        NarrativeRegistryJson.RejectUnknownKeys(selection, new[] { "kindFrequencyBand", "specificityStepMilli", "unplayedFirst" }, "selection", File);
        var kindFrequencyBand = ClosedMap(selection, "kindFrequencyBand", "selection", StoryletKinds, DropBands);
        var specificityStepMilli = Milli(selection, "specificityStepMilli", "selection");
        if (specificityStepMilli <= 0)
            throw Reject("selection.specificityStepMilli", "must be positive.");
        var unplayedFirst = NarrativeRegistryJson.RequireBool(selection, "unplayedFirst", "selection", File);

        var firing = new Dictionary<string, FiringRow>(StringComparer.Ordinal);
        var firingElement = Object(root, "firing", "firing");
        RequireExactKeys(firingElement, "firing", HostKindCatalog.All.Select(x => x.Id).ToArray());
        foreach (var host in HostKindCatalog.All)
        {
            var row = firingElement.GetProperty(host.Id);
            var path = "firing." + host.Id;
            NarrativeRegistryJson.RejectUnknownKeys(row, new[] { "baseMilli", "stepMilli" }, path, File);
            var baseMilli = Milli(row, "baseMilli", path);
            var stepMilli = Milli(row, "stepMilli", path);
            if (baseMilli < 0 || stepMilli < 0)
                throw Reject(path, "must not be negative.");
            firing[host.Id] = new FiringRow(baseMilli, stepMilli);
        }

        var cooldown = Object(root, "cooldown", "cooldown");
        NarrativeRegistryJson.RejectUnknownKeys(cooldown, new[] { "perStorylet", "perKind" }, "cooldown", File);
        var perStorylet = ClosedLongMap(cooldown, "perStorylet", "cooldown", CooldownClocks);
        var perKind = ClosedLongMap(cooldown, "perKind", "cooldown", CooldownClocks);

        var fairness = Object(root, "fairness", "fairness");
        NarrativeRegistryJson.RejectUnknownKeys(fairness, new[] { "maxNegativeInARow" }, "fairness", File);
        var maxNegativeInARow = NarrativeRegistryJson.RequireInt(fairness, "maxNegativeInARow", "fairness", File);
        if (maxNegativeInARow < 0)
            throw Reject("fairness.maxNegativeInARow", "must not be negative.");

        var relation = Object(root, "relation", "relation");
        NarrativeRegistryJson.RejectUnknownKeys(
            relation, new[] { "shiftByFactKind", "baseBandByRole", "baseBandByFactionKind", "topBandStoryGated" }, "relation", File);
        var shiftByFactKind = ShiftMap(relation);
        var baseBandByRole = BandMap(relation, "baseBandByRole",
            NarrativeRoleCatalog.All.Where(x => x.Id != "none").Select(x => x.Id).ToArray());
        var baseBandByFactionKind = BandMap(relation, "baseBandByFactionKind", FactionKinds);
        var topBandStoryGated = NarrativeRegistryJson.RequireBool(relation, "topBandStoryGated", "relation", File);

        var casting = Object(root, "casting", "casting");
        NarrativeRegistryJson.RejectUnknownKeys(
            casting, new[] { "score", "residentChanceMilliBySlot", "saveCastPerRole", "delveResidentsPerRole" }, "casting", File);
        var score = ClosedLongMap(casting, "score", "casting", CastingScoreKeys);
        var residentChance = ClosedLongMap(casting, "residentChanceMilliBySlot", "casting", ResidentSlots);
        var saveCastPerRole = ClosedLongMap(casting, "saveCastPerRole", "casting", SaveCastRoles);
        foreach (var role in saveCastPerRole.Keys)
            if (!NarrativeRoleCatalog.IsKnown(role) || role == "none")
                throw Reject($"casting.saveCastPerRole.{role}", "is not a role in the character registry.");
        var delveResidentsPerRole = ClosedLongMap(casting, "delveResidentsPerRole", "casting", DelveResidentRoles);
        foreach (var role in delveResidentsPerRole.Keys)
            if (!NarrativeRoleCatalog.IsKnown(role) || role == "none")
                throw Reject($"casting.delveResidentsPerRole.{role}", "is not a role in the character registry.");

        // The six blocks the seven later specs declared for wave 0 (plan D4: NR1.4 ships the union, so no
        // later module has to hand-edit a committed version).
        var quest = Object(root, "quest", "quest");
        NarrativeRegistryJson.RejectUnknownKeys(quest, new[] { "expiry" }, "quest", File);
        var questExpiry = ClosedLongMap(quest, "expiry", "quest", QuestExpiryClocks);

        var questLog = Object(root, "questLog", "questLog");
        NarrativeRegistryJson.RejectUnknownKeys(questLog, new[] { "recentClosed" }, "questLog", File);
        var questLogRecentClosed = ClosedLongMap(questLog, "recentClosed", "questLog", QuestLogClocks);

        var world = Object(root, "world", "world");
        NarrativeRegistryJson.RejectUnknownKeys(world, new[] { "offerLifetimeTurns" }, "world", File);
        var worldOfferLifetimeTurns = Milli(world, "offerLifetimeTurns", "world");
        if (worldOfferLifetimeTurns < 0)
            throw Reject("world.offerLifetimeTurns", "must not be negative.");

        var failure = Object(root, "failure", "failure");
        NarrativeRegistryJson.RejectUnknownKeys(failure, new[] { "priorityWindow" }, "failure", File);
        var failurePriorityWindow = ClosedLongMap(failure, "priorityWindow", "failure", FailureWindowClocks);

        var doctrine = ClosedLongMap(root, "doctrine", string.Empty, DoctrineKeys);
        var readings = ClosedLongMap(root, "readings", string.Empty, ReadingsKeys);

        var gates = Object(root, "gates", "gates");
        NarrativeRegistryJson.RejectUnknownKeys(gates, new[] { "leadLevel" }, "gates", File);
        var leadLevel = new Dictionary<string, long>(StringComparer.Ordinal);
        var leadLevelElement = Object(gates, "leadLevel", "gates.leadLevel");
        foreach (var gate in leadLevelElement.EnumerateObject())
        {
            var path = "gates.leadLevel." + gate.Name;
            NarrativeRegistryIds.RequireWireId(gate.Name, "gate id", File);
            if (gate.Value.ValueKind != System.Text.Json.JsonValueKind.Number || !gate.Value.TryGetInt32(out var threshold))
                throw Reject(path, "must be an integer.");
            if (threshold < 0)
                throw Reject(path, "must not be negative (a level gate is a threshold, never a curve).");
            leadLevel[gate.Name] = threshold;
        }

        return new NarrativeTuning(1, version, kindFrequencyBand, specificityStepMilli, unplayedFirst, firing,
            perStorylet, perKind, maxNegativeInARow, shiftByFactKind, baseBandByRole, baseBandByFactionKind,
            topBandStoryGated, score, residentChance, saveCastPerRole, leadLevel,
            delveResidentsPerRole, questExpiry, questLogRecentClosed, worldOfferLifetimeTurns,
            failurePriorityWindow, doctrine, readings);
    }

    // ---- the closed-map helpers: one named path per missing/unknown/rejected entry -----------------

    static System.Text.Json.JsonElement Object(System.Text.Json.JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw Reject(path, "is missing or is not an object.");
        return value;
    }

    static long Milli(System.Text.Json.JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.Number ||
            !value.TryGetInt64(out var number))
            throw Reject($"{path}.{key}", "must be a 64-bit integer (every *Milli, clock and score is long).");
        return number;
    }

    static string PathOf(string path, string key) => path.Length == 0 ? key : path + "." + key;

    static void RequireExactKeys(System.Text.Json.JsonElement map, string path, IReadOnlyList<string> expected)
    {
        foreach (var expectedKey in expected)
            if (!map.TryGetProperty(expectedKey, out _))
                throw Reject(PathOf(path, expectedKey), "is missing (the loader requires every key).");
        foreach (var property in map.EnumerateObject())
            if (!expected.Contains(property.Name))
                throw Reject(PathOf(path, property.Name), $"is not a known key (expected one of {string.Join(", ", expected)}).");
    }

    static IReadOnlyDictionary<string, string> ClosedMap(
        System.Text.Json.JsonElement parent, string key, string path, IReadOnlyList<string> keys, IReadOnlyList<string> values)
    {
        var map = Object(parent, key, PathOf(path, key));
        RequireExactKeys(map, PathOf(path, key), keys);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expected in keys)
        {
            var entry = map.GetProperty(expected);
            if (entry.ValueKind != System.Text.Json.JsonValueKind.String)
                throw Reject(PathOf(PathOf(path, key), expected), "must be a string.");
            var value = entry.GetString()!;
            if (!values.Contains(value))
                throw Reject(PathOf(PathOf(path, key), expected), $"'{value}' is not one of {string.Join(", ", values)}.");
            result[expected] = value;
        }
        return result;
    }

    static IReadOnlyDictionary<string, long> ClosedLongMap(
        System.Text.Json.JsonElement parent, string key, string path, IReadOnlyList<string> keys)
    {
        var map = Object(parent, key, PathOf(path, key));
        RequireExactKeys(map, PathOf(path, key), keys);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var expected in keys)
        {
            var value = Milli(map, expected, PathOf(path, key));
            if (value < 0)
                throw Reject(PathOf(PathOf(path, key), expected), "must not be negative.");
            result[expected] = value;
        }
        return result;
    }

    static IReadOnlyDictionary<string, int> ShiftMap(System.Text.Json.JsonElement relation)
    {
        var map = Object(relation, "shiftByFactKind", "relation.shiftByFactKind");
        var expected = ConsequenceKindCatalog.RelationShiftParams;
        RequireExactKeys(map, "relation.shiftByFactKind", expected);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kind in expected)
        {
            var entry = map.GetProperty(kind);
            if (entry.ValueKind != System.Text.Json.JsonValueKind.Number || !entry.TryGetInt32(out var step))
                throw Reject($"relation.shiftByFactKind.{kind}", "must be a 32-bit band step (bounded by the four-member ladder).");
            result[kind] = step;
        }
        return result;
    }

    static IReadOnlyDictionary<string, string> BandMap(
        System.Text.Json.JsonElement relation, string key, IReadOnlyList<string> keys)
    {
        var map = Object(relation, key, $"relation.{key}");
        RequireExactKeys(map, $"relation.{key}", keys);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expected in keys)
        {
            var entry = map.GetProperty(expected);
            if (entry.ValueKind != System.Text.Json.JsonValueKind.String)
                throw Reject($"relation.{key}.{expected}", "must be a disposition id.");
            var band = entry.GetString()!;
            // The ONE relation ladder (R4): read from the dungeon disposition registry, never re-declared.
            if (!Dungeon.Registry.DispositionCatalog.IsKnown(band))
                throw Reject($"relation.{key}.{expected}", $"'{band}' is not a disposition id in the one relation ladder.");
            result[expected] = band;
        }
        return result;
    }

    static NarrativeVocabularyRejection Reject(string path, string why) => new($"{File}: {path} {why}");
}

/// <summary>
/// `spec-narrative-vocabulary.md` §5: the process-wide tuning cache. `Configure` is the only writer and
/// there is NO default — a read before `Configure` throws, because a default here would be a balance
/// number nobody published (tunables-ssot T5).
/// </summary>
public static class NarrativeTuningHub
{
    static NarrativeTuning? _tuning;

    public static NarrativeTuning Tuning => _tuning ?? throw new InvalidOperationException(
        $"{nameof(NarrativeTuningHub)}.Configure(...) has not run.");

    public static void Configure(NarrativeTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
}
