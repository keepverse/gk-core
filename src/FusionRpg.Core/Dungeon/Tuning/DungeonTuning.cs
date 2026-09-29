using System.Text.Json;
using FusionRpg.Core.Dungeon.Registry;

namespace FusionRpg.Core.Dungeon.Tuning;

// ---------------------------------------------------------------------------------------------
// Block records. Every block mirrors one row of spec-dungeon-registries.md's tuning table
// (§"Tuning schema — gk-core/data/tuning/dungeon.v1.json") — units in the property name (T6), long for
// anything ContentScale/P(Θ) can touch, int for bands/counts/rows/rungs/Θ deltas.
// ---------------------------------------------------------------------------------------------

public sealed record RaidModeTuning(int Parties, int SquadSlots, int WalksDelta, int BossW, long? BossShieldPerPartyMilli, int PackRows, int PackCols);

public sealed record MinMax(int Min, int Max);

public sealed record NodeTuning(long WeightMilli, long EarliestRowMilli, long LatestRowMilli);

public sealed record UnknownPityTuning(long BaseMilli, long StepMilli);

public sealed record RewardWindow(string FloorRung, string CeilRung);

public sealed record NerveTuning(
    IReadOnlyList<int> StageThresholds, int StackPerElite, int StackPerBoss, int StackPerRetreat,
    int StackPerCurio, long RestRelief);

public sealed record WildOutcomeRow(long JoinsMilli, long TakesLeavesMilli, long FleesMilli, long AttacksMilli);

public sealed record WildOfferPreference(string Souls, string Spirit, string Item, string Creature);

public sealed record WildTideTuning(bool Enabled, IReadOnlyList<int> ShiftRungs);

public sealed record CaptureTuning(
    long UsableBelowMilli,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> ChanceMilliByHpBandThenDeltaBand,
    IReadOnlyDictionary<string, long> StatusBonusMilliByCountBand,
    IReadOnlyList<int> SealTierShiftBands,
    int FailStepBands);

/// <summary>Per rung, every reward/penalty column R8 requires — "hard" is the identity row: every
/// *MultMilli is 1000, every delta is 0.</summary>
public sealed record DifficultyRungTuning(
    int BandDelta, long EliteWeightMultMilli, long RestWeightMultMilli, long RestHealMultMilli,
    long HungerMultMilli, long SpiritDrainMultMilli, long MerchantMarkupMultMilli,
    int WildDispositionShiftRungs, int EnemyCountDeltaFight, int EnemyCountDeltaElite,
    int BossRetinuePerPartyDelta, int BossWDelta, int ProvisionCellsDelta,
    bool RestEveryOtherRow, bool RestRowsOnlyBeforeBoss, bool DoubleBoss,
    int EventSeverityTier, int EliteKitTier, int BossKitTier,
    long UnknownPityStepMultMilliCache, long UnknownPityStepMultMilliMerchant, long UnknownPityStepMultMilliFight,
    string? RarityFloor, int RarityShiftRungs);

public sealed record DifficultyTailTuning(bool Enabled, string StartsAfterRung, int BandStepPerPlus, string LabelFormat, string RulesFrozenAtRung);

public sealed record DomainOnceEntryTuning(int BandDelta, string BossRarityFloor, bool SealOnWipe, bool FailKeepsBossLoot);

public sealed record DomainTuning(string MaxRungWithoutOath, string PermadeathFromRung, DomainOnceEntryTuning OnceEntry);

public sealed record ObjectsTuning(string BreakMode, long BreakStaminaMilli, string StructureHpBand, int MerchantStockCount);

public sealed record PackProvisionTuning(int BaseCells, string AutopilotFloorRule, long AutopilotSwapMarginMilli, long FillBandIdentityMinMilli, long FillBandIdentityMaxMilli);

/// <summary>
/// The whole of <c>gk-core/data/tuning/dungeon.v1.json</c> — the Delve's structural + balance surface for
/// everything but encounters (see <see cref="EncounterTuning"/>). Every block is required; a
/// missing leaf is a load rejection naming its dotted path (T5), not a default.
/// </summary>
public sealed record DungeonTuning(
    int SchemaVersion, int Version,
    IReadOnlyDictionary<string, RaidModeTuning> RaidModes,
    int PreflightSampleSeeds,
    IReadOnlyDictionary<string, int> DangerBand,
    IReadOnlyDictionary<string, MinMax> DepthBandRows,
    IReadOnlyDictionary<string, MinMax> WidthBandCols,
    IReadOnlyDictionary<string, int> BranchinessPathWalks,
    IReadOnlyDictionary<string, long> GateDensityPerRoomMilli,
    IReadOnlyDictionary<string, long> SecretDensityPerRoomMilli,
    IReadOnlyDictionary<string, long> OneWayDensityPerRoomMilli,
    IReadOnlyDictionary<string, int> SightBandExtraLanes,
    int SightLanes, int SightScoutLanes,
    IReadOnlyDictionary<string, long> HazardBandHungerPerMille,
    long GraphFixedRowsMidCacheRowMilli, long GraphSecretAppearMilli, int GraphMinDeadEnds,
    IReadOnlyDictionary<string, NodeTuning> Nodes,
    IReadOnlyDictionary<string, UnknownPityTuning> UnknownPity,
    int DepthRowsPerBandStep, int DepthBossBandDelta,
    long AttritionSpiritPerEliteMilli, long AttritionSpiritBossPresenceMilli, long AttritionSpiritRetreatMilli,
    long AttritionRestHealMilli,
    NerveTuning AttritionNerve, long AttritionReviveHpMilli, IReadOnlyList<string> AttritionPersistAcrossDelves,
    IReadOnlyList<string> RestHealsPools,
    int RiskDownedRecoveryDelves, IReadOnlyDictionary<string, long> RiskRecoveryRitualSouls,
    int EventsNoRepeatRooms, int EventsOfferedPerRoom,
    long EventsClimateAffinityMatchMilli, long EventsClimateAffinityNoneMilli, long EventsClimateAffinityOffMilli,
    IReadOnlyDictionary<string, long> HpBandMilli,
    int QuestsOfferedAtEntry, long QuestsAutopilotCompletionBandMinMilli, long QuestsAutopilotCompletionBandMaxMilli,
    IReadOnlyDictionary<string, long> QuestsCountBandMilli, IReadOnlyDictionary<string, RewardWindow> QuestsRewardBand,
    IReadOnlyDictionary<string, WildOutcomeRow> WildOutcome,
    IReadOnlyList<int> WildDeltaBands, IReadOnlyList<int> WildDeltaShiftRungs, WildTideTuning WildTide,
    IReadOnlyDictionary<string, WildOfferPreference> WildOfferPreference,
    long WildOfferSpiritPerSoulMilli, long WildOfferSoulsMilliOfPullPrice, string WildProvisionOverrideTag,
    int WildTalkMaxSteps, long WildTalkFlatterMilli, string WildAutopilotRule, long WildCageMilli,
    CaptureTuning Capture,
    string AltarBannerId, bool AltarPoolFromDomain,
    long MerchantMarkupMilli, int RestActivations, long RestAmbushMilli,
    ObjectsTuning Objects,
    IReadOnlyDictionary<string, int> PackFootprintRole, IReadOnlyDictionary<string, int> PackFootprintMassStep,
    IReadOnlyDictionary<string, int> PackFootprintConsumableClass,
    IReadOnlyDictionary<string, int> PackStackConsumableClass, IReadOnlyDictionary<string, int> PackStackMaterialClass,
    PackProvisionTuning PackProvision,
    string? LootRoomsEliteRarityFloor, string? LootRoomsBossRarityFloor, int LootRoomsBossRarityShiftRungs,
    IReadOnlyDictionary<string, string> LootRoomsAffixChannel, string LootBossGrantDistribution, long LootExtendSlotChanceMicro,
    IReadOnlyDictionary<string, DifficultyRungTuning> Rungs, DifficultyTailTuning DifficultyTail, int MinOfferedBand,
    DomainTuning Domain);

public sealed class DungeonTuningRejection : Exception
{
    public DungeonTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser (tunables-ssot.md §7.2) — no file I/O. Takes the already-loaded
/// <see cref="DungeonRegistries"/> so every dictionary-shaped block (nodes, bands, rungs, raid
/// modes) is checked against the registry's own member set in both directions in the same pass
/// that reads it: a missing key rejects naming it, an extra key rejects naming it
/// (spec-dungeon-registries.md "Registry ↔ tuning agreement").
/// </summary>
public static class DungeonTuningLoader
{
    const string File = "dungeon.v3.json";

    // Plural forms included on purpose -- "downedRecoveryDays" is at least as plausible an
    // authoring choice as the singular, and R6's point is that NO real-time unit exists anywhere
    // in this file, not that one spelling of it is banned and its plural slips through.
    static readonly string[] DayHourMinuteMs = { "Day", "Days", "Hour", "Hours", "Minute", "Minutes", "Ms" };

    /// <summary>The five <c>CreaturePersonality</c> enum members (<c>ContractPolicy.cs:18-25</c>),
    /// lowercased — "Personality keys = contracts.v1.json personalityRates members, checked at
    /// load" (spec-dungeon-registries.md). Copied as strings rather than an enum reference so
    /// Dungeon.Tuning does not take a compile dependency on Creatures.Contracts for five words.</summary>
    static readonly IReadOnlyList<string> PersonalityIds = new[] { "loyal", "stoic", "proud", "calculating", "feral" };

    public static DungeonTuning Parse(string json, DungeonRegistries registries)
    {
        var root = Root(json);
        RejectDayHourMinuteMs(root, "$");

        var roomKindIds = registries.RoomKinds.Select(r => r.RoomKindId).ToList();
        var rungIds = registries.DifficultyRungs.Select(r => r.RungId).ToList();
        var raidModeIds = registries.RaidModes.ToList();
        var overrideTagIds = registries.OverrideTags.ToHashSet(StringComparer.Ordinal);

        // Read straight from the loaded registries -- never BandCatalog's hub, which is
        // configured AFTER this loader runs (DungeonRegistryHub.Configure needs DungeonTuningHub
        // already configured, so the reverse dependency here would be circular).
        IReadOnlyList<string> Members(string band) => registries.Bands[band].Members;

        // delve-attrition (spec-delve-attrition.md's own Tunables table): `attrition.persistAcrossDelves[]`
        // and `rest.healsPools[]` are both documented as "resource ids subset of ResourceIds,
        // loader-checked" -- checked here, at the one place every other closed-set id in this file is
        // checked, rather than left to whichever consumer reads the list first.
        void RequireResourceIdSubset(IReadOnlyList<string> ids, string path)
        {
            foreach (var id in ids)
                if (!FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds.Contains(id, StringComparer.Ordinal))
                    throw new DungeonTuningRejection(
                        $"{File}: {path} names '{id}', which is not one of the six resource ids.");
        }

        var raidModesEl = Obj(root, "raid", "$");
        var raidModesObj = Obj(raidModesEl, "modes", "raid");
        var raidModes = new Dictionary<string, RaidModeTuning>(StringComparer.Ordinal);
        foreach (var modeId in raidModeIds)
        {
            var m = Obj(raidModesObj, modeId, "raid.modes");
            var path = $"raid.modes.{modeId}";
            long? shield = modeId == "solo" ? null : Long(m, "bossShieldPerPartyMilli", path);
            var pack = Obj(m, "pack", path);
            raidModes[modeId] = new RaidModeTuning(
                Parties: Int(m, "parties", path), SquadSlots: Int(m, "squadSlots", path),
                WalksDelta: Int(m, "walksDelta", path), BossW: Int(m, "bossW", path),
                BossShieldPerPartyMilli: shield,
                PackRows: Int(pack, "rows", $"{path}.pack"), PackCols: Int(pack, "cols", $"{path}.pack"));
        }
        RejectExtraKeys(raidModesObj, raidModeIds, "raid.modes");
        if (raidModesObj.TryGetProperty("solo", out var soloEl) && soloEl.TryGetProperty("bossShieldPerPartyMilli", out _))
            throw new DungeonTuningRejection($"{File}: raid.modes.solo must not carry bossShieldPerPartyMilli — absent on solo by schema.");

        var preflight = Obj(root, "preflight", "$");
        var preflightSampleSeeds = Int(preflight, "sampleSeeds", "preflight");

        var bandsEl = Obj(root, "bands", "$");
        var dangerBand = IntTable(bandsEl, "dangerBand", Members("dangerBand"), "bands");
        var depthBandRows = MinMaxTable(bandsEl, "depth", Members("depthBand"), "bands", "rows");
        var widthBandCols = MinMaxTable(bandsEl, "width", Members("widthBand"), "bands", "cols");
        var branchiness = IntTable(bandsEl, "branchiness", Members("branchiness"), "bands", "pathWalks");
        var densityMembers = Members("density");
        var gateDensity = LongTable(bandsEl, "gateDensity", densityMembers, "bands", "perRoomMilli");
        var secretDensity = LongTable(bandsEl, "secretDensity", densityMembers, "bands", "perRoomMilli");
        var oneWayDensity = LongTable(bandsEl, "oneWayDensity", densityMembers, "bands", "perRoomMilli");
        var sightBandExtraLanes = IntTable(bandsEl, "sightBand", Members("sightBand"), "bands", "extraLanes");
        var hazardBand = LongTable(bandsEl, "hazardBand", Members("hazardBand"), "bands", "hungerPerMille");
        var hpBandMilli = LongTable(bandsEl, "hpBand", Members("hpBand"), "bands", "milli");

        var sight = Obj(root, "sight", "$");
        var sightLanes = Int(sight, "lanes", "sight");
        var sightScoutLanes = Int(sight, "scoutLanes", "sight");

        var graph = Obj(root, "graph", "$");
        var fixedRows = Obj(graph, "fixedRows", "graph");
        var graphFixedRowsMidCacheRowMilli = Long(fixedRows, "midCacheRowMilli", "graph.fixedRows");
        var graphSecretAppearMilli = Long(graph, "secretAppearMilli", "graph");
        var graphMinDeadEnds = Int(graph, "minDeadEnds", "graph");

        var nodesEl = Obj(root, "nodes", "$");
        var nodes = new Dictionary<string, NodeTuning>(StringComparer.Ordinal);
        foreach (var kindId in roomKindIds)
        {
            var n = Obj(nodesEl, kindId, "nodes");
            var path = $"nodes.{kindId}";
            nodes[kindId] = new NodeTuning(
                WeightMilli: Long(n, "weightMilli", path),
                EarliestRowMilli: Long(n, "earliestRowMilli", path),
                LatestRowMilli: Long(n, "latestRowMilli", path));
        }
        var unknownNode = Obj(nodesEl, "unknown", "nodes");
        var pityEl = Obj(unknownNode, "pity", "nodes.unknown");
        var unknownPity = new Dictionary<string, UnknownPityTuning>(StringComparer.Ordinal);
        foreach (var resolveTo in new[] { "cache", "merchant", "fight" })
        {
            var p = Obj(pityEl, resolveTo, "nodes.unknown.pity");
            unknownPity[resolveTo] = new UnknownPityTuning(
                BaseMilli: Long(p, "baseMilli", $"nodes.unknown.pity.{resolveTo}"),
                StepMilli: Long(p, "stepMilli", $"nodes.unknown.pity.{resolveTo}"));
        }
        RejectExtraKeys(nodesEl, roomKindIds, "nodes");

        var depth = Obj(root, "depth", "$");
        var depthRowsPerBandStep = Int(depth, "rowsPerBandStep", "depth");
        var depthBossBandDelta = Int(depth, "bossBandDelta", "depth");

        var attrition = Obj(root, "attrition", "$");
        var attritionSpirit = Obj(attrition, "spirit", "attrition");
        var attritionSpiritPerEliteMilli = Long(attritionSpirit, "perEliteMilli", "attrition.spirit");
        var attritionSpiritBossPresenceMilli = Long(attritionSpirit, "bossPresenceMilli", "attrition.spirit");
        var attritionSpiritRetreatMilli = Long(attritionSpirit, "retreatMilli", "attrition.spirit");
        var attritionRestHealMilli = Long(attrition, "restHealMilli", "attrition");
        var nerveEl = Obj(attrition, "nerve", "attrition");
        var nerveStages = Members("nerveStage");
        var stageThresholds = IntArray(nerveEl, "stageThresholds", "attrition.nerve");
        if (stageThresholds.Count != nerveStages.Count)
            throw new DungeonTuningRejection(
                $"{File}: attrition.nerve.stageThresholds must have {nerveStages.Count} entries (one per nerveStage member), found {stageThresholds.Count}.");
        for (var i = 1; i < stageThresholds.Count; i++)
            if (stageThresholds[i] <= stageThresholds[i - 1])
                throw new DungeonTuningRejection($"{File}: attrition.nerve.stageThresholds must be strictly increasing.");
        var nerve = new NerveTuning(
            StageThresholds: stageThresholds,
            StackPerElite: Int(nerveEl, "stackPerElite", "attrition.nerve"),
            StackPerBoss: Int(nerveEl, "stackPerBoss", "attrition.nerve"),
            StackPerRetreat: Int(nerveEl, "stackPerRetreat", "attrition.nerve"),
            StackPerCurio: Int(nerveEl, "stackPerCurio", "attrition.nerve"),
            RestRelief: Long(nerveEl, "restRelief", "attrition.nerve"));
        var revive = Obj(attrition, "revive", "attrition");
        var attritionReviveHpMilli = Long(revive, "hpMilli", "attrition.revive");
        var attritionPersistAcrossDelves = StringArray(attrition, "persistAcrossDelves", "attrition");
        RequireResourceIdSubset(attritionPersistAcrossDelves, "attrition.persistAcrossDelves");

        var rest = Obj(root, "rest", "$");
        var restHealsPools = StringArray(rest, "healsPools", "rest");
        RequireResourceIdSubset(restHealsPools, "rest.healsPools");
        var restActivations = Int(rest, "activations", "rest");
        var restAmbushMilli = Long(rest, "ambushMilli", "rest");

        var risk = Obj(root, "risk", "$");
        var riskDownedRecoveryDelves = Int(risk, "downedRecoveryDelves", "risk");
        var recoveryRitualSoulsEl = Obj(risk, "recoveryRitualSouls", "risk");
        var riskRecoveryRitualSouls = LongTable(recoveryRitualSoulsEl, rungIds, "risk.recoveryRitualSouls");

        var events = Obj(root, "events", "$");
        var eventsNoRepeatRooms = Int(events, "noRepeatRooms", "events");
        var eventsOfferedPerRoom = Int(events, "offeredPerRoom", "events");
        var climateAffinity = Obj(events, "climateAffinity", "events");
        var eventsClimateAffinityMatchMilli = Long(climateAffinity, "matchMilli", "events.climateAffinity");
        var eventsClimateAffinityNoneMilli = Long(climateAffinity, "noneMilli", "events.climateAffinity");
        var eventsClimateAffinityOffMilli = Long(climateAffinity, "offMilli", "events.climateAffinity");

        var quests = Obj(root, "quests", "$");
        var questsOfferedAtEntry = Int(quests, "offeredAtEntry", "quests");
        var autopilotBand = Obj(quests, "autopilotCompletionBand", "quests");
        var questsAutopilotCompletionBandMinMilli = Long(autopilotBand, "minMilli", "quests.autopilotCompletionBand");
        var questsAutopilotCompletionBandMaxMilli = Long(autopilotBand, "maxMilli", "quests.autopilotCompletionBand");
        // "quests.countBand.{lone,few,several,many}Milli" -- no dot before the unit suffix, so it
        // is glued onto the member name as one flat key ("loneMilli"), not a nested object
        // (contrast bands.hazardBand.{member}.hungerPerMille, which IS nested — dot present).
        var questsCountBandMilli = ConcatLongTable(quests, "countBand", Members("countBand"), "Milli", "quests");
        var rewardBandEl = Obj(quests, "rewardBand", "quests");
        var questsRewardBand = new Dictionary<string, RewardWindow>(StringComparer.Ordinal);
        foreach (var tier in Members("rewardBand"))
        {
            var t = Obj(rewardBandEl, tier, "quests.rewardBand");
            questsRewardBand[tier] = new RewardWindow(Str(t, "floorRung", $"quests.rewardBand.{tier}"), Str(t, "ceilRung", $"quests.rewardBand.{tier}"));
        }

        var wild = Obj(root, "wild", "$");
        var wildOutcomeEl = Obj(wild, "outcome", "wild");
        var wildOutcome = new Dictionary<string, WildOutcomeRow>(StringComparer.Ordinal);
        foreach (var d in registries.Disposition)
        {
            var o = Obj(wildOutcomeEl, d, "wild.outcome");
            var row = new WildOutcomeRow(
                JoinsMilli: Long(o, "joinsMilli", $"wild.outcome.{d}"), TakesLeavesMilli: Long(o, "takesLeavesMilli", $"wild.outcome.{d}"),
                FleesMilli: Long(o, "fleesMilli", $"wild.outcome.{d}"), AttacksMilli: Long(o, "attacksMilli", $"wild.outcome.{d}"));
            var sum = row.JoinsMilli + row.TakesLeavesMilli + row.FleesMilli + row.AttacksMilli;
            if (sum != 1000)
                throw new DungeonTuningRejection($"{File}: wild.outcome.{d} rows must sum to 1000‰, found {sum}.");
            wildOutcome[d] = row;
        }
        var wildDeltaBands = IntArray(wild, "deltaBands", "wild");
        if (wildDeltaBands.Count != 4)
            throw new DungeonTuningRejection($"{File}: wild.deltaBands must have exactly 4 signed Θ edges (5 resulting deltaBand members), found {wildDeltaBands.Count}.");
        var wildDeltaShiftRungs = IntArray(wild, "deltaShiftRungs", "wild");
        if (wildDeltaShiftRungs.Count != 5)
            throw new DungeonTuningRejection($"{File}: wild.deltaShiftRungs must have exactly 5 entries (one per deltaBand member: far-below..far-above), found {wildDeltaShiftRungs.Count}.");
        var tideEl = Obj(wild, "tide", "wild");
        var wildTide = new WildTideTuning(Bool(tideEl, "enabled", "wild.tide"), IntArray(tideEl, "shiftRungs", "wild.tide"));
        var offerPrefEl = Obj(wild, "offerPreference", "wild");
        var wildOfferPreference = new Dictionary<string, WildOfferPreference>(StringComparer.Ordinal);
        foreach (var personality in PersonalityIds)
        {
            var p = Obj(offerPrefEl, personality, "wild.offerPreference");
            var path = $"wild.offerPreference.{personality}";
            var offer = new WildOfferPreference(
                Str(p, "souls", path), Str(p, "spirit", path), Str(p, "item", path), Str(p, "creature", path));
            foreach (var v in new[] { offer.Souls, offer.Spirit, offer.Item, offer.Creature })
                if (v is not ("craves" or "accepts" or "scorns"))
                    throw new DungeonTuningRejection($"{File}: {path} values must be craves · accepts · scorns, found '{v}'.");
            wildOfferPreference[personality] = offer;
        }
        RejectExtraKeys(offerPrefEl, PersonalityIds, "wild.offerPreference");
        var offerEl = Obj(wild, "offer", "wild");
        var wildOfferSpiritPerSoulMilli = Long(offerEl, "spiritPerSoulMilli", "wild.offer");
        var wildOfferSoulsMilliOfPullPrice = Long(offerEl, "soulsMilliOfPullPrice", "wild.offer");
        if (wildOfferSoulsMilliOfPullPrice < 1000)
            throw new DungeonTuningRejection($"{File}: wild.offer.soulsMilliOfPullPrice must be >= 1000 (a floor at or above the pull price), found {wildOfferSoulsMilliOfPullPrice}.");
        var wildProvisionOverrideTag = Str(wild, "provisionOverrideTag", "wild");
        if (!overrideTagIds.Contains(wildProvisionOverrideTag))
            throw new DungeonTuningRejection($"{File}: wild.provisionOverrideTag '{wildProvisionOverrideTag}' is not a known override tag.");
        var talkEl = Obj(wild, "talk", "wild");
        var wildTalkMaxSteps = Int(talkEl, "maxSteps", "wild.talk");
        var wildTalkFlatterMilli = Long(talkEl, "flatterMilli", "wild.talk");
        var wildAutopilotEl = Obj(wild, "autopilot", "wild");
        var wildAutopilotRule = Str(wildAutopilotEl, "rule", "wild.autopilot");
        if (wildAutopilotRule is not ("fight" or "leave-hostile"))
            throw new DungeonTuningRejection($"{File}: wild.autopilot.rule must be 'fight' or 'leave-hostile', found '{wildAutopilotRule}'.");
        var wildCageMilli = Long(wild, "cageMilli", "wild");

        var captureEl = Obj(root, "capture", "$");
        var captureUsableBelowMilli = Long(captureEl, "usableBelowMilli", "capture");
        var hpBandMembers = Members("hpBand");
        var deltaBandMembers = Members("deltaBand");
        var chanceMilliEl = Obj(captureEl, "chanceMilli", "capture");
        var chanceTable = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
        foreach (var hpBand in hpBandMembers)
        {
            var row = Obj(chanceMilliEl, hpBand, "capture.chanceMilli");
            chanceTable[hpBand] = LongTable(row, deltaBandMembers, $"capture.chanceMilli.{hpBand}");
        }
        RejectExtraKeys(chanceMilliEl, hpBandMembers, "capture.chanceMilli");
        var statusBonusEl = Obj(captureEl, "statusBonusMilli", "capture");
        var captureStatusBonusMilli = LongTable(statusBonusEl, Members("countBand"), "capture.statusBonusMilli");
        var captureSealTierShiftBands = IntArray(captureEl, "sealTierShiftBands", "capture");
        var captureFailStepBands = Int(captureEl, "failStepBands", "capture");
        var capture = new CaptureTuning(captureUsableBelowMilli, chanceTable, captureStatusBonusMilli, captureSealTierShiftBands, captureFailStepBands);

        var altar = Obj(root, "altar", "$");
        var altarBannerId = Str(altar, "bannerId", "altar");
        var altarPoolFromDomain = Bool(altar, "poolFromDomain", "altar");

        var merchant = Obj(root, "merchant", "$");
        var merchantMarkupMilli = Long(merchant, "markupMilli", "merchant");
        var merchantStockCount = Int(merchant, "stockCount", "merchant");

        var objectsEl = Obj(root, "objects", "$");
        var objectsBreakMode = Str(objectsEl, "breakMode", "objects");
        if (objectsBreakMode is not ("none" or "stamina" or "structure" or "either"))
            throw new DungeonTuningRejection($"{File}: objects.breakMode must be one of none · stamina · structure · either, found '{objectsBreakMode}'.");
        var objects = new ObjectsTuning(
            BreakMode: objectsBreakMode,
            BreakStaminaMilli: Long(objectsEl, "breakStaminaMilli", "objects"),
            StructureHpBand: RequireBandMember(objectsEl, "structureHpBand", "countBand", "objects", registries),
            MerchantStockCount: merchantStockCount);

        var packEl = Obj(root, "pack", "$");
        var footprintEl = Obj(packEl, "footprint", "pack");
        var roleFootprint = IntTableFree(footprintEl, "role", "pack.footprint");
        var massStepFootprint = IntTableFree(footprintEl, "massStep", "pack.footprint");
        var consumableClassFootprint = IntTableFree(footprintEl, "consumableClass", "pack.footprint");
        var stackEl = Obj(packEl, "stack", "pack");
        var stackConsumableClass = IntTableFree(stackEl, "consumableClass", "pack.stack");
        var stackMaterialClass = IntTableFree(stackEl, "materialClass", "pack.stack");
        var provisionEl = Obj(packEl, "provision", "pack");
        var autopilotEl = Obj(packEl, "autopilot", "pack");
        var fillBandEl = Obj(packEl, "fillBand", "pack");
        var identityEl = Obj(fillBandEl, "identity", "pack.fillBand");
        var autopilotFloorRule = Str(autopilotEl, "floorRule", "pack.autopilot");
        if (autopilotFloorRule is not ("value-per-cell" or "leave"))
            throw new DungeonTuningRejection($"{File}: pack.autopilot.floorRule must be 'value-per-cell' or 'leave', found '{autopilotFloorRule}'.");
        var packProvision = new PackProvisionTuning(
            BaseCells: Int(provisionEl, "baseCells", "pack.provision"),
            AutopilotFloorRule: autopilotFloorRule,
            AutopilotSwapMarginMilli: Long(autopilotEl, "swapMarginMilli", "pack.autopilot"),
            FillBandIdentityMinMilli: Long(identityEl, "minMilli", "pack.fillBand.identity"),
            FillBandIdentityMaxMilli: Long(identityEl, "maxMilli", "pack.fillBand.identity"));

        var lootEl = Obj(root, "loot", "$");
        var lootRoomsEl = Obj(lootEl, "rooms", "loot");
        var eliteRoomEl = Obj(lootRoomsEl, "elite", "loot.rooms");
        var bossRoomEl = Obj(lootRoomsEl, "boss", "loot.rooms");
        var lootRoomsEliteRarityFloor = RungOrNone(eliteRoomEl, "rarityFloor", "loot.rooms.elite", rungIds);
        var lootRoomsBossRarityFloor = RungOrNone(bossRoomEl, "rarityFloor", "loot.rooms.boss", rungIds);
        var lootRoomsBossRarityShiftRungs = Int(bossRoomEl, "rarityShiftRungs", "loot.rooms.boss");
        var lootRoomsAffixChannel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kindId in roomKindIds)
        {
            var kindEl = Obj(lootRoomsEl, kindId, "loot.rooms");
            var isAffixKind = kindId is "elite" or "boss";
            if (!isAffixKind)
            {
                if (!kindEl.TryGetProperty("affixChannel", out _)) continue;
                // fall through: an affixChannel authored on a non-elite/boss kind is still validated below
            }
            var ch = isAffixKind
                ? Str(kindEl, "affixChannel", $"loot.rooms.{kindId}")
                : kindEl.GetProperty("affixChannel").GetString() ?? throw new DungeonTuningRejection($"{File}: loot.rooms.{kindId}.affixChannel must be a string.");
            if (ch is not ("drop" or "boss"))
                throw new DungeonTuningRejection($"{File}: loot.rooms.{kindId}.affixChannel must be 'drop' or 'boss', found '{ch}'.");
            lootRoomsAffixChannel[kindId] = ch;
        }
        var lootBossGrantDistribution = Str(lootEl, "bossGrantDistribution", "loot");
        if (lootBossGrantDistribution != "round-robin")
            throw new DungeonTuningRejection($"{File}: loot.bossGrantDistribution must be 'round-robin', found '{lootBossGrantDistribution}'.");
        var lootExtendSlotChanceMicro = Long(lootEl, "extendSlotChanceMicro", "loot");

        var rungsEl = Obj(root, "difficulty", "$");
        var rungsObj = Obj(rungsEl, "rungs", "difficulty");
        var rungs = new Dictionary<string, DifficultyRungTuning>(StringComparer.Ordinal);
        foreach (var rungId in rungIds)
        {
            var r = Obj(rungsObj, rungId, "difficulty.rungs");
            var path = $"difficulty.rungs.{rungId}";
            var enemyCountDelta = Obj(r, "enemyCountDelta", path);
            var unknownPityMult = Obj(r, "unknownPityStepMultMilli", path);
            rungs[rungId] = new DifficultyRungTuning(
                BandDelta: Int(r, "bandDelta", path),
                EliteWeightMultMilli: Long(r, "eliteWeightMultMilli", path), RestWeightMultMilli: Long(r, "restWeightMultMilli", path),
                RestHealMultMilli: Long(r, "restHealMultMilli", path), HungerMultMilli: Long(r, "hungerMultMilli", path),
                SpiritDrainMultMilli: Long(r, "spiritDrainMultMilli", path), MerchantMarkupMultMilli: Long(r, "merchantMarkupMultMilli", path),
                WildDispositionShiftRungs: Int(r, "wildDispositionShiftRungs", path),
                EnemyCountDeltaFight: Int(enemyCountDelta, "fight", $"{path}.enemyCountDelta"), EnemyCountDeltaElite: Int(enemyCountDelta, "elite", $"{path}.enemyCountDelta"),
                BossRetinuePerPartyDelta: Int(r, "bossRetinuePerPartyDelta", path), BossWDelta: Int(r, "bossWDelta", path),
                ProvisionCellsDelta: Int(r, "provisionCellsDelta", path),
                RestEveryOtherRow: Bool(r, "restEveryOtherRow", path), RestRowsOnlyBeforeBoss: Bool(r, "restRowsOnlyBeforeBoss", path), DoubleBoss: Bool(r, "doubleBoss", path),
                EventSeverityTier: Int(r, "eventSeverityTier", path), EliteKitTier: Int(r, "eliteKitTier", path), BossKitTier: Int(r, "bossKitTier", path),
                UnknownPityStepMultMilliCache: Long(unknownPityMult, "cache", $"{path}.unknownPityStepMultMilli"),
                UnknownPityStepMultMilliMerchant: Long(unknownPityMult, "merchant", $"{path}.unknownPityStepMultMilli"),
                UnknownPityStepMultMilliFight: Long(unknownPityMult, "fight", $"{path}.unknownPityStepMultMilli"),
                RarityFloor: RungOrNone(r, "rarityFloor", path, rungIds),
                RarityShiftRungs: Int(r, "rarityShiftRungs", path));
        }
        RejectExtraKeys(rungsObj, rungIds, "difficulty.rungs");
        ValidateRungNeighbours(rungIds, rungs);

        var tailEl = Obj(rungsEl, "tail", "difficulty");
        var tailLabelFormat = Str(tailEl, "labelFormat", "difficulty.tail");
        if (!tailLabelFormat.Contains("{n}", StringComparison.Ordinal))
            throw new DungeonTuningRejection($"{File}: difficulty.tail.labelFormat must contain '{{n}}', found '{tailLabelFormat}'.");
        var tail = new DifficultyTailTuning(
            Enabled: Bool(tailEl, "enabled", "difficulty.tail"),
            StartsAfterRung: RequireRungId(tailEl, "startsAfterRung", "difficulty.tail", rungIds),
            BandStepPerPlus: Int(tailEl, "bandStepPerPlus", "difficulty.tail"),
            LabelFormat: tailLabelFormat,
            RulesFrozenAtRung: RequireRungId(tailEl, "rulesFrozenAtRung", "difficulty.tail", rungIds));

        var minOfferedBand = Int(rungsEl, "minOfferedBand", "difficulty");
        if (minOfferedBand < 1)
            throw new DungeonTuningRejection($"{File}: difficulty.minOfferedBand must be >= 1 (band 0 is safe ground, never a delve) — found {minOfferedBand}.");

        var domainEl = Obj(root, "domain", "$");
        var onceEntryEl = Obj(domainEl, "onceEntry", "domain");
        var domain = new DomainTuning(
            MaxRungWithoutOath: RequireRungId(domainEl, "maxRungWithoutOath", "domain", rungIds),
            PermadeathFromRung: RequireRungId(domainEl, "permadeathFromRung", "domain", rungIds),
            OnceEntry: new DomainOnceEntryTuning(
                BandDelta: Int(onceEntryEl, "bandDelta", "domain.onceEntry"),
                BossRarityFloor: RequireRungId(onceEntryEl, "bossRarityFloor", "domain.onceEntry", rungIds),
                SealOnWipe: Bool(onceEntryEl, "sealOnWipe", "domain.onceEntry"),
                FailKeepsBossLoot: Bool(onceEntryEl, "failKeepsBossLoot", "domain.onceEntry")));

        return new DungeonTuning(
            Int(root, "schemaVersion", "$"), Int(root, "version", "$"),
            raidModes, preflightSampleSeeds,
            dangerBand, depthBandRows, widthBandCols, branchiness, gateDensity, secretDensity, oneWayDensity,
            sightBandExtraLanes, sightLanes, sightScoutLanes, hazardBand,
            graphFixedRowsMidCacheRowMilli, graphSecretAppearMilli, graphMinDeadEnds,
            nodes, unknownPity, depthRowsPerBandStep, depthBossBandDelta,
            attritionSpiritPerEliteMilli, attritionSpiritBossPresenceMilli, attritionSpiritRetreatMilli, attritionRestHealMilli,
            nerve, attritionReviveHpMilli, attritionPersistAcrossDelves, restHealsPools,
            riskDownedRecoveryDelves, riskRecoveryRitualSouls,
            eventsNoRepeatRooms, eventsOfferedPerRoom, eventsClimateAffinityMatchMilli, eventsClimateAffinityNoneMilli, eventsClimateAffinityOffMilli,
            hpBandMilli,
            questsOfferedAtEntry, questsAutopilotCompletionBandMinMilli, questsAutopilotCompletionBandMaxMilli, questsCountBandMilli, questsRewardBand,
            wildOutcome, wildDeltaBands, wildDeltaShiftRungs, wildTide, wildOfferPreference,
            wildOfferSpiritPerSoulMilli, wildOfferSoulsMilliOfPullPrice, wildProvisionOverrideTag,
            wildTalkMaxSteps, wildTalkFlatterMilli, wildAutopilotRule, wildCageMilli,
            capture, altarBannerId, altarPoolFromDomain, merchantMarkupMilli, restActivations, restAmbushMilli,
            objects, roleFootprint, massStepFootprint, consumableClassFootprint, stackConsumableClass, stackMaterialClass, packProvision,
            lootRoomsEliteRarityFloor, lootRoomsBossRarityFloor, lootRoomsBossRarityShiftRungs, lootRoomsAffixChannel, lootBossGrantDistribution, lootExtendSlotChanceMicro,
            rungs, tail, minOfferedBand, domain);
    }

    /// <summary>R8: neighbouring rungs must differ in bandDelta or a reward-bearing column
    /// (enemyCountDelta.*, rarityFloor, rarityShiftRungs) — never only a penalty.</summary>
    static void ValidateRungNeighbours(IReadOnlyList<string> rungIdsInOrdinalOrder, IReadOnlyDictionary<string, DifficultyRungTuning> rungs)
    {
        for (var i = 1; i < rungIdsInOrdinalOrder.Count; i++)
        {
            var prev = rungs[rungIdsInOrdinalOrder[i - 1]];
            var cur = rungs[rungIdsInOrdinalOrder[i]];
            var differs = prev.BandDelta != cur.BandDelta
                || prev.EnemyCountDeltaFight != cur.EnemyCountDeltaFight || prev.EnemyCountDeltaElite != cur.EnemyCountDeltaElite
                || prev.RarityFloor != cur.RarityFloor || prev.RarityShiftRungs != cur.RarityShiftRungs;
            if (!differs)
                throw new DungeonTuningRejection(
                    $"{File}: difficulty.rungs '{rungIdsInOrdinalOrder[i - 1]}' and '{rungIdsInOrdinalOrder[i]}' " +
                    "must differ in bandDelta or a reward-bearing column (R8) — found no difference.");
        }
    }

    static string RequireBandMember(JsonElement parent, string key, string bandName, string path, DungeonRegistries registries)
    {
        var v = Str(parent, key, path);
        if (!registries.Bands[bandName].Members.Contains(v, StringComparer.Ordinal))
            throw new DungeonTuningRejection($"{File}: {path}.{key} '{v}' is not a member of band '{bandName}'.");
        return v;
    }

    static string RequireRungId(JsonElement parent, string key, string path, IReadOnlyList<string> rungIds)
    {
        var v = Str(parent, key, path);
        if (!rungIds.Contains(v, StringComparer.Ordinal))
            throw new DungeonTuningRejection($"{File}: {path}.{key} '{v}' is not a known difficulty rung id.");
        return v;
    }

    static string? RungOrNone(JsonElement parent, string key, string path, IReadOnlyList<string> rungIds)
    {
        var v = Str(parent, key, path);
        if (v == "none") return null;
        if (!rungIds.Contains(v, StringComparer.Ordinal))
            throw new DungeonTuningRejection($"{File}: {path}.{key} '{v}' is not a known difficulty rung id or 'none'.");
        return v;
    }

    static void RejectDayHourMinuteMs(JsonElement el, string path)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                foreach (var suffix in DayHourMinuteMs)
                    if (prop.Name.EndsWith(suffix, StringComparison.Ordinal))
                        throw new DungeonTuningRejection(
                            $"{File}: '{path}.{prop.Name}' carries a real-time unit ({suffix}) — R6 removed the wall clock; " +
                            "recovery is counted in delves.");
                RejectDayHourMinuteMs(prop.Value, $"{path}.{prop.Name}");
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray()) RejectDayHourMinuteMs(item, path);
        }
    }

    static JsonElement Root(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new DungeonTuningRejection($"{File}: empty document");
        try { return JsonDocument.Parse(json).RootElement; }
        catch (JsonException ex) { throw new DungeonTuningRejection($"{File}: not valid JSON — {ex.Message}"); }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new DungeonTuningRejection($"{File}: missing or non-object '{path}.{key}'");
        return el;
    }

    static bool Bool(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DungeonTuningRejection($"{File}: missing or non-boolean '{path}.{key}'");
        return el.GetBoolean();
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new DungeonTuningRejection($"{File}: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new DungeonTuningRejection($"{File}: missing or non-integer '{path}.{key}'");
        return v;
    }

    static string Str(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new DungeonTuningRejection($"{File}: missing or non-string '{path}.{key}'");
        return el.GetString()!;
    }

    static IReadOnlyList<int> IntArray(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new DungeonTuningRejection($"{File}: missing or non-array '{path}.{key}'");
        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var v))
                throw new DungeonTuningRejection($"{File}: '{path}.{key}' must be an array of integers");
            list.Add(v);
        }
        return list;
    }

    static IReadOnlyList<string> StringArray(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new DungeonTuningRejection($"{File}: missing or non-array '{path}.{key}'");
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new DungeonTuningRejection($"{File}: '{path}.{key}' must be an array of strings");
            list.Add(item.GetString()!);
        }
        return list;
    }

    /// <summary>An int-valued table over an exact required member set — missing rejects naming
    /// the key, extra rejects naming the key ("Registry ↔ tuning agreement").</summary>
    static IReadOnlyDictionary<string, int> IntTable(JsonElement parent, string key, IReadOnlyList<string> requiredMembers, string path, string? suffix = null)
    {
        var obj = Obj(parent, key, path);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in requiredMembers)
            result[m] = suffix == null ? Int(obj, m, $"{path}.{key}") : Int(Obj(obj, m, $"{path}.{key}"), suffix, $"{path}.{key}.{m}");
        RejectExtraKeys(obj, requiredMembers, $"{path}.{key}");
        return result;
    }

    static IReadOnlyDictionary<string, long> LongTable(JsonElement parent, string key, IReadOnlyList<string> requiredMembers, string path, string? suffix = null)
    {
        var obj = Obj(parent, key, path);
        return LongTable(obj, requiredMembers, $"{path}.{key}", suffix);
    }

    static IReadOnlyDictionary<string, long> LongTable(JsonElement obj, IReadOnlyList<string> requiredMembers, string path, string? suffix = null)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var m in requiredMembers)
            result[m] = suffix == null ? Long(obj, m, path) : Long(Obj(obj, m, path), suffix, $"{path}.{m}");
        RejectExtraKeys(obj, requiredMembers, path);
        return result;
    }

    /// <summary>A table whose unit suffix is glued directly onto the member name to form one flat
    /// key ("loneMilli"), never nested — the "{member}Milli" spelling with no dot before the
    /// suffix (contrast the dotted nested tables above, e.g. "{member}.hungerPerMille").</summary>
    static IReadOnlyDictionary<string, long> ConcatLongTable(JsonElement parent, string key, IReadOnlyList<string> requiredMembers, string suffix, string path)
    {
        var obj = Obj(parent, key, path);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var concatKeys = requiredMembers.Select(m => m + suffix).ToList();
        foreach (var m in requiredMembers)
            result[m] = Long(obj, m + suffix, $"{path}.{key}");
        RejectExtraKeys(obj, concatKeys, $"{path}.{key}");
        return result;
    }

    static IReadOnlyDictionary<string, MinMax> MinMaxTable(JsonElement parent, string key, IReadOnlyList<string> requiredMembers, string path, string field)
    {
        var obj = Obj(parent, key, path);
        var result = new Dictionary<string, MinMax>(StringComparer.Ordinal);
        foreach (var m in requiredMembers)
        {
            var fieldEl = Obj(obj, m, $"{path}.{key}");
            var f = Obj(fieldEl, field, $"{path}.{key}.{m}");
            result[m] = new MinMax(Int(f, "min", $"{path}.{key}.{m}.{field}"), Int(f, "max", $"{path}.{key}.{m}.{field}"));
        }
        RejectExtraKeys(obj, requiredMembers, $"{path}.{key}");
        return result;
    }

    /// <summary>A table whose key set is item-registry vocabulary (mass classes, item roles) —
    /// read at load, never restated in a dungeon registry; not exactness-checked against a
    /// dungeon registry because none owns this vocabulary.</summary>
    static IReadOnlyDictionary<string, int> IntTableFree(JsonElement parent, string key, string path)
    {
        var obj = Obj(parent, key, path);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            result[prop.Name] = Int(obj, prop.Name, $"{path}.{key}");
        return result;
    }

    static void RejectExtraKeys(JsonElement obj, IReadOnlyList<string> requiredMembers, string path)
    {
        var required = new HashSet<string>(requiredMembers, StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            if (!required.Contains(prop.Name))
                throw new DungeonTuningRejection($"{File}: '{path}.{prop.Name}' is not a known member — extra key.");
    }
}

/// <summary>Single configuration point for every dungeon reader (registries.py's C# counterpart).
/// Configure this BEFORE <see cref="Registry.DungeonRegistryHub"/> — <c>RoomKindDef.WeightMilli</c>
/// and friends join this hub at first property read.</summary>
public static class DungeonTuningHub
{
    static DungeonTuning? _tuning;

    public static void Configure(DungeonTuning tuning) => _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static DungeonTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "DungeonTuningHub.Configure(...) has not run. Every dungeon rule reads data/tuning/" +
        "dungeon.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
