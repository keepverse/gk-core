using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// tunables-ssot.md §7.2: "Tests → construct one inline; no fixture files." Mirrors
/// `FusionRpg.Core.Tests`' `ContractTuningTestBootstrap` (a different, internal class in a different
/// assembly, so not directly reusable) — just the hubs this assembly's tests actually touch today
/// (class-system-todo.md P2.5, <c>WebMatchService.AptitudeChannelMods</c>, which transitively needs
/// <see cref="DerivedStatPolicy"/> too: <c>DerivedStatRegistry.CreateDefault()</c> reads
/// <see cref="DerivedStatPolicy.CategoryResistCap"/> in its constructor; T22 (action-todo.md) added
/// <see cref="RungPolicy"/> — <c>WebMatchService.BuildSquad</c> now resolves each specimen's equipped
/// action set via <c>RpgStore.GetLoadoutOrAutoEquip</c>, which ranks candidates through
/// <c>RungPolicy.Table</c>).
/// </summary>
internal static class PowerAndAptitudeTuningTestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        // solid-remediation SR-17 (2026-09-17): the lawn die path asks LawnPermadeathLadder whether a
        // death is permanent, and LawnAttritionTuningHub throws rather than defaulting. Without this,
        // a lawn die event leaves the actor ActiveBound instead of settling at all.
        //
        // ⚠ Permadeath is pinned CERTAIN here, which is NOT the shipped 40permille. The roll derives
        // from (instanceId, matchKey, occurrenceId) and test ids are fresh GUIDs, so under the shipped
        // curve any assertion about a die outcome would be a coin flip. Deterministic default; the
        // probabilistic gate is proven in FusionRpg.Data.Tests' LawnPermadeathGateTests.
        FusionRpg.Core.Battle.Attrition.LawnAttritionTuningHub.Configure(
            new FusionRpg.Core.Battle.Attrition.LawnAttritionTuning(
                SchemaVersion: 1, Version: 1,
                InjuryFloorMilli: 250, InjuryChanceAtFullMilli: 120,
                PermadeathFloorMilli: 700, PermadeathChanceAtFullMilli: 1000));

        PowerTuningHub.Configure(DefaultPower);
        AptitudeTuningHub.Configure(DefaultAptitudes);
        DerivedStatPolicy.Configure(DefaultDerivedStats);
        RungPolicy.Configure(DefaultRungs);
        AuraTuningHub.Configure(DefaultAura);
        // item-content T1 (2026-09-06): the card route now composes a real item name, and
        // `ItemNameComposer.RareNameThreshold` reads this hub. Latent until today for the same reason
        // AuraTuningHub was — nothing in this assembly reached the read.
        FusionRpg.Core.Items.ItemsTuningHub.Configure(DefaultItems);
        // T4.7 step 2 / T4.8 (catalog-runtime) — behaviour-preserving; see the Core.Tests bootstrap's
        // own identical comment (tests/FusionRpg.Core.Tests/ContractTuningTestBootstrap.cs).
        CreatureSpeciesCatalog.ConfigureFromCompiledDefault();
        // D2.16 (party-dungeon) — DelveBattleSessionTests/DelveBattleSessionManagerTests resolve real
        // battles under the `delve` profile (`BattleModeProfileCatalog.Delve`), which throws unless
        // `BattleModeProfileCatalog.Configure` has run. Mirrors Core.Tests' own `ContractTuningTest
        // Bootstrap.DefaultBattle` (tunables-ssot.md §7.2's "construct one inline") — not the shipped
        // file, minimal enough to cover the two rows this assembly's tests actually resolve.
        BattleTuningHub.Configure(DefaultBattle);
        // World projection and district-assault endpoint tests reach these process-global board
        // policies too. Configure them once from the current shipped tuning files so test order
        // cannot turn a missing dependency into a misleading HTTP 500 response.
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        // Creature minting now auto-binds a contract and therefore reads ContractPolicy before the
        // atom-push fixture reaches its subject. Keep this assembly-wide prerequisite here so test
        // order cannot decide whether a server test has a configured contract capacity.
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(
                File.ReadAllText(Path.Combine(tuningDir, "contracts.v1.json"))));
        FusionRpg.Core.Battle.Board.BattleBoardTuningPolicy.Configure(
            FusionRpg.Core.Battle.Board.BattleBoardTuningLoader.Parse(
                File.ReadAllText(Path.Combine(tuningDir, "battle-board.v1.json"))));
        // combat-ai `profile-schema` CAI1.8 (H7): siege.v2.json was the shipped file production read;
        // `stance-wiring` CAI3.1 published `siege.v3.json` (the two dead keys removed) and switched the
        // reader in the same commit, so this bootstrap now reads what production reads. The ten scorer
        // weights live in CombatAiProfilePolicy (DefaultCombatAi below), configured immediately after --
        // BattleRunState's siege construction path reads that hub unconditionally once aiTuning != null,
        // exactly like every other hub this file configures.
        FusionRpg.Core.Battle.Board.SiegeTuningPolicy.Configure(
            FusionRpg.Core.Battle.Board.SiegeTuningLoader.Parse(
                File.ReadAllText(Path.Combine(tuningDir, "siege.v3.json"))));
        FusionRpg.Core.Actions.Ai.CombatAiProfilePolicy.Configure(DefaultCombatAi);
        FusionRpg.Core.Battle.Timeline.ReactionLanePolicy.Configure(
            FusionRpg.Core.Battle.Timeline.ReactionLaneTuningLoader.Parse(
                File.ReadAllText(Path.Combine(tuningDir, "reaction-lane.v3.json"))));
        FusionRpg.Core.World.StructureCatalog.Configure(
            FusionRpg.Core.World.StructureSeed.StructureCorpus.Load(
                Path.Combine(KeepverseRoots.Content(), "data", "seed", "structures")));
        // Same reason: BattleRunState's constructor unconditionally reads ActionTimingPolicy.Tuning.
        // Values transcribed from the real, shipped gk-core/data/tuning/action-timing.v1.json, matching Core
        // .Tests' own ContractTuningTestBootstrap.DefaultActionTiming exactly.
        FusionRpg.Core.Actions.ActionTimingPolicy.Configure(DefaultActionTiming);
        // action-enrich action-base (AE1.1/AE2.2), corrected here: `ActionBaseTuningHub` is the hub
        // `BattleEngine.ApplyBasicAttack` reads on every basic attack, and this assembly's bootstrap
        // never configured it -- so every class that resolved a REAL battle threw
        // `ActionBaseTuningHub.Configure(...) has not run` (18 failures, one cause, found 2026-09-19
        // while lane A ran the boundary for ST4.5c; the hub is lane A's own AE1.1/AE2.2 addition and
        // was wired into Program.cs, RpgHost.cs, Core.Tests, Data.Tests and Injector.Tests but not
        // here). Read from the real shipped file the way those four do, never an inline default: the
        // value is authored, measured data (AE1.4's corrected `basePowerMilli`), and an inline working
        // set would let this assembly's battles drift from what ships.
        FusionRpg.Core.Actions.ActionBaseTuningHub.Configure(
            FusionRpg.Core.Actions.ActionBaseTuningLoader.Parse(
                File.ReadAllText(Path.Combine(tuningDir, "action-base.v2.json"))));
        // Same reason: BattleStatComposer.Compose unconditionally reads BattleRuleset.ResourceTuning
        // for every actor. Transcribed from the real, shipped gk-core/data/tuning/battle-resources.v1.json,
        // matching Core.Tests' own DefaultBattleResources exactly.
        BattleRuleset.ConfigureResources(DefaultBattleResources);
        // The rest of the real-combat-resolve cascade a delve battle test actually walks through
        // OverlayCombatCalculator.Compute -> ApplyBasicAttack. Values match Core.Tests'
        // ContractTuningTestBootstrap exactly (tunables-ssot.md §7.2's "construct one inline").
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(DefaultStats);
        FusionRpg.Core.Combat.CombatPolicy.Configure(DefaultCombat);
        FusionRpg.Core.Status.StatusPolicy.Configure(DefaultStatus);
    }

    /// <summary>
    /// aura-skill-todo.md Phase 5 / TC3 — added 2026-08-30 after a real, diagnosed failure.
    ///
    /// <para><b>The bug.</b> <c>CommanderSnapshotBroadcastTests</c> never configured
    /// <see cref="AuraTuningHub"/>, but <c>GET /api/commanders/{playerId}</c> reaches it —
    /// <c>CommanderEndpoints</c> calls <c>AuraRuntimeEndpoints.ResolveRuntimeForEndpoints</c>, and
    /// <c>AuraTuningHub.Tuning</c> throws <c>InvalidOperationException</c> when unconfigured (there is
    /// deliberately no built-in default). The endpoint 500'd, the developer exception page returned
    /// text, and the test died on <c>'S' is an invalid start of a value</c> — a JSON parse error whose
    /// real cause was three layers away.</para>
    ///
    /// <para><b>Why it only appeared now.</b> Two sibling classes
    /// (<c>AuraRuntimeEndpointsTests</c>, <c>CommanderListEndpointsTests</c>) DO configure the hub in
    /// their own <c>InitializeAsync</c>. Under xUnit's default cross-class parallelism one of them
    /// usually won the race and configured the process-global first, so the gap was invisible. Once
    /// <c>AssemblyParallelism.cs</c> serialised the assembly (to fix a different cross-class static
    /// race), ordering became deterministic and this class could run first — turning an invisible
    /// latent dependency into a reproducible failure. <b>That is the serialisation doing its job:</b>
    /// the dependency was always there, and a deterministic red beats an intermittent green.</para>
    ///
    /// <para>Configuring it here, in the assembly's own <c>[ModuleInitializer]</c> alongside the other
    /// four process-global hubs, fixes every class at once — including ones not yet written — instead
    /// of adding a line to each. Classes that need the REAL shipped <c>aura.v1.json</c> still call
    /// <c>Configure</c> themselves and simply overwrite this; the hub allows reconfiguration.</para>
    /// </summary>
    // Minimal and hand-authored, per tunables-ssot.md §7.2's "construct one inline" convention, and
    // matching DefaultAptitudes/DefaultRungs above. Rungs 7-10 are the only legal span (AuraTuning
    // rejects anything outside it at load), and maxActiveAuras just has to be positive.
    public static readonly AuraTuning DefaultAura = new(
        new Dictionary<int, long> { [7] = 5359, [8] = 7090, [9] = 9379, [10] = 12407 },
        MaxActiveAuras: 1);

    // Matches gk-core/data/tuning/items.v1.json exactly, the same way Core.Tests' own bootstrap does — the two
    // values are unchanged from their prior ItemNameComposer/RoleFamilyTable consts (3, 5).
    public static readonly FusionRpg.Core.Items.ItemsTuning DefaultItems = new(
        SchemaVersion: 1, Version: 1, RareNameThreshold: 3, DefaultMaxTier: 5);

    public static readonly DerivedStatTuning DefaultDerivedStats = new(
        SchemaVersion: 2, Version: 2, CategoryResistCap: 0.95, TurnDefaultSpeed: 100);

    // Minimal, hand-authored -- not the shipped gk-core/data/tuning/action-rungs.v1.json (tunables-ssot.md's
    // "construct one inline" convention). Every specimen in this assembly's tests holds zero action
    // grants today (T22's own note: no production caller grants actions to a creature instance yet), so
    // AutoEquip.Select never actually ranks a real candidate against this table -- it only needs to be
    // a STRUCTURALLY valid one-rung table so RungPolicy.Table does not throw "not configured".
    public static readonly RungTable DefaultRungs = new(
        cap: 1, rows: new[] { new RungRow(1, 1, 1, 1, 1000, 1000, 1000, Array.Empty<string>()) });

    // Same working values as gk-core/data/tuning/power-scale.v2.json (T4.2: bMilli=400) — the shipped dial,
    // not the historical bMilli=0 baseline ContractTuningTestBootstrap pins for its own reasons.
    // cMilli/pinIndex/pinValue are literal here, not PowerTuning.FixedCMilli/FixedPinIndex/FixedPinValue
    // (mirrors them exactly) -- those are `internal`, visible only to FusionRpg.Core.Tests
    // (FusionRpg.Core.csproj's one InternalsVisibleTo grant), not this assembly.
    public static readonly PowerTuning DefaultPower = PowerTuning.Build(
        schemaVersion: 1, version: 2,
        cMilli: 80_000, bMilli: 400, pinIndex: 20, pinValue: 680,
        wdMilli: 1000, waMilli: 25000, wrMilli: 250, wzMilli: 1000, wmMilli: 5000, wwMilli: 5000, wfMilli: 25000,
        channels: new Dictionary<string, PowerChannelTuning>
        {
            ["atk"] = new PowerChannelTuning(CMilli: 12_000, PinValue: 92),
            ["defense"] = new PowerChannelTuning(CMilli: 2_000, PinValue: 22),
        });

    // Minimal, hand-authored -- not the 486-edge shipped file (tunables-ssot.md's "construct one
    // inline" convention). Enough to exercise AptitudeChannelMods' plumbing; the real coefficients are
    // proven elsewhere (AptitudeTuningTests.ParsesTheShippedFile in FusionRpg.Core.Tests).
    // Minimal, hand-authored (tunables-ssot.md §7.2) -- only the rows/keys this assembly's tests
    // actually resolve: `classic-round` (the engine's own no-profile default) and `delve`
    // (DelveBattleSessionTests/DelveBattleSessionManagerTests). W/WReact/PassQuantum/MaxPoints copy
    // the real shipped battle.v{n}.json's `delve` row shape (spec-delve-battle-profile.md §1).
    public static readonly BattleTuning DefaultBattle = new(
        SchemaVersion: 1, Version: 1,
        RoundDurationMs: 1000, MaxRounds: 50,
        PrimaryAffinityDivisor: 4, SecondaryAffinityDivisor: 8,
        Traits: new Dictionary<string, TraitMagnitudes>(StringComparer.Ordinal),
        TimelineProfiles: new Dictionary<string, TimelineProfileTuning>(StringComparer.Ordinal)
        {
            ["classic-round"] = new(W: 1, WReact: 0, PassQuantum: 1, MaxPoints: null),
            ["delve"] = new(W: 4, WReact: 0, PassQuantum: 1, MaxPoints: 2),
            ["siege"] = new(W: 2, WReact: 0, PassQuantum: 1, MaxPoints: null),
        },
        HybridSecondaryWeightMilli: 300,
        LoopGuardRoundMultiple: 4000,
        SpeciesTempoReferenceIntervalMs: 1500);

    // Matches Core.Tests' ContractTuningTestBootstrap.DefaultActionTiming exactly (transcribed from
    // the real, shipped gk-core/data/tuning/action-timing.v1.json).
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

    // Matches the real, shipped gk-core/data/tuning/battle-resources.v1.json exactly (Core.Tests' own
    // DefaultBattleResources). `hp` is absent on purpose -- its max mirrors BattleActorSetup.MaxHp.
    public static readonly FusionRpg.Core.Battle.BattleResourceTuning DefaultBattleResources = new(
        SchemaVersion: 1, Version: 1,
        PoolShareMilli: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["stamina"] = 500, ["hunger"] = 500, ["spirit"] = 500, ["qi"] = 500, ["poise"] = 500,
        },
        // Regen share deliberately all-zero -- ambient fixture keeps the pre-T11 baseline
        // (lawn-combat-wire T11, spec-lawn-combat-calibration.md); the real `stamina=50` share
        // now lives in the shipped `battle-resources.v2.json`.
        RegenPerSecondShareMilli: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["stamina"] = 0, ["hunger"] = 0, ["spirit"] = 0, ["qi"] = 0, ["poise"] = 0,
        });

    public static readonly StatsTuning DefaultStats = new(
        SchemaVersion: 1, Version: 1,
        MinimumInterval: 0.01, MatchupShareK: 0.25,
        AccuracyScale: 100.0, CritRateScale: 100.0, CritDamageScale: 100.0, Steepness: 1.0);

    public static readonly FusionRpg.Core.Combat.CombatTuning DefaultCombat = new(
        SchemaVersion: 1, Version: 1,
        ProcDepthLimit: 6, DefaultMaxTargets: 8,
        AreaDefaultSquareSize: 3, AreaDefaultRectangleWidth: 3, AreaDefaultRectangleHeight: 3,
        DotDefaultPeriodMs: 1000, DotDefaultDurationMs: 5000,
        PierceScale: 10.0, AmpScale: 10.0,
        BlockCapPermille: 950, ParryCapPermille: 950, AvoidanceBandCapPermille: 950,
        ReflectRateScale: 10.0, ReflectShareScale: 100.0, ParryNeutralShareKPm: 500,
        DefenseShape: FusionRpg.Core.Combat.DefenseShape.Divisive, DefenseDivisorK: 0.45,
        ReflectReadsPostShield: true, AmpShape: FusionRpg.Core.Combat.AmpShape.Reciprocal);

    public static readonly FusionRpg.Core.Status.StatusTuning DefaultStatus = new(
        SchemaVersion: 1, Version: 1,
        ApplyScaleK: 100.0, ApplyScaleFloor: 1.0,
        ResistFromPowerRatio: 1.0, MinNetFactor: 0.0, MaxNetFactor: 10_000.0, NetFactorScale: 10.0,
        ProgressionPowerStubDefault: 1.0, ProcDepthLimitDefault: 6, ApplySteepnessDefault: 1.0,
        ApplyShape: FusionRpg.Core.Status.StatusApplyShape.Sigmoid, ApplyOffsetK: 0.0);

    public static readonly AptitudeTuning DefaultAptitudes = AptitudeTuningLoader.Parse("""
        {
          "schemaVersion": 1, "version": 1,
          "grant": { "aptitudePointsPerTheta": 3, "skillPointsPerTheta": 1 },
          "pointEconomy": { "aptitudePointsPerThetaMilliByScope": { "commander": 3, "creatureType": 4, "aspect": 4, "uniqueCreature": 6 }, "respecPrice": 10 }, "guardEconomy": { "flatCommitCost": 50, "absorbDrainSharePermille": 300, "riposteShareCapPermille": 400 }, "mitigation": { "scaleMilli": 1000, "families": ["combat.defense", "combat.dodge", "combat.parry", "combat.block", "combat.absorption", "combat.heal"] },
          "read": { "contest": { "spanPoints": 100.0, "shareExponentMilli": 1000 }, "magnitude": { "shareExponentMilli": 1000 }, "layerWeightMilliByScope": {"commander":1000,"creatureType":1000,"aspect":1000,"uniqueCreature":1000} },
          "recovery": { "scaleMilli": 374, "targetRecoveryShareMilli": 670, "families": ["resource.regen"] },
          "familyRead": { "combat.power": "magnitude" },
          "edges": [ { "channel": "combat.power.omni", "source": "Might", "kMilli": 2200 } ]
        }
        """);

    // combat-ai `profile-schema` CAI1.8: the SAME identity shape gk-core/data/tuning/combat-ai.v1.json ships --
    // constructed inline (tunables-ssot.md §7.2: "tests construct one inline; no fixture files"), the
    // shipped siege.v1.json ten weights carried at "siege/default" and the root "*/default", argmax/
    // keepPct 1000/no reserves/guards off/anti-repeat off/personality 0 everywhere, tierByActorClass
    // total over both classes. Matches Core.Tests' ContractTuningTestBootstrap.DefaultCombatAi exactly.
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

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
