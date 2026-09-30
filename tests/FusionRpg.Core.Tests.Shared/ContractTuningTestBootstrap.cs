using System.Runtime.CompilerServices;
using FusionRpg.Core;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Creatures.Fusion;
using FusionRpg.Core.Creatures.Patron;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Match;
using FusionRpg.Core.Overlay;
using FusionRpg.Core.Power;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Items;
using FusionRpg.Core.Vfx;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Ai;
using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests;

/// <summary>
/// tunables-ssot.md §7.2: "Tests → construct one inline; no fixture files." Every test in this
/// assembly that reaches a migrated Policy class (<see cref="ContractPolicy"/>, <see cref="LoamPolicy"/>,
/// <see cref="WorldTuningHub"/>, <see cref="SoulEarnPolicy"/>, <see cref="PatronPolicy"/>,
/// <see cref="ShieldPolicy"/>, <see cref="CombatPolicy"/>, <see cref="StarPolicy"/>,
/// <see cref="StatusPolicy"/>, <see cref="OverlayTuningHub"/>, <see cref="StatsTuningHub"/>,
/// <see cref="ExpeditionTuningHub"/>) needs it configured first; a module initializer does that once,
/// so no individual test class has to repeat the boilerplate. Values are the same working set as the
/// matching <c>gk-core/data/tuning/*.v1.json</c> — kept as literal C# objects here rather than a file read,
/// so tests exercise construction, not file I/O.
/// </summary>
internal static class ContractTuningTestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        ContractPolicy.Configure(DefaultContracts);
        LoamPolicy.Configure(DefaultLoam);
        WorldTuningHub.Configure(DefaultWorld);
        // species-gear-chain T7: values transcribed from the real, shipped
        // gk-core/data/tuning/world-spawn.v1.json, matching this file's own stated convention.
        WorldSpawnTuningHub.Configure(DefaultWorldSpawn);
        RecruitPolicy.Configure(DefaultWorld.Growth);
        SoulEarnPolicy.Configure(DefaultSouls);
        PatronPolicy.Configure(DefaultPatron);
        ShieldPolicy.Configure(DefaultShield);
        CombatPolicy.Configure(DefaultCombat);
        StarPolicy.Configure(DefaultFusion);
        StatusPolicy.Configure(DefaultStatus);
        DerivedStatPolicy.Configure(DefaultDerivedStats);
        // T4.7 step 2 / T4.8 (catalog-runtime) — the SAME compiled roster every test in this
        // assembly already read via the old lazy CreatureSpeciesCatalog.All, now routed through
        // Configure. Every existing test (element distribution, id uniqueness, etc. across the
        // real 84 species) keeps reading identical data.
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        // T8.4 (ds 18, fusion-recipe-runtime) — the SAME transitional shape: every test in this
        // assembly already read CreatureRecipeCatalog.All via its own old lazy `_all ??= Build()`
        // cache, computed against the compiled species roster just configured above. Configure
        // with a fresh BuildDeterministicOnly() call reproduces that exact same data (the compiled
        // 84-ish roster has zero shortfall, so the deterministic pass alone already covers every
        // eligible output) — every existing CreatureRecipeCatalogTests case keeps reading identical
        // data, zero test-body changes needed beyond the BuildForTest -> BuildDeterministicOnly rename.
        CreatureRecipeCatalog.Configure(CreatureRecipeCatalog.BuildDeterministicOnly());
        OverlayTuningHub.Configure(DefaultOverlay);
        StatsTuningHub.Configure(DefaultStats);
        ExpeditionTuningHub.Configure(DefaultExpeditions);
        // species-gear-chain T31: same working set as the shipped gk-core/data/tuning/creature-yield.v1.json,
        // matching this file's own stated convention.
        CreatureYieldTuningHub.Configure(DefaultCreatureYield);
        MatchTuningPolicy.Configure(DefaultMatch);
        EffectsTuningHub.Configure(DefaultEffects);
        SimDefaults.Configure(DefaultSim);
        ProgressionTuningHub.Configure(DefaultProgression);
        SpeciesProgressionTuningHub.Configure(DefaultSpeciesProgression);
        // respec-free-counter EP4.4 / empire-level EP4.3: the grant amount one empire level pays, the
        // same value the shipped species-build.v6.json publishes. Without it every empire
        // credit would queue an empty grant list, which is what a HOST that never wired the
        // key gets — legal, but not what these tests mean to exercise.
        EmpireLevelTuningHub.Configure(new EmpireLevelTuning(FreeRespecsPerEmpireLevel: 1));
        BattleTuningHub.Configure(DefaultBattle);
        // battle-tempo battle-resources (2026-09-05): every battle actor held all six resource pools
        // at max 0 until this landed, because BattleStatComposer seeded no resource.* channel. Same
        // bootstrap shape (and same "one more Configure every harness must remember") as
        // ActionTimingPolicy below -- values transcribed from the real, shipped
        // gk-core/data/tuning/battle-resources.v1.json, not invented.
        FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(DefaultBattleResources);
        // base-defense siege-board (2026-09-05): values transcribed from the real, shipped
        // gk-core/data/tuning/siege.v1.json, matching this file's own stated convention.
        SiegeTuningPolicy.Configure(DefaultSiege);
        // combat-ai `profile-schema` CAI1.8 (H7): BattleRunState's aiTuning != null construction path
        // now reads CombatAiProfilePolicy for the scorer's weights -- every test in this assembly that
        // exercises a siege AI decision needs this configured, exactly like every other hub above.
        FusionRpg.Core.Actions.Ai.CombatAiProfilePolicy.Configure(DefaultCombatAi);
        // A10 battle-board (2026-09-07): values transcribed from the real, shipped
        // gk-core/data/tuning/battle-board.v1.json, matching this file's own stated convention.
        BattleBoardTuningPolicy.Configure(DefaultBattleBoard);
        // battle-tempo action-timing (2026-09-05): the bootstrap's own new gap this session's
        // base-defense work found and closed -- BattleRunState's constructor now unconditionally
        // reads ActionTimingPolicy.Tuning, and this Configure call was missing from every test
        // bootstrap. Values transcribed from the real, shipped gk-core/data/tuning/action-timing.v1.json,
        // matching this file's own stated convention ("same working set as the matching
        // gk-core/data/tuning/*.v1.json") -- not invented.
        FusionRpg.Core.Actions.ActionTimingPolicy.Configure(DefaultActionTiming);
        // action-enrich action-base (AE1.1): read the real shipped file, like production does —
        // the value is authored data (a measured calibration), not an inline working set.
        // Rooted from the test assembly the same way ElementHubDocDriftTests.FindRepoRoot does
        // (walk up to the dir holding gk-fusion/src/FusionRpg.Injector), never from the CWD.
        // v2 (AE1.4): the shipped version production loads — a test bootstrap on v1 would hide a
        // reader/publish split.
        FusionRpg.Core.Actions.ActionBaseTuningHub.Configure(
            FusionRpg.Core.Actions.ActionBaseTuningLoader.Parse(
                File.ReadAllText(Path.Combine(CoreRoot(), "data", "tuning", "action-base.v2.json"))));
        SummoningTuningHub.Configure(DefaultSummoning);
        WorldAiPolicy.Configure(DefaultAi);
        VfxTuningHub.Configure(DefaultVfx);
        ActorHudTuningHub.Configure(DefaultActorHud);
        PowerTuningHub.Configure(DefaultPower);
        RungPolicy.Configure(DefaultActionRungs);
        ItemsTuningHub.Configure(DefaultItems);
        // commander-identity SE4.2/SE4.3: the process-wide commander directory, read from the real
        // authored registry exactly like production does (Core never touches a path).
        FusionRpg.Core.Commanders.CommanderDirectoryHub.Configure(
            FusionRpg.Core.Commanders.DataCommanderDirectory.Parse(
                File.ReadAllText(Path.Combine(
                    ContentRoot(), "data", "seed", "commanders", "_registry", "default-commanders.v1.json"))));
        // identity-rename T13: the lead-names registry, same convention — a world template names
        // its empires from it and an empty save is named from it, so every test that builds either
        // needs it configured before production's own boot would.
        FusionRpg.Core.Narrative.LeadNamesHub.Configure(FusionRpg.Core.Narrative.LeadNames.Parse(
                File.ReadAllText(Path.Combine(
                    ContentRoot(), "data", "seed", "narrative", "_registry", "names.en.v1.json"))));
        // save-identity SE4.12: the authored new-save registry, same convention.
        FusionRpg.Core.Saves.NewSaveEmpiresHub.Configure(
            FusionRpg.Core.Saves.NewSaveEmpires.Parse(
                File.ReadAllText(Path.Combine(
                    ContentRoot(), "data", "seed", "saves", "_registry", "new-save-empires.v1.json"))));
        // species-gear-chain T34b: the trophy id registry, read from the real committed generated
        // corpus exactly like production does (Core never reads a file itself) — every test in this
        // assembly that names a real trophy id (e.g. "trophy.species.abyssswordstar.1") resolves it
        // the same way production's MaterialCatalog.ClassOf would.
        FusionRpg.Core.Items.Materials.MaterialCatalog.ConfigureTrophyRegistry(
            FusionRpg.Core.Items.Materials.MaterialCatalog.ParseTrophyRegistryIds(
                File.ReadAllText(Path.Combine(
                    ContentRoot(), "data", "seed", "items", "materials", "trophy-registry.json"))));
    }

    public static readonly ContractTuning DefaultContracts = new(
        SchemaVersion: 1,
        Version: 1,
        Loyalty: new ContractLoyaltyTuning(
            Max: 1000, DeployFloor: 200, BindLoyalty: 300,
            SwornThreshold: 400, TrustedThreshold: 600, DevotedThreshold: 800,
            WinGain: 15, LossPenalty: 10, DailyGainCap: 60, DecayPerDay: 25, RitualGain: 100,
            RankBonusSwornMilli: 15, RankBonusTrustedMilli: 35, RankBonusDevotedMilli: 60),
        Slots: new ContractSlotsTuning(BaseSlots: 12, SlotPriceStep: 300),
        Settlement: new ContractSettlementTuning(MaxSettleDays: 30),
        PersonalityRates: new Dictionary<CreaturePersonality, PersonalityRateTuning>
        {
            [CreaturePersonality.Loyal] = new(120, 80, 100),
            [CreaturePersonality.Stoic] = new(90, 60, 100),
            [CreaturePersonality.Proud] = new(100, 100, 130),
            [CreaturePersonality.Calculating] = new(100, 90, 110),
            [CreaturePersonality.Feral] = new(80, 150, 70),
        },
        // Full 10-key coverage (seed-to-concrete T4.1) — ContractPolicy.BaseUpkeepPerDay/
        // RitualPrice throw ArgumentOutOfRangeException on a missing key, no fallback.
        BaseUpkeepPerDay: new Dictionary<CreatureRarity, int>
        {
            [CreatureRarity.Chaff] = 2, [CreatureRarity.Sprout] = 3, [CreatureRarity.Grafted] = 4,
            [CreatureRarity.Cultivated] = 5, [CreatureRarity.Fused] = 7, [CreatureRarity.Chimeric] = 9,
            [CreatureRarity.Heirloom] = 12, [CreatureRarity.Firstseed] = 16, [CreatureRarity.Sunwoven] = 25,
            [CreatureRarity.Almanac] = 32,
        },
        RitualPriceSouls: new Dictionary<CreatureRarity, long>
        {
            [CreatureRarity.Chaff] = 50, [CreatureRarity.Sprout] = 65, [CreatureRarity.Grafted] = 80,
            [CreatureRarity.Cultivated] = 100, [CreatureRarity.Fused] = 130, [CreatureRarity.Chimeric] = 160,
            [CreatureRarity.Heirloom] = 200, [CreatureRarity.Firstseed] = 260, [CreatureRarity.Sunwoven] = 400,
            [CreatureRarity.Almanac] = 500,
        });

    public static readonly LoamTuning DefaultLoam = new(
        SchemaVersion: 1,
        Version: 1,
        Upkeep: new LoamUpkeepTuning(
            SeepPerTurn: 50, LoamCapacity: 300, BaseUpkeepPerSector: 10,
            GarrisonUpkeepPerMember: 2, DevelopmentUpkeepPerLevel: 5, DangerUpkeepPerBand: 3,
            // wonder-effect-empire Task 2.3c, matches loam.v5.json's own upkeep
            // block exactly. Provisional 50 (five percent), flagged for a balance pass.
            EmpireWonderUpkeepRateMilli: 50),
        // world-map W55, matches loam.v2.json's own `development.yieldPerLevel` exactly.
        Development: new LoamDevelopmentTuning(YieldPerLevel: 6),
        Fade: new LoamFadeTuning(
            RecoveryMilli: 20, BaseDecayMilli: 40, DecayPerDeficitUnitMilli: 1,
            DecayScaleDivisor: 5, MaxDecayMilli: 300, AbandonmentHorizonTurns: 3),
        LegionSupply: new LoamLegionSupplyTuning(CarryPerBearer: 200, BurnPerMember: 10, BesiegedRationMilli: 1000),
        Structures: new LoamStructuresTuning(
            WellYieldMultiplierMilli: 2000, WellCost: 200, WaystationCost: 300,
            WellBuildTurns: 2, WaystationBuildTurns: 4, WaystationRangeHops: 3,
            GranaryCost: 150, GranaryCapacityBonus: 300, GranaryBuildTurns: 2,
            // world-map W56, matches loam.v4.json's own soulConduit*/extractor*/hatchery* keys
            // (renamed off *CostMilli by world-map W57 -- no value changed).
            SoulConduitCost: 250, SoulConduitFlatYieldPerTurn: 20, SoulConduitBuildTurns: 3,
            ExtractorCost: 200, ExtractorFlatYieldPerTurn: 15, ExtractorBuildTurns: 2,
            HatcheryCost: 300, HatcheryYieldMultiplierMilli: 1500, HatcheryBuildTurns: 3),
        Texture: new LoamTextureTuning(
            ContagionPressurePerTurn: 60, MaxPressureMilli: 300, PressureDecayPerTurn: 40,
            SurgeDecayMultiplierMilli: 1500, UnmadeSpawnAfterTurns: 5,
            UnmadeMemberHp: 120, UnmadeMemberCount: 2));

    public static readonly WorldSpawnTuning DefaultWorldSpawn = new(
        SchemaVersion: 1,
        Version: 1,
        OffClimateMilli: 200,
        FallbackSpeciesId: "NormalZombie",
        Sectors: new Dictionary<string, WorldSpawnSectorTuning>(StringComparer.Ordinal)
        {
            ["barren"] = new(CreatureRarity.Chaff, CreatureRarity.Fused,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["chaff"] = 100, ["sprout"] = 100, ["grafted"] = 100,
                    ["cultivated"] = 100, ["fused"] = 100,
                }, 0, Array.Empty<WorldSpawnMemberEntry>()),
            ["stable"] = new(CreatureRarity.Chaff, CreatureRarity.Cultivated,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["chaff"] = 100, ["sprout"] = 100, ["grafted"] = 100, ["cultivated"] = 100,
                }, 0, Array.Empty<WorldSpawnMemberEntry>()),
        });

    public static readonly WorldTuning DefaultWorld = new(
        SchemaVersion: 1,
        Version: 1,
        LaneCostMultiplierMilli: new Dictionary<string, int>
        {
            ["rift"] = 1000, ["corridor"] = 700, ["ley"] = 900,
            ["deep"] = 1600, ["one-way"] = 800, ["gated"] = 1000,
        },
        WorldSizeNodes: new Dictionary<string, WorldSizeNodeRange>
        {
            ["small"] = new(6, 10), ["medium"] = new(14, 18), ["large"] = new(28, 36),
            ["huge"] = new(56, 72), ["giant"] = new(112, 144),
        },
        StrengthBands: new[]
        {
            new StrengthBandTuning(0, 0, 0),
            new StrengthBandTuning(1, 499, 250),
            new StrengthBandTuning(500, 1_499, 1_000),
            new StrengthBandTuning(1_500, 3_999, 2_750),
            new StrengthBandTuning(4_000, 9_999, 7_000),
            new StrengthBandTuning(10_000, 20_000, 20_000),
        },
        Calendar: new WorldCalendarTuning(
            DaysPerWeek: 7, WeeksPerMonth: 4,
            SpecialWeekChanceMilli: 250, SpecialMonthChanceMilli: 400, PlagueChanceMilli: 100),
        // world-stage W30, matches gk-core/data/tuning/world.v2.json's own starting value.
        // act-price-table 4A.5, matches gk-core/data/tuning/world.v6.json's own movement rows.
        Movement: new MovementTuning(
            DowseBudgetMilli: 250,
            ClaimCostMilli: 250,
            DepositCostMilli: 100,
            WithdrawCostMilli: 100,
            LoadCostMilli: 50,
            UnloadCostMilli: 50,
            HoldAllowanceMilli: 250),
        // world-map W58, matches gk-core/data/tuning/world.v5.json's own real growth-pulse/season values
        // (world.v42/W51 identity is history now -- growth and the season upkeep term are live).
        Growth: new WorldGrowthTuning(
            SeatPulsePerWeek: 20, LairMultiplierMilli: 4000, SpecialWeekMultiplierMilli: 1500,
            RaiseCostPoints: 100, RaiseMemberHp: 110,
            LegionTarget: new LegionTargetTuning(Min: 6, Max: 10, ByTurn: 40)),
        Seasons: new WorldSeasonsTuning(
            Count: 4, MonthsPerSeason: 1,
            YieldMilli: new[] { 1000, 1000, 1000, 1000 },
            UpkeepMilli: new[] { 1000, 850, 1100, 1400 },
            MovementMilli: new[] { 1000, 1000, 1000, 1000 }));

    public static readonly SoulEarnTuning DefaultSouls = new(
        SchemaVersion: 1,
        Version: 1,
        Kill: new SoulKillTuning(KillDelta: 1),
        MatchEnd: new SoulMatchEndTuning(VictoryDelta: 100, DefeatDelta: 25),
        // Full 10-key coverage (seed-to-concrete T4.1) — SoulEarnPolicy.DiscoveryDelta has
        // no fallback for a missing key.
        DiscoveryDelta: new Dictionary<CreatureRarity, int>
        {
            [CreatureRarity.Chaff] = 25, [CreatureRarity.Sprout] = 42, [CreatureRarity.Grafted] = 58,
            [CreatureRarity.Cultivated] = 75, [CreatureRarity.Fused] = 115, [CreatureRarity.Chimeric] = 160,
            [CreatureRarity.Heirloom] = 200, [CreatureRarity.Firstseed] = 350, [CreatureRarity.Sunwoven] = 500,
            [CreatureRarity.Almanac] = 750,
        },
        Codex: new SoulCodexTuning(HalfMilestone: 500, FullMilestone: 1500));

    public static readonly PatronTuning DefaultPatron = new(
        SchemaVersion: 1,
        Version: 1,
        SwitchCostSouls: 100,
        AuraClampMilli: 150,
        PerStarMilli: 10,
        PThetaKMilli: 220, // matches the real shipped patron.v1.json (aura-skill T22)
        // Full 10-key coverage (seed-to-concrete T4.1) — PatronPolicy.RarityBaseMilli's
        // fallback reads [CreatureRarity.Almanac], which must itself be present.
        RarityBaseMilli: new Dictionary<CreatureRarity, int>
        {
            [CreatureRarity.Chaff] = 20, [CreatureRarity.Sprout] = 24, [CreatureRarity.Grafted] = 27,
            [CreatureRarity.Cultivated] = 30, [CreatureRarity.Fused] = 34, [CreatureRarity.Chimeric] = 38,
            [CreatureRarity.Heirloom] = 45, [CreatureRarity.Firstseed] = 50, [CreatureRarity.Sunwoven] = 60,
            [CreatureRarity.Almanac] = 70,
        });

    public static readonly ShieldTuning DefaultShield = new(
        SchemaVersion: 1,
        Version: 1,
        MatchupShareKPm: 250,
        ChipFloorKPm: 100,
        PenCapKPm: 3000,
        MaxShieldsPerActor: 3,
        DrainPriority: new ShieldDrainPriorityTuning(Aura: 30, Skill: 20, Innate: 10));

    public static readonly CombatTuning DefaultCombat = new(
        SchemaVersion: 1,
        Version: 1,
        ProcDepthLimit: 6,
        DefaultMaxTargets: 8,
        AreaDefaultSquareSize: 3,
        AreaDefaultRectangleWidth: 3,
        AreaDefaultRectangleHeight: 3,
        DotDefaultPeriodMs: 1000,
        DotDefaultDurationMs: 5000,
        PierceScale: 10.0,
        AmpScale: 10.0,
        BlockCapPermille: 950,
        ParryCapPermille: 950,
        AvoidanceBandCapPermille: 950,
        ReflectRateScale: 10.0,
        ReflectShareScale: 100.0,
        ParryNeutralShareKPm: 500,
        DefenseShape: DefenseShape.Divisive,
        DefenseDivisorK: 0.45,
        ReflectReadsPostShield: true,
        AmpShape: AmpShape.Reciprocal);

    public static readonly FusionTuning DefaultFusion = new(
        SchemaVersion: 1,
        Version: 1,
        PerStarPowerMilli: 30,
        PerStarDefenseMilli: 30,
        // Full 10-key coverage (seed-to-concrete T4.1) — StarPolicy.StarCap's own fallback
        // reads Tuning.StarCap[CreatureRarity.Almanac] when a rarity is missing, so Almanac itself
        // must always be present or that fallback throws too (found exactly this way: a post-
        // promotion star-merge test hit Sprout, which fell back to Almanac, which wasn't here).
        StarCap: new Dictionary<CreatureRarity, int>
        {
            [CreatureRarity.Chaff] = 6, [CreatureRarity.Sprout] = 6, [CreatureRarity.Grafted] = 6,
            [CreatureRarity.Cultivated] = 8, [CreatureRarity.Fused] = 8, [CreatureRarity.Chimeric] = 8,
            [CreatureRarity.Heirloom] = 10, [CreatureRarity.Firstseed] = 10, [CreatureRarity.Sunwoven] = 10,
            [CreatureRarity.Almanac] = 10,
        },
        StarMergeCost: new FusionCostTuning(Souls: 50, ShardCount: 1, EssenceCount: 1),
        PromotionCost: new FusionCostTuning(Souls: 200, ShardCount: 3, EssenceCount: 3),
        // Per-rung promotion price (effort-power M5). Mirrors the shipped table so a fixture drift
        // shows up as a test failure rather than as silently different balance.
        PromotionCostByRarity: new Dictionary<CreatureRarity, FusionCostTuning>
        {
            [CreatureRarity.Chaff] = new(150, 2, 2), [CreatureRarity.Sprout] = new(185, 2, 2),
            [CreatureRarity.Grafted] = new(220, 2, 3), [CreatureRarity.Cultivated] = new(320, 3, 4),
            [CreatureRarity.Fused] = new(450, 3, 5), [CreatureRarity.Chimeric] = new(620, 4, 6),
            [CreatureRarity.Heirloom] = new(820, 4, 7), [CreatureRarity.Firstseed] = new(1000, 5, 8),
            [CreatureRarity.Sunwoven] = new(1000, 5, 8), [CreatureRarity.Almanac] = new(1000, 5, 8),
        },
        RecipeCost: new Dictionary<CreatureRarity, RecipeCostTuning>
        {
            [CreatureRarity.Cultivated] = new(Souls: 150, ShardRarity: CreatureRarity.Chaff, ShardCount: 2, EssenceCount: 2),
            [CreatureRarity.Heirloom] = new(Souls: 400, ShardRarity: CreatureRarity.Cultivated, ShardCount: 3, EssenceCount: 4),
            [CreatureRarity.Sunwoven] = new(Souls: 1000, ShardRarity: CreatureRarity.Heirloom, ShardCount: 4, EssenceCount: 8),
        },
        // Full 10-key coverage (unlike StarCap/RecipeCost above) — FusionRoller.SlotsFor has no
        // TryGetValue fallback, so a test exercising any rung must find a real entry here.
        SlotsByRarity: new Dictionary<CreatureRarity, int>
        {
            [CreatureRarity.Chaff] = 1, [CreatureRarity.Sprout] = 1, [CreatureRarity.Grafted] = 1,
            [CreatureRarity.Cultivated] = 2, [CreatureRarity.Fused] = 2, [CreatureRarity.Chimeric] = 2,
            [CreatureRarity.Heirloom] = 2, [CreatureRarity.Firstseed] = 3, [CreatureRarity.Sunwoven] = 3,
            [CreatureRarity.Almanac] = 3,
        },
        // WAVE F2.3 — mirrors RecipeCost's own (sparse, same rungs) souls values above; a pick's
        // cost is read from its own source rarity, never the fusion output's.
        InheritCostByRarity: new Dictionary<CreatureRarity, long>
        {
            [CreatureRarity.Cultivated] = 150,
            [CreatureRarity.Heirloom] = 400,
            [CreatureRarity.Sunwoven] = 1000,
        });

    public static readonly DerivedStatTuning DefaultDerivedStats = new(
        SchemaVersion: 2, Version: 2, CategoryResistCap: 0.95, TurnDefaultSpeed: 100);

    public static readonly StatusTuning DefaultStatus = new(
        SchemaVersion: 1,
        Version: 1,
        ApplyScaleK: 100.0,
        ApplyScaleFloor: 1.0,
        ResistFromPowerRatio: 1.0, // T3.1 (power-plan.md, done 2026-08-24): 0 -> 1.0, matched pair contests at delta=0
        MinNetFactor: 0.0,
        MaxNetFactor: 10_000.0,
        NetFactorScale: 10.0, // T3.2 (power-plan.md, done 2026-08-24): netFactor = 1 + delta/NetFactorScale (audit F4)
        ProgressionPowerStubDefault: 1.0,
        ProcDepthLimitDefault: 6,
        ApplySteepnessDefault: 1.0,
        ApplyShape: StatusApplyShape.Sigmoid,
        ApplyOffsetK: 0.0);

    public static readonly OverlayTuning DefaultOverlay = new(
        SchemaVersion: 1,
        Version: 1,
        Pause: new OverlayPauseTuning(PausedTimeScale: 0f, MaxResumeScale: 10f),
        SwitchLayout: new OverlaySwitchLayoutTuning(
            BaseButtonW: 72f, BaseButtonH: 28f, BaseMargin: 16f, ReferenceHeight: 1080f,
            MinScale: 1f, MaxScale: 3f),
        SwitchState: new OverlaySwitchStateTuning(DebounceMs: 300, ProbeIntervalMs: 30_000, SendTimeoutMs: 3_000),
        SettingsGui: new OverlaySettingsGuiTuning(PanelW: 280f, PanelH: 196f),
        RiftMenu: new RiftMenuTuning(
            AnchorCenterX: 0.5f, AnchorCenterY: 0.62f,
            WidthFraction: 0.11f, HeightFraction: 0.16f, MinDevicePx: 44f));

    public static readonly StatsTuning DefaultStats = new(
        SchemaVersion: 1,
        Version: 1,
        MinimumInterval: 0.01,
        MatchupShareK: 0.25,
        AccuracyScale: 100.0,
        CritRateScale: 100.0,
        CritDamageScale: 100.0,
        Steepness: 1.0);

    public static readonly ExpeditionTuning DefaultExpeditions = new(
        SchemaVersion: 1,
        Version: 2,
        Tiers: new Dictionary<string, ExpeditionTierNumbers>
        {
            ["scout-30m"] = new(DurationMinutes: 30, TickCount: 6, BattleCount: 1, SquadSlots: 2, DangerBand: 1),
            ["forage-4h"] = new(DurationMinutes: 240, TickCount: 8, BattleCount: 2, SquadSlots: 3, DangerBand: 2),
            ["hunt-8h"] = new(DurationMinutes: 480, TickCount: 8, BattleCount: 3, SquadSlots: 4, DangerBand: 3),
            ["warpath-20h"] = new(DurationMinutes: 1200, TickCount: 10, BattleCount: 4, SquadSlots: 5, DangerBand: 4),
        },
        EventRoll: new ExpeditionEventRollTuning(
            QuietCeilMilli: 400, FoundSoulsCeilMilli: 750, WildCeilMilli: 900, WildJoinMilli: 250,
            ShinyDie: 64, InjuryPowerDivisor: 4),
        // NR2.19's published keys, mirrored from gk-core/data/tuning/expeditions.v2.json so an in-memory
        // bootstrap and the shipped file agree.
        Encounter: new ExpeditionEncounterTuning(WildCreatureMetMilli: 250, QuietMilli: 50));

    // species-gear-chain T31 — same identity map as the shipped gk-core/data/tuning/creature-yield.v1.json.
    public static readonly CreatureYieldTuning DefaultCreatureYield = new(
        SchemaVersion: 1, Version: 1,
        ShardByRung: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chaff"] = "shard.chaff", ["sprout"] = "shard.sprout", ["grafted"] = "shard.grafted",
            ["cultivated"] = "shard.cultivated", ["fused"] = "shard.fused", ["chimeric"] = "shard.chimeric",
            ["heirloom"] = "shard.heirloom", ["firstseed"] = "shard.firstseed",
            ["sunwoven"] = "shard.sunwoven", ["almanac"] = "shard.almanac",
        });

    public static readonly MatchTuning DefaultMatch = new(
        SchemaVersion: 1, Version: 1, MaxLivingPlants: 50, MaxLivingZombies: 80,
        // E36 (spec-wave-control.md §2.2), matches gk-core/data/tuning/match.v1.json's own waveHoldFloorSeconds.
        WaveHoldFloorSeconds: 30);

    public static readonly EffectsTuning DefaultEffects = new(
        SchemaVersion: 1, Version: 1,
        MatchupReadSlotShareMilli: 250,
        DamageFxFloater: new DamageFxFloaterTuning(Cap: 64, LifeSeconds: 0.9, RisePixels: 56));

    public static readonly SimTuning DefaultSim = new(
        SchemaVersion: 1, Version: 1,
        PlantHp: 300, PlantAttack: 20, ZombieHp: 270, ZombieAttack: 50, HitDamage: 50);

    public static readonly ProgressionTuning DefaultProgression = new(
        SchemaVersion: 1, Version: 3,
        PlantCurve: new XpCurveParams(80, 32),
        ZombieCurve: new XpCurveParams(70, 28),
        PlayerCurve: new XpCurveParams(100, 45),
        SpecimenCurve: new XpCurveParams(100, 45),
        Awards: new XpAwardsTuning(Kill: 12, Defeat: -100, Mower: -30, PlantPlace: 8, ZombieSpawn: 9)
        {
            // empire-level EP4.2/EP4.3: the empire credit reads this as its delta, and the
            // shipped progression.v3.json carries it, so the bootstrap carries it too.
            SpeciesLevelUp = 1,
        })
    {
        // empire-level EP4.2: `EmpireCurve`/`SpeciesLevelUp` are init properties on the record,
        // not constructor parameters, so they ride the object initializer — and the shipped
        // progression.v3.json carries both.
        EmpireCurve = new XpCurveParams(10, 5),
    };

    // species-build T1.1/T1.2 — same working set as the shipped species-progression.v1.json, per
    // this file's own convention. Configuring it here (not just in SpeciesProgressionTests' own
    // static ctor) makes RpgXpAwardMap's species-placement award a permanent, deterministic part of
    // this assembly's ambient state rather than something that only appears when some other test
    // class happens to run first and configure it as a side effect.
    public static readonly SpeciesProgressionTuning DefaultSpeciesProgression = new(
        CurveFirst: 60, CurveStep: 24, RunCompletionAward: 100, PlacementAward: 4);

    public static readonly SiegeTuning DefaultSiege = new(
        SchemaVersion: 1, Version: 1,
        MoveCostOpen: 10, MoveCostRough: 20, DiagonalSurcharge: 0, MaxCells: 4096,
        District: new DistrictTuning(
            SideByBaseTier: new Dictionary<int, int> { [0] = 18, [1] = 24, [2] = 30 },
            CoreSideMilli: 400, GateCount: 2, RampartThickness: 1, FortressRampartBonus: 1,
            ApproachDepth: 4, ApproachDepthPerWardLevel: 1),
        Structure: new StructureTuning(
            RepairCostRatioMilli: 600,
            TierMultiplierMilli: new Dictionary<int, int> { [1] = 1000, [2] = 1800, [3] = 3000 },
            StorageCapacityPerDevelopmentLevel: 50,
            DepletionPerHarvestMilli: 10),
        Objective: new SiegeObjectiveTuning(
            FieldCapMaxLivingPerSide: -1,
            LegionSlotsPerSide: 2, LegionSlotsPerDevelopmentLevel: 0,
            MaxLegionMembers: -1,
            DefenseSlotsAtDevelopmentZero: 4, DefenseSlotsPerDevelopmentLevel: 2, DefenseSlotsGridCapacityPoint: 2,
            DistrictDefenderBonusMilli: 1000),
        Waves: new SiegeWavesTuning(
            MaxArrivalsPerRound: 8, BatchIntervalTicks: -1, FieldClearedThreshold: 0, BatchSize: -1),
        Shooting: new FusionRpg.Core.Battle.Siege.SiegeShootingTuning(
            RangeThresholdMilli: 500, RangePowerMilli: 500,
            ObstructionPowerMilli: 700, ObstructionFloorMilli: 250, MeleeLockPowerMilli: 500),
        Construction: new ConstructionTuning(
            ShardVeinYieldPerTurn: 4, MaterialSeamYieldPerTurn: 3,
            RefineRubblePerIronwork: 4, RefineYieldMilli: 600, RefinePerTurnCap: -1,
            LabourMoatStaminaCost: 30, LabourMoatHungerCost: 15, LabourMoatTurns: 2, SummonQiCost: 25),
        Economy: new EconomyTuning(
            NodeYieldPerRoundLoam: 5, NodeYieldPerRoundIronwork: 3,
            DepotSeedMilli: 1000, CaptureRecoveryMilli: 1000),
        // combat-ai `profile-schema` CAI1.8 (H7): narrowed to the two dead keys and the two siege-
        // geometry keys -- the ten scorer weights moved to CombatAiProfilePolicy (DefaultCombatAi
        // below), configured in this same bootstrap, and CAI3.1 deleted the two dead keys.
        Ai: new FusionRpg.Core.Battle.Siege.AiTuning(
            ObjectiveReferenceDistanceCells: 20, ThreatRadiusCells: 4),
        Fog: new FusionRpg.Core.Battle.Board.FogTuning(Enabled: true, DefaultVisionRangeTiles: 6));

    // combat-ai `profile-schema` CAI1.8: the SAME identity shape gk-core/data/tuning/combat-ai.v1.json ships --
    // constructed inline (tunables-ssot.md S7.2: "tests construct one inline; no fixture files"), the
    // shipped siege.v1.json ten weights carried at "siege/default" and the root "*/default", argmax/
    // keepPct 1000/no reserves/guards off/anti-repeat off/personality 0 everywhere, tierByActorClass
    // total over both classes.
    public static readonly FusionRpg.Core.Actions.Ai.CombatAiTuning DefaultCombatAi = BuildDefaultCombatAi();

    static FusionRpg.Core.Actions.Ai.CombatAiTuning BuildDefaultCombatAi()
    {
        var scoring = new FusionRpg.Core.Actions.Ai.AiScoringBlock(
            WeightHitChance: 70, WeightObjective: 50, WeightKill: 15, WeightLowHp: 10,
            WeightCannotCounter: 10, WeightRound: 1, WeightRisk: 120,
            AggressionRange: 2, MaxCandidatesScored: 32);
        var selection = new FusionRpg.Core.Actions.Ai.AiSelectionBlock(
            FusionRpg.Core.Actions.Ai.SelectionMode.Argmax, KeepPctMilli: 1000, RngStreamName: "ai.select");
        var tierByActorClass = new Dictionary<FusionRpg.Core.Actions.Ai.AiActorClass, FusionRpg.Core.Actions.Ai.AiTier>
        {
            [FusionRpg.Core.Actions.Ai.AiActorClass.Unique] = FusionRpg.Core.Actions.Ai.AiTier.Smart,
            [FusionRpg.Core.Actions.Ai.AiActorClass.General] = FusionRpg.Core.Actions.Ai.AiTier.Performance,
        };
        var rows = new[]
        {
            new FusionRpg.Core.Actions.Ai.AiProfileRow(
                FusionRpg.Core.Actions.Ai.TargetSelector.Nearest, FusionRpg.Core.Actions.Ai.AiRowCondition.Always,
                0, "", FusionRpg.Core.Actions.Ai.AiCensusCondition.None, 0,
                new FusionRpg.Core.Actions.Ai.AiActionFilter(null, null, null, null)),
        };
        var guards = new FusionRpg.Core.Actions.Ai.AiWasteGuards(1, 0, 0);
        var antiRepeat = new FusionRpg.Core.Actions.Ai.AiAntiRepeat(0, 0, 0);
        var personality = new FusionRpg.Core.Actions.Ai.AiPersonalityBounds(
            new Dictionary<FusionRpg.Core.Actions.Ai.PersonalityAxis, int>
            {
                [FusionRpg.Core.Actions.Ai.PersonalityAxis.Aggression] = 0,
                [FusionRpg.Core.Actions.Ai.PersonalityAxis.Recklessness] = 0,
                [FusionRpg.Core.Actions.Ai.PersonalityAxis.Focus] = 0,
                [FusionRpg.Core.Actions.Ai.PersonalityAxis.Thrift] = 0,
            });

        var root = new FusionRpg.Core.Actions.Ai.CombatAiProfile(
            "*/default", FusionRpg.Core.Actions.Ai.AiPlace.Battle, FusionRpg.Core.Actions.Ai.AiRole.Default,
            TierOverride: null, tierByActorClass, rows, scoring, selection,
            Array.Empty<FusionRpg.Core.Actions.Ai.AiReserveFloor>(), guards, antiRepeat, Trigger: null, personality);
        var siege = root with { ProfileId = "siege/default", Place = FusionRpg.Core.Actions.Ai.AiPlace.Siege, TierOverride = FusionRpg.Core.Actions.Ai.AiTier.Smart };

        return new FusionRpg.Core.Actions.Ai.CombatAiTuning(
            SchemaVersion: 1, Version: 1,
            Profiles: new Dictionary<string, FusionRpg.Core.Actions.Ai.CombatAiProfile>(StringComparer.Ordinal)
            {
                ["*/default"] = root,
                ["siege/default"] = siege,
            },
            Router: new FusionRpg.Core.Actions.Ai.AiRouterBlock(OrderTimeoutTicks: 5000, ReactionsPerRoundExpected: 1000));
    }

    public static readonly BattleBoardTuning DefaultBattleBoard = new(
        SchemaVersion: 1, Version: 1, MinSide: 5, MaxSide: 9);

    public static readonly BattleTuning DefaultBattle = new(
        SchemaVersion: 1, Version: 1,
        RoundDurationMs: 1000, MaxRounds: 50,
        PrimaryAffinityDivisor: 4, SecondaryAffinityDivisor: 8,
        Traits: new Dictionary<string, TraitMagnitudes>(StringComparer.Ordinal)
        {
            ["berserker"] = new(BerserkRampHalfMilli: 250, BerserkRampQuarterMilli: 500),
            ["regenerator"] = new(RegenPerRoundMilli: 20),
            ["soul-eater"] = new(OnKillHealMilli: 100),
            ["guardian"] = new(GuardShareMilli: 250),
            ["swift"] = new(InitiativeBonusMilli: 1000),
            ["immortal"] = new(DeathRefusalCharges: 1),
            ["coward"] = new(RetreatBelowMilli: 250),
            ["greedy"] = new(SoulLootBonusMilli: 250),
            ["genius"] = new(SpecimenXpBonusMilli: 250),
            ["void-touched"] = new(EssenceProcMilli: 100, EssenceRiderMilli: 150),
            ["chaos-marked"] = new(EssenceProcMilli: 100, EssenceRiderMilli: 150),
        },
        TimelineProfiles: new Dictionary<string, TimelineProfileTuning>(StringComparer.Ordinal)
        {
            ["classic-round"] = new(W: 1, WReact: 0, PassQuantum: 1, MaxPoints: null),
            ["galaxy-sync"] = new(W: 2, WReact: 0, PassQuantum: 1, MaxPoints: null),
            ["hybrid-atb"] = new(W: 4, WReact: 0, PassQuantum: 1, MaxPoints: 2),
            // base-defense F2/decision 29: w/wReact/passQuantum are real values (spec's own
            // tunables table); MaxRounds/RoundDurationMs stay null (unset) so siege inherits
            // the ruleset horizon until a real board exists to measure one on.
            ["siege"] = new(W: 2, WReact: 0, PassQuantum: 1, MaxPoints: null),
            // party-dungeon D2.9: hybrid-atb-shaped magnitudes, copied verbatim (matches this
            // fixture's own hybrid-atb row above, not the real battle.v4.json's WReact=1 -- this
            // inline fixture predates reaction-lane RL1 and this task does not reconcile that).
            ["delve"] = new(W: 4, WReact: 0, PassQuantum: 1, MaxPoints: 2),
        },
        // Wave E3/Phase 7 F1 (2026-09-07): matches gk-core/data/tuning/battle.v5.json's shipped value --
        // secondary now carries 300/1000 of an attack's payload for any actor with a real
        // ElementSecondary. Hand-built fixtures in this file carry no secondary element, so this
        // change alone does not move any Core/Data/E2E golden built from them; only the real
        // WaveCatalog-driven expedition goldens (real ElementSecondary, WaveCatalog.cs:115) can move.
        HybridSecondaryWeightMilli: 300,
        // base-defense F2: matches gk-core/data/tuning/battle.v2.json's shipped value exactly, so
        // 50 * 4000 = 200_000 reproduces the pre-F2 MaxLoopIterations constant.
        LoopGuardRoundMultiple: 4000,
        // battle-tempo tempo-content (2026-09-05): matches gk-core/data/tuning/battle.v3.json's shipped
        // speciesTempo.referenceIntervalMs exactly (1500ms = the shipped "steady" attack tempo).
        SpeciesTempoReferenceIntervalMs: 1500);

    /// <summary>Transcribed from the shipped gk-core/data/tuning/battle-resources.v1.json. `hp` is absent on
    /// purpose -- its max mirrors BattleActorSetup.MaxHp (spec-battle-resources.md S2.6).
    ///
    /// <para>Regen share deliberately stays all-zero here — this is the assembly-wide AMBIENT
    /// fixture every test in this project reads through <see cref="BattleHubCompose"/>, so keeping it
    /// at the pre-T11 baseline is what makes the whole assembly byte-identical unless a test opts in
    /// to the real v2 numbers itself (lawn-combat-wire T11, spec-lawn-combat-calibration.md — the
    /// real `stamina=50` share now lives in the shipped `battle-resources.v2.json`, read directly by
    /// the Balance guard test that exercises it, not through this ambient default).</para></summary>
    public static readonly FusionRpg.Core.Battle.BattleResourceTuning DefaultBattleResources = new(
        SchemaVersion: 1, Version: 1,
        PoolShareMilli: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["stamina"] = 500,
            ["hunger"] = 500,
            ["spirit"] = 500,
            ["qi"] = 500,
            ["poise"] = 500,
        },
        RegenPerSecondShareMilli: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["stamina"] = 0,
            ["hunger"] = 0,
            ["spirit"] = 0,
            ["qi"] = 0,
            ["poise"] = 0,
        });

    public static readonly FusionRpg.Core.Actions.ActionTimingTuning DefaultActionTiming = new(
        WindupPerPowerMilli: 20,
        WindupCapReferenceMilli: 300,
        RecoveryPerPowerMilli: 8,
        BasicAttack: new FusionRpg.Core.Actions.BasicAttackTimingTuning(WindupTicks: 150, RecoveryTicks: 50),
        Categories: new Dictionary<FusionRpg.Core.Actions.ActionCategory, FusionRpg.Core.Actions.ActionTimingCategoryTuning>
        {
            [FusionRpg.Core.Actions.ActionCategory.Attack] = new(TimeCostBaseTicks: 100, CooldownBaseTicks: 200),
            [FusionRpg.Core.Actions.ActionCategory.Defense] = new(TimeCostBaseTicks: 120, CooldownBaseTicks: 150),
            [FusionRpg.Core.Actions.ActionCategory.Support] = new(TimeCostBaseTicks: 100, CooldownBaseTicks: 250),
            [FusionRpg.Core.Actions.ActionCategory.Movement] = new(TimeCostBaseTicks: 80, CooldownBaseTicks: 100),
            [FusionRpg.Core.Actions.ActionCategory.Status] = new(TimeCostBaseTicks: 90, CooldownBaseTicks: 180),
        });

    public static readonly SummoningTuning DefaultSummoning = new(
        SchemaVersion: 1, Version: 1,
        Banners: new Dictionary<string, BannerTuning>(StringComparer.Ordinal)
        {
            ["standard-rift"] = new(CostPerPull: 100, CostPerTen: 900, FocusWeightMultiplier: 1.0),
            ["element-focus"] = new(CostPerPull: 120, CostPerTen: 1080, FocusWeightMultiplier: 3.0),
        },
        Roller: new RollerTuning(
            HeirloomHardPity: 25, SunwovenSoftStart: 41, SunwovenHardPity: 55,
            SunwovenBasePerMille: 8, SunwovenRampPerMille: 60, AlmanacPerMille: 2,
            HeirloomPerMille: 25, FirstseedPerMille: 15, ChimericPerMille: 40, FusedPerMille: 60,
            CultivatedPerMille: 100, GraftedPerMille: 150, SproutPerMille: 250, ShinyOneIn: 64));

    public static readonly WorldAiTuning DefaultAi = new(
        SchemaVersion: 1, Version: 3,
        // Momentum matches the SHIPPED value on purpose. A test default of 0 would quietly exercise a
        // configuration nothing runs, and the whole point of this term is that it changes behaviour.
        FrontierRules: new FrontierRulesTuning(
            RecoverAtMilli: 400, ExploreTurns: 3, SeveranceThresholdCost: 10_000, MomentumMarginMilli: 250),
        ThreatMap: new ThreatMapTuning(StaleDecayPerTurn: 150, MaxSpreadHops: 4, ProximityFalloffPerHop: 400),
        ValueMap: new ValueMapTuning(
            OptimismMilli: 700, OverextensionPenaltyMilli: 1400, HabitabilityPenaltyMilli: 1400,
            DefaultWeights: new ValueWeightsTuning(
                Yield: 1000, Strategic: 800, Defensibility: 500, Cost: 700, Risk: 900, Curiosity: 600)),
        // ai-build-scorer EP5.2: the shipped ai.v3.json block, same working-set convention as every
        // other field above -- global defaults, and no empire overriding them yet.
        BuildScorer: new BuildScorerTuningBlock(
            Defaults: new BuildScorerTuning(
                NeedFitCurve: FusionRpg.Core.World.Ai.Utility.ResponseCurve.Linear, NeedFitThreshold: 500,
                CounterFitCurve: FusionRpg.Core.World.Ai.Utility.ResponseCurve.Smoothstep, CounterFitThreshold: 500,
                ContinuityCooldownTurns: 3),
            ByEmpire: new Dictionary<string, BuildScorerTuning>(StringComparer.Ordinal)));

    public static readonly VfxTuning DefaultVfx = new(
        SchemaVersion: 1, Version: 1,
        TintMaxStrength: 0.35,
        BurstConeHalfAngle: 0.6, BurstRisingSideFactor: 0.4, BurstDirectionalSideFactor: 0.5,
        Rules: new VfxRulesTuning(
            BurstCap: 24, BurstLifeSeconds: 0.55, FloaterRateLimitSeconds: 0.05, BurstRateLimitSeconds: 0.15,
            GlobalCuePerTickCap: 32, CueQueueCap: 256, CritFontScale: 1.25, CritPopStartScale: 1.5,
            CritPopSettleT: 0.3, AmountTierSmallBelow: 50, AmountTierBigFrom: 200,
            AmountTierSmallScale: 0.9, AmountTierBigScale: 1.15),
        Sustained: new VfxSustainedTuning(
            GlobalCap: 24, PerHostCap: 2, TtlGraceSeconds: 2.0, InfiniteTtlSeconds: 60.0,
            AuraPulseSeconds: 0.3, AuraMaxParticles: 6, SpanScale: 1.5),
        Render: new VfxRenderTuning(
            BurstParticles: 28, ParticleSortingOrder: 80, SortOffsetAboveUnit: 1, SustainedWorldYOffset: 0.25,
            ParticleTextureSize: 64, MarkerEdgeSoftness: 0.14, MarkerGlowStrength: 0.45,
            MarkerSizeScale: 0.24, MarkerYOffsetScale: 0.12,
            ShieldBar: new VfxShieldBarTuning(
                BarWorldWidth: 0.95, BarWorldHeight: 0.12, WorldYOffset: -0.35,
                MaxSegments: 3, Cap: 32, MaxPips: 3),
            TintReassertSeconds: 0.25),
        Identity: new VfxIdentityTuning(SimilarRgbDistanceThreshold: 45, SimilarApplyRgbDistanceThreshold: 35),
        ImpactStamp: new VfxImpactStampTuning(
            PoolCap: 12, LifeSeconds: 0.55, SpanScale: 1.0,
            StartAlpha: 0.9, EndAlpha: 0.0, StartScale: 0.9, EndScale: 1.1, SortOffset: 2));

    public static readonly ActorHudTuning DefaultActorHud = new(
        SchemaVersion: 1,
        Version: 2,
        StatusStripMax: 3,
        HpSliverEnabled: false,
        BadgeMax: 99,
        AnchorKind: "body",
        WorldYOffset: -0.35,
        BarWorldWidth: 0.95,
        BarWorldHeight: 0.12,
        RowOffsetIdentity: 0.30,
        RowOffsetResources: 0.0,
        RowOffsetStatuses: 0.16,
        MaxStackPips: 3,
        EliteTierThreshold: null,
        MagnitudeMidThreshold: 10.0,
        MagnitudeHighThreshold: 30.0);

    // bMilli=0 matches the shipped power-scale.v1.json exactly (plan.md "B=0 first, dial second") —
    // BattleRuleset.BaseHp/Atk/Defense must stay byte-identical to their pre-T2.1 literal formulas
    // for every test in this assembly, the same zero-golden-movement guarantee production gets.
    // Matches gk-core/data/tuning/action-rungs.v1.json exactly (spec-rung-table.md, A12).
    public static readonly RungTable DefaultActionRungs = new(10, new[]
    {
        new RungRow(1,  1, 1, 1, 1000,  1000,  1000, Array.Empty<string>()),
        new RungRow(2,  1, 1, 1, 1323,  1380,  1150, Array.Empty<string>()),
        new RungRow(3,  2, 2, 1, 1750,  1904,  1322, new[] { "scopeSplit", "riderStatus" }),
        new RungRow(4,  2, 2, 1, 2315,  2628,  1521, new[] { "scopeSplit", "riderStatus" }),
        new RungRow(5,  3, 3, 2, 3062,  3627,  1749, new[] { "scopeSplit", "riderStatus", "condition" }),
        new RungRow(6,  3, 3, 2, 4051,  5005,  2011, new[] { "scopeSplit", "riderStatus", "condition" }),
        new RungRow(7,  4, 4, 2, 5359,  6907,  2313, new[] { "scopeSplit", "riderStatus", "condition", "sequence", "consumption" }),
        new RungRow(8,  4, 4, 2, 7090,  9531,  2660, new[] { "scopeSplit", "riderStatus", "condition", "sequence", "consumption" }),
        new RungRow(9,  5, 5, 3, 9379,  13153, 3059, new[] { "scopeSplit", "riderStatus", "condition", "sequence", "consumption", "reaction", "restriction" }),
        new RungRow(10, 5, 5, 3, 12407, 18151, 3518, new[] { "scopeSplit", "riderStatus", "condition", "sequence", "consumption", "reaction", "restriction" }),
    });

    // Matches gk-core/data/tuning/items.v1.json exactly (magic-number fix, 2026-09-05) — the two values are
    // unchanged from their prior ItemNameComposer/RoleFamilyTable consts (3, 5).
    public static readonly ItemsTuning DefaultItems = new(
        SchemaVersion: 1, Version: 1, RareNameThreshold: 3, DefaultMaxTier: 5);

    public static readonly PowerTuning DefaultPower = PowerTuning.Build(
        schemaVersion: 1, version: 1,
        cMilli: PowerTuning.FixedCMilli, bMilli: 400, pinIndex: PowerTuning.FixedPinIndex, pinValue: PowerTuning.FixedPinValue, // T4.2 (power-dial, 2026-08-24): 0 -> 400, matches shipped power-scale.v2.json
        wdMilli: 1000, waMilli: 25000, wrMilli: 250, wzMilli: 1000, wmMilli: 5000, wwMilli: 5000, wfMilli: 25000,
        channels: new Dictionary<string, PowerChannelTuning>
        {
            ["atk"] = new PowerChannelTuning(CMilli: 12_000, PinValue: 92),
            ["defense"] = new PowerChannelTuning(CMilli: 2_000, PinValue: 22),
        });

    // ── workspace roots ───────────────────────────────────────────────────────────────────────────
    // The split moved gk-data/packs/fusion/data/seed and gk-data/packs/fusion/data/generated into a gk-data pack and left gk-core/data/tuning in
    // gk-core, so ONE repo root no longer answers for both. Before the split FindRepoRoot() was
    // correct for every read above; after it, the gk-data/packs/fusion/data/seed reads pointed into gk-core and threw.
    // That is not a cosmetic path problem. This is a [ModuleInitializer]: it runs at ASSEMBLY LOAD,
    // so one unresolvable file failed every test in every assembly that links this file.
    // FusionRpg.Core.ClassSystem.Tests measured 238 of 238 red in the workspace with gk-data
    // PRESENT - a green-looking repository that could not run a single test.
    // KeepverseRoots is the one resolver; these two helpers are the only place the roots are named,
    // and each is resolved once because five reads share it.
    private static string? _contentRoot;
    private static string? _coreRoot;

    /// <summary>Root the authored content registries resolve from: a gk-data pack after the split,
    /// the repository root before it.</summary>
    private static string ContentRoot() => _contentRoot ??= KeepverseRoots.Content();

    /// <summary>Root the authored tuning files resolve from: gk-core after the split.</summary>
    private static string CoreRoot() => _coreRoot ??= KeepverseRoots.Core();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
