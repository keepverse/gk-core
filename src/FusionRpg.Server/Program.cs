using System.Text.Json;
using FusionRpg.CheatCore;
using FusionRpg.Contracts;
using FusionRpg.Core;
using FusionRpg.Core.Overlay;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using FusionRpg.Data.Seed;
using FusionRpg.Server;
using FusionRpg.Server.Gates;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

var builder = WebApplication.CreateBuilder(args);
var urls = Environment.GetEnvironmentVariable("FUSIONRPG_URLS");
var listenUrl = string.IsNullOrWhiteSpace(urls) ? "http://127.0.0.1:5088" : urls.Trim();

// The verbs a missing endpoint can be asked for, declared once because the terminator has to cover
// all of them: a terminator registered for GET alone leaves POST /api/nothing reachable by the SPA
// fallback, which is the same defect one verb narrower.
var MissingApiMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
builder.WebHost.UseUrls(listenUrl);
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FUSIONRPG_DATA")))
    builder.WebHost.UseContentRoot(AppContext.BaseDirectory);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(origin =>
         origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
         || origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials()));
builder.Services.AddSignalR().AddJsonProtocol();

// tunables-ssot.md §7.2: hosts load and inject; Core stays file-I/O-free. Migrated domains are
// copied next to the exe by FusionRpg.Server.csproj (M.1 contracts, M.2 loam).
var tuningDir = Path.Combine(AppContext.BaseDirectory, "data", "tuning");
FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
    FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "contracts.v1.json"))));
FusionRpg.Core.World.Loam.LoamPolicy.Configure(
    FusionRpg.Core.World.Loam.LoamTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "loam.v5.json"))));
// wonder-wire §Design 1 (plan Task 4A.4): `GET /api/world/catalog` projects
// `WonderPolicy.ExistenceCapFor` per structure row — without this the route throws
// "Configure(...) has not run" (loud, never a default). Found during implementation: no prior
// caller configured it because nothing read it over HTTP before this wire.
FusionRpg.Core.World.WonderPolicy.Configure(
    FusionRpg.Core.World.Loam.WonderTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "loam-relics-wonders.v1.json"))));
FusionRpg.Core.World.LegionCargo.ScopedInventoryPolicy.Configure(
    FusionRpg.Core.World.LegionCargo.ScopedInventoryTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "scoped-inventory.v1.json"))));
var worldTuning = FusionRpg.Core.World.WorldTuningLoader.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "world.v6.json")));
FusionRpg.Core.World.WorldTuningHub.Configure(worldTuning);
FusionRpg.Core.World.Loam.WorldSpawnTuningHub.Configure(
    FusionRpg.Core.World.Loam.WorldSpawnTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "world-spawn.v1.json"))));
FusionRpg.Core.World.Growth.RecruitPolicy.Configure(worldTuning.Growth);
FusionRpg.Core.Creatures.SoulEarnPolicy.Configure(
    FusionRpg.Core.Creatures.SoulEarnTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "souls.v1.json"))));
FusionRpg.Core.Creatures.Patron.PatronPolicy.Configure(
    FusionRpg.Core.Creatures.Patron.PatronTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "patron.v1.json"))));
FusionRpg.Core.Match.LawnDeployEventsTuningHub.Configure(
    FusionRpg.Core.Match.LawnDeployEventsTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "lawn-deploy-events.v1.json"))));
// creature-lawn-deploy unique-deploy-cap (lawn LW5.1, owner ruling D6): the concurrency limits the
// deploy admission rule reads. Loaded here with the file's reader in the same commit (H7) — the store's
// own gate call is the caller this file's owner still owes, and it lives in FusionRpg.Data.
FusionRpg.Core.Match.LawnDeployLimitsTuningHub.Configure(
    FusionRpg.Core.Match.LawnDeployLimitsTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "lawn-deploy.v1.json"))));
FusionRpg.Core.Match.Ai.ZombossDeployTuningHub.Configure(
    FusionRpg.Core.Match.Ai.ZombossDeployTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "zomboss-deploy-ai.v1.json"))));
FusionRpg.Core.Combat.Shield.ShieldPolicy.Configure(
    FusionRpg.Core.Combat.Shield.ShieldTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "shield.v1.json"))));
FusionRpg.Core.Combat.CombatPolicy.Configure(
    FusionRpg.Core.Combat.CombatTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "combat.v1.json"))));
FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
    FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "fusion.v2.json"))));
FusionRpg.Core.Status.StatusPolicy.Configure(
    FusionRpg.Core.Status.StatusTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "status.v1.json"))));
FusionRpg.Core.Stats.Derived.DerivedStatPolicy.Configure(
    FusionRpg.Core.Stats.Derived.DerivedStatTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "derived-stats.v2.json"))));
FusionRpg.Core.Hud.ActorHudTuningHub.Configure(
    FusionRpg.Core.Hud.ActorHudTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "actor-hud.v7.json"))));
FusionRpg.Core.ActorSurface.ActorSurfaceCatalogHub.ConfigureAll(
    FusionRpg.Core.ActorSurface.AptitudeSurfaceCatalogLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "aptitude-catalog.v1.json"))),
    FusionRpg.Core.ActorSurface.DerivedStatSurfaceCatalogLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "derived-stat-catalog.v3.json"))),
    FusionRpg.Core.ActorSurface.StatusSurfaceCatalogLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "status-catalog.v1.json"))),
    FusionRpg.Core.ActorSurface.ResourceSurfaceCatalogLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "resource-catalog.v1.json"))),
    // v2, not v1: v2 is a strict superset (same ids, ordinals, colors, displayNames; `hudGlyph` is
    // the only added key) and it is the revision the Injector host already reads (RpgHost.cs:133).
    // On v1 every row's HudGlyph is null, so the actor-surface fan-in this Server serves carried no
    // glyph kind for any element and the FE's actorHudElementArtUrl was undefined for all of them.
    // A host-side version choice, never a data edit: element-catalog is published, not hand-edited
    // (tunables-ssot.md T4/T7.2, gk-core/tools/tuning/publish.py --add-element-hud-glyph).
    FusionRpg.Core.ActorSurface.ElementSurfaceCatalogLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "element-catalog.v2.json"))),
    FusionRpg.Core.ActorSurface.ActorSheetSurfaceCatalogLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "actor-sheet.v1.json"))));
FusionRpg.Core.Overlay.OverlayTuningHub.Configure(
    FusionRpg.Core.Overlay.OverlayTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "overlay.v4.json"))));
FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
    FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "stats.v1.json"))));
FusionRpg.Core.Expeditions.ExpeditionTuningHub.Configure(
    FusionRpg.Core.Expeditions.ExpeditionTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "expeditions.v2.json"))));
// species-gear-chain T31 (creature-drop-tables E3a): the rung -> shard map ExpeditionResolver's
// plan-time mint reads. Core never touches the file directly (tunables-ssot.md §7.2) -- the host
// loads and injects it, in the same commit that creates it (v1, no reader switch owed).
FusionRpg.Core.Expeditions.CreatureYieldTuningHub.Configure(
    FusionRpg.Core.Expeditions.CreatureYieldTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "creature-yield.v1.json"))));
// party-dungeon D1.4: registries load first (pure, no tuning needed); DungeonTuningHub and
// EncounterTuningHub configure next, cross-checked against those registries at parse time; then
// DungeonRegistryHub last -- RoomKindDef.WeightMilli joins DungeonTuningHub at first property
// read, so the tuning hub must already be configured by the time any catalog row is touched.
var dungeonRegistryDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "dungeon", "_registry");
var dungeonRegistries = FusionRpg.Core.Dungeon.Registry.DungeonRegistryLoader.LoadAll(dungeonRegistryDir);
FusionRpg.Core.Dungeon.Tuning.DungeonTuningHub.Configure(
    FusionRpg.Core.Dungeon.Tuning.DungeonTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "dungeon.v3.json")), dungeonRegistries));
var creatureThreatTuning = FusionRpg.Core.Creatures.Generation.CreatureThreatTuningLoader.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "creature-threat.v1.json")));
FusionRpg.Core.Dungeon.Tuning.EncounterTuningHub.Configure(
    FusionRpg.Core.Dungeon.Tuning.EncounterTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "encounter.v1.json")), dungeonRegistries,
        // tier-propagation-contract T-2: the threat ladder is read from its own tuning file, never restated.
        creatureThreatTuning.RungIds));
// spec-species-rank.md §6 (Task 8 boot wire): the rank floors' production switch. The Server is
// the ONE host that configures them — every enforcing gate runs on this path (the fusion
// transaction in RpgStore.Fusion.cs, the expedition/wave resolvers, the delve wild endpoints),
// while the Injector owns no rank-gate caller, so a second Configure there would be dead weight.
// A file missing a gate is REFUSED here at boot (Configure throws naming the gate vocabulary),
// never defaulted; at the shipped bottom rung this is a pass-through (zero behavior change).
FusionRpg.Core.Creatures.CreatureRankFloors.Configure(
    FusionRpg.Core.Creatures.CreatureRankTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "creature-rank.v1.json")),
        creatureThreatTuning.RungIds,
        // tier-propagation-contract T-2: the rarity ladder's ids come from its declaring enum, never restated.
        FusionRpg.Core.Creatures.CreatureRarityLadder.All
            .Select(r => FusionRpg.Core.Creatures.CreatureRarityIds.ToId(r)).ToList()));
FusionRpg.Core.Dungeon.Registry.DungeonRegistryHub.Configure(dungeonRegistries);
// LayoutTemplateHub after DungeonRegistryHub -- Load reads BandCatalog.All/RaidModeCatalog.All,
// both configured by the call above (D4.30's real prerequisite chain, 2026-09-07: closes D4.19/
// D4.21's own named "no LayoutTemplateCatalog exists" gap now that real content exists).
{
    var layoutRows = FusionRpg.Core.Delve.Roll.LayoutSeedFile.LoadAll(
        Path.Combine(AppContext.BaseDirectory, "data", "seed", "dungeon", "layouts"));
    var layoutLoad = FusionRpg.Core.Delve.Roll.LayoutTemplateCatalog.Load(
        layoutRows, FusionRpg.Core.Dungeon.Registry.BandCatalog.All, FusionRpg.Core.Dungeon.Registry.RaidModeCatalog.All);
    if (layoutLoad.Rejections.Count > 0)
        throw new InvalidOperationException(
            "data/seed/dungeon/layouts/*.json failed to load: " + string.Join("; ", layoutLoad.Rejections));
    FusionRpg.Core.Delve.Roll.LayoutTemplateHub.Configure(layoutLoad.Catalog);
}
FusionRpg.Core.SimDefaults.Configure(
    FusionRpg.Core.SimTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "sim.v1.json"))));
// zomboss-commander-clock SP7.1 (H7): switched to v2 in the same commit that published it --
// v2 adds awards.zombossRunVictoryXp/zombossRunDefeatXp (SP7.2's own award pair). v1 stays on
// disk untouched; every other v1.json reader is an unrelated test fixture, never re-pointed
// (progression's own required keys are unchanged; the two new ones are absence-tolerant, see
// XpAwardsTuning.ZombossRunVictoryXp's own doc comment for why).
FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
    FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "progression.v3.json"))));
// passive-tree A1: server-only for now — no consumer reads PassiveTreeTuningHub.Tuning yet (B1+
// build the first ones). Wired here ahead of need so every later task finds it already configured,
// matching AptitudeTuningHub's own precedent.
FusionRpg.Core.PassiveTree.State.PassiveTreeTuningHub.Configure(
    FusionRpg.Core.PassiveTree.State.PassiveTreeTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "passive-tree.v1.json"))));
// species-build T1.1: server-only, mirroring AptitudeTuningHub's own shape — the injector never
// computes a species level, so it never loads this file.
FusionRpg.Core.Progression.SpeciesProgressionTuningHub.Configure(
    FusionRpg.Core.Progression.SpeciesProgressionTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "species-progression.v1.json"))));
// species-build T1.5: server-only, mirroring SpeciesProgressionTuningHub's own shape — the generation
// tool (gk-forge/tools/CreatureBuildPlanGen) reads this file directly and never goes through this hub.
// respec-free-counter EP4.4: the SAME read wires the empire-level grant amount, because
// `freeRespecsPerEmpireLevel` lives in this file and nowhere else — one parse, two hubs, so the Data
// credit never opens a tuning file of its own. The loader only returns null for a pre-v6 document, and
// v6 is required to carry the key, so a null here is a wiring defect rather than a legal state.
{
    var speciesBuildTuning = FusionRpg.Core.Creatures.Generation.SpeciesBuildTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "species-build.v6.json")));
    FusionRpg.Core.Creatures.Generation.SpeciesBuildTuningHub.Configure(speciesBuildTuning);
    FusionRpg.Core.Progression.EmpireLevelTuningHub.Configure(
        new FusionRpg.Core.Progression.EmpireLevelTuning(
            speciesBuildTuning.FreeRespecsPerEmpireLevel
                ?? throw new InvalidOperationException(
                    "species-build.v6.json must carry 'freeRespecsPerEmpireLevel'")));
}
// species-build T2.1: the committed, generated plan (gk-forge/tools/CreatureBuildPlanGen owns writing it) —
// `SpeciesBuildPlanCatalog` is `creature-type-allocation`'s runtime reader for it. `tuningDir`'s parent is
// ".../data", so "generated/creatures" (not "gk-data/packs/fusion/data/generated/creatures") — same relative shape Program.cs
// already uses for the generated-tree convention.
FusionRpg.Core.Creatures.Generation.SpeciesBuildPlanCatalog.Configure(
    FusionRpg.Core.Creatures.Generation.SpeciesBuildPlanReader.Parse(
        File.ReadAllText(Path.Combine(
            Directory.GetParent(tuningDir)!.FullName, "generated", "creatures", "_species-build-plan.json"))));
// species-build T4.4: ⛔ server-only — the Zomboss exists on battle and expedition surfaces, never the
// lawn, so wiring this into the injector would be dead weight.
FusionRpg.Core.Battle.Ai.ZombossAdaptiveTuningHub.Configure(
    FusionRpg.Core.Battle.Ai.ZombossAdaptiveTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "zomboss-adaptive.v1.json"))));
FusionRpg.Core.Battle.BattleTuningHub.Configure(
    FusionRpg.Core.Battle.BattleTuningLoader.Parse(
        // v2 -> v3 (battle-tempo tempo-content, 2026-09-05): adds speciesTempo.referenceIntervalMs.
        // v3 also closes a pre-existing, unrelated gap -- ruleset.loopGuardRoundMultiple, which
        // BattleTuningLoader already required (base-defense F2 WIP) but no version of this file ever
        // carried; see battle.v3.json's own _meta.noteV3LoopGuard.
        // v3 -> v4 (battle-tempo reaction-lane RL1, 2026-09-05): timeline.profiles.hybrid-atb.wReact
        // 0 -> 1, published via gk-core/tools/tuning/publish.py (no hand-edit). classic-round/galaxy-sync
        // stay at 0. Config-only -- ReactionLane has no production caller yet (D14's own pattern), so
        // this is a tuning row change with no observable effect until a caller exists, matching the
        // module's own "buy the option, don't pay for the feature" framing.
        // v4 -> v5 (combat-unification Phase 7 F1, 2026-09-07, owner decision): hybrid.
        // secondaryWeightMilli 0 -> 300, published via gk-core/tools/tuning/publish.py. Wave E3's mechanism
        // goes live -- an actor with a real ElementSecondary now carries a genuine two-component
        // attack payload instead of the pre-F1 single-primary shape.
        File.ReadAllText(Path.Combine(tuningDir, "battle.v5.json"))));
// battle-tempo battle-resources (2026-09-05): the per-resource share of BaseHp that
// BattleStatComposer seeds every actor's six pools from. Before this, every battle actor held all
// six pools at max 0, so no action in a battle could cost anything and reaction-lane's counter
// declined every time. Its own file rather than a battle.v{n}.json section because publish.py's
// `set` path refuses to invent keys, and the file forbids hand-editing
// (spec-battle-resources.md §2.2a).
// v1 -> v2 (lawn-combat-wire T11, spec-lawn-combat-calibration.md, 2026-09-14): adds
// `regenPerSecondShareMilli` — stamina now regenerates (the other four ids stay explicit 0s),
// published via `gk-core/tools/tuning/publish.py battle-resources --add-regen-block`, no hand-edit. v1
// stays on disk for revert; every other reader of this file (tests, tools) still pins v1 directly
// and gets the old byte-identical zero-regen behaviour, since BattleResourceTuningLoader.Parse
// treats a missing regen block as an implicit all-zero share.
// solid-remediation SR-17 (2026-09-17): the lawn's attrition curve, loaded at startup. Before this
// NOTHING called LawnAttritionTuningHub.Configure anywhere in src/ or tests/ -- the hub and the tuning
// file both shipped and neither was ever read, which is the second half of the same dark-carrier debt
// SR-17 records. The lawn death path reads this to decide whether a death is permanent, so it must be
// configured before the first plant.die/zombie.die is observed.
FusionRpg.Core.Battle.Attrition.LawnAttritionTuningHub.Configure(
    FusionRpg.Core.Battle.Attrition.LawnAttritionTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "lawn-attrition.v2.json"))));
// lawn LW1.1 (spec-rider-default-on.md): the perf ceiling every lawn perf gate reads, replacing the
// <=6% figure that lived in a plan document and in commit messages. Loaded here for the same reason
// the SR-17 line above is: a tuning file with no production reader is dark config, and the module that
// reads this one is the lawn perf gate. `ceiling.minFpsRatioOfOff` is null while unmeasured — a
// declaration, never a satisfied gate.
FusionRpg.Core.Diagnostics.LawnPerfBudgetTuningHub.Configure(
    FusionRpg.Core.Diagnostics.LawnPerfBudgetTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, FusionRpg.Core.Diagnostics.LawnPerfBudgetFiles.Current))));
// solid-enforcement SE3.14 (2026-09-18): cache decay + retrieval moved from RpgStore constants into
// this domain file. It is also the file's first production loader: before this, only tests parsed it.
FusionRpg.Core.Items.Materials.DeploymentHierarchyTuningHub.Configure(
    FusionRpg.Core.Items.Materials.DeploymentHierarchyTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "deployment-hierarchy.v5.json"))));
// species-gear-chain T41: the host loads craft-assurance.v1.json in the same commit that creates it.
// No consumer yet (T42's `assure` spend, T44's `repair` coverage) -- Configure() here is what lets
// either read CraftAssuranceTuningHub.Tuning later without a further Program.cs change.
FusionRpg.Core.Items.Mutation.CraftAssuranceTuningHub.Configure(
    FusionRpg.Core.Items.Mutation.CraftAssuranceTuning.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "craft-assurance.v1.json"))));
// species-gear-chain T59 (lane sgc-4, 2026-09-21; RE-LANDED 2026-09-22): the `items` domain had NO
// production reader at all. The file shipped (module 8's magic-number fix, 2026-09-05), ItemsTuningLoader
// parses it, and a sweep of every ItemsTuningHub.Configure call site returned only test bootstraps
// (Core.Tests.Shared/ContractTuningTestBootstrap.cs:128, Data.Tests:166, E2E.Tests:136 and
// Server.Tests/PowerAndAptitudeTuningTestBootstrap.cs:50) -- so `GET /api/items/{instanceId}/card`
// (ItemCardEndpoints.cs:715) threw InvalidOperationException the moment an item with a rolled affix
// reached ItemNameComposer.Compose, which reads RareNameThreshold off this hub
// (RpgStore.ItemCard.cs:329 -> ItemNameAssembly.Compose -> ItemNameComposer.cs:32). The file itself says
// `hosts read data/tuning/items.v{n}.json and call Configure once at startup`. Same reader-in-the-host
// shape, and the same class of debt, as SR-17's lawn-attrition line above.
//
// ⛔ This block was DROPPED once already by a cross-lane merge resolution (`8b81e395d`, "Merge branch
// 'features/mega-merge' into cmdc/cai4": 2 occurrences before, 0 after), which silently restored the live
// defect. `gk-core/tests/FusionRpg.Server.Tests/ContentBootStartupWiringTests.cs` now pins this wiring so the next
// bad resolution fails there instead of in play.
FusionRpg.Core.Items.ItemsTuningHub.Configure(
    FusionRpg.Core.Items.ItemsTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "items.v1.json"))));
FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(
    FusionRpg.Core.Battle.BattleResourceTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "battle-resources.v2.json"))));
FusionRpg.Core.Battle.Board.SiegeTuningPolicy.Configure(
    FusionRpg.Core.Battle.Board.SiegeTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "siege.v3.json"))));
// combat-ai profile-schema (CAI1.8, H7): the publish (siege.v1 -> siege.v2 above, removing the ten
// AI weights) and this reader switch land in the same commit -- a publish without its reader is a
// silent behaviour change on the next deploy.
FusionRpg.Core.Actions.Ai.CombatAiProfilePolicy.Configure(
    FusionRpg.Core.Actions.Ai.CombatAiTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, FusionRpg.Core.Actions.Ai.CombatAiTuningFiles.Current))));
// combat-ai `replay-identity` (CAI2.2): the version-addressed source those same published files are
// loaded into, so a match pinned to an older `combat-ai.v{n}.json` can be re-resolved under the set it
// actually used instead of whatever has been published since. Constructed HERE, beside the policy
// reader and before the DI registrations, for two reasons:
//   - the load-time refusals (a name/version disagreement, a version published twice, an empty
//     directory) then fire at boot, before any match can run -- which is what the class's own contract
//     promises;
//   - the boot check below compares the two against each other, which is only possible with both in
//     hand.
// ⛔ The POLICY still resolves through `CombatAiTuningFiles.Current`, never through
// `CombatAiProfileFiles.Current` ("newest on disk"). `CAI-F1` made the revision constant the thing both
// hosts read, precisely so an H7 publish moves the filename and its readers together; auto-picking the
// newest here would let a `v3` that no host has adopted become the set a fresh match is stamped with.
var combatAiProfileFiles = new CombatAiProfileFiles(tuningDir);
// The invariant that makes the stamp truthful rather than merely present: the revision the process
// RESOLVES profiles under is the newest published one, so `StampFor(source)` names the set the battle
// actually ran on. Were a publish to land without its reader switch, this fails at boot instead of
// stamping every new row with a version the policy hub is not using.
if (combatAiProfileFiles.Current.Version
    != FusionRpg.Core.Actions.Ai.CombatAiProfilePolicy.TuningVersion)
    throw new FusionRpg.Core.Actions.Ai.CombatAiTuningRejection(
        $"combat-ai: {FusionRpg.Core.Actions.Ai.CombatAiTuningFiles.Current} is the revision the host " +
        $"resolves profiles under (v{FusionRpg.Core.Actions.Ai.CombatAiProfilePolicy.TuningVersion}) but the " +
        $"newest published file is v{combatAiProfileFiles.Current.Version}. A publish and its reader " +
        "switch must land in the same commit (H7 / CAI-F1).");
// A10 battle-board (spec-battle-board.md §1): a normal encounter's own seeded, bounded board size --
// siege gets its GridSpec from DistrictLayout, never from this roll, so this is a separate tunable.
FusionRpg.Core.Battle.Board.BattleBoardTuningPolicy.Configure(
    FusionRpg.Core.Battle.Board.BattleBoardTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "battle-board.v1.json"))));
// base-defense-todo.md 25.4: structure-catalog-import reads the committed corpus instead of the C#
// literal — the same AppContext.BaseDirectory-relative pattern the dungeon registry above already
// uses (`dungeonRegistryDir`), with the matching .csproj copy rule (FusionRpg.Server.csproj) so a
// published build finds it next to the exe, not just in a local dev checkout.
FusionRpg.Core.World.StructureCatalog.Configure(
    FusionRpg.Core.World.StructureSeed.StructureCorpus.Load(
        Path.Combine(AppContext.BaseDirectory, "data", "seed", "structures")));
FusionRpg.Core.Creatures.SummoningTuningHub.Configure(
    FusionRpg.Core.Creatures.SummoningTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "summoning.v1.json"))));
FusionRpg.Core.Aura.AuraTuningHub.Configure(
    FusionRpg.Core.Aura.AuraTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "aura.v1.json"))));
// ⛔ ai.v2.json -> ai.v3.json RE-LANDED 2026-09-22 (lane sgc-4). EP5.2 (`2114445b6`, "publish ai.v3 with the
// buildScorer block and switch every reader in the same commit") made this switch, and the cross-lane merge
// resolution `8b81e395d` ("Merge branch 'features/mega-merge' into cmdc/cai4") silently reverted it to ai.v2
// while LEAVING the loader's requirement in place -- `WorldAiTuningLoader.Parse` now demands `buildScorer`
// (`WorldAiTuning.cs:81`), which only v3 carries, so the server could not boot at all: the E2E harness threw
// `WorldAiTuningRejection: ai tuning: missing or non-object 'buildScorer'` at this line. Restored to the
// latest published revision; `ContentBootStartupWiringTests` now pins the latest-revision contract for this
// domain and for `items` so a bad resolution fails in a test instead of at boot.
FusionRpg.Core.World.Ai.WorldAiPolicy.Configure(
    FusionRpg.Core.World.Ai.WorldAiTuningLoader.Parse(
        // ai-build-scorer EP5.2 (H7): the publish (v2 -> v3, adding `buildScorer`) and this reader move in
        // the same change, and the loader REQUIRES the block -- reading v2 here cannot boot the host.
        // Re-asserted after the features/mega-merge merge reverted this one line.
        File.ReadAllText(Path.Combine(tuningDir, "ai.v3.json"))));
FusionRpg.Data.Policies.SealedCompactionPolicy.Configure(
    FusionRpg.Data.Policies.DataTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "data.v1.json"))));
FusionRpg.Core.Power.PowerTuningHub.Configure(
    FusionRpg.Core.Power.PowerTuningLoader.Parse(
        // T4.2 (power-dial, 2026-08-24): v1 (bMilli=0) -> v2 (bMilli=400). v1 stays on disk --
        // reverting is pointing this back at power-scale.v1.json and un-bumping RulesetVersion, no
        // other code change (PS-7).
        File.ReadAllText(Path.Combine(tuningDir, "power-scale.v2.json"))));
FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Configure(
    FusionRpg.Core.Stats.Aptitudes.AptitudeTuningLoader.Parse(
        // class-system-todo.md P8.2/P8.3 (2026-08-27): v1 -> v2. Phase 0 six-resource coverage (2026-09-02): v2 -> v3, then v3 -> v4 (0.8: combat.heal.power generalised to resource.restore.{resource}) -- 32 edges added so every (family x resource) cell is fed, closing P7.2's poise gap. v2 stays on disk -- reverting is pointing this back at aptitudes.v2.json. passive-tree C6 (2026-09-06): v5 -> v6, pointEconomy gains skillPointsPerThetaMilliByScope (D34) -- v5 stays on disk. passive-tree D55 (2026-09-06): v6 -> v7, published via gk-core/tools/tuning/publish.py -- creatureType/aspect/uniqueCreature skillPointsPerThetaMilliByScope moved from the borrowed-placeholder {4,4,6} to the {3,4,4,6}-ratio-derived {15,15,22} against the already-settled commander=11 (spec-tree-state.md open question 3) -- v6 stays on disk. solid-enforcement retire-atk (2026-09-18, R3): v8 -> v9, removes the two reader-less Might/Ferocity -> progression.bonus.atk edges. species-progression SP6.0 (2026-09-19, R21) republished on top of it (empire-progression-20260920's first merge had spliced the same content directly into v9 by hand; superseded by this real publish): v9 -> v10, published via gk-core/tools/tuning/publish.py -- adds read.layerWeightMilliByScope (commander 500 / creatureType 667 / aspect 667 / uniqueCreature 1000), applied by step 6.1 -- v9 stays on disk.
        File.ReadAllText(Path.Combine(tuningDir, "aptitudes.v10.json"))));
// aptitude-sheet AS-3.1: soft max presets (E8) — separate file so aptitudes.v{n} is not republished for a library-size knob.
FusionRpg.Core.Stats.Aptitudes.AptitudePresetTuningHub.Configure(
    FusionRpg.Core.Stats.Aptitudes.AptitudePresetTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "aptitude-presets.v2.json"))));
// build-preset BP1.10 (spec-preset-store.md, H7): the build-preset library's own soft max, loaded
// here in the same commit that creates the file -- a published file nothing reads is dark. This is
// the first version of a NEW tuning domain, so it is authored with its module; every later revision
// goes through gk-core/tools/tuning/publish.py.
FusionRpg.Core.BuildPresets.BuildPresetTuningHub.Configure(
    FusionRpg.Core.BuildPresets.BuildPresetTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "build-preset.v1.json"))));
// Server-side only (spec-action-catalog.md, T30): actions are battle-mode and the injector never
// sees one, so the rung ladder has no reason to load there.
FusionRpg.Core.Actions.Rungs.RungPolicy.Configure(
    FusionRpg.Core.Actions.Rungs.RungTableLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "action-rungs.v4.json"))));
// action-enrich action-base (AE1.1, spec-action-base.md): the basic attack's base power. Loaded
// here, in the same commit that creates the file (H7) — a published file nothing reads is dark.
// v2 (AE1.4): the hit site activated the base and found v1's value 1000x too large; the reader
// switch to the republished version lands with the swap, never a version behind it.
FusionRpg.Core.Actions.ActionBaseTuningHub.Configure(
    FusionRpg.Core.Actions.ActionBaseTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "action-base.v2.json"))));
// A26 T62 (spec-unlock-tuning-activation.md): the unlock ladder's own tuning. Without this call
// `UnlockTuningPolicy.Tuning` is null for the whole server process, so `TryRollActionUnlocks` no-ops
// and a real level-up can never grant a real action — the engine is built and tested, and inert for
// every player. Same shape as its siblings above; a missing or corrupt file throws here at startup
// rather than silently disabling grants later.
FusionRpg.Core.Actions.Unlock.UnlockTuningPolicy.Configure(
    FusionRpg.Core.Actions.Unlock.UnlockTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "action-unlock.v1.json"))));
// battle-tempo action-timing (2026-09-05): every seeded action's wind-up/recovery/timeCost/cooldown,
// derived at RpgStore.BuildActionCatalog (D2), never by the seeder.
FusionRpg.Core.Actions.ActionTimingPolicy.Configure(
    FusionRpg.Core.Actions.ActionTimingTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "action-timing.v1.json"))));
// battle-tempo reaction-lane RL2/RL3 (2026-09-05/06): the counter's poise spend and riposte
// share. v1 -> v2/v3 (RL3, 2026-09-06): sized against the landed Phase 2 sweep -- poiseSpend=50
// matches AptitudeGuardEconomy.flatCommitCost, riposteShareCapMilli=400 matches its
// riposteShareCapPermille, so a counter costs about what a guard costs from the same pool
// (spec-reaction-lane.md's own "the spend range is this module's to size"). Loaded regardless
// so ReactionCounter.TryCounter always has a real, non-hardcoded number wherever a caller
// reaches it.
FusionRpg.Core.Battle.Timeline.ReactionLanePolicy.Configure(
    FusionRpg.Core.Battle.Timeline.ReactionLaneTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "reaction-lane.v3.json"))));
// item-ideal.md, rarity-bands (module 7): no Hub needed today -- SeedRarityLadder consumes this
// parse once, at boot, to populate rarity_budget rows. A future consumer (drop-volume/enhance-reroll)
// reads those rows back through RpgStore.GetRarityBudget, not this parsed value directly.
var itemRarityTuning = FusionRpg.Core.Items.ItemRarityTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "item-rarity.v1.json")));
// item-ideal.md, item-power-reads (module 9): no Hub needed either -- parsed and validated at boot
// (the powerDisplayBandPercent == DriftTolerancePercent check fails fast here rather than at first
// use), consumed by module 10 item-card once it exists as the real production caller.
var itemPowerTuning = FusionRpg.Core.Items.Power.ItemPowerTuningLoader.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "item-power.v1.json")));
_ = itemPowerTuning;
// item-ideal.md, drop-volume (module 11): D18's volume curve, D38's kill rate and Correction 5's
// re-solved pity thresholds. Parsed and validated at boot so a self-inconsistent balance edit fails
// here rather than at the first drop; ImportLootCorpus below consumes it.
var dropVolumeTuning = FusionRpg.Core.Items.Drops.DropVolumeTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "item-drop-volume.v1.json")));
// item-ideal.md, threshold-grants (module 12): D3's frame-mix recovery curve. Parsed and validated at
// boot for the same reason as the two above — a knot list that is flat over any interval reinstates
// the step function the curve exists to prevent, and that failure must land here rather than at the
// first hybrid body priced against it.
var frameMixTuning = FusionRpg.Core.Items.Thresholds.FrameMixTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "item-frame-mix.v1.json")));
_ = frameMixTuning;
// item-ideal.md, salvage-craft (module 14): the ten-operation reference cost table and the ten-rung
// salvage coefficients. Parsed and validated at boot for the same reason as the four above — the
// parser refuses rather than defaults, and D24's "imbue prices on bore's curve" is checked at load,
// so a balance pass that moves one and forgets the other fails here rather than at the first
// crafted socket. Consumed by SeedSalvageYield and by the recipe import below.
// species-gear-chain T32 (species-cost-shaping): v3 adds speciesCostMultiplierMilli/
// speciesCostThresholdRung/speciesCostThresholdRungByVerb (R-SC2) -- switched in the same commit as
// the publish (H7), v2 stays on disk for revert.
var materialTuningFileName = FusionRpg.Core.Items.Sockets.SocketTuningFiles.Materials;
var materialTuning = FusionRpg.Core.Items.Materials.MaterialTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, materialTuningFileName)));
// species-gear-chain T34b: the trophy id registry is GENERATED content (species-gear-chain T34's own
// deterministic seedsmith planner, gk-data/packs/fusion/data/seed/items/materials/trophy-registry.json), not tuning — but
// it is injected the same way (Core never reads a file, tunables-ssot §7.2). MaterialCatalog.ClassOf
// resolves a concrete trophy.* id only if this set holds it, and still throws on anything else.
FusionRpg.Core.Items.Materials.MaterialCatalog.ConfigureTrophyRegistry(
    FusionRpg.Core.Items.Materials.MaterialCatalog.ParseTrophyRegistryIds(
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "data", "seed", "items", "materials", "trophy-registry.json"))));
// item-ideal.md, enhance-reroll (module 15): the gain asymptote's K, the three risk bands, the craft
// pity threshold, the transfer ratio and the reroll price's two legs. ⛔ THE soft cap lives in this
// file — the parser refuses a closed top band, a zero success floor, a lossless transfer ratio and a
// rung-dominant reroll price at BOOT, so an edit that would put a hard ceiling back on +X fails here
// rather than at the first crafted item. Consumed by SeedRerollCostMult below.
var enhancementTuning = FusionRpg.Core.Items.Mutation.EnhancementTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "enhancement.v1.json")));
// item-ideal.md, sockets (module 16): the per-role socket ceiling, the per-rung grant windows, the
// resonance shapes, the removal tiers and D20's ingredient count. Parsed and validated at boot for
// the same reason as the five above — the parser refuses a ceiling above the structural
// `SocketMaxCeiling` (8), a
// rarityGrant table whose adjacent windows do not overlap (which would make socket count a strict
// ladder), a `standard` ceiling row D14 puts out of scope, and an ingredient count no item could
// ever hold. Consumed by SeedSocketGrants and SeedComboRecipes below.
var socketTuningFileName = FusionRpg.Core.Items.Sockets.SocketTuningFiles.Current;
var socketTuning = FusionRpg.Core.Items.Sockets.SocketTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, socketTuningFileName)));
// item-ideal.md, strain-splice-gen (module 21): the min-tier plan D20's four ingredients are priced
// on, the per-shape base tier, the learnability bar and the two name-distinctness thresholds.
// CROSS-VALIDATED against socketTuning at parse time — a min-tier plan whose length disagrees with
// the ingredient count, or a tier outside the shipped insert ladder, fails here rather than
// producing 102 combinations the evaluator can never match. ⛔ It deliberately re-declares NONE of
// module 16's socket numbers; the seedsmith parser refuses a file that does.
var strainSpliceTuningFileName = FusionRpg.Core.Items.Sockets.SocketTuningFiles.StrainSplice;
var strainSpliceTuning = FusionRpg.Core.Items.Sockets.StrainSpliceTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, strainSpliceTuningFileName)), socketTuning);
// item-ideal.md, uniques (module 17): the rung floor, the identity spread, the shared 1.5 AE premium,
// `narrow`'s ceiling, the role quota and the parity band. Parsed at boot for the same reason as the
// six above — the parser refuses an inverted parity band, a forbiddenRoles entry naming a role that
// does not exist, and a budget drift tolerance that has drifted from definitions §7's shared number,
// so a bad edit fails here rather than at the first imported unique. Consumed by SeedUniqueEligible.
var uniqueTuning = FusionRpg.Core.Items.Uniques.UniqueTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "uniques.v1.json")));
// item-ideal.md, consumables (module 18): which of the six classes and four use contexts v1 authors,
// `bands.v1.json`'s mirrored grade map, §4.4's authoring ceiling and the run-start binding priority.
// Parsed and validated at boot for the same reason as the seven above — the parser refuses a
// gradeTierMap that is not a bijection onto 1..5, an empty authored list, and (BY NAME) a withdrawn
// `carryLimit` key, so a balance edit that would silently do nothing fails here rather than at the
// first dispatch. ⛔ Nothing seeds a rarity_budget key from it: consumables never enter the ladder.
var consumableTuning = FusionRpg.Core.Items.Consumables.ConsumableTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "consumables.v1.json")));
_ = consumableTuning;
// item-ideal.md, item-surfaces (module 20): the GG-50 render bands, the compendium's four-state
// boundary and name-only tail cap, the loot filter's default and its two per-content-event watch
// numbers, and each surface's GG-44 unlock key. Parsed at boot for the same reason as the eight
// above — the parser refuses an unordered render band, a zero one-away distance (which would name
// the active set) and, by name, a surfaceUnlocks table that has forgotten one of the six surfaces,
// because a surface with no declared unlock renders as present-but-dead. ⛔ Nothing here meters the
// player: every number is a presentation threshold and D26 keeps it that way.
var itemSurfaceTuning = FusionRpg.Core.Items.Surfaces.ItemSurfaceTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "item-surfaces.v1.json")));
// item-ideal.md, charm-carry (module 22, split out of 12 by D40): the AP cost domain, the capacity
// LADDER (its last rung is the last AUTHORED rung, never a ceiling — AGENTS.md), the axis and copy
// caps, and the run-start binding shape. Parsed at boot for the same reason as the nine above — the
// parser refuses a starting capacity below the largest charm (which would make every signet dead
// content), a `unique_carry` cap looser than the default, a non-negative binding priority, and BY
// NAME both a `maxCapacityAp`-shaped ceiling key and a `player`/`match` binding owner kind, because
// D33(a) binds charms at unique-actor: scope and `player:` resolves match-wide in the stat layer —
// a charm that buffs the zombies. A bad edit fails here rather than at the first dispatch.
var charmAttunementTuning = FusionRpg.Core.Items.Thresholds.CharmAttunementTuning.Parse(
    File.ReadAllText(Path.Combine(tuningDir, "charm-attunement.v1.json")));
_ = charmAttunementTuning;

// notify-vocabulary spec §4: the notify-catalog and notify tuning files, beside every other tuning
// load above. The injector never receives a notification, so it never loads either file (map
// "What this program is not").
FusionRpg.Core.Notify.NotificationCatalogHub.Configure(
    FusionRpg.Core.Notify.NotificationCatalogLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "notification-catalog.v3.json"))));
FusionRpg.Core.Notify.NotificationTuningHub.Configure(
    FusionRpg.Core.Notify.NotificationTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "notification.v1.json"))));

// Default: {ServerExeDir}/data/{rpg-hot,rpg-media}.sqlite — override with FUSIONRPG_DATA only for tests/special runs.
var dataDir = Environment.GetEnvironmentVariable("FUSIONRPG_DATA");
if (string.IsNullOrWhiteSpace(dataDir))
    dataDir = Path.Combine(AppContext.BaseDirectory, "data");

// memory-storage-plan: FUSIONRPG_DATA may also be a shared-memory URI
// (`file:{name}?mode=memory&cache=shared`, SqliteConnectionFactory.MemoryUri) instead of a directory.
// The E2E host boots the REAL server this way, so a test run opens no file at all (the disk is used only
// when the disk is the thing under test — docs/contributing/testing-standard.md R1/R2). Two conditions,
// never a fallback: a real directory path keeps the exact file behaviour below, and the URI trap stays
// armed (RpgStoreOptions.Resolve still throws if a memory URI ever reaches the file plan, so a URI can
// never be rewritten into a path and silently written to disk).
//
// The shape test is spelled out here instead of calling the Data-owned predicate
// (SqliteConnectionFactory.IsMemoryUri) because guard-dal.ps1 bans those SQLite type names anywhere under
// src/ outside FusionRpg.Data, and a hard boundary is not widened for convenience. These are that
// predicate's own two conditions. A mismatch is loud, never silent: RpgApiFactory seeds the species
// roster into the databases this URI names, so the wrong plan leaves the catalog empty and the boot
// throws on `BuildCreatureSpeciesSnapshot`.
var memoryPlan = dataDir.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
    && dataDir.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);
if (!memoryPlan)
    Directory.CreateDirectory(dataDir);
Console.WriteLine(memoryPlan
    ? $"[data] {dataDir} (shared-memory plan — no file, no directory)"
    : $"[data] {dataDir} (hot=rpg-hot.sqlite media=rpg-media.sqlite)");

// A shared-memory database lives under a NAME, and both this host's store and whoever seeded/reads the same
// data (the E2E fixture's keeper, gk-core/tests/FusionRpg.E2E.Tests/RpgApiFactory.cs) must address the SAME two
// names — RpgStore's per-store generated names would give each store its own empty database instead. The
// name is therefore taken from the URI, and media is the `<name>-media` convention that fixture mirrors; a
// mismatch between the two is loud, not silent: the roster read below throws on an empty species catalog.
var rpgStore = memoryPlan
    ? new RpgStore(new FusionRpg.Data.Sqlite.RpgStoreOptions
    {
        InMemory = true,
        HotName = MemoryDbName(dataDir),
        MediaName = MemoryDbName(dataDir) + "-media"
    }.Resolve())
    : new RpgStore(dataDir);

static string MemoryDbName(string uri)
{
    var name = uri["file:".Length..];
    var query = name.IndexOf('?');
    return query < 0 ? name : name[..query];
}
builder.Services.AddSingleton(rpgStore);
builder.Services.AddSingleton(sp => new TypeIconStore(sp.GetRequiredService<RpgStore>()));
// ai-empire-species EP4.18 (R23): the one empire-keyed Theta read, handed to the store so the
// world-turn/siege seam can size a NON-human empire's commander pool from that empire's own commander
// level. The store declares the need -- it cannot compose Theta itself without a private f(level), which
// guard-power.py forbids -- and this composition root supplies it, the same inversion HubInputsFor /
// UnlockStateFor already use. ONE provider instance, registered as the interface every endpoint reads.
var powerIndex = new FusionRpg.Server.Power.ServerPowerIndexProvider(
    rpgStore, FusionRpg.Core.Power.PowerTuningHub.Tuning);
rpgStore.ConfigureActorTheta((save, empire) => powerIndex.ActorIndexFor(save, empire));
builder.Services.AddSingleton<FusionRpg.Core.Power.IPowerIndexProvider>(powerIndex);
builder.Services.AddSingleton<IColdArchiveWriter>(sp => new ColdArchiveWriter(sp.GetRequiredService<RpgStore>()));
builder.Services.AddSingleton<IColdArchiveCatalog>(sp => new ColdArchiveCatalog(sp.GetRequiredService<RpgStore>()));
builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
builder.Services.AddSingleton<CompactionWorker>();
builder.Services.AddSingleton<EventIngest>();
builder.Services.AddSingleton<InjectorCommandInbox>();
builder.Services.AddSingleton<InjectorCommandSender>();
// build-preset gate-services (BP1.1/BP1.2): one PatronService/ContractService, called by the
// route and (later) by a build-preset applier -- no second implementation of the store write
// plus broadcasts (and, for PatronService, the injector push).
builder.Services.AddSingleton<PatronService>();
builder.Services.AddSingleton<ContractService>();
// BP1.3: registered for a future build-preset skills applier (BP2.3) to consume via DI; the
// /api/loadout route itself constructs its own instance inline (see LoadoutEndpoints.cs) since
// its only dependency, RpgStore, is already a route parameter there.
builder.Services.AddSingleton<ActionLoadoutService>();
// BP1.4: the aptitude-preset activate route's whole post-validation body (budget, materialize,
// budget check, the one transactional store call, the scoped broadcast), called by the route and
// (later) by the build-preset AptitudesApplier -- one implementation of the gate, not a second copy.
builder.Services.AddSingleton<AptitudePresetActivation>();
builder.Services.AddSingleton<FusionRpg.Core.Effects.EffectGrantSession>();
builder.Services.AddSingleton<FusionRpg.Core.Effects.SimEffectHost>();
builder.Services.AddSingleton<UniqueActorService>();
// BP1.7: ItemEquipService itself is still constructed inline at its MapItemEquip call site
// (unchanged, out of this task's scope); registering it here too is only so
// ItemLoadoutApplyService can get one through DI rather than a third construction site.
builder.Services.AddSingleton<ItemEquipService>();
builder.Services.AddSingleton<ItemLoadoutApplyService>();
builder.Services.AddSingleton<IActorLiveStateStore, ActorLiveStateStore>();
builder.Services.AddSingleton<PerfWindowBuffer>();
// combat-ai `replay-identity` (CAI2.2): the ONE loaded source, shared by the two services that stamp
// and resolve against it. Registered as the interface (never the concrete type) so the consumers depend
// on the seam Core owns; the instance is the one built and checked at boot above, so the file is read
// once and the refusal detail is identical everywhere.
builder.Services.AddSingleton<FusionRpg.Core.Actions.Ai.ICombatAiProfileSource>(combatAiProfileFiles);
builder.Services.AddSingleton<WebMatchService>();
builder.Services.AddSingleton<ExpeditionService>();
builder.Services.AddSingleton<IDelveLivePush, HubDelveLivePush>();
builder.Services.AddSingleton<DelveBattleSessionManager>();
// player-routing spec §1 (notification-ssot NS1.6/NS1.7): the per-connection player group map and
// the content-push seam that routes on it. RpgHub takes PlayerConnectionRegistry as a constructor
// dependency, so this registration is load-bearing, not optional.
builder.Services.AddSingleton<PlayerConnectionRegistry>();
builder.Services.AddSingleton<IPlayerPush, HubPlayerPush>();
// notify-service spec §2-3 (notification-ssot NS3.1-NS3.5): the publisher validates against the
// catalog Hub configured above, then durably appends + pushes; the pump collects from every
// registered IWorldTurnNotificationSource, open for extension (the pump itself is never edited for
// a new one) — each source below is a plain `AddSingleton<IWorldTurnNotificationSource, T>()`, so
// ASP.NET Core's own IEnumerable<T> collection does the wiring (world-notify-source's source adds
// its own line here, the same way, when it lands). Runs after a commit (WorldEndpoints.cs) or once
// at boot (NotificationBootCatchUp).
builder.Services.AddSingleton(sp => new FusionRpg.Server.Notifications.NotificationContract(FusionRpg.Core.Notify.NotificationCatalogHub.Catalog));
builder.Services.AddSingleton<FusionRpg.Server.Notifications.NotificationPublisher>();
builder.Services.AddSingleton<FusionRpg.Server.Notifications.IWorldFactionSaves, FusionRpg.Server.Notifications.WorldFactionSaves>();
builder.Services.AddSingleton<FusionRpg.Server.Notifications.IWorldTurnNotificationSource, FusionRpg.Server.Notifications.CacheNotificationSource>();
builder.Services.AddSingleton<FusionRpg.Server.Notifications.IWorldTurnNotificationSource, FusionRpg.Server.Notifications.WorldReportNotificationSource>();
builder.Services.AddSingleton<FusionRpg.Server.Notifications.WorldTurnNotificationPump>();
builder.Services.AddHostedService<FusionRpg.Server.Notifications.NotificationBootCatchUp>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventIngest>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CompactionWorker>());
builder.Services.AddHostedService<UniqueActorDeployWatchdog>();
if (SimFlags.Enabled)
{
    builder.Services.AddSingleton<SimService>();
    builder.Services.AddHostedService<SimHeartbeatHost>();
}

// The clock seam (rpg-simulator RS3, docs/architecture/rpg-simulator-spec-clock-seam.md §2): the
// server's wall clock is an INPUT, not an ambient fact. Configured exactly ONCE here, from the
// environment, and NEVER accepted from a route — a player-reachable clock setter would make every
// deadline in the game lie. FUSIONRPG_CLOCK_OFFSET is a signed number of seconds added to the
// machine clock; absent or 0 means the machine clock itself. A net8.0 host hands the seam a
// TimeProvider by adapting that provider's own read (shape B, spec §0.1): the seam stores the
// delegate, never the TimeProvider.
{
    var clockOffsetSeconds = 0L;
    var rawClockOffset = Environment.GetEnvironmentVariable("FUSIONRPG_CLOCK_OFFSET");
    if (!string.IsNullOrWhiteSpace(rawClockOffset) &&
        !long.TryParse(rawClockOffset.Trim(), out clockOffsetSeconds))
        throw new InvalidOperationException(
            $"FUSIONRPG_CLOCK_OFFSET must be a signed integer number of seconds, got '{rawClockOffset}'");

    var timeProvider = TimeProvider.System;
    if (clockOffsetSeconds == 0)
    {
        ServerClock.Configure(timeProvider.GetUtcNow, 0);
    }
    else
    {
        var offset = TimeSpan.FromSeconds(clockOffsetSeconds);
        ServerClock.Configure(() => timeProvider.GetUtcNow() + offset, clockOffsetSeconds);
    }
}

var app = builder.Build();
var store = app.Services.GetRequiredService<RpgStore>();
// save-identity SE4.12: the authored new-save registry, read by the host (Core never touches a path).
// Configured BEFORE Init, which seeds the current save's empires from it.
FusionRpg.Core.Saves.NewSaveEmpiresHub.Configure(
    FusionRpg.Core.Saves.NewSaveEmpires.Parse(
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "data", "seed", "saves", "_registry", "new-save-empires.v1.json"))));
// identity-rename T13: the names registry is configured BEFORE store.Init(), because an empty
// save's player is named from it (`RpgStore`'s onboarding insert) — Core never touches a path.
// identity-rename T2: the authored lead-names registry (the three leads of owner ruling R11), read by
// the host (Core never touches a path). It sits with the other authored registries so a missing file
// fails the boot loudly, naming the path — never a silent default name. The literal path stays here
// (not behind a constant) so BootContentCopyRuleTests can see that a <Content> rule covers it.
FusionRpg.Server.Narrative.LeadNamesBoot.Configure(Path.Combine(
    AppContext.BaseDirectory, "data", "seed", "narrative", "_registry", "names.en.v1.json"));

store.Init();
// commander-identity SE4.3: the authored commander registry, read by the host (Core never touches a
// path). The callers that answer for a save supply the player's name (CommanderEndpoints.ProjectList),
// so the directory itself needs no store lookup.
// commander-roster EP3.2: the same directory, with the role source composed in — a creature that
// holds the commander role (rpg_commander_role) is a commander, with no code change per creature. The
// authored rows stay first and every answer they own is unchanged; the source only answers for
// `commander:unique:{instanceId}` ids. `store` supplies the three role reads (Core never touches a
// store), and the directory is still ONE class (WithSource, not a second directory).
var authoredCommanders = FusionRpg.Core.Commanders.DataCommanderDirectory.Parse(
    File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "data", "seed", "commanders", "_registry", "default-commanders.v1.json")));
FusionRpg.Core.Commanders.CommanderDirectoryHub.Configure(
    authoredCommanders.WithSource(new FusionRpg.Core.Commanders.UniqueCommanderSource(
        authoredCommanders, store)));
// catalog-runtime (creature-seed module 13, spec-catalog-runtime.md §7 step 5) — THE FLIP, 2026-09-05:
// species now load from the store `species-import` writes (gk-data/packs/fusion/data/generated/creatures/**.json, imported
// via `gk-forge/tools/CreatureSpeciesImport`), not the compiled `CreatureSpeciesCatalog.Generated.cs` snapshot.
// Owner decision (2026-09-05): the classification/generation pipeline is real and current
// (829 species, up from 84); further review happens against the real, running roster, not by
// withholding it. Loaded once here, immutable for the process lifetime (§3a — no live reload; an
// import needs a server restart, already how this repo deploys).
//
// E46 / CS-F3 (OWNER RULING 2026-09-23): the roster is store-backed, so an EMPTY store used to be a
// hard startup failure -- a fresh player install had no writer for these tables (`gk-forge/tools/CreatureSpeciesImport`
// is a developer CLI), so it died here. The shipped `gk-data/packs/fusion/data/generated/creatures/**` tree is now read once
// beside the exe by `SpeciesImportRunner.RunSelfHealing` when the tables are empty. It never throws and
// never runs twice: a non-empty roster is a true no-op (the same gate `SeedImportRunner.RunSelfHealing`
// uses), and a missing or broken tree is logged loudly here and then left to `Configure`'s own refusal
// below -- running the compiled 84-species snapshot instead would be exactly the "absence looks like
// success" defect E46 exists to kill.
var speciesBoot = FusionRpg.Data.Seed.SpeciesImportRunner.RunSelfHealing(store, AppContext.BaseDirectory);
Console.WriteLine(speciesBoot switch
{
    { Status: FusionRpg.Data.Seed.SpeciesImportStatus.Imported } =>
        $"[species] imported the roster — {speciesBoot.Outcome!.Written} written, {speciesBoot.Outcome.Unchanged} unchanged, from data/generated/creatures beside the exe",
    { Status: FusionRpg.Data.Seed.SpeciesImportStatus.AlreadyCurrent } =>
        "[species] the roster is already imported; no import needed",
    _ =>
        $"[species] the roster was NOT imported ({speciesBoot.Status}) — {speciesBoot.Detail}",
});
FusionRpg.Core.Creatures.CreatureSpeciesCatalog.Configure(store.BuildCreatureSpeciesSnapshot());
store.SeedRarityLadder(itemRarityTuning);
// item-ideal.md, salvage-craft (module 14): `salvage_yield`, the sixth `rarity_budget` key, whose
// shape ssot-rarity.md §5 recorded as "awaiting I9" until this module decided it. Seeded here rather
// than inside SeedRarityLadder so module 7's own seeding never grows a dependency on a later
// module's tuning file.
store.SeedSalvageYield(materialTuning.Salvage);
// item-ideal.md, enhance-reroll (module 15): `reroll_cost_mult`, the seventh `rarity_budget` key,
// whose shape ssot-rarity.md §5 recorded as "awaiting I7" until this module decided it. Same
// placement rule as salvage_yield above — a later module's tuning never reaches module 7's seeding.
store.SeedRerollCostMult(enhancementTuning);
// item-ideal.md, sockets (module 16): `socket_min` and `socket_max`, the eighth and ninth
// `rarity_budget` keys, whose shape ssot-rarity.md §5 recorded as "awaiting I4" until this module
// decided it. Same placement rule as the two above.
store.SeedSocketGrants(socketTuning);
// item-ideal.md, uniques (module 17): `unique_eligible`, the tenth `rarity_budget` key and the ONE key
// ssot-uniques.md §5.3 asked for. Derived from each rung's ordinal against the tuning floor rather
// than authored as a second per-rung table, and seeded here under the same placement rule as the three
// above — a later module's tuning never reaches module 7's own seeding.
store.SeedUniqueEligible(uniqueTuning);
// action-instance-and-grant (A21, T59.4): the corpus import — content-seeded, idempotent (a
// re-import of an unchanged brief moves zero revisions, T30's own guard). Defaults ON, matching
// this file's own kill-switch convention (FUSIONRPG_PERF, FUSIONRPG_NO_BROWSER) rather than
// requiring an opt-in for content that already exists and is safe to re-run every start. Never a
// live game/injector path -- this runs once, here, before the server accepts any connection.
if (Environment.GetEnvironmentVariable("FUSIONRPG_ACTION_CORPUS_IMPORT") != "0")
{
    // v1 -> v2 (lawn-combat-wire T11, spec-lawn-combat-calibration.md, 2026-09-14): kinds.basic
    // baseAmountAtRung1 derived from the pool/regen envelope (20 -> 25), published via
    // gk-core/tools/tuning/publish.py, no hand-edit. v1 stays on disk for revert.
    var actionCorpusTemplatePath = Path.Combine(AppContext.BaseDirectory, "data", "tuning", "action-corpus-cost-templates.v2.json");
    if (!File.Exists(actionCorpusTemplatePath))
    {
        // Loud, never fatal -- the same rule the loot corpus below and the content boot follow (a broken
        // content tree must not take the server down). Silence here is what let a published build ship
        // an EMPTY action catalog (action-skill-tiers ST4.5, 2026-09-19): the cost template is this
        // import's own gate, so its absence skipped the whole corpus and nothing said so.
        Console.Error.WriteLine(
            $"[actions] {actionCorpusTemplatePath} is missing next to the exe — the action corpus was NOT "
            + "imported and the catalog stays empty (check FusionRpg.Server.csproj's data\\tuning copy rule)");
    }
    else
    {
        var actionCostTemplate = FusionRpg.Core.Actions.Corpus.ActionCorpusCostTemplateLoader.Parse(File.ReadAllText(actionCorpusTemplatePath));
        // A29 (action-corpus-import-completion): all four committed rounds, keyed by filename so the
        // cross-file id-collision guard below can name both source files on a rejection. `round-909`
        // and `round-2000` were audited clean against this parser's schema (T65: identical keys, same
        // closed-vocab values as round-1/2, zero id overlap today) -- their omission until now left 155
        // of 180 authored briefs (`audit-2026-09-13-distribution.md`) never read by any production code.
        // `authored-basics.json` (T7, basic-attack-seed): the hand-authored `act.attack` fallback, never
        // generator output (spec-basic-attack-seed.md).
        var actionBriefsByFile = new Dictionary<string, IReadOnlyList<FusionRpg.Core.Actions.Corpus.ActionCorpusBrief>>(StringComparer.Ordinal);
        foreach (var briefFile in new[]
        {
            "committed-round-1.json", "committed-round-2.json",
            "committed-round-909.json", "committed-round-2000.json",
            "authored-basics.json",
        })
        {
            var briefPath = Path.Combine(AppContext.BaseDirectory, "data", "seed", "actions", briefFile);
            if (File.Exists(briefPath))
                actionBriefsByFile[briefFile] = FusionRpg.Core.Actions.Corpus.ActionCorpusBriefJson.Parse(File.ReadAllText(briefPath));
            else
                Console.Error.WriteLine(
                    $"[actions] brief {briefPath} is missing next to the exe — it was NOT imported "
                    + "(check FusionRpg.Server.csproj's data\\seed\\actions copy rule)");
        }
        // Reject loudly on a same-id-different-payload split across files (the proven historical defect,
        // audit-2026-09-13-distribution.md §7 finding J) BEFORE the idempotent-by-id Import could
        // silently pick whichever file this array lists last.
        FusionRpg.Data.ActionCorpusImporter.AssertNoCrossFileIdCollisions(actionBriefsByFile);
        var actionBriefs = actionBriefsByFile.Values.SelectMany(b => b).ToList();
        FusionRpg.Data.ActionCorpusImporter.Import(store, actionBriefs, actionCostTemplate, FusionRpg.Core.Actions.Rungs.RungPolicy.Table);
    }
}
// strain-splice-host SSH4.4 (spec-combo-bind §1): the combination import and its pricing
// provenance check MOVED to just after the content boot (below). Its container build upserts into
// `effect_container`, whose validator resolves atoms through the store's `effect_atom` table — and
// `SeedImportRunner.RunSelfHealing` is what populates that table, so the container build can only
// run after it. The recipe seed still happens before anything binds, which is what the
// one-acceptance-set rule needs.

// item-ideal.md, salvage-craft (module 14): the authored recipe corpus
// (gk-data/packs/fusion/data/seed/items/recipes/*.json) resolved against the reference cost table. Never fatal, same rule
// as the loot corpus below. ⛔ Entries this build cannot resolve are refused BY NAME with the module
// that unblocks them — never silently dropped — and the count is printed so a corpus regression is
// visible at boot.
// ⭐ Kept rather than discarded after the import: the workbench executor resolves every price
// through this same catalog, so the running server and the imported rows can never be two
// different corpora.
FusionRpg.Core.Items.Materials.MaterialRecipeCatalog? recipeCatalog = null;
{
    var recipesDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "recipes");
    if (Directory.Exists(recipesDir))
    {
        try
        {
            var catalog = FusionRpg.Core.Items.Materials.MaterialRecipeCatalog.Load(
                Directory.EnumerateFiles(recipesDir, "*.json").OrderBy(f => f, StringComparer.Ordinal)
                    .Select(File.ReadAllText),
                materialTuning);
            var imported = store.ImportRecipeCatalog(catalog);
            recipeCatalog = catalog;
            Console.WriteLine($"[craft] imported {imported} recipes, refused {catalog.Refusals.Count}");
            foreach (var refusal in catalog.Refusals)
                Console.WriteLine($"[craft]   refused {refusal.RecipeId}: {refusal.Rule} — {refusal.Detail}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[craft] recipe import failed — no recipes loaded: {ex.Message}");
        }
    }
}

// ⭐ item modules 14/15/16 — THE WORKBENCH EXECUTOR. All three shipped their own half of the
// salvage → craft → enhance → socket loop and all three recorded the same blocker: nothing called
// them against a stored item, so `TrySpendRecipe`, `AppendMutationOp` and `SetSockets` each had zero
// production callers. This is the joint. It is registered only when the recipe corpus loaded —
// a workbench with no prices could only ever refuse, and a route that always refuses is worse than
// a route that is absent, because it looks wired.
// ⏸ Module 16 shipped the gem catalog as seed JSON, not a table — the same boot-time stopgap shape
// `BaseTypeSocketMaxCorpus` is. Loaded ONCE here and handed to all three item route groups: the
// workbench (`socket-insert`), the surface routes (`/combinations`) and the card routes each need an
// insert's real element, and three separate loads would be three chances to disagree about it.
var gemInserts = FusionRpg.Server.GemInsertCorpus.Load(
    Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "gems"));
// ⏸ The same boot-time stopgap, for the same missing table (module 6 shipped the 740-entry base-type
// corpus as seed JSON and no `item_base_type`). ✅ CORRECTED 2026-09-23: the table exists (it carries
// `(id, frame, role)` only), so this stopgap's trigger is the missing `socketMax` column. Hoisted above `MapItemSurfaces` by item-content T3 so
// the armoury row and the item card read ONE corpus: two loads are two chances to disagree about what
// an item is called, which is exactly the class of defect T2 fixed inside the card.
var itemBaseTypes = FusionRpg.Server.ItemBaseTypeCorpus.Load(
    Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "base-types"));

// item-ideal.md, drop-volume (module 11) — `item_base_type`, the table the two ⏸ comments above
// already name as their own target. `BuildLiveLootContentView`'s own `BaseTypesFor` reads it (D4.12,
// party-dungeon-todo.md, 2026-09-07); the two boot-time JSON readers above stay as they are (display /
// socket-max shape, a different job) rather than being folded into this import.
{
    var baseTypesDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "base-types");
    try
    {
        var baseTypeRows = FusionRpg.Core.Items.Drops.BaseTypeSeedFile.LoadAll(baseTypesDir);
        store.ImportBaseTypes(baseTypeRows);
        Console.WriteLine($"[items] imported {baseTypeRows.Count} base types");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[items] base-type import failed — no base types loaded: {ex.Message}");
    }
}

// item-ideal.md, `slot-roles` (module 3) — `item_role`/`item_role_frame`, the two tables P1.3
// claimed "schema populated in full" while the todo's own 2026-09-06 bullet recorded that
// `RpgStore.SeedRoles` had **no production caller**, so both were EMPTY in a real deployed database.
// That bullet named its blocker as a content-pipeline decision — "where does this file live at
// runtime" — and the twenty seed readers around it answer that question: the items seed tree ships
// (`FusionRpg.Server.csproj:76`, `data\seed\items\**\*.json`) and is read from
// `AppContext.BaseDirectory`, the SAME way the workbench block below reads `_registry/
// family-overrides.v1.json` from that very directory. So this is the mechanical wire the bullet said
// it would be once the path question was settled, and it closes the gap before the first reader of
// the tables lands (none exists yet, which is what made the gap inert rather than visible). Non-fatal,
// the same rule as every sibling block: a missing registry leaves the tables empty and says so.
{
    var roleRegistryPath = Path.Combine(
        AppContext.BaseDirectory, "data", "seed", "items", "_registry", "core.v1.json");
    if (File.Exists(roleRegistryPath))
    {
        try
        {
            store.SeedRoles(File.ReadAllText(roleRegistryPath));
            Console.WriteLine($"[items] seeded {store.ListRoles().Count} slot roles from core.v1.json");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[items] role seed failed — item_role/item_role_frame left empty: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine($"[items] role registry missing at {roleRegistryPath} — item_role/item_role_frame left empty");
    }
}

FusionRpg.Server.ItemWorkbench? itemWorkbench = null;
// ⏸ `socketMax` still comes off the base-type seed JSON at boot. ✅ CORRECTED 2026-09-23: this used to
// say "module 6 shipped the base-type corpus but no `item_base_type` table, so socketMax comes off the
// seed JSON — deleted the day that table exists". The table HAS existed since 2026-09-07
// (`RpgStore.BaseTypes.cs`, imported at boot below), but it carries only `(id, frame, role)`, so the
// real trigger is the missing `socketMax` COLUMN, not the table. Hoisted ABOVE the workbench `if`
// (2026-09-23) because the card and surface routes need this same lookup and they are mapped at the
// bottom of this file, outside that block's scope — which is how the stub survived SSH1.3.
var socketMaxForBaseType = FusionRpg.Server.BaseTypeSocketMaxCorpus.Load(
    Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "base-types"), socketTuning);
// species-gear-chain T13's milestone append — the two lookups `ItemWorkbench` takes and that nothing
// supplied until now, which is why the whole feature was inert (the workbench's own doc: "`null` (not
// supplied) means NO milestone append, and the attempt still succeeds"). Both read authored corpus
// facts only: the track off the base-type seed JSON, and the amount range off the atom row the store
// already holds. `knownFamilies` is what lets the lookup keep T13's two absences opposite — a family the
// corpus never named is LOUD (`MilestoneUnknownFamily`), a known family the generator could not expand is
// QUIET (no append, never a failed enhance).
var baseTypeEnhanceTracks = FusionRpg.Core.Items.Mutation.BaseTypeEnhanceTrackFile.LoadAll(
    Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "base-types"));
Func<string, IReadOnlyList<FusionRpg.Core.Items.Mutation.MilestoneTrackEntry>?> baseTypeEnhanceTrack =
    id => baseTypeEnhanceTracks.TryGetValue(id, out var track) ? track : null;
var milestoneFamilies = new HashSet<string>(StringComparer.Ordinal);
{
    var milestonesPath = Path.Combine(
        AppContext.BaseDirectory, "data", "seed", "items", "enhancement-milestones", "milestones.json");
    if (File.Exists(milestonesPath))
        foreach (var family in FusionRpg.Core.Effects.Atoms.Generation.MilestoneFamilyFile.Read(
                     "milestones.json", File.ReadAllText(milestonesPath)))
            milestoneFamilies.Add(family.Id);   // FamilyEntryInput.Id IS the runtimeFamily, per that reader's own doc
}
var milestoneAtomFor = FusionRpg.Server.MilestoneAtomLookup.Load(store.GetAtom, milestoneFamilies);
if (recipeCatalog is { } workbenchRecipes)
{
    // species-gear-chain T37: Rule 1's production input — each affix family's own `roles` allow-list,
    // read from the same `gk-data/packs/fusion/data/seed/items/affix-families/**` corpus `forgeMintCells` derives from.
    // Hoisted into a local (P8.1): `RoleFamilyTable.Derive` needs the SAME loaded rows, and a second
    // read of the same directory would be two chances to disagree.
    var affixFamilySources = FusionRpg.Core.Items.AffixFamilySeedFile
        .LoadAll(Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "affix-families"));
    var affixFamilyRoles = affixFamilySources
        .ToDictionary(
            f => f.FamilyId,
            f => (IReadOnlySet<string>)new HashSet<string>(f.Roles, StringComparer.Ordinal),
            StringComparer.Ordinal);

    // P8.1 — `forge`'s mint, wired at last. `ItemWorkbench.Forge` refused every forge with
    // `forge.mint-unavailable` (ItemWorkbench.cs:488) because `Program.cs` never supplied the two
    // inputs the mint needs, while both were in fact resolvable at boot: the role-family cells are
    // `RoleFamilyTable.Derive` over the affix-family corpus ALREADY loaded above plus the two shipped
    // registries, and the power tuning is the Hub's own — the same value `gemPowerTuning` below
    // already carries, configured at Program.cs:302 before this block runs. `RoleFamilyTable.Derive`
    // is the ONE derivation the drop path documents too (RpgStore.Mint.cs:27), so forge and drop mint
    // through the same cells rather than two that could drift. A missing or malformed registry is
    // reported and leaves the cells NULL, which keeps the mint's own named refusal — never a
    // half-derived table that would mint a wrong item.
    IReadOnlyList<FusionRpg.Core.Items.RoleFamilyCell>? forgeMintCells;
    try
    {
        var itemsSeedRoot = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items");
        forgeMintCells = FusionRpg.Core.Items.RoleFamilyTable.Derive(
            affixFamilySources,
            FusionRpg.Core.Items.FamilyOverrides.Parse(File.ReadAllText(
                Path.Combine(itemsSeedRoot, "_registry", "family-overrides.v1.json"))),
            FusionRpg.Core.Items.RoleRelocationTable.Parse(File.ReadAllText(
                Path.Combine(itemsSeedRoot, "_registry", "role-relocation.v1.json"))));
        Console.WriteLine($"[craft] forge mint cells: {forgeMintCells.Count} role/family/frame cells");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[craft] forge mint cells failed to derive — every forge will refuse " +
                          $"forge.mint-unavailable: {ex.Message}");
        forgeMintCells = null;
    }

    // species-gear-chain T37: each base type's own implicit family, off the same seed JSON socketMax
    // and class are read from — the fact the upgrade's card needs and no store table carries.
    var baseTypeImplicitFamily = FusionRpg.Server.BaseTypeSocketMaxCorpus.LoadImplicitFamilyById(
        Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "base-types"));

    // species-gear-chain T38: the authored upgrade edges, read at boot from the EMITTED CORPUS
    // (`successorOf` on the base-type rows), not from the authoring registry: found 2026-09-21 that the
    // generator wrote that field and nothing in src/ read it, while the executor read the registry — the
    // same fact in two places and an emitted field with no reader. The registry is the generator's own
    // input and the closure gate's subject. A missing or unreadable corpus is reported and configured
    // EMPTY, so every upgrade then refuses `upgrade.no-successor` — never a class-ladder guess.
    try
    {
        var baseTypesDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "base-types");
        FusionRpg.Core.Items.Mutation.ItemUpgradeEdgeHub.Configure(
            FusionRpg.Core.Items.Mutation.ItemUpgradeEdgeCorpusReader.Parse(
                Directory.Exists(baseTypesDir)
                    ? Directory.EnumerateFiles(baseTypesDir, "*.json", SearchOption.AllDirectories)
                        .OrderBy(f => f, StringComparer.Ordinal)
                        .Select(File.ReadAllText)
                    : Array.Empty<string>()));
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"[items] base-type corpus edges failed to load — every upgrade will refuse " +
            $"upgrade.no-successor: {ex.Message}");
        FusionRpg.Core.Items.Mutation.ItemUpgradeEdgeHub.Configure(
            new FusionRpg.Core.Items.Mutation.ItemUpgradeEdgeTable(1, 1,
                new Dictionary<string, string>(StringComparer.Ordinal)));
    }
    // species-gear-chain T24: craft wear's own inputs, off the SAME seed JSON socketMax reads. The
    // class is what `DurabilityTable.DeriveMax` needs, the rung comes off the instance's own ladder
    // index, and the rate is the craft-wear key that already ships — never battle wear's.
    var baseTypeClassForId = FusionRpg.Server.BaseTypeSocketMaxCorpus.LoadClassById(
        Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "base-types"));
    var deploymentTuning = FusionRpg.Core.Items.Materials.DeploymentHierarchyTuningHub.Tuning;
    var craftWear = new FusionRpg.Server.CraftWearSource(
        (baseTypeId, rungIndex) =>
            baseTypeClassForId(baseTypeId) is { Length: > 0 } cls &&
            rungIndex >= 0 && rungIndex < FusionRpg.Core.Items.RarityLadder.RungIds.Count
                ? FusionRpg.Core.Items.Materials.DurabilityTable.DeriveMax(
                    new FusionRpg.Core.Items.Materials.HeadDerivationEntry(
                        baseTypeId, cls, Array.Empty<string>()),
                    FusionRpg.Core.Items.RarityLadder.RungIds[rungIndex],
                    deploymentTuning)
                : null,
        deploymentTuning.CraftWearPerAttemptMilli,
        // species-gear-chain T61: the SIBLING derivation craft wear's potential gate needs — the same
        // base type class + ladder rung, through `PotentialTable.DeriveMax` (which honours the authored
        // `potential.overrides` first). Without this the pair was never derived and no craft could wear.
        PotentialMaxFor: (baseTypeId, rungIndex) =>
            baseTypeClassForId(baseTypeId) is { Length: > 0 } cls &&
            rungIndex >= 0 && rungIndex < FusionRpg.Core.Items.RarityLadder.RungIds.Count
                ? FusionRpg.Core.Items.Materials.PotentialTable.DeriveMax(
                    new FusionRpg.Core.Items.Materials.HeadDerivationEntry(
                        baseTypeId, cls, Array.Empty<string>()),
                    FusionRpg.Core.Items.RarityLadder.RungIds[rungIndex],
                    deploymentTuning)
                : null);
    itemWorkbench = new FusionRpg.Server.ItemWorkbench(
        store, materialTuning, workbenchRecipes, enhancementTuning, socketTuning, socketMaxForBaseType,
        gemInserts,
        // species-gear-chain T22: socket-insert's real mint. The atom/affix rows come off the
        // live store (the same rows the delve mint reads); the seed rides the single gem load
        // above, so the mint resolves the same entry the card names; the tuning is the Hub's own.
        // P8.1: `forge`'s cells are wired here too — see the derivation above.
        lookupAtom: store.GetAtom,
        lookupAffix: store.GetAffix,
        lookupGemSeed: id => gemInserts(id)?.Seed,
        gemPowerTuning: FusionRpg.Core.Power.PowerTuningHub.Tuning,
        forgeMintCells: forgeMintCells,
        forgePowerTuning: FusionRpg.Core.Power.PowerTuningHub.Tuning,
        craftWear: craftWear,
        // species-gear-chain T13: the milestone append's two lookups — built above from the base-type
        // seed JSON and the store's own atom rows.
        baseTypeEnhanceTrack: baseTypeEnhanceTrack,
        milestoneAtomFor: milestoneAtomFor,
        // species-gear-chain T37: Rule 1's production input — each affix family's own `roles`
        // allow-list off the same shipped corpus the mint cells derive from, so the upgrade's
        // legality filter IS the drop path's filter rather than a second vocabulary.
        affixFamilyRoles: affixFamilyRoles,
        baseTypeImplicitFamily: baseTypeImplicitFamily);
}
// item-ideal.md, item-card (module 10): N1's item_display_template, seeded from the already-shipped
// gk-data/packs/fusion/data/seed/items/display-templates/*.json (98 rows, one per affix family) -- never re-authored here.
{
    var displayTemplatesDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "display-templates");
    if (Directory.Exists(displayTemplatesDir))
    {
        var rows = Directory.EnumerateFiles(displayTemplatesDir, "*.json")
            .SelectMany(f => FusionRpg.Core.Items.Display.DisplayTemplates.Parse(File.ReadAllText(f)))
            .ToList();
        store.SeedItemDisplayTemplates(rows);
    }
}

// item-ideal.md, drop-volume (module 11): the runtime drop-table corpus (data/seed/loot/tables*.json).
// Validated whole and imported in one transaction — E14's all-or-nothing policy. Never fatal: a
// broken loot corpus must not take the server down, the same rule the content boot below follows.
{
    var lootDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "loot");
    if (Directory.Exists(lootDir))
    {
        try
        {
            var corpus = FusionRpg.Core.Items.Drops.LootCorpusReader.Merge(
                Directory.EnumerateFiles(lootDir, "tables*.json")
                    .Select(f => FusionRpg.Core.Items.Drops.LootCorpusReader.Parse(File.ReadAllText(f))));
            store.ImportLootCorpus(corpus, dropVolumeTuning, new FusionRpg.Core.Items.Drops.DropContentLookups(
                CurrencyExists: id => string.Equals(id, "souls", StringComparison.Ordinal),
                RarityIdExists: FusionRpg.Core.Items.RarityLadder.RungIds.Contains,
                // species-gear-chain T30b: bounds-checks a parametric trophy entry's own authored
                // slot against the injected registry (configured above) rather than a
                // perSpecies/perFamily count this file never reads.
                TrophySlotExists: FusionRpg.Core.Items.Materials.MaterialCatalog.IsTrophySlotIssuable));
            Console.WriteLine($"[loot] imported {corpus.Tables.Count} drop tables, {corpus.Sources.Count} loot sources");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[loot] drop-table import failed — no loot tables loaded: {ex.Message}");
        }
    }
    else
    {
        // Loud, never fatal — the same rule the action corpus above follows. This guard used to skip in
        // silence, so a published build with no drop tables looked exactly like a healthy one.
        Console.Error.WriteLine(
            $"[loot] {lootDir} is missing next to the exe — the drop-table corpus was NOT imported "
            + "(check FusionRpg.Server.csproj's data\\seed\\loot copy rule)");
    }
}

// item-ideal.md, threshold-grants (module 12): the authored set catalog
// (gk-data/packs/fusion/data/seed/items/sets/*.json) into ssot-sets.md §4.2's three tables. Never fatal, same rule as the
// loot corpus above. ⛔ The tier CONTAINERS these ids name cannot be bound yet — ContainerKind ships
// six values and D27's `set` is not one of them (X7, effect-atom's own ask) — so this populates the
// breakpoint table the evaluator reads and stops there. A wiring gap, named, not a silent drop.
{
    var setsDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "sets");
    if (Directory.Exists(setsDir))
    {
        try
        {
            var sets = Directory.EnumerateFiles(setsDir, "*.json")
                // The generation ledger lives beside the partitions but is not a corpus document.
                // Parsing it as a set file makes one metadata file abort the entire import.
                .Where(f => !Path.GetFileName(f).EndsWith(".ledger.json", StringComparison.OrdinalIgnoreCase))
                .SelectMany(f => FusionRpg.Core.Items.Thresholds.SetCorpus.Parse(File.ReadAllText(f)))
                .ToList();
            store.ImportSetCorpus(sets);
            Console.WriteLine($"[sets] imported {sets.Count} item sets, "
                              + $"{sets.Sum(s => s.Members.Count)} members, {sets.Sum(s => s.Tiers.Count)} tiers");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sets] set corpus import failed — no sets loaded: {ex.Message}");
        }
    }
}

// item-ideal.md, charm-carry (module 22): the authored charm catalog (gk-data/packs/fusion/data/seed/items/charms/*.json)
// into ssot-charms.md §4.2's charm_def and charm_resonance. Never fatal, same rule as the two corpus
// imports above. `resonance.json` is a SEPARATE population and is deliberately imported only into
// charm_resonance — §4.2: "a `charm.` container with no charm_def row is not attunable", which is how
// a resonance tier can be granted BY the pouch and never sit in it. ⛔ The containers these ids name
// still cannot be bound: ContainerKind ships six values and D27's `charm` is not one of them (X7,
// effect-atom's own ask), so this populates the def and breakpoint tables the gate and the evaluator
// read, and stops there. A wiring gap, named, not a silent drop.
{
    var charmsDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "charms");
    if (Directory.Exists(charmsDir))
    {
        try
        {
            var defs = new List<FusionRpg.Core.Items.Thresholds.CharmDef>();
            var resonance = new List<FusionRpg.Core.Items.Thresholds.CharmResonanceRow>();

            foreach (var f in Directory.EnumerateFiles(charmsDir, "*.json")
                         .Where(f => !Path.GetFileName(f).EndsWith(".ledger.json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var json = File.ReadAllText(f);
                if (Path.GetFileNameWithoutExtension(f).Equals("resonance", StringComparison.OrdinalIgnoreCase))
                    resonance.AddRange(FusionRpg.Core.Items.Thresholds.CharmResonance.DeriveTable(json));
                else
                    defs.AddRange(FusionRpg.Core.Items.Thresholds.CharmCorpus.Parse(json));
            }

            foreach (var d in defs)
            {
                var fails = FusionRpg.Core.Items.Thresholds.CharmPouchGate.ValidateForCarry(d, charmAttunementTuning);
                foreach (var fail in fails) Console.WriteLine($"[charms] {fail}");
            }

            store.ImportCharmCorpus(defs, resonance);
            Console.WriteLine($"[charms] imported {defs.Count} attunable charms, "
                              + $"{resonance.Count} resonance breakpoints");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[charms] charm corpus import failed — no charms loaded: {ex.Message}");
        }
    }
}

// E46 (player-content-boot): AtomImporter was only ever invoked from a dev script
// (gk-fusion/scripts/deploy-play.py), never from a player's own install — so a real player's content tables
// were never populated and the server ran forever on the shipped code fallback with nothing saying
// so. Self-heal here, gated on catalog_revision so a normal relaunch never re-imports (that would
// bump the revision and make every already-rolled effect_instance unbindable,
// spec-player-content-boot.md §4): revision 0 means this is the first launch since unzip (or a
// corrupted/skipped install), so this is also the "first-run repair" the spec's own §3.1 asks for —
// for this launcher there is no separate install step, so both happen at the same moment, in the
// same code path. RunSelfHealing never throws: a broken or missing seed tree must not take the
// server down with it (§3.2, §4) — the fallback stays playable either way.
var contentBoot = SeedImportRunner.RunSelfHealing(store, AppContext.BaseDirectory);
store.RecordContentBootOutcome(contentBoot.ContentSource, contentBoot.Detail);

// The eager roster roll that used to sit here is GONE (species-progression SP0.4, owner ruling
// 2026-09-16): rolling every species' passive instance at boot made one player's fusion picks define a
// species for every player, and it refused every eligible fusion afterwards (defect C2). Layer 1b now
// comes from `rpg_player_species_mod` (the ledger) or `SpeciesRollPreview`, both written/computed only
// at the moment a pick is actually made, so a save that never fused has no 1b rows at all.
// `No_production_code_rolls_a_players_species_eagerly` (Guard.Tests) keeps this from coming back.
Console.WriteLine(contentBoot.Status switch
{
    SeedImportStatus.Imported =>
        $"[content] imported the seed tree — catalog now at revision {store.GetCatalogRevision()}",
    SeedImportStatus.AlreadyCurrent =>
        $"[content] catalog already at revision {store.GetCatalogRevision()} — no import needed",
    SeedImportStatus.SeedTreeNotFound =>
        $"[content] no seed tree found near the server — running on the shipped code fallback ({contentBoot.Detail})",
    _ => $"[content] seed import failed — running on the shipped code fallback: {contentBoot.Detail}",
});

// H9's own "committed" acceptance bullet (passive-tree-todo.md, found 2026-09-07): ImportTreeCatalog/
// ImportTreeCatalogFiles (task C4/C5) had no production caller anywhere — a bound
// gk-data/packs/fusion/data/generated/passive-tree/*.json catalog never reached the store on its own. Independent of the
// atom-content self-heal above and never gating it or being gated by it, matching this codebase's own
// established "a lint, never a gate" pattern for boot-time content checks: H9's corpus is still
// partial, and a tree-catalog import finding nothing (or failing) must never slow or block anything
// else in content boot. Never throws, for the identical reason SeedImportRunner.RunSelfHealing doesn't.
var treeBoot = PassiveTreeImportRunner.RunSelfHealing(store, AppContext.BaseDirectory);

{
    // item-ideal.md, strain-splice-gen (module 21) + recipe-import (module 3): read the authored
    // `gk-data/packs/fusion/data/seed/items/combinations/*.json` corpus, validate every recipe against the DERIVED grid,
    // seed the accepted ones beside the 25 generated resonances, and retire any authored row this
    // boot no longer accepts. ⛔ "Print and skip", never "print and seed": a row the grid refuses is
    // content no cell asked for, and the CI contract (`the_shipped_corpus_has_no_refusal`) is what
    // keeps the skip path unreachable from a committed corpus. The archetype axis is READ from module
    // 13's registry here, never declared in Core (§7.2).
    // SSH4.4 (spec-combo-bind §1): the container build resolves every grant family against the SHIPPED
    // atom catalog — read from disk here, because the store's `effect_atom` import runs later in boot
    // and the containers must be written BEFORE the recipe seed (one acceptance set).
    var comboAtomById = new Dictionary<string, FusionRpg.Core.Effects.Atoms.AtomRow>(StringComparer.Ordinal);
    var comboAtomSeedDir = Path.Combine(AppContext.BaseDirectory, "data", "seed", "atoms");
    if (Directory.Exists(comboAtomSeedDir))
    {
        var comboAtomFiles = Directory.EnumerateFiles(comboAtomSeedDir, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path: f, Json: File.ReadAllText(f)));
        foreach (var atom in FusionRpg.Core.Effects.Atoms.AtomSeedFile.Collect(comboAtomFiles).Content.Atoms)
            comboAtomById[atom.AtomId] = atom;
    }
    var comboContainerLookups = new FusionRpg.Core.Items.Sockets.ComboContainerBuild.ComboContainerLookups(
        id => comboAtomById.TryGetValue(id, out var atom) ? atom : null);

    FusionRpg.Server.CombinationBoot.Seed(
        store, socketTuning, strainSpliceTuning,
        FusionRpg.Server.CombinationBoot.ReadArchetypes(AppContext.BaseDirectory),
        Path.Combine(AppContext.BaseDirectory,
            FusionRpg.Server.CombinationBoot.CombinationsRelativeDir),
        comboContainerLookups,
        Console.WriteLine);
}

// strain-splice-host SSH6.7 (spec-combo-budget §5): every input is loaded now — sockets,
// strain-splice, materials and the combination set the store just accepted — so the pricing's
// provenance is checked BEFORE anything binds. A measurement taken against another revision or
// another corpus is a start failure that names the field; today's files carry no `comboPricing` at
// all (one rung, nothing to bind against), so this boots exactly as it did before the check existed.
{
    var combinationsDir = Path.Combine(AppContext.BaseDirectory,
        FusionRpg.Server.CombinationBoot.CombinationsRelativeDir);
    var grantsById = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    if (Directory.Exists(combinationsDir))
    {
        foreach (var file in Directory.GetFiles(combinationsDir, "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var (id, granted) in
                     FusionRpg.Core.Items.Sockets.CombinationCorpus.ReadGrants(File.ReadAllText(file)))
                grantsById[id] = granted;
        }
    }

    // The ACCEPTED set — what the store holds, not what the files claim (recipe-import's own rule).
    var acceptedCombinations = store.GetComboRecipes()
        .Where(r => FusionRpg.Core.Items.Sockets.ComboShapes.IsStrainOrSplice(r.Shape))
        .ToList();

    var pricingLoaded = FusionRpg.Server.ComboPricingBoot.LoadedRevisions(
        new FusionRpg.Server.ComboPricingBoot.LoadedTuningFiles(
            socketTuningFileName, strainSpliceTuningFileName, materialTuningFileName),
        acceptedCombinations, grantsById);
    FusionRpg.Server.ComboPricingBoot.RequireVerified(
        socketTuning.ComboPricingMeasuredAgainst, pricingLoaded, strainSpliceTuning);
    Console.WriteLine(
        $"[combo] pricing provenance ok — sockets v{pricingLoaded.SocketsVersion}, " +
        $"strain-splice v{pricingLoaded.StrainSpliceVersion}, materials v{pricingLoaded.MaterialsVersion}, " +
        $"corpus {pricingLoaded.CombinationCorpusDigest[..12]}");
}
Console.WriteLine(treeBoot.Status switch
{
    PassiveTreeImportStatus.Imported =>
        $"[content] imported the passive-tree catalog — {treeBoot.Outcome!.TreesImported} tree(s), " +
        $"now at revision {store.GetTreeCatalogRevision()}",
    PassiveTreeImportStatus.AlreadyCurrent =>
        $"[content] passive-tree catalog already at revision {store.GetTreeCatalogRevision()} — no import needed",
    PassiveTreeImportStatus.TreeNotFound =>
        $"[content] no generated passive-tree catalog found near the server ({treeBoot.Detail})",
    _ => $"[content] passive-tree catalog import failed: {treeBoot.Detail}",
});

// E20: without this, ElementTable/PowerTables.Current never move off their shipped code copy, and
// an imported roster or coefficient row changes the content hash and nothing else (completeness
// audit A2). A store with nothing imported behaves exactly as before.
store.LoadContentIntoRuntime();
// item-ideal.md, item-power-reads (module 9): the rarity-keyed power budget, with a REAL ceilingFor.
// Runs here and not beside SeedRarityLadder above because it needs all three halves at once — the
// imported `rarity` rows and containers (the self-healing import two blocks up), the seeded
// `rarity_budget.power_ceiling` column, and the coefficients LoadContentIntoRuntime just published.
// A lint, never a gate: an over-budget container is reported naming its id and the server boots
// anyway (ContentValidation's own "a content test that fails naming the offender — and never a
// generation input"). The `evaluated` count is the load-bearing half of the line: it was
// structurally 0 for as long as no caller passed a ceilingFor at all.
{
    var rarityBudget = store.ValidateRarityPowerBudget();
    var ceilings = store.GetRarityPowerCeilings();
    Console.WriteLine(
        $"[items] {rarityBudget.Render("rarity power budget")} "
        + $"— {ceilings.PricedRungs} rung(s) priced, pinAE {ceilings.RenderPinAe()}");
}
// Fail fast on creature content errors: the catalogs are lazy, and a bad species surfacing on the
// first request would permanently poison WaveCatalog's static initializer (review I6).
_ = FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All;
// base-defense siege-waves §3.5 (task 12.4, 2026-09-06): wave composition moved out of a hand-written
// WaveCatalog.cs array into gk-core/data/tuning/waves.v1.json — species selection by rarity band still
// happens in Core (WaveCatalog.Band/Enemies, reused verbatim by the loader), only WHICH waves exist
// and their picks are now data. Must run after CreatureSpeciesCatalog.Configure above: Band() reads
// CreatureSpeciesCatalog.All. Same Loader.Parse(File.ReadAllText(...)) -> Configure(...) shape as every
// other gk-core/data/tuning/*.json load in this file.
FusionRpg.Core.Battle.WaveCatalog.Configure(
    FusionRpg.Core.Battle.WaveCatalogLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "waves.v1.json"))));
// ds 18 fusion-recipe-runtime §3 step 4 — THE FLIP, 2026-09-06: recipes now load from the committed
// seed fusion-recipe-reconcile owns writing, not a live BuildDeterministicOnly() recomputation
// (T8.4's own transitional call). Today's committed file carries 695 real deterministic recipes
// plus 14 real crossRungGapFill entries for every Almanac deficit — Checkpoint 8a's own propose
// pass, performed 2026-09-06 by reasoning directly (this environment cannot reach a live LM
// Studio endpoint), run through the unmodified reconcile()/vote/validate pipeline exactly like any
// other proposal source. A missing/unparseable file fails loudly here rather than falling back to
// the old live algorithm, matching spec-catalog-runtime.md's own binding rule for the sibling
// catalog.
{
    var fusionRecipesPath = Path.Combine(AppContext.BaseDirectory, "data", "generated", "creatures", "_fusion-recipes.json");
    if (!File.Exists(fusionRecipesPath))
        throw new InvalidOperationException(
            $"Fusion recipe seed not found at '{fusionRecipesPath}'. Run 'python tools/seedsmith/seedsmith/" +
            "adapters/creatures/fusion/reconcile.py' (add --deterministic-only for a model-free pass) against " +
            "the data directory this host points at.");
    var fusionRecipes = FusionRpg.Core.Creatures.Fusion.FusionRecipeSeedReader.Parse(File.ReadAllText(fusionRecipesPath));
    FusionRpg.Core.Creatures.Fusion.CreatureRecipeCatalog.Configure(fusionRecipes);
}
// Boot sweep: web matches logged but never ingested (crash window) re-resolve deterministically.
var sweptMatches = app.Services.GetRequiredService<WebMatchService>().SweepUnresolved();
if (sweptMatches > 0)
    Console.WriteLine($"[web-match] boot sweep re-ingested {sweptMatches} logged matches");
var portraits = app.Services.GetRequiredService<TypeIconStore>().BackfillPortraitsFromDumps();
if (portraits > 0)
    Console.WriteLine($"[icons] backfilled {portraits} portraits from dump layer 'image'");
// C1 (completeness-audit.md): entity: bindings are never durable — IL2CPP reuses the pointer, so
// one surviving a restart would attach to whatever object takes its address next. A fresh boot is
// exactly the moment every entity: binding from the previous process is guaranteed stale, whether
// the last shutdown was clean or a crash. A no-op today (nothing binds one yet — completeness-audit
// A4), and the cheap place for this to already be correct once something does.
var clearedBindings = app.Services.GetRequiredService<RpgStore>().ClearSessionScopedBindings();
if (clearedBindings > 0)
    Console.WriteLine($"[atoms] boot sweep cleared {clearedBindings} stale session-scoped binding(s)");
var orphanInstances = app.Services.GetRequiredService<RpgStore>().CountOrphanInstances();
if (orphanInstances > 0)
    Console.WriteLine($"[atoms] {orphanInstances} orphan instance(s) remain after the boot sweep");
// battle-hub-fuse T6: the old global BattleStatComposer.UseEquipment(EquippedBoundAtoms
// .SourceFromStore(...)) resolver is deleted with the composer -- it fed nothing once BattleEngine
// moved to BattleHubCompose in T5 (BattleHubCompose reads per-actor setup.HubInputs.BoundAtoms, not
// a global default). Wiring a real per-actor EquippedBoundAtoms source into battle setups' HubInputs
// is T7's named scope ("Battle equip path Hub op-aware only"), not re-created here.
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapStorageEndpoints();
app.MapUniqueActors();
app.MapRelics();
app.MapCreatures();
app.MapZombossDeploy();
app.MapSouls();
app.MapExpeditions();
app.MapFusion();
app.MapPatron();
app.MapCommanders();
app.MapContracts();
app.MapDelve();
app.MapDelveWild();
app.MapDelveBattle();
// combat-ai `commander-direct-orders` (module 20, CAI4.9): the web FE's order route, one relay to the
// injector over the same command seam as every other host command.
app.MapLawnOrders();
app.MapWorld();
app.MapWorldWarden();
app.MapNotifications();
app.MapAptitudes();
app.MapAptitudePresets();
app.MapBuildPresets();
app.MapPassiveTree();
app.MapGateCounters();
app.MapSpeciesBuild();
// empire-level EP4.7: the one read an empire level has (its row + the free-respec stock it paid for).
app.MapEmpireRoutes();
app.MapLoadout();
// A27 (specimen-loadout-endpoints, action-map.md §17): the earlier audit finding this task itself was
// built to close -- MapSpecimenLoadout() did nothing until this real call was added.
app.MapSpecimenLoadout();
// A28 (unlock-discard-endpoint, action-map.md §17): UnlockDiscardService (T20) had zero Server callers
// until this real call was added.
app.MapUnlockDiscard();
app.MapAuraDerived();
app.MapDerivedSurface();
// The actor-surface fan-in read the FE has requested since it landed and no host had ever mapped —
// decisions.md's "Actor-surface catalogs" row named it and marked it "still open". Unmapped it was
// invisible, not loud: the SPA fallback answered it 200 text/html, tryGetJson threw parsing HTML, and
// the client fell back to a fixture carrying no hudPresentation.
app.MapActorSurface();
app.MapAuraRuntime();
app.MapAuraCatalog();
app.MapOnboarding();
app.MapOverlay();
// item module 20 (`item-surfaces`) — READ-ONLY. No MapPost lives in that file: equipping, socketing
// and salvaging already have owners (modules 4, 16, 14), and a second write path through the
// presentation layer is the "second surface" this module exists to prevent.
app.MapItemSurfaces(itemSurfaceTuning, socketTuning, gemInserts, itemBaseTypes, socketMaxForBaseType);
// ⭐ item modules 14/15/16 — the WRITE half, and the production caller all three named as their
// shared blocker. Mapped only when the recipe corpus loaded: a workbench with no prices could only
// ever refuse, and a route that always refuses is worse than an absent one because it looks wired.
if (itemWorkbench is { } workbench) app.MapWorkbench(workbench);
// ⭐ item module 4 (`equip-assign`) — the WRITE half, and the production caller `SaveAssignment` /
// `RemoveAssignment` never had. Unconditional, unlike the workbench above: equipping needs no recipe
// corpus and no price, so there is no state in which these routes could only refuse.
app.MapItemEquip(new FusionRpg.Server.ItemEquipService(store));
// build-preset BP1.8 (spec-item-loadout-apply.md): the item loadout library's list/save/delete/
// preview/apply routes -- the route row spec-armoury.md:220 named but deferred to module 4.
app.MapItemLoadouts();
// ⭐ item modules 10 + 20 — the SEE and COMPARE surfaces. `ItemCardRenderer.Render`,
// `ItemCardCompare.Compare` and `DominancePresentation` all shipped tested with zero callers outside
// `tests/`, which is why every block of the web card rendered its honest "pending" state. These two
// read-only routes are their production caller. Unconditional, like the equip routes: an item card
// needs no recipe corpus and no price, so there is no state in which they could only refuse.
//
// ⏸ The two corpora are the same boot-time stopgap `BaseTypeSocketMaxCorpus` already is — module 6
// shipped the base-type corpus and module 16 the gem corpus as seed JSON, neither as a table.
// item-lore T6: N2's string catalog (ssot-presentation.md §5.3), copied next to the exe the same way
// gk-core/data/tuning and gk-data/packs/fusion/data/seed/items already are. Absent degrades to "no sentence resolved" — the card's
// flavour line still carries its key and nothing is invented, which is the same
// absence-degrades-never-guesses rule DisplayCheck applies to the very same file.
var displayStrings = FusionRpg.Server.DisplayStringCatalogFile.Load(
    Path.Combine(AppContext.BaseDirectory, "content", "display", "en.json"));
// ⭐ item-content T1: the two corpora module 8's `ItemNameComposer` needed and nobody loaded, which is
// the whole reason a rolled item had no name. Same boot-time stopgap shape as the base-type and gem
// corpora above — the `nameWords` half is the `item_affix_name` PROJECTION `AffixNameTable`'s own doc
// describes and no importer ever built; the rare half is a head/tail word table that did not exist in
// the seed tree at all until today.
//
// ⛔ A malformed family is reported and naming degrades to the base type's authored name — it never
// takes the process down. Same posture as the recipe-corpus import above: a card that reads
// "Card-Proof Blade" instead of "Sap Tangle" is a worse card; a server that will not start is no game.
Func<string, FusionRpg.Core.Items.AffixNameSlot?>? affixNameWords = null;
try
{
    affixNameWords = FusionRpg.Server.AffixNameWordCorpus.Load(
        Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "affix-families"));
}
catch (Exception ex)
{
    Console.WriteLine($"[items] affix nameWords corpus failed to load — item naming stays off: {ex.Message}");
}

var rareNameDraw = FusionRpg.Server.RareNameCorpus.Load(
    Path.Combine(AppContext.BaseDirectory, "data", "seed", "items", "rare-names", "rare-names.json"));
if (affixNameWords is null || rareNameDraw is null)
    Console.WriteLine(
        "[items] item naming is OFF — every card falls back to its base type's authored name. " +
        "Both halves are required: the families' nameWords AND rare-names.json (a 3+ affix item has " +
        "no honest two-word name without the second).");
var itemCardCorpus = new ItemCardCorpus(
    itemBaseTypes,
    gemInserts,
    socketTuning, itemSurfaceTuning, enhancementTuning,
    LookupString: displayStrings,
    LookupNameWords: affixNameWords,
    RareNameDraw: rareNameDraw);
app.MapItemCard(new FusionRpg.Server.ItemCardService(store, itemCardCorpus, socketMaxFor: socketMaxForBaseType));
// strain-splice-host SSH4.6 (spec-combo-bind §2): the equip projection evaluates each worn host's
// combinations with the SAME socket tuning and gem lookup the card path uses, so a firing word binds
// through the one projection that carries the host and its inserts — never a parallel Bind. Set once
// at boot; without it no combination binds, which is the pre-SSH4.6 shape rather than an error.
store.UseEquipCombinationEvaluation(new FusionRpg.Data.RpgStore.EquipCombinationInputs(
    socketTuning, itemCardCorpus.LookupInsert));
// ⭐ item-content module `atom-preview` — the same renderer, over an UNSAVED container. It shares the
// corpus above rather than loading a second one: a preview that read a different base-type corpus than
// the live card would preview something the game does not ship.
//
// ⛔ Read-only despite being a POST. The body is a whole container definition, which is why it cannot
// be a GET; nothing it receives is persisted anywhere.
app.MapItemPreview(new FusionRpg.Server.ItemPreviewService(
    store, itemCardCorpus, FusionRpg.Core.Power.PowerTuningHub.Tuning));
PatronEndpoints.RefreshRuntimeState(app.Services.GetRequiredService<RpgStore>()); // SIM plugins read it

app.MapGet("/health", (RpgStore store, EventIngest ingest) => ingest.Decorate(store.ToHealth(SimFlags.Enabled)));

app.MapGet("/api/players", (RpgStore store) => new PlayersListDto
{
    Items = store.ListPlayers(),
    CurrentPlayerId = store.GetCurrentPlayerId()
});
app.MapPost("/api/players", (CreatePlayerRequest body, RpgStore store) =>
{
    if (string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "name required" });
    return Results.Ok(store.CreatePlayer(body.Name));
});
app.MapGet("/api/players/current", (RpgStore store) =>
{
    var p = store.GetCurrentPlayer();
    return p is null ? Results.NotFound() : Results.Ok(p);
});
app.MapPut("/api/players/current", async (SelectPlayerRequest body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    if (!store.SetCurrentPlayer(body.Id)) return Results.NotFound();
    // species-progression `species-layer-delivery` step 6.2, trigger 6 (SP6.6) — a save switch is a
    // key-set edge for every cache the ONE existing AptitudesUpdated fan-out already reaches
    // (commander, species, speciesLayers). save-identity's own strengthened design dropped the
    // "T2 signal" cache this task's own spec draft expected (ownership now travels per-spawn, never
    // cached) and no SE task emits a save-switch notice to the injector -- this is that ONE signal,
    // generic (kind "save"), never species-specific, so any later injector consumer reuses it rather
    // than growing a second one.
    _ = FusionRpg.Server.AptitudeEndpoints.BroadcastBestEffort(hub,
        new FusionRpg.Server.AptitudeEndpoints.AptitudesUpdatedDto(body.Id, "save", null, null));
    // live-probe Task 24 (2026-09-16, reproven live 2026-09-20): the broadcast above reaches the
    // injector's "AptitudesUpdated" listener, but that handler only ever enqueues
    // aptitudes.allocation.reload -- it never touches CheatState.CurrentPlayerId, which is set ONLY
    // by RpgClient.RefreshPowerIndexAsync (session start, reconnect, or an explicit
    // power.index.reload command). Nothing sent that command on a player switch before this line, so
    // every measurement after switching players was silently attributed to the boot-time player.
    // Sent as its own command (not folded into the "save"-scope AptitudesUpdated handler) because
    // that handler already carries mid-match deferral logic for save switches, and CurrentPlayerId
    // must update immediately regardless of match state -- it is not aptitude allocation.
    await SendInjectorCommand(hub, inbox, new CommandDto
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "power.index.reload"
    });
    return Results.Ok(store.GetCurrentPlayer());
});

// --- PvzStats: player-bound modifier SSOT + derived sheet ---
app.MapGet("/api/pvz-stats/current", (RpgStore store) =>
{
    var id = store.GetCurrentPlayerId();
    var sheet = store.GetPvzStatsSheet(id);
    return sheet is null ? Results.NotFound() : Results.Ok(sheet);
});
app.MapGet("/api/pvz-stats/{playerId:long}", (long playerId, RpgStore store) =>
{
    var sheet = store.GetPvzStatsSheet(playerId);
    return sheet is null ? Results.NotFound() : Results.Ok(sheet);
});
app.MapGet("/api/pvz-stats/{playerId:long}/channels/{channel}", (long playerId, string channel, RpgStore store) =>
{
    var detail = store.GetPvzStatsChannel(playerId, channel);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});
app.MapGet("/api/pvz-stats/{playerId:long}/modifiers", (long playerId, RpgStore store) =>
{
    var mods = store.GetPvzStatsModifiers(playerId);
    return mods is null ? Results.NotFound() : Results.Ok(mods);
});
app.MapPost("/api/pvz-stats/{playerId:long}/modifiers/upsert", async (long playerId, PvzStatModifierDto body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    try
    {
        var sheet = store.UpsertPvzStatModifier(playerId, body);
        await BroadcastPvzStats(store, hub, playerId, sheet.Revision);
        await SendInjectorCommand(hub, inbox, new CommandDto { Name = "pvz.stats.reload", Payload = new { playerId, revision = sheet.Revision } });
        return Results.Ok(sheet);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException)
    {
        return Results.NotFound();
    }
});
app.MapPost("/api/pvz-stats/{playerId:long}/modifiers/withdraw", async (long playerId, JsonElement body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    try
    {
        var sourceKind = body.TryGetProperty("sourceKind", out var sk) ? sk.GetString() : null;
        var sourceId = body.TryGetProperty("sourceId", out var sid) ? sid.GetString() : null;
        var channel = body.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
        var op = body.TryGetProperty("op", out var opEl) ? opEl.GetString() : null;
        var pluginId = body.TryGetProperty("pluginId", out var pl) ? pl.GetString() : null;
        var sheet = store.WithdrawPvzStatModifiers(playerId, sourceKind, sourceId, channel, op, pluginId);
        await BroadcastPvzStats(store, hub, playerId, sheet.Revision);
        await SendInjectorCommand(hub, inbox, new CommandDto { Name = "pvz.stats.reload", Payload = new { playerId, revision = sheet.Revision } });
        return Results.Ok(sheet);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException)
    {
        return Results.NotFound();
    }
});
app.MapPost("/api/pvz-stats/{playerId:long}/modifiers/reset", async (long playerId, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    try
    {
        var sheet = store.ResetPvzStats(playerId);
        await BroadcastPvzStats(store, hub, playerId, sheet.Revision);
        await SendInjectorCommand(hub, inbox, new CommandDto { Name = "pvz.stats.reload", Payload = new { playerId, revision = sheet.Revision } });
        return Results.Ok(sheet);
    }
    catch (InvalidOperationException)
    {
        return Results.NotFound();
    }
});

// --- PvzActivity: append-only facts + rollup cache ---
app.MapGet("/api/pvz-activity/current", (RpgStore store) =>
{
    var id = store.GetCurrentPlayerId();
    var rollup = store.GetPvzActivityRollup(id);
    return rollup is null ? Results.NotFound() : Results.Ok(rollup);
});
app.MapGet("/api/pvz-activity/{playerId:long}", (long playerId, RpgStore store) =>
{
    var rollup = store.GetPvzActivityRollup(playerId);
    return rollup is null ? Results.NotFound() : Results.Ok(rollup);
});
app.MapGet("/api/pvz-activity/{playerId:long}/facts", (long playerId, RpgStore store, string? kind, long? runId, int limit = 100, long afterId = 0) =>
{
    var page = store.ListPvzActivityFacts(playerId, kind, runId, limit, afterId);
    return page is null ? Results.NotFound() : Results.Ok(page);
});
app.MapPost("/api/pvz-activity/{playerId:long}/facts/append", async (long playerId, PvzActivityAppendRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
{
    try
    {
        var result = store.AppendPvzActivityFact(playerId, body);
        await BroadcastPvzActivity(store, hub, playerId, result.Rollup.Revision);
        foreach (var d in result.Progression)
        {
            await BroadcastRpgProgression(hub, d.PlayerId, d.Kind, d.TypeId, d.Revision);
            // empire-level EP4.7: a level crossing rides THIS dirty (EP4.3) and is emitted after the
            // append committed -- the same record-then-drain discipline every other notification on this
            // path uses. The payload itself lives in EmpireLevelBroadcast so a test can assert its shape
            // across the real hub while this route stays the production caller.
            await EmpireLevelBroadcast.SendEmpireLevelUpsAsync(hub, store, result.Progression);
        }        return Results.Ok(result.Rollup);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException)
    {
        return Results.NotFound();
    }
});

// --- RpgProgression ---
app.MapGet("/api/rpg/progression/{playerId:long}/summary", (long playerId, RpgStore store) =>
{
    var s = store.GetRpgProgressionSummary(playerId);
    return s is null ? Results.NotFound() : Results.Ok(s);
});
app.MapGet("/api/rpg/progression/{playerId:long}/stats", (long playerId, RpgStore store) =>
{
    var s = store.GetRpgProgressionStats(playerId);
    return s is null ? Results.NotFound() : Results.Ok(s);
});
app.MapGet("/api/rpg/progression/{playerId:long}", (long playerId, RpgStore store, string? kind, string sort = "level", int limit = 200, int offset = 0) =>
{
    var list = store.ListRpgProgression(playerId, kind, sort, limit, offset);
    return list is null ? Results.NotFound() : Results.Ok(list);
});
app.MapGet("/api/rpg/progression/{playerId:long}/ledger", (long playerId, RpgStore store, string? kind, int? typeId, string? reason, int limit = 100, long? afterId = null) =>
{
    var page = store.ListRpgXpLedger(playerId, kind, typeId, reason, limit, afterId);
    return page is null ? Results.NotFound() : Results.Ok(page);
});
app.MapGet("/api/rpg/progression/{playerId:long}/{kind}/{typeId:int}", (long playerId, string kind, int typeId, RpgStore store) =>
{
    var row = store.GetRpgActor(playerId, kind, typeId);
    return row is null ? Results.NotFound() : Results.Ok(row);
});
app.MapPost("/api/rpg/progression/{playerId:long}/{kind}/{typeId:int}/clear-demotion", async (long playerId, string kind, int typeId, RpgStore store, IHubContext<RpgHub> hub) =>
{
    var row = store.ClearRpgDemotion(playerId, kind, typeId);
    if (row is null) return Results.NotFound();
    await BroadcastRpgProgression(hub, playerId, kind, typeId, row.Revision);
    return Results.Ok(row);
});

// --- PvzIntent: game write commands ---
app.MapPost("/api/pvz-intent/spawn-extra", async (PvzSpawnExtraRequest body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox, HttpContext http) =>
{
    var playerId = body.PlayerId ?? store.GetCurrentPlayerId();
    if (!store.PlayerExists(playerId)) return Results.NotFound();
    var correlationId = string.IsNullOrWhiteSpace(body.CorrelationId)
        ? Guid.NewGuid().ToString("N")
        : body.CorrelationId.Trim();
    var reason = string.IsNullOrWhiteSpace(body.Reason) ? "extra" : body.Reason.Trim();
    var side = string.IsNullOrWhiteSpace(body.Side) ? "zombie" : body.Side.Trim().ToLowerInvariant();
    try
    {
        var (rollup, inserted) = store.RecordExtraSpawnIntent(playerId, correlationId, body.TypeId, reason, side);
        if (inserted)
            await BroadcastPvzActivity(store, hub, playerId, rollup.Revision);

        if (inserted)
        {
            var payload = new
            {
                typeId = body.TypeId,
                col = body.Col,
                row = body.Row,
                reason,
                correlationId,
                side,
                playerId,
                source = "extra"
            };
            await SendInjectorCommand(hub, inbox, new CommandDto
            {
                Id = correlationId,
                Name = "pvz.spawn.extra",
                Payload = payload
            });

            // No live injector: drive sim so CI can prove source=extra spawn without Unity.
            if (!store.LiveInjector && SimFlags.Enabled)
            {
                var sim = http.RequestServices.GetService<SimService>();
                if (sim is not null)
                {
                    await sim.RunAsync(stats => sim.Engine.SpawnZombie(stats, new SimSpawnZombieRequest
                    {
                        Type = body.TypeId,
                        Source = "extra",
                        Ptr = "extra-" + correlationId
                    }));
                }
            }
        }

        return Results.Ok(new { ok = true, correlationId, inserted, rollup });
    }
    catch (InvalidOperationException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/stats", (RpgStore store) => store.GetStats());
app.MapPut("/api/stats", async (StatsConfig body, RpgStore store, IHubContext<RpgHub> hub) =>
{
    store.PutStats(body);
    await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("StatsUpdated", body);
    return Results.Ok(body);
});

app.MapPost("/api/events", (JsonElement body, EventIngest ingest) =>
{
    var accepted = 0;
    var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("events", out var arr) && arr.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in arr.EnumerateArray())
            accepted += AcceptOne(item, ingest, json);
    }
    else
    {
        accepted += AcceptOne(body, ingest, json);
    }
    return Results.Ok(new { accepted });
});

app.MapGet("/api/events", (RpgStore store, int limit = 100, long afterId = 0, long? playerId = null) =>
    new { items = store.ListEvents(limit, afterId, playerId) });

app.MapGet("/api/runs", (RpgStore store, long? playerId) => new { items = store.ListRuns(playerId) });
app.MapGet("/api/types", (RpgStore store, string? side) => new { items = store.ListTypes(side) });

app.MapGet("/api/icons/dump", (TypeIconStore icons, string? side) =>
    Results.Ok(new { items = icons.ListDumps(side) }));

app.MapGet("/api/icons/dump/{side}/{typeId:int}", (string side, int typeId, TypeIconStore icons) =>
{
    if (!TypeIconStore.IsValidSide(side) || typeId < 0) return Results.BadRequest();
    var dump = icons.GetDump(side, typeId);
    return dump is null ? Results.NotFound() : Results.Ok(dump);
});

app.MapGet("/api/icons/dump/{side}/{typeId:int}/layer/{layer}", (string side, int typeId, string layer, TypeIconStore icons) =>
{
    if (!TypeIconStore.IsValidSide(side) || typeId < 0) return Results.BadRequest();
    var png = icons.GetLayerPng(side, typeId, layer);
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
});

app.MapPut("/api/icons/dump/{side}/{typeId:int}", async (string side, int typeId, HttpRequest req, TypeIconStore icons, IHubContext<RpgHub> hub) =>
{
    if (!TypeIconStore.IsValidSide(side) || typeId < 0) return Results.BadRequest(new { error = "bad side/typeId" });
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        var (created, layerCount, url, portraitSet) = await icons.SaveDumpAsync(side, typeId, doc.RootElement);
        await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("TypeIconUpdated", new
        {
            side = icons.NormalizeSide(side),
            typeId,
            dump = true,
            created,
            layerCount,
            portraitSet,
            url
        });
        return Results.Ok(new
        {
            created,
            layerCount,
            portraitSet,
            url,
            side = icons.NormalizeSide(side),
            typeId,
            composedUrl = portraitSet ? $"/api/icons/{icons.NormalizeSide(side)}/{typeId}.png" : null
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/icons/{side}/{typeId:int}.png", (string side, int typeId, TypeIconStore icons) =>
{
    if (!TypeIconStore.IsValidSide(side) || typeId < 0) return Results.BadRequest();
    var png = icons.GetComposedPng(side, typeId);
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
});

app.MapGet("/api/icons/{side}/{typeId:int}", (string side, int typeId, TypeIconStore icons) =>
{
    if (!TypeIconStore.IsValidSide(side) || typeId < 0) return Results.BadRequest();
    var composed = icons.GetComposedPng(side, typeId);
    if (composed != null)
        return Results.Ok(new { side = icons.NormalizeSide(side), typeId, url = $"/api/icons/{icons.NormalizeSide(side)}/{typeId}.png", composed = true });
    var dump = icons.GetDump(side, typeId);
    if (dump is null) return Results.NotFound();
    return Results.Ok(new { side = icons.NormalizeSide(side), typeId, dump = true, layers = dump.Layers.Count, url = $"/api/icons/dump/{icons.NormalizeSide(side)}/{typeId}" });
});

app.MapGet("/api/recipes", (RpgStore store) => new { items = store.ListRecipes() });

app.MapGet("/api/almanac/dump", (RpgStore store, string? side) =>
    Results.Ok(new { items = store.ListAlmanacTextDumps(side) }));

app.MapGet("/api/almanac/dump/{side}/{typeId:int}", (string side, int typeId, RpgStore store) =>
{
    if (!TypeIconStore.IsValidSide(side) || typeId < 0) return Results.BadRequest();
    var dump = store.GetAlmanacTextDump(side, typeId);
    return dump is null ? Results.NotFound() : Results.Ok(dump);
});

app.MapPut("/api/almanac/dump/{side}/{typeId:int}", async (string side, int typeId, HttpRequest req, RpgStore store, IHubContext<RpgHub> hub) =>
{
    if (!TypeIconStore.IsValidSide(side) || typeId < 0) return Results.BadRequest(new { error = "bad side/typeId" });
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        if (!doc.RootElement.TryGetProperty("fields", out var fieldsEl) || fieldsEl.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "fields required" });

        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in fieldsEl.EnumerateObject())
            fields[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();

        Dictionary<string, string>? sources = null;
        if (doc.RootElement.TryGetProperty("sources", out var srcEl) && srcEl.ValueKind == JsonValueKind.Object)
        {
            sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in srcEl.EnumerateObject())
            {
                var v = p.Value.GetString();
                if (!string.IsNullOrWhiteSpace(v)) sources[p.Name] = v!;
            }
        }

        if (fields.Count == 0) return Results.BadRequest(new { error = "empty fields" });
        var created = !store.HasAlmanacTextDump(side, typeId);
        store.UpsertAlmanacTextDump(side, typeId, fields, sources);
        var sNorm = side.Trim().ToLowerInvariant();
        await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("AlmanacTextUpdated", new
        {
            side = sNorm,
            typeId,
            created,
            fieldCount = fields.Count
        });
        return Results.Ok(new { created, fieldCount = fields.Count, side = sNorm, typeId, url = $"/api/almanac/dump/{sNorm}/{typeId}" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapGet("/api/almanac/seed", (RpgStore store, string? side) =>
    Results.Ok(new { contractVersion = RpgStore.AlmanacSeedContractVersion, items = store.ListAlmanacSeed(side) }));

app.MapGet("/api/almanac/seed/{side}/{typeId:int}", (string side, int typeId, RpgStore store) =>
{
    if (!TypeIconStore.IsValidSide(side) || typeId < 0) return Results.BadRequest();
    var dto = store.GetAlmanacSeed(side, typeId);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

app.MapPost("/api/almanac/seed/rebuild", (RpgStore store) => Results.Ok(store.RebuildAlmanacSeed()));

app.MapPost("/api/almanac/seed/enrich", (RpgStore store) =>
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "seed", "external-reference", "almanac-enrichment", "pvz-fusion-almanac-3.6.1.json")))
        dir = dir.Parent;
    if (dir is null)
        return Results.Problem("enrichment export file not found (data/seed/external-reference/almanac-enrichment/pvz-fusion-almanac-3.6.1.json)", statusCode: 404);

    var path = Path.Combine(dir.FullName, "data", "seed", "external-reference", "almanac-enrichment", "pvz-fusion-almanac-3.6.1.json");
    List<AlmanacEnrichmentImportRow>? rows;
    try
    {
        var json = File.ReadAllText(path);
        rows = JsonSerializer.Deserialize<List<AlmanacEnrichmentImportRow>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (Exception ex)
    {
        return Results.Problem("failed to read/parse enrichment export: " + ex.Message, statusCode: 400);
    }
    if (rows is null) return Results.Problem("enrichment export deserialized to null", statusCode: 400);

    var summary = store.ImportAlmanacEnrichment(rows, "pvz-fusion-almanac-3.6.1");
    return Results.Ok(new { matched = summary.Matched, unmatched = summary.Unmatched });
});

app.MapGet("/api/runs/{id:long}/spawns", (long id, RpgStore store) => new { items = store.ListSpawnStats(id) });
app.MapGet("/api/metrics", (RpgStore store) => new { items = store.ListMetrics() });
app.MapPost("/api/heartbeat", (HeartbeatDto? body, RpgStore store) =>
{
    store.Heartbeat(body?.Source);
    return Results.Ok(new { ok = true, source = store.Source });
});
app.MapPost("/api/metrics", (JsonElement body, RpgStore store) =>
{
    if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in arr.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var value = item.TryGetProperty("value", out var v) && v.TryGetDouble(out var d) ? d : 0;
            store.UpsertMetric(name, value);
        }
    }
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/commands/reload-stats", async (RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    var stats = store.GetStats();
    await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("StatsUpdated", stats);
    await SendInjectorCommand(hub, inbox, new CommandDto { Name = "reload-stats" });
    return Results.Ok(new { ok = true });
});

// Injector polls this when SignalR server→client Command delivery fails.
app.MapGet("/api/cheats/commands/pending", (InjectorCommandInbox inbox) =>
    Results.Ok(new { items = inbox.Drain(64), remaining = inbox.Count }));

app.MapGet("/api/cheats", (RpgStore store) =>
{
    var json = store.GetCheatsJson();
    if (string.IsNullOrWhiteSpace(json))
        return Results.Ok(new { menuEnabled = true, entries = Array.Empty<object>(), catalog = new { } });
    return Results.Content(json, "application/json");
});

app.MapPut("/api/cheats", async (JsonElement body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    store.PutCheatsJson(body.GetRawText());
    await SendInjectorCommand(hub, inbox, new CommandDto
    {
        Name = "cheat.set",
        Payload = body
    });
    await BroadcastCheats(store, hub);
    return Results.Ok(new { ok = true });
});

// Injector telemetry only — catalog merge, never overwrite entries, never push CheatsUpdated to web.
// One-way cheats: web/server → injector → game (not game → FE).
app.MapPut("/api/cheats/mirror", (JsonElement body, RpgStore store) =>
{
    store.MergeCheatsCatalog(body);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/cheats/action", async (JsonElement body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    var actionName = body.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
    if (actionName is "reset-all")
    {
        store.PutCheatsJson("""{"menuEnabled":false,"revision":0,"entries":[]}""");
        await BroadcastCheats(store, hub);
    }
    else if (actionName is "reset-group")
    {
        var prefix = body.TryGetProperty("prefix", out var p) ? p.GetString() ?? "" : "";
        var raw = store.GetCheatsJsonRaw() ?? """{"menuEnabled":false,"revision":0,"entries":[]}""";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var dict = new Dictionary<string, object?>();
            long revision = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("entries")) continue;
                if (prop.NameEquals("revision") && prop.Value.TryGetInt64(out var r)) revision = r;
                else dict[prop.Name] = prop.Value.Clone();
            }
            var kept = new List<object>();
            if (doc.RootElement.TryGetProperty("entries", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(prefix) && id.StartsWith(prefix, StringComparison.Ordinal))
                        continue;
                    kept.Add(item.Clone());
                }
            }
            dict["entries"] = kept;
            dict["revision"] = revision + 1;
            dict["updatedAt"] = ServerClock.UtcNowDateTime.ToString("o");
            dict["menuEnabled"] = false;
            // Already bumped once — do not bump again inside PutCheatsJson.
            store.PutCheatsJson(System.Text.Json.JsonSerializer.Serialize(dict), bumpRevision: false);
            await BroadcastCheats(store, hub);
        }
        catch { /* still send command */ }
    }
    await SendInjectorCommand(hub, inbox, new CommandDto
    {
        Name = "cheat.action",
        Payload = body
    });
    return Results.Ok(new { ok = true, queued = inbox.Count });
});

app.MapPost("/api/cheats/toggle", async (JsonElement body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    var id = body.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
    var enabled = body.TryGetProperty("enabled", out var en) && en.GetBoolean();
    store.MergeCheatField(id, enabled, null);
    await SendInjectorCommand(hub, inbox, new CommandDto
    {
        Name = "cheat.toggle",
        Payload = body
    });
    await BroadcastCheats(store, hub);
    return Results.Ok(new { ok = true });
});

// ---- User settings (solid-remediation 2026-09-17) --------------------------------------------
// Owner: "Make new user setting module if we dont have centralize user settings". We did not: the
// `settings` table is singleton app state (stats/cheats/current_player_id) with no player column, and
// the one real preference that had shipped (lawnViewMode) lived in browser localStorage, which cannot
// reach the injector and does not follow the player. FusionRpg.Core.Settings.UserSettingKeys is the
// closed registry; RpgStore.UserSettings persists per player; these two endpoints are the surface.
app.MapGet("/api/settings", (RpgStore store) =>
{
    var playerId = store.GetCurrentPlayerId();
    var chosen = store.ListUserSettings(playerId);
    // Defaults are merged HERE rather than in the store, so "never chosen" stays visible to anyone
    // reading the table directly and a future default change is not silently pre-baked into old rows.
    var entries = FusionRpg.Core.Settings.UserSettingKeys.All.Select(def => new
    {
        key = def.Key,
        kind = def.Kind.ToString(),
        summary = def.Summary,
        value = chosen.TryGetValue(def.Key, out var raw) ? raw : def.DefaultJson,
        isDefault = !chosen.ContainsKey(def.Key),
    });
    return Results.Ok(new { playerId, entries });
});

app.MapPut("/api/settings", async (JsonElement body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    var key = body.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
    var def = FusionRpg.Core.Settings.UserSettingKeys.Find(key);
    if (def is null)
        return Results.BadRequest(new { ok = false, reason = $"unknown user setting '{key}'" });
    if (!body.TryGetProperty("value", out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        return Results.BadRequest(new { ok = false, reason = $"'{key}' is {def.Kind}; value must be a JSON boolean" });

    var enabled = v.GetBoolean();
    var playerId = store.GetCurrentPlayerId();
    store.SetUserSettingBool(playerId, key, enabled);

    // A setting the injector acts on is pushed as well as stored. Storing without pushing would leave
    // the running game disagreeing with the saved preference until the next restart.
    var injectorCommand = key switch
    {
        FusionRpg.Core.Settings.UserSettingKeys.WorldHud => "hud.world",
        FusionRpg.Core.Settings.UserSettingKeys.VisualEffects => "vfx.enabled",
        _ => null,
    };
    if (injectorCommand is not null)
    {
        await SendInjectorCommand(hub, inbox, new CommandDto
        {
            Name = injectorCommand,
            Payload = JsonSerializer.SerializeToElement(new { enabled }),
        });
    }

    return Results.Ok(new { ok = true, key, value = enabled });
});

app.MapPost("/api/cheats/set-float", async (JsonElement body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    var id = body.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
    double? fv = body.TryGetProperty("value", out var v) && v.TryGetDouble(out var d) ? d : null;
    store.MergeCheatField(id, true, fv);
    await SendInjectorCommand(hub, inbox, new CommandDto
    {
        Name = "cheat.set-float",
        Payload = body
    });
    await BroadcastCheats(store, hub);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/cheats/clear-field", async (JsonElement body, RpgStore store, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    var id = body.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
    store.ClearCheatField(id);
    await SendInjectorCommand(hub, inbox, new CommandDto
    {
        Name = "cheat.clear-field",
        Payload = body
    });
    await BroadcastCheats(store, hub);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/cheats/schema", () =>
    Results.Ok(new
    {
        fields = CheatSchema.All.Select(f => new
        {
            id = f.Id,
            role = f.Role.ToString(),
            kind = f.Kind,
            channel = f.Channel,
            op = f.Op,
            displayDefault = f.DisplayDefault,
            toggleDefault = f.ToggleDefault,
            groupPrefix = f.GroupPrefix
        }).ToList()
    }));

app.MapGet("/api/cheats/packs", () =>
    Results.Ok(new { items = ProbePacks.All.Select(ProbePacks.ToApiDto).ToList() }));

app.MapPost("/api/cheats/probe", async (JsonElement body, EventIngest ingest, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    var packId = body.TryGetProperty("packId", out var pidEl) ? pidEl.GetString() ?? "" : "";
    var pack = ProbePacks.Get(packId);
    if (pack == null)
        return Results.NotFound(new { error = "unknown pack", packId });

    var probeId = body.TryGetProperty("probeId", out var pr) && pr.ValueKind == JsonValueKind.String
        ? pr.GetString()!
        : Guid.NewGuid().ToString("N");

    ingest.Enqueue(new EventEnvelope
    {
        T = ServerClock.UtcNowDateTime.ToString("o"),
        Kind = "probe.start",
        Payload = new Dictionary<string, object>
        {
            ["probeId"] = probeId,
            ["packId"] = pack.Id,
            ["label"] = pack.Label,
            ["hint"] = pack.Hint,
            ["expectedKinds"] = pack.ExpectedKinds.ToList()
        }
    });

    await SendInjectorCommand(hub, inbox, new CommandDto
    {
        Name = "cheat.probe-begin",
        Payload = new { probeId, packId = pack.Id }
    });

    foreach (var step in pack.Steps)
    {
        var corr = Guid.NewGuid().ToString("N");
        if (step.Op is "toggle")
        {
            await SendInjectorCommand(hub, inbox, new CommandDto
            {
                Name = "cheat.toggle",
                Payload = new
                {
                    id = step.Id,
                    enabled = step.Enabled ?? true,
                    probeId,
                    packId = pack.Id,
                    correlationId = corr,
                    source = "pack"
                }
            });
        }
        else if (step.Op is "set-float")
        {
            await SendInjectorCommand(hub, inbox, new CommandDto
            {
                Name = "cheat.set-float",
                Payload = new
                {
                    id = step.Id,
                    value = step.Value ?? 0,
                    probeId,
                    packId = pack.Id,
                    correlationId = corr,
                    source = "pack"
                }
            });
        }
        else if (step.Op is "action")
        {
            var payload = new Dictionary<string, object?>
            {
                ["action"] = step.Action,
                ["probeId"] = probeId,
                ["packId"] = pack.Id,
                ["correlationId"] = corr,
                ["source"] = "pack"
            };
            if (step.Which != null) payload["which"] = step.Which;
            if (step.Value is { } sv) payload["value"] = sv;
            if (step.Add is { } sa) payload["add"] = sa;
            await SendInjectorCommand(hub, inbox, new CommandDto
            {
                Name = "cheat.action",
                Payload = payload
            });
        }
    }

    return Results.Ok(new
    {
        probeId,
        packId = pack.Id,
        label = pack.Label,
        hint = pack.Hint,
        expectedKinds = pack.ExpectedKinds,
        steps = pack.Steps.Count
    });
});

app.MapPost("/api/cheats/probe/end", async (JsonElement body, EventIngest ingest, IHubContext<RpgHub> hub, InjectorCommandInbox inbox) =>
{
    var probeId = body.TryGetProperty("probeId", out var pr) ? pr.GetString() : null;
    var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "web" : "web";
    ingest.Enqueue(new EventEnvelope
    {
        T = ServerClock.UtcNowDateTime.ToString("o"),
        Kind = "probe.end",
        Payload = new Dictionary<string, object?>
        {
            ["probeId"] = probeId ?? "",
            ["reason"] = reason
        }
    });
    await SendInjectorCommand(hub, inbox, new CommandDto
    {
        Name = "cheat.probe-end",
        Payload = new { probeId, reason }
    });
    return Results.Ok(new { ok = true, probeId, reason });
});

if (SimFlags.Enabled)
{
    app.MapSimAndProbes();
    // DM-F2 (owner ruling 2026-09-22, `tasks/rpg-simulator-decisions.md` F1 -> B): `MapSimEffect()`
    // used to sit OUTSIDE this guard while its siblings were inside it, so the six `/api/sim/effect/*`
    // routes existed on every server including a live owner run. That was drift, not intent — the
    // effect surface belongs on the same `FUSIONRPG_SIM=1` switch as `/api/sim` + `/api/test`, and
    // it is not a consumer anything needs while the sim is off.
    app.MapSimEffect();
}

// Debug endpoints are spawn/kill/mods control — loopback-only unless explicitly forced
// (2026-08-21 review I3: a 0.0.0.0 rebind must not expose them to the LAN unauthenticated).
var debugAllowed = listenUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                   || listenUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Environment.GetEnvironmentVariable("FUSIONRPG_DEBUG_REMOTE"), "1", StringComparison.Ordinal);
if (debugAllowed)
    app.MapDebug();
else
    Console.WriteLine("[debug] endpoints disabled on non-loopback bind (set FUSIONRPG_DEBUG_REMOTE=1 to force)");
app.MapPerf();

app.MapHub<RpgHub>("/hub/rpg");

// AN UNMATCHED /api PATH IS A MISSING ENDPOINT, NOT A CLIENT-SIDE ROUTE. Registered before the SPA
// fallback, which is otherwise unconstrained and answers EVERY unmatched path - including /api/... -
// with index.html at HTTP 200. Two consequences, and the second is the serious one.
//
// A typo'd or version-mismatched API call returned 200 and an HTML body, so a client parsing JSON
// failed far from the cause with a parse error instead of a 404. And every /api path "responded":
// measured on this build, /api/definitely-not-a-route returned HTTP 200 and the SPA shell, while
// /api/players returned real JSON. Any evidence gathered about the API through a status code was
// therefore unfalsifiable - the specific shape of proof this workspace forbids, where a successful
// response fabricates the precondition it appears to confirm.
//
// /hub is included because it is the other non-SPA prefix. MapHub is registered above, so the real
// hub still matches first and only genuinely unmatched hub paths terminate here.
app.MapMethods("/api/{**rest}", MissingApiMethods,
               () => Results.NotFound(new { error = "no such endpoint" }));
app.MapMethods("/hub/{**rest}", MissingApiMethods,
               () => Results.NotFound(new { error = "no such hub" }));

app.MapFallbackToFile("index.html");

try
{
    if (Environment.GetEnvironmentVariable("FUSIONRPG_NO_BROWSER") != "1")
    {
        var openUrl = listenUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "http://127.0.0.1:5088";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(openUrl) { UseShellExecute = true });
    }
}
catch { /* no default browser */ }

app.Run();

static async Task SendInjectorCommand(IHubContext<RpgHub> hub, InjectorCommandInbox inbox, CommandDto cmd)
{
    // One implementation lives in InjectorCommandSender (rift-gate overlay-hide needed the same
    // enqueue-and-send and the spec forbids a third copy). This thin shim keeps the ~17 existing
    // call sites unchanged.
    await new InjectorCommandSender(hub, inbox).SendAsync(cmd);
}

static async Task BroadcastCheats(RpgStore store, IHubContext<RpgHub> hub)
{
    var json = store.GetCheatsJson();
    if (string.IsNullOrWhiteSpace(json)) return;
    try
    {
        using var doc = JsonDocument.Parse(json);
        await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("CheatsUpdated", doc.RootElement.Clone());
    }
    catch { /* ignore bad json */ }
}

static async Task BroadcastPvzStats(RpgStore store, IHubContext<RpgHub> hub, long playerId, long revision)
{
    var sheet = store.GetPvzStatsSheet(playerId);
    if (sheet is null) return;
    await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("PvzStatsUpdated", sheet);
    await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("PvzStatsUpdated", new { playerId, revision });
}

static async Task BroadcastPvzActivity(RpgStore store, IHubContext<RpgHub> hub, long playerId, long revision)
{
    var rollup = store.GetPvzActivityRollup(playerId);
    if (rollup is null) return;
    await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("PvzActivityUpdated", rollup);
    await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("PvzActivityUpdated", new { playerId, revision });
}

static async Task BroadcastRpgProgression(IHubContext<RpgHub> hub, long playerId, string kind, int typeId, long revision)
{
    await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("RpgProgressionUpdated", new { playerId, kind, typeId, revision });
}

static int AcceptOne(JsonElement item, EventIngest ingest, JsonSerializerOptions json)
{
    var env = item.Deserialize<EventEnvelope>(json);
    if (env is null || string.IsNullOrWhiteSpace(env.Kind)) return 0;
    return ingest.Enqueue(env);
}

public partial class Program { }
