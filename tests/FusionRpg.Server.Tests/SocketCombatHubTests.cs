using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// species-gear-chain T21 (arm 1): the Hub end of the seam — a socketed gem measurably changes a
/// combat number THROUGH ActorHub (never a second composer), attributed under the amended §8.1
/// grammar, with its op honoured. The projection half lives in Data
/// <c>EquipProjectionSocketsTests</c> (this project cannot be referenced from there); what is
/// proven here is compose-time: bindings in, contributions out, one shared walk.
/// Closed enums + relationships only; no population counts.
/// </summary>
public class SocketCombatHubTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SocketCombatHubTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    string MintHost()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.socket-hub-host", "", 1), KindId = "stat.derived",
            FamilyId = "atom.socket-hub-host", Variant = "", Tier = 1, Name = "Socket Hub Host",
            ParamsJson = "{\"channel\":\"combat.power.earth\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.socket-hub-host", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.socket-hub-host.t1") },
        }).IsOk);
        var container = _store.GetContainer("item.socket-hub-host")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    string SaveGem(string op = "flat", string channel = "combat.power.fire")
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.socket-hub-gem", "", 1), KindId = "stat.derived",
            FamilyId = "atom.socket-hub-gem", Variant = "", Tier = 1, Name = "Socket Hub Gem",
            ParamsJson = "{\"channel\":\"" + channel + "\",\"op\":\"" + op + "\",\"amount\":30}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "gem.socket-hub-gem", Kind = ContainerKind.Gem,
            Atoms = new[] { new ContainerAtomRow(1, "atom.socket-hub-gem.t1") },
        }).IsOk);
        return _store.SaveInstance(new InstanceRow
        {
            ContainerId = "gem.socket-hub-gem",
            RollSeed = 7,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, "atom.socket-hub-gem.t1", "{\"amount\":30}") },
        }, "gem-hub-inst-1");
    }

    (ActorHub Hub, StatContext Ctx) HubFor(string specimenId)
    {
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = new ActorHub(stats);
        hub.Register(new AtomDerivedSubsystem(
            _ => EquippedBoundAtoms.DerivedFromStore(_store, specimenId)));
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        return (hub, ctx);
    }

    [Fact]
    public void A_socketed_gem_moves_its_channel_and_names_the_insert()
    {
        var host = MintHost();
        var gem = SaveGem();
        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, host);
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-hub-gem", gem) });
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var (hub, ctx) = HubFor("specimen-1");
        var (snapshot, contributions) = hub.ResolveDerivedWithContributions(ctx);

        // The channel moved by exactly the gem's amount — the host touches earth only, so every
        // point of fire is the insert's. This has never been true before this module.
        Assert.Equal(30, snapshot.Get("combat.power.fire", 0.0));
        var contribution = Assert.Single(contributions.ContributionsFor("combat.power.fire"));
        Assert.Equal($"insert:armament-primary:{host}#0", contribution.SourceId);
        Assert.Equal(30, contribution.Value, 6);
    }

    [Fact]
    public void An_increased_insert_is_not_silently_folded_as_flat()
    {
        // status.power.omni reads SumIncreased (flat-only fire would refuse this atom at upsert —
        // correctly, and for the wrong test). The op rides the contribution into the Hub's own
        // parser — folding it to flat there would be battle-hub-fuse's old ignore-op debt anew.
        var host = MintHost();
        var gem = SaveGem(op: "increased", channel: "status.power.omni");
        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, host);
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-hub-gem", gem) });
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var (hub, ctx) = HubFor("specimen-1");
        var (_, contributions) = hub.ResolveDerivedWithContributions(ctx);

        var contribution = Assert.Single(contributions.ContributionsFor("status.power.omni"));
        Assert.Equal(DerivedModifierOp.Increased, contribution.Op);
    }

    [Fact]
    public void An_actor_with_no_sockets_resolves_with_zero_insert_attribution()
    {
        var host = MintHost();
        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, host);
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var (hub, ctx) = HubFor("specimen-1");
        var (_, contributions) = hub.ResolveDerivedWithContributions(ctx);

        // Byte-identical-today, proven structurally: every contribution on a socketless actor mints
        // through Equip(), so no insert: id can appear — on the Hub path and, post-fuse sharing the
        // same walk, the battle path too. The raw walk read below pins the same for battle.
        var derived = EquippedBoundAtoms.SourceFromStore(_store).DerivedAtomsFor("specimen-1");
        Assert.All(derived, d => Assert.StartsWith("equip:", d.SourceId, StringComparison.Ordinal));
        Assert.DoesNotContain(derived, d => d.SourceId.Contains("insert:", StringComparison.Ordinal));
    }
}
