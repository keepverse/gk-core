using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Actions.Eligibility;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// T22 (action-todo.md): "the auto-equipped set appears in the battle report — otherwise a dominant
/// auto-loadout is invisible to a matrix that compares allocations, not loadouts." Closed 2026-08-28
/// by wiring `WebMatchService.BuildSquad` to `RpgStore.GetLoadoutOrAutoEquip` — this proves the wiring
/// through the REAL production seam (a real summoned specimen, a real action grant, the real
/// `BuildSquad` entry point), not a re-implementation of `AutoEquip`'s own already-exhaustively-tested
/// ranking logic (that's `AutoEquipTests.cs`'s job).
/// </summary>
public class BuildSquadEquippedActionsTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly WebMatchService _service;

    /// <summary>T59.8: process-wide statics (`RungPolicy`/`CreatureSpeciesCatalog`'s own established
    /// shape), configured once for this whole test class -- an `AlwaysAccepts` tuning (no chance
    /// decay) so a controlled XP award lands an unlock deterministically, never a real random wait.</summary>
    static BuildSquadEquippedActionsTests()
    {
        UnlockTuningPolicy.Configure(new UnlockTuning(
            P1Milli: 1000, DeltaMilli: 1000, FloorMilli: 1000, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));
        ActionFamilyMapPolicy.Configure(new Dictionary<string, IReadOnlyList<string>>());
    }

    public BuildSquadEquippedActionsTests()
    {
        // ExecuteSummon's real mint path reaches SummonBannerCatalog/SummonRoller (SummoningTuningHub)
        // AND ContractPolicy (auto-bind on mint) -- neither covered by this assembly's
        // [ModuleInitializer] bootstrap, so configured here exactly like AptitudeChannelModsTests'
        // own RealBattle test configures the policies IT needs beyond that bootstrap.
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        SummoningTuningHub.Configure(SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(Read("contracts.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        // RollTraits -> FusionRoller.SlotsFor now reads StarPolicy.Tuning.SlotsByRarity
        // (seed-to-concrete T4.1 moved the old hardcoded switch into fusion.v1.json), so the mint
        // path this test drives needs StarPolicy configured too, exactly like AptitudeChannelModsTests.
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(Read("fusion.v2.json")));
        // T59.8: AwardUniqueActorXp's own XpToNext call reads RpgXpCurve.Tuning -- not covered by
        // this assembly's [ModuleInitializer] bootstrap either (no prior test in this file awarded
        // specimen XP through a real level-up).
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(Read("progression.v3.json")));
        // T59.8: the real BattleEngine.Resolve call needs the same three tunables
        // ContractTuningTestBootstrap configures for Core.Tests -- none covered by this assembly's
        // own [ModuleInitializer] bootstrap. Use the current battle.v5.json so every shipped
        // profile remains available after the timeline catalog grows.
        FusionRpg.Core.Battle.BattleTuningHub.Configure(
            FusionRpg.Core.Battle.BattleTuningLoader.Parse(Read("battle.v5.json")));
        FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(
            FusionRpg.Core.Battle.BattleResourceTuningLoader.Parse(Read("battle-resources.v1.json")));
        FusionRpg.Core.Actions.ActionTimingPolicy.Configure(
            FusionRpg.Core.Actions.ActionTimingTuningLoader.Parse(Read("action-timing.v1.json")));
        // A24: the new activation proof runs a real combat round past BindContainers for the first
        // time in this file (every earlier test's own battle died at BindContainers before any round
        // ran) -- OverlayCombatCalculator.Compute needs StatsTuningHub configured, not covered by this
        // assembly's own [ModuleInitializer] bootstrap either, matching AptitudeChannelModsTests'
        // own RealBattle test configuring the same tunable for the same reason.
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
            FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(Read("stats.v1.json")));

        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();
        _service = new WebMatchService(_store, hub);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    string SeedSkillAction(string actionId)
    {
        var containerId = actionId + "-container";
        var atomId = FusionRpg.Core.Effects.Atoms.AtomRow.DeriveId(
            "atom." + actionId.Replace('.', '-'), "", 1);
        var atomResult = _store.UpsertAtom(new FusionRpg.Core.Effects.Atoms.AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom." + actionId.Replace('.', '-'),
            Variant = "",
            Tier = 1,
            Name = actionId,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        });
        Assert.True(atomResult.IsOk, atomResult.ToString());

        var containerResult = _store.UpsertContainer(new FusionRpg.Core.Effects.Atoms.ContainerRow
        {
            ContainerId = containerId,
            Kind = FusionRpg.Core.Effects.Atoms.ContainerKind.Skill,
            Atoms = new[] { new FusionRpg.Core.Effects.Atoms.ContainerAtomRow(0, atomId) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());

        var actionResult = _store.UpsertAction(new ActionRow
        {
            ActionId = actionId,
            Name = actionId,
            Kind = ActionKind.Skill,
            Rung = 1,
            ContainerId = containerId,
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        });
        Assert.True(actionResult.IsOk, actionResult.ToString());
        return actionId;
    }

    static (long PlayerId, string InstanceId) SummonOneSpecimen(RpgStore store, string correlationSuffix, ulong rngSeed)
    {
        var playerId = store.CreatePlayer("buildsquad-test-" + correlationSuffix).Id;
        store.AwardSouls(playerId, 10_000, SoulEarnPolicy.Reasons.Seed, "test-bankroll");
        var (ok, reason, outcome) = store.ExecuteSummon(
            playerId, SummonBannerCatalog.StandardRift, 1, "c-buildsquad-" + correlationSuffix, rngSeed, focusElementId: null);
        Assert.True(ok, reason);
        return (playerId, Assert.Single(outcome!.Specimens).Profile.InstanceId);
    }

    [Fact]
    public void A_specimen_with_no_grant_and_no_loadout_row_auto_equips_from_nothing_but_basics()
    {
        // No SeedSkillAction call at all -- this specimen holds zero skills, exactly the "no
        // candidates" state most creatures are in today (T22's own honest admission: nothing in
        // production grants an action to a creature instance yet). BuildSquad must still succeed and
        // must not throw reaching for a loadout/rung table that was never configured.
        var (playerId, instanceId) = SummonOneSpecimen(_store, "no-grant", rngSeed: 1);

        var (ok, reason, squad, _) = _service.BuildSquad(playerId, new[] { instanceId });

        Assert.True(ok, reason);
        var actor = Assert.Single(squad!);
        Assert.NotNull(actor.EquippedActionIds);
    }

    [Fact]
    public void A_real_skill_grant_reaches_the_built_squads_EquippedActionIds()
    {
        // The real point of the wire: a skill actually GRANTED to this specimen (via UpsertGrant,
        // the same table T23's ActionSetAssembler and T21's loadout both read) must appear on the
        // BattleActorSetup BuildSquad produces -- not just "the code compiles and BuildSquad still
        // returns a squad."
        var (playerId, instanceId) = SummonOneSpecimen(_store, "real-grant", rngSeed: 2);
        var actionId = SeedSkillAction("skill.buildsquad-fireball");

        var grantResult = _store.UpsertGrant(
            new ActionGrantRow(OwnerKind.UniqueActor, instanceId, actionId, Source: "test"));
        Assert.True(grantResult.IsOk, grantResult.ToString());

        var (ok, reason, squad, _) = _service.BuildSquad(playerId, new[] { instanceId });

        Assert.True(ok, reason);
        var actor = Assert.Single(squad!);
        Assert.NotNull(actor.EquippedActionIds);
        Assert.Contains(actionId, actor.EquippedActionIds!);
    }

    [Fact]
    public void A_real_loadout_row_wins_over_auto_equip_exactly_as_GetLoadoutOrAutoEquip_documents()
    {
        var (playerId, instanceId) = SummonOneSpecimen(_store, "loadout-wins", rngSeed: 3);
        var granted = SeedSkillAction("skill.buildsquad-granted");
        var chosen = SeedSkillAction("skill.buildsquad-chosen");
        // Grants read under UniqueActor (action-grant-owner-kind-durability, fixed 2026-09-07);
        // loadout stays under Entity -- the two scopes are deliberately different, matching
        // EquippedActionIdsFor's own split.
        _store.UpsertGrant(new ActionGrantRow(OwnerKind.UniqueActor, instanceId, granted, Source: "test"));
        _store.UpsertGrant(new ActionGrantRow(OwnerKind.UniqueActor, instanceId, chosen, Source: "test"));

        var scope = new OwnerScope(OwnerKind.Entity, instanceId);
        var held = new HashSet<string> { granted, chosen };
        var setResult = _store.SetLoadout(scope, new[] { chosen }, held.Contains, () => false);
        Assert.True(setResult.Ok, setResult.ToString());

        var (ok, reason, squad, _) = _service.BuildSquad(playerId, new[] { instanceId });

        Assert.True(ok, reason);
        var actor = Assert.Single(squad!);
        Assert.Equal(new[] { chosen }, actor.EquippedActionIds);
    }

    [Fact]
    public void Two_specimens_of_the_same_species_carry_independent_loadouts()
    {
        // Keyed on the specimen's own instance id, never the player -- two creatures one player owns
        // must not share a loadout just because they share a summon.
        var (playerId, a) = SummonOneSpecimen(_store, "independent-a", rngSeed: 4);
        _store.AwardSouls(playerId, 10_000, SoulEarnPolicy.Reasons.Seed, "test-bankroll-2");
        var (_, _, secondOutcome) = _store.ExecuteSummon(
            playerId, SummonBannerCatalog.StandardRift, 1, "c-buildsquad-independent-b", rngSeed: 5, focusElementId: null);
        var b = Assert.Single(secondOutcome!.Specimens).Profile.InstanceId;
        var skillA = SeedSkillAction("skill.buildsquad-a");
        _store.UpsertGrant(new ActionGrantRow(OwnerKind.UniqueActor, a, skillA, Source: "test"));

        var (ok, reason, squad, _) = _service.BuildSquad(playerId, new[] { a, b });

        Assert.True(ok, reason);
        Assert.Equal(2, squad!.Count);
        var setupA = squad.Single(s => s.Key.EndsWith("0", StringComparison.Ordinal));
        var setupB = squad.Single(s => s.Key.EndsWith("1", StringComparison.Ordinal));
        Assert.Contains(skillA, setupA.EquippedActionIds!);
        Assert.DoesNotContain(skillA, setupB.EquippedActionIds ?? Array.Empty<string>());
    }

    static ActionCorpusCostTemplate CostTemplate() => new(new Dictionary<ActionCategory, ActionCorpusCostTemplateRow>
    {
        [ActionCategory.Attack] = new("qi", 20, ActionCostTiming.OnCommit),
        [ActionCategory.Defense] = new("qi", 30, ActionCostTiming.OnCommit),
        [ActionCategory.Support] = new("qi", 40, ActionCostTiming.OnCommit),
        [ActionCategory.Movement] = new("qi", 15, ActionCostTiming.OnCommit),
        [ActionCategory.Status] = new("qi", 35, ActionCostTiming.OnCommit),
    });

    /// <summary>
    /// T59.8 (spec-action-instance-and-grant.md criterion 4): summon → real corpus import (T59.3/
    /// T59.4's real pipeline, a hand-built brief — the REAL committed corpus mostly can't resolve
    /// today, T59.5's own honest finding, not a reason to weaken THIS proof) → award XP until the
    /// unlock ladder grants it (a controlled, `AlwaysAccepts`-tuned roll, never a real random wait) →
    /// `BuildSquad`. Proves the "equips" half of criterion 4 in full, the same shape T22's own
    /// `A_real_skill_grant_reaches_the_built_squads_EquippedActionIds` already proved, now for a
    /// GENERATED, IMPORTED action rather than a hand-authored one.
    ///
    /// <para>A24 (spec-container-effect-resolver-production.md): the "can activate" half, PROVEN FALSE
    /// by this exact test when it was first written (`WebMatchService`'s 3 real `BattleEngine.Resolve`
    /// call sites supplied no `IContainerEffectResolver` at all), is now proven TRUE for this fixture —
    /// `atom.e2e-test`'s bare `"amount":1` is `AtomJson.TryReadValueSpec`'s own documented
    /// `42 -> Fixed(42)` shape (`Min == Max`), so `Compilability.Classify` routes it to
    /// `AtomPath.Compiled`, never `Runner`. A real, `RpgStore`-backed resolver
    /// (`ActionContainerEffectResolverFactory`) now compiles it and registers the result into the same
    /// `Host.Bag.Catalog` `BindContainers` reads from, mirroring `WebMatchService`'s own new wiring
    /// exactly. This does NOT mean every real imported action activates —
    /// `ActionCorpusRealContentQualityTests.TheThreeRealImportedActionsCompileToZeroEffectDefsBecauseTheirAtomsAreRunnerPathOnly`
    /// proves the real committed corpus (Fortitude/Vitality, both per-hit-roll) still cannot, precisely
    /// because their atoms need the Runner path (`battle-runner-path-not-wired`, named but not fixed by
    /// A24) — this fixture is deliberately Compiled-path-eligible, proving the class of content A24
    /// actually closes the gap for.</para>
    /// </summary>
    [Fact]
    public void A_generated_imported_unlock_ladder_grant_reaches_BuildSquad_and_a_real_battle_now_activates_it()
    {
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.e2e-test", "", 1), KindId = "stat.modify",
            FamilyId = "atom.e2e-test", Variant = "", Tier = 1, Name = "e2e test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        };
        Assert.Empty(_store.UpsertAtoms(new[] { atom }).Rejected);

        var brief = new ActionCorpusBrief(
            Id: "action.e2e.only", Name: "E2E Only", Category: "attack", Scope: "general", ScopeKey: null,
            RungFloor: 1, RungCeiling: 1, AtomFamilies: new[] { "atom.e2e-test" },
            TargetMode: "single", Relation: "enemy");
        var importResult = ActionCorpusImporter.Import(_store, new[] { brief }, CostTemplate(), RungPolicy.Table);
        Assert.Equal(1, importResult.ImportedCount);

        var (playerId, instanceId) = SummonOneSpecimen(_store, "e2e", rngSeed: 6);

        var (awardOk, awardReason, actor, _) = _store.AwardUniqueActorXp(instanceId, delta: 1_000_000);
        Assert.True(awardOk, awardReason);
        Assert.True(actor!.Level > 1); // liveness -- the level gain that should have triggered a roll actually happened

        var grants = _store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, instanceId));
        Assert.Contains(grants, g => g.ActionId == "action.e2e.only"); // "equips" half of criterion 4

        var (squadOk, squadReason, squad, _) = _service.BuildSquad(playerId, new[] { instanceId });
        Assert.True(squadOk, squadReason);
        var mySetup = Assert.Single(squad!);
        Assert.Contains("action.e2e.only", mySetup.EquippedActionIds!);

        // A24: the same wiring WebMatchService's own 3 call sites now carry, built explicitly here
        // rather than going through WebMatchService itself (this test's own job is BuildSquad's
        // integration with the unlock ladder, not a second copy of WebMatchService's plumbing).
        var catalog = _store.BuildActionCatalog(RungPolicy.Table);
        var (containerResolver, containerDefs, _, _) = ActionContainerEffectResolverFactory.Build(_store);
        var setup = new BattleSetup
        {
            Squad = new[] { mySetup with { Key = "squad:0" } },
            Wave = new[] { new BattleActorSetup { Key = "wave:0", Side = "wave", MaxHp = 1_000_000, Level = 1 } },
        };

        BattleEffectHost? capturedHost = null;
        var report = BattleEngine.Resolve(setup, seed: 9001, actionCatalog: catalog,
            containerResolver: containerResolver,
            onEffectHostReady: host =>
            {
                capturedHost = host;
                ActionContainerEffectResolverFactory.RegisterInto(host, containerDefs);
            });

        Assert.NotNull(report); // no throw -- the exact call this test used to prove threw, above
        Assert.NotNull(capturedHost);
        var containerId = _store.GetAction("action.e2e.only")!.ContainerId;
        var effectId = Assert.Single(containerResolver.EffectIdsFor(containerId));
        Assert.True(capturedHost!.Bag.HasGrantForEffect(effectId),
            "BindContainers should have granted the resolved effect id into the same battle's Bag");
    }

    /// <summary>
    /// A25 (battle-runner-path-integration): the decisive proof that `BasicAttack.cs`'s own two
    /// `Bag.OnEvent` sites now ALSO reach `Host.Runner`, inside a real `BattleEngine.Resolve` call
    /// driven by the real production seams (summon → grant → BuildSquad), not a synthetic
    /// `BattleRunState` constructed by hand. `atom.e2e-runner-only` authors an explicit
    /// `"when":{"trigger":"OnActivate"}` -- unlike `atom.fortitude`/`atom.vitality`, which author none
    /// and are correctly excluded from `runnerBindings` (proven separately in
    /// `ActionCorpusRealContentQualityTests`) -- and `OnActivate` fires once per resolved intent
    /// independent of hit/miss (`BasicAttack.cs`'s own doc comment), making this deterministic: no
    /// combat-roll luck needed for the trigger to fire at least once.
    ///
    /// <para>⛔ <b>Real, empirically-confirmed boundary, not assumed</b>: running this without a
    /// registered def throws `InvalidOperationException: unknown effect_id: atom.e2e-runner-only.t1`
    /// from `EffectBag.Grant`, reached via `EffectFunnel.DrainOnce` during `Host.Flush()` — proof that
    /// the Runner correctly evaluated the trigger and successfully called `Funnel.EnqueueModifier`
    /// (nothing would reach `Grant` with this exact atom id otherwise). The remaining requirement — a
    /// registered `EffectDef` per runner entry — is `spec-atom-runner.md`'s own named, pre-existing,
    /// separate scope ("nothing emits a def for a runner atom... until [E19] a host has to have the
    /// def in its catalog already"), not something A25 needs to solve to prove ITS OWN mechanism
    /// works. Asserted here as the exact expected exception, the same technique
    /// `ActionCorpusRealContentQualityTests`'s own Layer-3 proof already uses for a different,
    /// precisely-bounded failure.</para>
    /// </summary>
    [Fact]
    public void A_well_formed_triggered_runner_path_action_reaches_the_real_AtomRunner_in_a_real_battle()
    {
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.e2e-runner-only", "", 1), KindId = "stat.modify",
            FamilyId = "atom.e2e-runner-only", Variant = "", Tier = 1, Name = "e2e runner test",
            WhenJson = "{\"trigger\":\"OnActivate\"}",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onApply\"}}",
        };
        Assert.Empty(_store.UpsertAtoms(new[] { atom }).Rejected);

        var brief = new ActionCorpusBrief(
            Id: "action.e2e.runner-only", Name: "E2E Runner Only", Category: "attack", Scope: "general",
            ScopeKey: null, RungFloor: 1, RungCeiling: 1, AtomFamilies: new[] { "atom.e2e-runner-only" },
            TargetMode: "single", Relation: "enemy");
        var importResult = ActionCorpusImporter.Import(_store, new[] { brief }, CostTemplate(), RungPolicy.Table);
        Assert.Equal(1, importResult.ImportedCount);

        var (playerId, instanceId) = SummonOneSpecimen(_store, "e2e-runner", rngSeed: 7);
        var (awardOk, awardReason, actor, _) = _store.AwardUniqueActorXp(instanceId, delta: 1_000_000);
        Assert.True(awardOk, awardReason);
        Assert.True(actor!.Level > 1);

        var grants = _store.ListGrants(new OwnerScope(OwnerKind.UniqueActor, instanceId));
        Assert.Contains(grants, g => g.ActionId == "action.e2e.runner-only");

        var (squadOk, squadReason, squad, _) = _service.BuildSquad(playerId, new[] { instanceId });
        Assert.True(squadOk, squadReason);
        var mySetup = Assert.Single(squad!);
        Assert.Contains("action.e2e.runner-only", mySetup.EquippedActionIds!);

        var catalog = _store.BuildActionCatalog(RungPolicy.Table);
        var (containerResolver, containerDefs, runnerBindings, runnerCoverage) = ActionContainerEffectResolverFactory.Build(_store);
        Assert.NotEmpty(runnerBindings); // liveness -- this atom must actually reach the runner seam
        Assert.Contains(_store.GetAction("action.e2e.runner-only")!.ContainerId, runnerCoverage);

        var setup = new BattleSetup
        {
            Squad = new[] { mySetup with { Key = "squad:0" } },
            Wave = new[] { new BattleActorSetup { Key = "wave:0", Side = "wave", MaxHp = 1_000_000, Level = 1 } },
        };

        BattleEffectHost? capturedHost = null;
        var ex = Assert.Throws<InvalidOperationException>(() => BattleEngine.Resolve(setup, seed: 9002, actionCatalog: catalog,
            containerResolver: containerResolver,
            onEffectHostReady: host =>
            {
                capturedHost = host;
                ActionContainerEffectResolverFactory.RegisterInto(host, containerDefs);
            },
            runnerBindings: runnerBindings,
            containersWithRunnerCoverage: runnerCoverage));

        // The precise, expected boundary -- proves the Runner reached and dispatched this exact atom
        // (only a successful Funnel.EnqueueModifier call reaches EffectBag.Grant with this id at all),
        // not a generic or unrelated crash.
        Assert.Contains("unknown effect_id: atom.e2e-runner-only.t1", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(capturedHost);
        Assert.NotNull(capturedHost!.Runner); // A25's own wiring: a runner-path atom means a real Runner attached
    }

    /// <summary>
    /// `equip-atom-source-not-wired` (action-plan.md §5, found 2026-09-06, fixed 2026-09-07):
    /// the old `BattleStatComposer.Equipment` global defaulted to `EquipAtomSource.None` forever --
    /// no production caller ever called `UseEquipment`. Closed in production by <c>Program.cs</c> →
    /// <c>EquippedBoundAtoms.SourceFromStore</c> → <c>EquipAtomSource.FromEquippedResolver</c>
    /// (<c>equip:{role}:{itemRef}</c>); battle-hub-fuse T6 deleted that global entirely, so this
    /// fixture now feeds the resolved atoms through <see cref="BattleHubInputs.BoundAtoms"/> per-setup
    /// instead (the same shape <c>EquipRuntimeTests</c> uses), not a global static.
    ///
    /// <para>This fixture still drives <c>FromResolver</c> (legacy flatten →
    /// <c>equip:unknown:{atomId}</c>) to prove battle compose accepts that shape; it is <b>not</b>
    /// the Program.cs production contract. It proves a real store-backed equipped atom reaches
    /// <c>BattleHubCompose.Compose</c> and changes a derived channel by the declared magnitude.</para>
    ///
    /// <para><b>Why a synthetic atom+container, not a real catalogued item.</b> Every real, shipped
    /// item today (`gk-data/packs/fusion/data/seed/containers/unique-equip.json`) wraps a `stat.modify` atom, never
    /// `stat.derived` -- confirmed directly (`atom.fx-passive-atk-flat.t1`'s `KindId` is
    /// `"stat.modify"`), and `EquipAtomSource.EquippedDerived`'s own filter correctly, by design,
    /// consumes only `stat.derived`. That is a CONTENT gap (no equippable item wraps the one atom
    /// kind this seam reads), not a wiring gap -- so this test binds a well-formed `stat.derived` atom
    /// directly via <c>RpgStore.ProduceAndBind</c>, the SAME primitive
    /// `ReconcileUniqueEquipmentAtomBindingsUnlocked` calls internally for every real item equip
    /// (`RpgStore.UniqueActors.cs`: `ProduceAndBind(container, DomainMembers, rollSeed,
    /// UniqueEquipAtomPinTheta, PowerTuningHub.Tuning, new OwnerScope(OwnerKind.UniqueActor,
    /// instanceId), slot, ...)`), only skipping the `UniqueEquipmentCatalog.IsKnownItem` item-name
    /// indirection a synthetic fixture cannot pass. The shape mirrors the real corpus's only concrete
    /// `stat.derived` atom, `atom.critical-hunter` (`{"channel":"combat.crit.rate.omni","op":"flat",
    /// "amount":150}`) -- a fixed, unrolled magnitude, exactly like this fixture's.</para>
    /// </summary>
    [Fact]
    public void A_real_equipped_items_atom_reaches_BattleHubCompose_through_the_real_production_resolver()
    {
        // A real registered stat.derived channel (DerivedStatRegistry.RegisterDefaults,
        // DerivedComposeKind.FlatSum -- accepts "flat"), not an arbitrary string: AtomRowValidator's
        // G6 check refuses any stat.derived channel outside that closed vocabulary.
        // (Was `ProgressionBonusAtk` until 2026-09-18, when the owner retired that channel --
        // solid-enforcement `retire-atk` R4. The mechanism under test is identical on any surviving
        // FlatSum channel.)
        const string channel = FusionRpg.Core.Stats.Derived.DerivedStatChannels.ProgressionBonusMaxHp;
        const long amount = 250;
        var (_, instanceId) = SummonOneSpecimen(_store, "equip-wiring", rngSeed: 8);

        var upsertAtom = _store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.equip-wiring-test.t1", KindId = "stat.derived",
            FamilyId = "atom.equip-wiring-test", Variant = "", Tier = 1, Name = "Equip Wiring Test Derived",
            ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"flat\",\"amount\":{amount}}}",
        });
        Assert.True(upsertAtom.IsOk, upsertAtom.ToString());
        var container = new ContainerRow
        {
            ContainerId = "item.equip-wiring-test", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.equip-wiring-test.t1") },
        };
        Assert.True(_store.UpsertContainer(container).IsOk);

        // Same tuning/theta pin AtomInstanceStoreTests/InstanceProducerStoreTests already establish
        // for this store -- reused rather than re-derived so this fixture rolls through the real
        // production PowerTuning shape, not a private one.
        var tuning = FusionRpg.Core.Power.PowerTuning.Build(
            1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);
        const int pinTheta = 20;
        static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

        var owner = new OwnerScope(OwnerKind.UniqueActor, instanceId);
        var produce = _store.ProduceAndBind(
            container, NoDomains, rollSeed: 1, pinTheta, tuning, owner,
            slot: "weapon", priority: 0, source: "test", out var producedInstanceId, out var producedBindingId);
        Assert.True(produce.IsOk, produce.ToString());
        Assert.NotNull(producedInstanceId);
        Assert.NotNull(producedBindingId);

        // DIAGNOSTIC: confirm ResolveBindings itself sees the bound atom before blaming the composer.
        var diag = _store.ResolveBindings(owner, new BindContext(RuntimeId.Battle));
        Assert.True(diag.Bindings.Count > 0, $"Bindings=0, Refused=[{string.Join(",", diag.Refused.Select(r => r.ToString()))}]");
        Assert.NotNull(diag.AtomsByBinding);
        var diagAtoms = diag.AtomsByBinding!.Values.SelectMany(a => a).ToList();
        var diagAtom = diagAtoms.Single(a => a.AtomId == "atom.equip-wiring-test.t1");
        Assert.Equal("stat.derived", diagAtom.KindId);

        // Legacy FromResolver flatten (equip:unknown:{atomId}) — not Program.cs production
        // (EquippedBoundAtoms → FromEquippedResolver). Locks that battle still accepts this shape.
        var source = EquipAtomSource.FromResolver(specimenId =>
        {
            var resolution = _store.ResolveBindings(
                new OwnerScope(OwnerKind.UniqueActor, specimenId), new BindContext(RuntimeId.Battle));
            if (resolution.AtomsByBinding is null) return Array.Empty<AtomRow>();
            var flattened = new List<AtomRow>();
            foreach (var atoms in resolution.AtomsByBinding.Values) flattened.AddRange(atoms);
            return flattened;
        });

        var geared = BattleHubCompose.Compose(new BattleActorSetup
        {
            Key = "squad:0", Side = "squad", SpeciesId = "spec", TypeId = 1, Level = 5,
            SpecimenId = instanceId, MaxHp = 100, Atk = 50, Defense = 20,
            HubInputs = new BattleHubInputs { BoundAtoms = source.DerivedAtomsFor(instanceId) },
        });
        var bare = BattleHubCompose.Compose(new BattleActorSetup
        {
            Key = "squad:0", Side = "squad", SpeciesId = "spec", TypeId = 1, Level = 5,
            SpecimenId = null, MaxHp = 100, Atk = 50, Defense = 20,
        });

        // Exact arithmetic, not just "changed" -- proves the declared magnitude flows through
        // unmodified, not merely that some unrelated value moved.
        Assert.Equal(bare.Get(channel) + amount, geared.Get(channel));
    }
}
