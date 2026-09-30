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
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// cold-equip-one T3 parity — the sole Cold materialize path (rolled/atom bindings, shared by
/// battle and sheet through <see cref="EquippedBoundAtoms"/>) reaches Hub Derived with GG-49
/// SourceIds and honored ops, with no <c>BattleStatComposer</c> anywhere in the loop. The rolled
/// fixture shape (mint → assign → <c>BuildSquad</c> materialize) is
/// <c>RolledItemEquipRuntimeTests</c>'s own established pattern.
/// </summary>
public class EquippedHubParityTests : IDisposable
{
    readonly RpgStore _store;
    readonly WebMatchService _service;

    static EquippedHubParityTests()
    {
        UnlockTuningPolicy.Configure(new UnlockTuning(
            P1Milli: 1000, DeltaMilli: 1000, FloorMilli: 1000, HeldCap: 10, RungCap: 10, DiscardTaxCoeffMilli: 100));
        ActionFamilyMapPolicy.Configure(new Dictionary<string, IReadOnlyList<string>>());
    }

    public EquippedHubParityTests()
    {
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

        _store = RpgStore.InMemory();
        _store.Init();

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

    public void Dispose() => _store.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    static (long PlayerId, string InstanceId) SummonOneSpecimen(RpgStore store, string correlationSuffix, ulong rngSeed)
    {
        var playerId = store.CreatePlayer("equipped-hub-test-" + correlationSuffix).Id;
        store.AwardSouls(playerId, 10_000, SoulEarnPolicy.Reasons.Seed, "test-bankroll");
        var (ok, reason, outcome) = store.ExecuteSummon(
            playerId, SummonBannerCatalog.StandardRift, 1, "c-equipped-hub-" + correlationSuffix, rngSeed, focusElementId: null);
        Assert.True(ok, reason);
        return (playerId, Assert.Single(outcome!.Specimens).Profile.InstanceId);
    }

    string MintRolledInstance(string containerId)
    {
        var container = _store.GetContainer(containerId)!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    void UpsertRolledContainer(string containerId, params (string AtomId, string Channel, string Op, long Amount)[] atoms)
    {
        foreach (var (atomId, channel, op, amount) in atoms)
        {
            Assert.True(_store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId(atomId, "", 1), KindId = "stat.derived",
                FamilyId = atomId, Variant = "", Tier = 1, Name = atomId,
                ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"{op}\",\"amount\":{amount}}}",
            }).IsOk);
        }
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId, Kind = ContainerKind.Item,
            Atoms = atoms.Select((a, i) => new ContainerAtomRow(i + 1, AtomRow.DeriveId(a.AtomId, "", 1))).ToArray(),
        }).IsOk);
    }

    string EquipRolled(string specimenId, long playerId, string containerId, ulong rngSeed)
    {
        var itemInstanceId = MintRolledInstance(containerId);
        _store.SaveItem(new RpgItemRow { InstanceId = itemInstanceId, PlayerId = playerId.ToString(), AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveAssignment(specimenId, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, itemInstanceId);
        return itemInstanceId;
    }

    [Fact]
    public void Fresh_actor_has_no_assignments_bindings_or_hub_derived()
    {
        // T4: the equip API never defaults a stub (or any) row onto a new actor — everything
        // equipped is an explicit Upsert, so a fresh actor is bare on all three reads.
        var playerId = _store.GetCurrentPlayerId();
        var actor = _store.CreateUniqueActor(playerId, "plant", 1);

        Assert.Empty(_store.ListAssignments(actor.InstanceId));
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, actor.InstanceId)));
        Assert.Empty(EquippedBoundAtoms.DerivedFromStore(_store, actor.InstanceId));

        var hub = ActorHubBootstrap.CreateDefault(boundDerivedAtoms: _ =>
            EquippedBoundAtoms.DerivedFromStore(_store, actor.InstanceId));
        var (snapshot, _) = hub.ResolveDerivedWithContributions(
            new StatContextFactory().ForPlant(actor.InstanceId, new EntityBaseline()));
        Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.CombatPowerOmni, 0), 6);
    }

    [Fact]
    public void Rolled_flat_stat_derived_reaches_hub_snapshot_without_composer()
    {
        UpsertRolledContainer("item.rolled-equip-hub",
            ("atom.rolled-equip-hub", DerivedStatChannels.CombatPowerFire, "flat", 40));

        var (playerId, specimenId) = SummonOneSpecimen(_store, "hub-flat", rngSeed: 201);
        var itemInstanceId = EquipRolled(specimenId, playerId, "item.rolled-equip-hub", rngSeed: 201);

        var (ok, reason, _, _) = _service.BuildSquad(playerId, new[] { specimenId });
        Assert.True(ok, reason);

        var derived = EquippedBoundAtoms.DerivedFromStore(_store, specimenId);
        var bound = Assert.Single(derived);
        Assert.Equal(40L, bound.Amount);
        Assert.StartsWith("equip:", bound.SourceId, StringComparison.Ordinal);
        Assert.EndsWith(itemInstanceId, bound.SourceId);

        var hub = ActorHubBootstrap.CreateDefault(boundDerivedAtoms: _ =>
            EquippedBoundAtoms.DerivedFromStore(_store, specimenId));
        var (snapshot, bag) = hub.ResolveDerivedWithContributions(
            new StatContextFactory().ForPlant(specimenId, new EntityBaseline()));
        Assert.Equal(40.0, snapshot.Get(DerivedStatChannels.CombatPowerFire, 0), 6);
        Assert.Contains(bound.SourceId,
            bag.ContributionsFor(DerivedStatChannels.CombatPowerFire).Select(c => c.SourceId));
    }

    [Fact]
    public void Rolled_increased_op_is_honored_on_hub_path_not_coerced_to_flat()
    {
        // status.power.omni is SumIncreased with default 0 and no cap: an Increased 250 must
        // compose to exactly 250 on the Hub path (op honored through TryParseOp), while the
        // battle-side ModsFor keeps its documented always-additive fold (battle-ops-parity owns it).
        UpsertRolledContainer("item.rolled-equip-hub-ops",
            ("atom.rolled-equip-hub-flat", DerivedStatChannels.CombatPowerFire, "flat", 40),
            ("atom.rolled-equip-hub-inc", DerivedStatChannels.StatusPowerOmni, "increased", 250));

        var (playerId, specimenId) = SummonOneSpecimen(_store, "hub-ops", rngSeed: 202);
        EquipRolled(specimenId, playerId, "item.rolled-equip-hub-ops", rngSeed: 202);

        var (ok, reason, _, _) = _service.BuildSquad(playerId, new[] { specimenId });
        Assert.True(ok, reason);

        var derived = EquippedBoundAtoms.DerivedFromStore(_store, specimenId);
        Assert.Equal(2, derived.Count);
        var inc = Assert.Single(derived, d => d.Channel == DerivedStatChannels.StatusPowerOmni);
        Assert.Equal(DerivedModifierOp.Increased, inc.Op);

        var hub = ActorHubBootstrap.CreateDefault(boundDerivedAtoms: _ =>
            EquippedBoundAtoms.DerivedFromStore(_store, specimenId));
        var (snapshot, _) = hub.ResolveDerivedWithContributions(
            new StatContextFactory().ForPlant(specimenId, new EntityBaseline()));
        Assert.Equal(40.0, snapshot.Get(DerivedStatChannels.CombatPowerFire, 0), 6);
        Assert.Equal(250.0, snapshot.Get(DerivedStatChannels.StatusPowerOmni, 0), 6);
    }
}
