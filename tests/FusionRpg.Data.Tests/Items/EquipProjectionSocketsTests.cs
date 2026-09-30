using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// species-gear-chain T21 (arm 1): filled sockets project their insert instances through the same
/// projection that carries the host — never a parallel Bind. The Hub end of the seam is proven in
/// Server <c>SocketCombatHubTests</c> (this project cannot reference the Server-side input walk);
/// what is pinned here is the projection contract itself: one binding per filled socket, the reaper
/// keeps it, unequip and removal withdraw it, and sockets without instances project nothing.
/// Closed enums + relationships only; no population counts.
/// </summary>
[Trait("VerificationId", "data.item-socket")]
public class EquipProjectionSocketsTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public EquipProjectionSocketsTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    string MintInstance(string containerId)
    {
        var container = _store.GetContainer(containerId)!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    string SaveGemInstance(string instanceId)
    {
        // A gem instance is arranged by hand: this Data-layer contract tests the projection, not
        // the verb. Production mints the same shape through GemContainerBuild + TryInstantiate
        // inside socket-insert (species-gear-chain T22); the Server verb tests prove that path.
        return _store.SaveInstance(new InstanceRow
        {
            ContainerId = "gem.socket-gem-power",
            RollSeed = 7,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, "atom.socket-gem-power.t1", "{\"amount\":30}") },
        }, instanceId);
    }

    void SeedHostAndGem(out string hostInstance, out string gemInstance)
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.socket-host-power", "", 1), KindId = "stat.derived",
            FamilyId = "atom.socket-host-power", Variant = "", Tier = 1, Name = "Socket Host Power",
            ParamsJson = "{\"channel\":\"combat.power.earth\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.socket-host", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.socket-host-power.t1") },
        }).IsOk);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.socket-gem-power", "", 1), KindId = "stat.derived",
            FamilyId = "atom.socket-gem-power", Variant = "", Tier = 1, Name = "Socket Gem Power",
            ParamsJson = "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":30}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "gem.socket-gem-power", Kind = ContainerKind.Gem,
            Atoms = new[] { new ContainerAtomRow(1, "atom.socket-gem-power.t1") },
        }).IsOk);

        hostInstance = MintInstance("item.socket-host");
        gemInstance = SaveGemInstance("gem-inst-1");
        _store.SaveAssignment("specimen-1", ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, hostInstance);
    }

    string PlayerKey => _store.GetCurrentPlayerId()
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>`SocketHostFor` reads the host's `item_generation` row for its role and frame
    /// (`RpgStore.ItemCard.cs:404`) — without one there is no host, so a combination never evaluates.
    /// Same shape as the Server's `SocketHostForTests.SeedItem`.</summary>
    void PersistGeneration(string instanceId, string containerId, string role)
    {
        if (!_store.ListRarities().Any(r => r.RarityId == "common"))
            Assert.True(_store.UpsertRarity(new RarityRow("common", 1, 1, 0, 1, 5)).Ok);
        var ordinal = _store.ListRarities().First(r => r.RarityId == "common").Ordinal;
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = PlayerKey,
            AcquiredUtc = "2026-09-22T00:00:00Z", OriginKind = "drop",
        }).IsOk);
        _store.PersistLoot(
            PlayerKey,
            new LootManifest("combo-probe-" + instanceId, "table.combo-probe", 11UL, 20,
                Array.Empty<LootGrant>(), Array.Empty<string>(), "{}", LootPityState.Empty,
                LootPityState.Empty, null, false, null),
            "test", "combo-probe", _store.GetCatalogRevision(), 1,
            new[] { new ItemGenerationRow(instanceId, 0, containerId, ordinal, 20, "humanoid", role, "drop") });
    }

    /// <summary>The SSH4.6 inputs the boot sets: the socket tuning plus the gem lookup that resolves an
    /// insert container's family. The combo container itself is content SSH4.4 builds at boot; here it
    /// is arranged by hand, exactly as the gem containers already are in this file.</summary>
    void SeedComboRuntime(string gemContainerId, string gemFamily)
    {
        var sockets = SocketTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));
        _store.UseEquipCombinationEvaluation(new RpgStore.EquipCombinationInputs(
            sockets,
            id => string.Equals(id, gemContainerId, StringComparison.Ordinal)
                ? new CardInsertLookup(new InsertDef(id, gemFamily, "", 1), "insert." + gemFamily)
                : null));

        var lookups = new ComboContainerBuild.ComboContainerLookups(_store.GetAtom);
        var container = ComboContainerBuild.TryBuild(
            ComboContainerId, new[] { gemFamily }, 1, lookups, out var refusal);
        Assert.NotNull(container);
        Assert.True(_store.UpsertContainer(container!).IsOk, refusal);
        _store.SeedComboRecipes(new[]
        {
            new ComboRecipe(ComboContainerId, ComboShape.Strain, "", 0, "", "", 1, 1,
                new[] { new ComboIngredient(gemFamily, 1) }, BaseFloors: new[] { 1 }),
        });
    }

    const string ComboContainerId = "combo.strain-combo-probe";

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    [Fact]
    public void A_firing_strain_binds_its_container_through_the_data_projection()
    {
        SeedHostAndGem(out var host, out var gem);
        PersistGeneration(host, "item.socket-host", ItemRoles.Id(ItemRole.ArmamentPrimary));
        SeedComboRuntime("gem.socket-gem-power", "atom.socket-gem-power");
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-gem-power", gem) });

        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var bindings = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1"));
        var combo = Assert.Single(bindings, b => b.InstanceId.StartsWith("cmb:", StringComparison.Ordinal));
        Assert.Equal(ComboBindTargets.InstanceId(host, 0, ComboContainerId + "-t1"), combo.InstanceId);
        Assert.Equal(ItemRoles.Id(ItemRole.ArmamentPrimary), combo.Slot);
    }

    [Fact]
    public void Removing_one_ingredient_withdraws_the_combination()
    {
        SeedHostAndGem(out var host, out var gem);
        PersistGeneration(host, "item.socket-host", ItemRoles.Id(ItemRole.ArmamentPrimary));
        SeedComboRuntime("gem.socket-gem-power", "atom.socket-gem-power");
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-gem-power", gem) });
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);
        Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")),
            b => b.InstanceId.StartsWith("cmb:", StringComparison.Ordinal));

        // The fill no longer satisfies the word: the next projection produces no target, so the
        // reaper withdraws the binding in the same reconcile.
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false) });
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        Assert.DoesNotContain(
            _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")),
            b => b.InstanceId.StartsWith("cmb:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_host_fingerprint_is_unchanged_by_binding()
    {
        SeedHostAndGem(out var host, out var gem);
        PersistGeneration(host, "item.socket-host", ItemRoles.Id(ItemRole.ArmamentPrimary));
        SeedComboRuntime("gem.socket-gem-power", "atom.socket-gem-power");
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-gem-power", gem) });

        var before = _store.GetInstance(host)!.ContentFingerprint();
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);
        var after = _store.GetInstance(host)!.ContentFingerprint();

        // A combination binds, never rewrites the host: the fingerprint is the identity SC5 reproduces.
        Assert.Equal(before, after);
        Assert.Contains(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")),
            b => b.InstanceId.StartsWith("cmb:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_filled_socket_projects_exactly_one_binding_at_the_hosts_role()
    {
        SeedHostAndGem(out var host, out var gem);
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-gem-power", gem) });

        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")));

        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var bindings = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1"));
        Assert.Equal(2, bindings.Count);
        var insert = Assert.Single(bindings, b => b.InstanceId == gem);
        Assert.Equal(ItemRoles.Id(ItemRole.ArmamentPrimary), insert.Slot);
    }

    [Fact]
    public void Materializing_twice_keeps_the_insert_binding_intact()
    {
        SeedHostAndGem(out var host, out var gem);
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-gem-power", gem) });

        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        // The reaper compares by InstanceId against the projection's own desired set: the insert
        // row comes from the same projection, so the second reconcile finds it desired, not stale.
        // A parallel-Bind design would have lost it here — the regression this test exists for.
        var bindings = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1"));
        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, b => b.InstanceId == gem);
    }

    [Fact]
    public void Unequipping_the_host_withdraws_the_insert_binding_in_the_same_reconcile()
    {
        SeedHostAndGem(out var host, out var gem);
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-gem-power", gem) });
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);
        Assert.Equal(2, _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")).Count);

        _store.RemoveAssignment("specimen-1", ItemRole.ArmamentPrimary);
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")));
    }

    [Fact]
    public void Emptying_the_socket_withdraws_the_insert_binding_but_keeps_the_host()
    {
        SeedHostAndGem(out var host, out var gem);
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.socket-gem-power", gem) });
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);
        Assert.Equal(2, _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")).Count);

        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false) });
        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var only = Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")));
        Assert.Equal(host, only.InstanceId);
    }

    [Fact]
    public void Sockets_without_an_instance_project_nothing_and_invent_nothing()
    {
        SeedHostAndGem(out var host, out _);
        _store.SetSockets(host, new List<SocketSlot>
        {
            new(0, "", false),
            new(1, "", false, "gem.socket-gem-power", null), // container but no instance
        });

        _store.MaterializeRolledEquipRuntime("specimen-1", level: 50);

        var only = Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-1")));
        Assert.Equal(host, only.InstanceId);
    }
}
