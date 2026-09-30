using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Eligibility;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// item-todo.md Checkpoint 1 / Phase 1's own named gap, closed: <c>RpgStore.ApplyEquipProjection</c>
/// and <c>ApplyEquippedGrants</c> had zero production callers, so a rolled item persisted in
/// <c>rpg_item_assignment</c> and changed nothing else a real battle could see. Fixed via
/// <c>RpgStore.MaterializeRolledEquipRuntime</c>, called from <c>WebMatchService.BuildSquad</c> — this
/// class proves BOTH halves through that real production seam (a real summoned specimen, a real
/// <c>BuildSquad</c> call), matching <c>BuildSquadEquippedActionsTests</c>'s own established shape for
/// the sibling (unlock-ladder / relic) proofs, kept in its own file rather than appended to that one
/// since it has separate in-flight edits this session.
/// </summary>
public class RolledItemEquipRuntimeTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly WebMatchService _service;

    static RolledItemEquipRuntimeTests()
    {
        UnlockTuningPolicy.Configure(new UnlockTuning(
            P1Milli: 1000, DeltaMilli: 1000, FloorMilli: 1000, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));
        ActionFamilyMapPolicy.Configure(new Dictionary<string, IReadOnlyList<string>>());
    }

    public RolledItemEquipRuntimeTests()
    {
        // Same bootstrap BuildSquadEquippedActionsTests needs for the same reason -- ExecuteSummon's
        // real mint path reaches SummonBannerCatalog/SummonRoller/ContractPolicy, none of it covered by
        // this assembly's own [ModuleInitializer]. Duplicated per that class's own established
        // convention ("configured here exactly like AptitudeChannelModsTests' own RealBattle test
        // configures the policies IT needs beyond that bootstrap") rather than shared, to avoid editing
        // a file with separate in-flight edits this session.
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        SummoningTuningHub.Configure(SummoningTuningLoader.Parse(Read("summoning.v1.json")));
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(Read("contracts.v1.json")));
        SoulEarnPolicy.Configure(SoulEarnTuningLoader.Parse(Read("souls.v1.json")));
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(Read("fusion.v2.json")));
        FusionRpg.Core.Progression.ProgressionTuningHub.Configure(
            FusionRpg.Core.Progression.ProgressionTuningLoader.Parse(Read("progression.v3.json")));
        FusionRpg.Core.Battle.BattleTuningHub.Configure(
            FusionRpg.Core.Battle.BattleTuningLoader.Parse(Read("battle.v5.json")));
        FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(
            FusionRpg.Core.Battle.BattleResourceTuningLoader.Parse(Read("battle-resources.v1.json")));
        FusionRpg.Core.Actions.ActionTimingPolicy.Configure(
            FusionRpg.Core.Actions.ActionTimingTuningLoader.Parse(Read("action-timing.v1.json")));
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    static (long PlayerId, string InstanceId) SummonOneSpecimen(RpgStore store, string correlationSuffix, ulong rngSeed)
    {
        var playerId = store.CreatePlayer("rolled-equip-test-" + correlationSuffix).Id;
        store.AwardSouls(playerId, 10_000, SoulEarnPolicy.Reasons.Seed, "test-bankroll");
        var (ok, reason, outcome) = store.ExecuteSummon(
            playerId, SummonBannerCatalog.StandardRift, 1, "c-rolled-equip-" + correlationSuffix, rngSeed, focusElementId: null);
        Assert.True(ok, reason);
        return (playerId, Assert.Single(outcome!.Specimens).Profile.InstanceId);
    }

    /// <summary>Mints one rolled item instance wrapping <paramref name="containerId"/>, matching
    /// <c>EquipRuntimeStoreTests.SeedInstance</c>'s exact shape (module 5's own established DAL-level
    /// fixture pattern) rather than going through the full gated <c>ItemEquipService.Equip</c>, since
    /// this test's job is the squad-build materialization, not a second proof of the gate.</summary>
    string MintRolledInstance(string containerId)
    {
        var container = _store.GetContainer(containerId)!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    [Fact]
    public void A_rolled_items_bound_atom_reaches_ResolveBindings_after_BuildSquad()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.rolled-equip-power", "", 1), KindId = "stat.derived",
            FamilyId = "atom.rolled-equip-power", Variant = "", Tier = 1, Name = "Rolled Equip Power",
            ParamsJson = "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":40}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.rolled-equip-power", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.rolled-equip-power.t1") },
        }).IsOk);

        var (playerId, specimenId) = SummonOneSpecimen(_store, "bindings", rngSeed: 101);
        var itemInstanceId = MintRolledInstance("item.rolled-equip-power");
        _store.SaveItem(new RpgItemRow { InstanceId = itemInstanceId, PlayerId = playerId.ToString(), AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveAssignment(specimenId, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, itemInstanceId);

        // Nothing outside rpg_item_assignment exists yet -- confirms the gap this test is closing is
        // real before proving the fix, not assumed.
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, specimenId)));

        var (ok, reason, squad, _) = _service.BuildSquad(playerId, new[] { specimenId });
        Assert.True(ok, reason);
        Assert.Single(squad!);

        var resolution = _store.ResolveBindings(
            new OwnerScope(OwnerKind.UniqueActor, specimenId), new BindContext(RuntimeId.Battle));
        var atoms = resolution.AtomsByBinding!.Values.SelectMany(a => a).ToList();
        var atom = Assert.Single(atoms, a => a.AtomId == "atom.rolled-equip-power.t1");
        Assert.Equal("stat.derived", atom.KindId);
    }

    [Fact]
    public void A_rolled_items_granted_action_reaches_BuildSquads_EquippedActionIds()
    {
        const string containerId = "item.rolled-equip-grant";
        const string actionContainerId = "skill.rolled-equip-grant-action";
        const string actionId = "skill.rolled-equip-grant-fireball";

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.rolled-equip-grant", "", 1), KindId = "stat.modify",
            FamilyId = "atom.rolled-equip-grant", Variant = "", Tier = 1, Name = "Rolled Equip Grant",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.rolled-equip-grant.t1") },
        }).IsOk);
        // The granted SKILL needs its own container and its own atom -- an atom belongs to one
        // container's tier lineage, and the action's container must exist before UpsertAction
        // references it (SeedSkillAction, BuildSquadEquippedActionsTests, does both in this order).
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.rolled-equip-grant-action", "", 1), KindId = "stat.modify",
            FamilyId = "atom.rolled-equip-grant-action", Variant = "", Tier = 1, Name = "Rolled Equip Grant Action",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = actionContainerId, Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(1, "atom.rolled-equip-grant-action.t1") },
        }).IsOk);
        Assert.True(_store.UpsertAction(new ActionRow
        {
            ActionId = actionId, Name = actionId, Kind = ActionKind.Skill, Rung = 1,
            ContainerId = actionContainerId, Grantable = true, Tags = new[] { ActionTag.Offensive },
        }).IsOk);
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow(containerId, 0, actionId, ItemGrantRole.Granted));

        var (playerId, specimenId) = SummonOneSpecimen(_store, "grants", rngSeed: 102);
        var itemInstanceId = MintRolledInstance(containerId);
        _store.SaveItem(new RpgItemRow { InstanceId = itemInstanceId, PlayerId = playerId.ToString(), AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveAssignment(specimenId, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, itemInstanceId);

        var (ok, reason, squad, _) = _service.BuildSquad(playerId, new[] { specimenId });
        Assert.True(ok, reason);
        var actor = Assert.Single(squad!);
        Assert.Contains(actionId, actor.EquippedActionIds!);

        // Unequip must make it disappear -- the exact lifetime the Entity-scope choice
        // (EquippedGrantProjection.GrantFor) exists to guarantee, proven rather than assumed.
        _store.RemoveAssignment(specimenId, ItemRole.ArmamentPrimary);
        var (ok2, reason2, squad2, _) = _service.BuildSquad(playerId, new[] { specimenId });
        Assert.True(ok2, reason2);
        var actorAfter = Assert.Single(squad2!);
        Assert.DoesNotContain(actionId, actorAfter.EquippedActionIds ?? Array.Empty<string>());
    }

    /// <summary>
    /// spec-equip-runtime.md's Battle-half amendment (2026-09-07): the actual payoff for real content
    /// — a `stat.modify` affix atom (the kind virtually all real item/relic content ships as, per this
    /// session's own audit), NOT a synthetic `stat.derived` fixture, reaches a real
    /// <c>BattleEngine.Resolve</c> call through <see cref="ActionContainerEffectResolverFactory.BuildEquip"/>
    /// → <c>BattleRunState.BindEquip</c> — the compile/grant path this amendment added, proven the same
    /// way <c>A_generated_imported_unlock_ladder_grant_reaches_BuildSquad_and_a_real_battle_now_activates_it</c>
    /// already proves the sibling action-grant path: a real grant lands in the real battle's own Bag,
    /// not merely that the wiring compiles.
    /// </summary>
    [Fact]
    public void An_equipped_items_stat_modify_atom_reaches_a_real_battle_through_the_compiler_path()
    {
        const string containerId = "item.stat-modify-equip-proof";
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.stat-modify-equip-proof", "", 1), KindId = "stat.modify",
            FamilyId = "atom.stat-modify-equip-proof", Variant = "", Tier = 1, Name = "Stat Modify Equip Proof",
            ParamsJson = "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":250}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.stat-modify-equip-proof.t1") },
        }).IsOk);

        var (playerId, specimenId) = SummonOneSpecimen(_store, "stat-modify-proof", rngSeed: 103);
        var itemInstanceId = MintRolledInstance(containerId);
        _store.SaveItem(new RpgItemRow { InstanceId = itemInstanceId, PlayerId = playerId.ToString(), AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveAssignment(specimenId, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, itemInstanceId);

        var (ok, reason, squad, _) = _service.BuildSquad(playerId, new[] { specimenId });
        Assert.True(ok, reason);
        var mySetup = Assert.Single(squad!);
        Assert.Equal(specimenId, mySetup.SpecimenId);

        // Confirms ResolveBindings itself already sees the equip atom before blaming the battle wiring
        // — the same diagnostic-first discipline BuildSquadEquippedActionsTests' own stat.derived proof
        // uses.
        var diag = _store.ResolveBindings(
            new OwnerScope(OwnerKind.UniqueActor, specimenId), new BindContext(RuntimeId.Battle));
        var diagAtom = diag.AtomsByBinding!.Values.SelectMany(a => a)
            .Single(a => a.AtomId == "atom.stat-modify-equip-proof.t1");
        Assert.Equal("stat.modify", diagAtom.KindId);

        var (equipDefs, equipEffectIdsFor) = ActionContainerEffectResolverFactory.BuildEquip(_store, new[] { specimenId });
        var equipEffectId = Assert.Single(equipEffectIdsFor(specimenId));
        Assert.NotEmpty(equipDefs);

        var setup = new BattleSetup
        {
            Squad = new[] { mySetup with { Key = "squad:0" } },
            Wave = new[] { new BattleActorSetup { Key = "wave:0", Side = "wave", MaxHp = 1_000_000, Level = 1 } },
        };

        BattleEffectHost? capturedHost = null;
        var report = BattleEngine.Resolve(setup, seed: 9003,
            onEffectHostReady: host =>
            {
                capturedHost = host;
                ActionContainerEffectResolverFactory.RegisterInto(host, equipDefs);
            },
            equipEffectIdsFor: equipEffectIdsFor);

        Assert.NotNull(report);
        Assert.NotNull(capturedHost);
        Assert.True(capturedHost!.Bag.HasGrantForEffect(equipEffectId),
            "BindEquip should have granted the compiled equip effect into the same battle's Bag");
    }

    /// <summary>
    /// P1.5-L (2026-09-07): the real, previously-undiscovered Lawn half of this same gap.
    /// <c>RpgStore.MaterializeRolledEquipRuntime</c>'s own doc names its only production caller as
    /// <c>WebMatchService.BuildSquad</c> — the Battle/expedition path. Module 4's own
    /// <see cref="ItemEquipService"/> (the real production write surface behind
    /// <c>POST /api/items/equip</c>) never called it, so a specimen bound to the live Lawn (never sent
    /// into a battle) had its equipped rolled item persist in <c>rpg_item_assignment</c> and change
    /// nothing else — <c>effect_binding</c> stayed empty forever, and
    /// <c>AtomPushService.Build</c>'s Lawn-runtime <c>ResolveBindings</c> call had nothing to find. This
    /// proves the gap first (equip alone leaves bindings empty), then proves the fix
    /// <c>ItemEquipEndpoints.SyncLawnRuntimeAsync</c> now applies after every successful equip/unequip —
    /// the same <c>MaterializeRolledEquipRuntime</c> call Battle's squad-build already made, now also
    /// reachable from the Lawn's own write surface.
    /// </summary>
    [Fact]
    public void Equipping_through_the_real_ItemEquipService_alone_leaves_bindings_empty_until_the_new_Lawn_sync_runs()
    {
        const string containerId = "item.lawn-equip-proof";
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.lawn-equip-proof", "", 1), KindId = "stat.modify",
            FamilyId = "atom.lawn-equip-proof", Variant = "", Tier = 1, Name = "Lawn Equip Proof",
            ParamsJson = "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":77}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.lawn-equip-proof.t1") },
            Slot = ItemRoles.Id(ItemRole.ArmamentPrimary),
        }).IsOk);

        var (playerId, specimenId) = SummonOneSpecimen(_store, "lawn-equip", rngSeed: 104);
        var itemInstanceId = MintRolledInstance(containerId);
        _store.SaveItem(new RpgItemRow
        {
            InstanceId = itemInstanceId, PlayerId = playerId.ToString(), AcquiredUtc = "2026-01-01T00:00:00Z"
        });

        // The real production write surface behind POST /api/items/equip -- not the DAL-level
        // SaveAssignment shortcut the earlier tests in this file use, since this test's whole point is
        // whether THIS caller alone reaches the runtime.
        var equipService = new ItemEquipService(_store);
        var outcome = equipService.Equip(playerId, specimenId, itemInstanceId, ItemRoles.Id(ItemRole.ArmamentPrimary));
        Assert.True(outcome.Ok, outcome.Reason);

        // Confirms the gap is real before proving the fix: equip alone (module 4) never materializes
        // anything outside rpg_item_assignment.
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, specimenId)));

        // The exact call ItemEquipEndpoints.SyncLawnRuntimeAsync now makes after a successful
        // equip/unequip -- mirrors WebMatchService.BuildSquad's own call for the Battle path.
        var actor = _store.GetUniqueActor(specimenId)!;
        _store.MaterializeRolledEquipRuntime(specimenId, checked((int)actor.Level));

        var resolution = _store.ResolveBindings(
            new OwnerScope(OwnerKind.UniqueActor, specimenId), new BindContext(RuntimeId.Lawn));
        var atoms = resolution.AtomsByBinding!.Values.SelectMany(a => a).ToList();
        var atom = Assert.Single(atoms, a => a.AtomId == "atom.lawn-equip-proof.t1");
        Assert.Equal("stat.modify", atom.KindId);

        // Unequip must withdraw it -- the same lifetime guarantee the Battle-path grant test proves,
        // now proven for the Lawn's own sync call.
        var unequipOutcome = equipService.Unequip(playerId, specimenId, ItemRoles.Id(ItemRole.ArmamentPrimary));
        Assert.True(unequipOutcome.Ok, unequipOutcome.Reason);
        _store.MaterializeRolledEquipRuntime(specimenId, checked((int)actor.Level));
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, specimenId)));
    }
}
