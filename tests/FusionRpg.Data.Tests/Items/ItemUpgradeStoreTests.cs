using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// species-gear-chain T37 — the consume-and-replace write (`spec-item-upgrade-tree.md` § the executor's
/// consume-and-replace seam): one transaction that appends the op for the CONSUMED instance, gives it the
/// salvage-shaped disposition, and saves the successor's own instance and generation rows. Replay is the
/// spend log's own, and the outcome ref is the successor.
/// </summary>
[Trait("VerificationId", "data.item-upgrade")]
public class ItemUpgradeStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ItemUpgradeStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    InstanceRow Carried(string instanceId) => new()
    {
        InstanceId = instanceId,
        ContainerId = "item.cloth-footing",
        RollSeed = 77_001,
        CatalogRevision = _store.GetCatalogRevision(),
        Origin = InstanceOrigin.Drop,
        Atoms = new[]
        {
            new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}"""),
        },
    };

    static WorkbenchMutation Consume(string instanceId) => new(
        instanceId, MutationOpKind.Upgrade,
        new MutationResult("upgraded", 0, Array.Empty<AtomValueSet>(), Array.Empty<int>(),
            Array.Empty<AtomAppend>()),
        StateHash: null, OriginValuesJson: null, AppliedUtc: DateTime.UtcNow.ToString("O"));

    [Fact]
    public void The_input_is_consumed_the_successor_is_saved_and_the_outcome_ref_is_the_successor()
    {
        var consumed = _store.SaveInstance(Carried("consumed-1"));
        var successor = _store.SaveInstance(Carried("successor-1"));
        var generation = new ItemGenerationRow("successor-1", DropLogId: 42,
            BaseTypeId: "item.humanoid-footing-leather-001", RarityOrdinal: 2, ItemLevel: 12,
            Frame: "humanoid", Role: "footing", AffixChannel: "drop");

        var applied = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-upgrade-1", Consume(consumed), _store.GetInstance(successor)!, generation);

        Assert.True(applied.Ok, applied.Reason);
        Assert.Equal("successor-1", applied.OutcomeRef);

        // The successor's generation row carries the successor's chassis and the original provenance.
        var stamped = _store.GetItemGeneration("successor-1");
        Assert.NotNull(stamped);
        Assert.Equal("item.humanoid-footing-leather-001", stamped!.BaseTypeId);
        Assert.Equal(42, stamped.DropLogId);

        // The op is the CONSUMED item's own history — the ledger reads as that item's lineage.
        var op = Assert.Single(_store.ReadMutationOps(consumed));
        Assert.Equal("upgrade", MutationOpKinds.Id(op.Kind));
        Assert.Equal("corr-upgrade-1", op.CorrelationId);

        // ⛔ Never deleted: the disposition is the salvage-shaped write, so the ledger stays readable.
        Assert.NotNull(_store.GetInstance(consumed));
    }

    [Fact]
    public void A_replayed_correlation_returns_the_same_successor_and_writes_no_second_op()
    {
        var consumed = _store.SaveInstance(Carried("consumed-2"));
        var generation = new ItemGenerationRow("successor-2", DropLogId: 43,
            BaseTypeId: "item.humanoid-footing-leather-001", RarityOrdinal: 2, ItemLevel: 12,
            Frame: "humanoid", Role: "footing", AffixChannel: "drop");

        var first = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-upgrade-2", Consume(consumed), Carried("successor-2"), generation);
        var second = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-upgrade-2", Consume(consumed), Carried("successor-2"), generation);

        Assert.True(first.Ok, first.Reason);
        Assert.True(second.Ok, second.Reason);
        Assert.True(second.Replayed);
        Assert.Equal("successor-2", second.OutcomeRef);
        Assert.Single(_store.ReadMutationOps(consumed));
    }

    [Fact]
    public void A_successor_that_is_the_consumed_instance_itself_is_refused_by_the_caller_contract()
    {
        var consumed = _store.SaveInstance(Carried("consumed-3"));
        // `SaveInstance` mints the id, so the generation row names the id the store actually returned.
        var sameId = new ItemGenerationRow(consumed, DropLogId: 44,
            BaseTypeId: "item.humanoid-footing-leather-001", RarityOrdinal: 2, ItemLevel: 12,
            Frame: "humanoid", Role: "footing", AffixChannel: "drop");

        Assert.Throws<ArgumentException>(() => _store.TryUpgradeAndApply(1, "upgrade",
            Array.Empty<MaterialCostLine>(), "corr-upgrade-3", Consume(consumed), Carried("consumed-3"), sameId));
    }

    // ── T37 criterion 6 — Hub bindings against the consumed instance ───────────────────────────────

    const string ProjectedFamily = "atom.up-proj";

    /// <summary>A host whose atom PROJECTS (a `stat.derived` channel, the shape `EquipAtomSource`
    /// composes), assigned to a specimen, so the reconcile has a binding to reap.</summary>
    (string instanceId, string specimenId) SeedBoundHost()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId(ProjectedFamily, "", 1), KindId = "stat.derived",
            FamilyId = ProjectedFamily, Variant = "", Tier = 1, Name = "up projection",
            ParamsJson = """{"channel":"combat.power.earth","op":"flat","amount":25}""",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.up-proj-host", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(0, AtomRow.DeriveId(ProjectedFamily, "", 1)) },
        }).IsOk);

        var instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = "item.up-proj-host",
            RollSeed = 9,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, AtomRow.DeriveId(ProjectedFamily, "", 1), """{"amount":25}""") },
        });
        const string specimen = "specimen-up-1";
        _store.SaveAssignment(specimen, ItemRole.ArmamentPrimary,
            EquipRefKinds.Rolled, instanceId);
        return (instanceId, specimen);
    }

    [Fact]
    public void The_consumed_instances_binding_is_withdrawn_by_the_existing_reconcile_and_the_successor_carries_none()
    {
        var (host, specimen) = SeedBoundHost();
        _store.MaterializeRolledEquipRuntime(specimen, level: 50);

        var scope = new OwnerScope(OwnerKind.UniqueActor, specimen);
        Assert.Contains(_store.ListBindings(scope), b => b.InstanceId == host);

        var successorId = "successor-bound-1";
        var applied = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-up-bind-1", Consume(host), Carried(successorId),
            new ItemGenerationRow(successorId, DropLogId: 77, BaseTypeId: "item.up-proj-host",
                RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid", Role: "armament-primary",
                AffixChannel: "drop"));
        Assert.True(applied.Ok, applied.Reason);

        // The reconcile runs again, exactly as a squad build does.
        _store.MaterializeRolledEquipRuntime(specimen, level: 50);

        var bindings = _store.ListBindings(scope);
        Assert.DoesNotContain(bindings, b => b.InstanceId == host);
        Assert.DoesNotContain(bindings, b => b.InstanceId == successorId);
    }

    /// <summary>
    /// T37 criterion 6, the harder half: the consumed host may hold a FILLED SOCKET, and the insert's
    /// own binding must go too. A stale insert binding is worse than a stale host one — the gem would
    /// go on contributing its atoms after the piece it sits in has been consumed.
    /// </summary>
    [Fact]
    public void A_consumed_hosts_socket_insert_binding_is_withdrawn_too()
    {
        var (host, specimen) = SeedBoundHost();

        // A gem whose own atom projects, so the insert gets a binding of its own (the shape
        // EquipProjectionSocketsTests pins for the host's filled socket).
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.up-proj-gem", "", 1), KindId = "stat.derived",
            FamilyId = "atom.up-proj-gem", Variant = "", Tier = 1, Name = "up projection gem",
            ParamsJson = """{"channel":"combat.power.fire","op":"flat","amount":30}""",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "gem.up-proj", Kind = ContainerKind.Gem,
            Atoms = new[] { new ContainerAtomRow(0, AtomRow.DeriveId("atom.up-proj-gem", "", 1)) },
        }).IsOk);
        var gem = _store.SaveInstance(new InstanceRow
        {
            ContainerId = "gem.up-proj",
            RollSeed = 11,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, AtomRow.DeriveId("atom.up-proj-gem", "", 1), """{"amount":30}""") },
        });
        _store.SetSockets(host, new List<FusionRpg.Core.Items.Sockets.SocketSlot>
        {
            new(0, "", false, "gem.up-proj", gem),
        });

        _store.MaterializeRolledEquipRuntime(specimen, level: 50);
        var scope = new OwnerScope(OwnerKind.UniqueActor, specimen);
        var before = _store.ListBindings(scope);
        Assert.Contains(before, b => b.InstanceId == host);
        Assert.Contains(before, b => b.InstanceId == gem);

        var successorId = "successor-socketed-1";
        var applied = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-up-bind-2", Consume(host), Carried(successorId),
            new ItemGenerationRow(successorId, DropLogId: 78, BaseTypeId: "item.up-proj-host",
                RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid", Role: "armament-primary",
                AffixChannel: "drop"));
        Assert.True(applied.Ok, applied.Reason);

        _store.MaterializeRolledEquipRuntime(specimen, level: 50);

        var after = _store.ListBindings(scope);
        Assert.DoesNotContain(after, b => b.InstanceId == host);
        // ⛔ The insert's binding is the one that must not survive: the gem is still owned, but the
        // piece it was socketed into is gone, so the projection has nothing to hang it on.
        Assert.DoesNotContain(after, b => b.InstanceId == gem);
        Assert.Empty(after);
    }

    /// <summary>
    /// T37 — the upgrade must stay AUDITABLE: the op row records what was spent (D2 clause 11's "a spent
    /// cost with no op is theft; an op with no cost is duplication", read from the other side), and the
    /// consumed item's own generation row SURVIVES the disposal, so the ledger can still say what was
    /// consumed. Deleting the row instead of disposing it would erase the receipt.
    /// </summary>
    [Fact]
    public void The_consumed_items_generation_row_and_the_spend_record_survive_the_upgrade()
    {
        var consumed = _store.SaveInstance(Carried("consumed-audit-1"));
        _store.PersistLoot(
            "1",
            new LootManifest("audit-drop", "table.up-test", 7UL, 20, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "up-audit", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(consumed, 0, "item.up-test-base-a-001",
                    RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid", Role: "armament-primary",
                    AffixChannel: "drop"),
            });

        // The spend needs funds; the audit is about what the records say afterwards.
        _store.AwardSouls(1, 10_000, "test-seed", "up-audit-seed-1");

        var successorId = "successor-audit-1";
        var applied = _store.TryUpgradeAndApply(1, "upgrade",
            new[] { MaterialCostLine.Souls(240) }, "corr-up-audit-1", Consume(consumed),
            Carried(successorId),
            new ItemGenerationRow(successorId, DropLogId: 91, BaseTypeId: "item.up-test-base-b-001",
                RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid",
                Role: "armament-primary", AffixChannel: "drop"));
        Assert.True(applied.Ok, applied.Reason);

        // The receipt: what was consumed is still readable, and the spend is on the op that consumed it.
        var consumedGeneration = _store.GetItemGeneration(consumed);
        Assert.NotNull(consumedGeneration);
        Assert.Equal("item.up-test-base-a-001", consumedGeneration!.BaseTypeId);

        var op = Assert.Single(_store.ReadMutationOps(consumed));
        Assert.Equal("upgrade", MutationOpKinds.Id(op.Kind));
        Assert.Contains("Souls", op.CostJson, StringComparison.Ordinal);
        Assert.Contains("240", op.CostJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// species-gear-chain T60 — a worn input must NOT become a pristine successor. The successor's
    /// durability is written in the same transaction from the caller's carried fraction; this pins the
    /// Data half (the caller's arithmetic is pinned at the Server layer).
    /// </summary>
    [Fact]
    public void The_successor_carries_the_used_durability_fraction_instead_of_a_fresh_max()
    {
        var consumed = _store.SaveInstance(Carried("consumed-wear-1"));
        _store.SetDurability(consumed, max: 100, current: 25);        // 25% left
        var successorId = "successor-wear-1";

        var applied = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-up-wear-1", Consume(consumed), Carried(successorId),
            new ItemGenerationRow(successorId, DropLogId: 92, BaseTypeId: "item.up-test-base-b-001",
                RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid", Role: "armament-primary",
                AffixChannel: "drop"),
            successorDurabilityCurrent: 50);                           // 25% of a 200 max
        Assert.True(applied.Ok, applied.Reason);

        var pair = _store.GetDurability(successorId);
        Assert.Equal(50, pair.Current);

        // And with NO carry parameter the successor stays underived (the pre-fix behaviour, kept for
        // callers with no wear inputs) rather than silently inheriting something.
        var plainId = "successor-wear-2";
        var plain = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-up-wear-2", Consume(consumed), Carried(plainId),
            new ItemGenerationRow(plainId, DropLogId: 93, BaseTypeId: "item.up-test-base-b-001",
                RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid", Role: "armament-primary",
                AffixChannel: "drop"));
        Assert.True(plain.Ok, plain.Reason);
        Assert.Null(_store.GetDurability(plainId).Current);
    }

    /// <summary>
    /// species-gear-chain T60 (potential half) — the upgrade must not be a potential-reset loop either
    /// (spec-item-upgrade-tree.md § Open question 3). The successor's <c>craft_potential_current</c> is
    /// written in the SAME transaction from the caller's carried fraction, on the successor's own id; a
    /// caller with no carry parameter leaves it underived rather than inventing a value.
    /// </summary>
    [Fact]
    public void The_successor_carries_the_used_potential_fraction_instead_of_a_fresh_max()
    {
        var consumed = _store.SaveInstance(Carried("consumed-potential-1"));
        _store.SetPotential(consumed, max: 400, current: 100);        // 25% left
        var successorId = "successor-potential-1";

        var applied = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-up-potential-1", Consume(consumed), Carried(successorId),
            new ItemGenerationRow(successorId, DropLogId: 94, BaseTypeId: "item.up-test-base-b-001",
                RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid", Role: "armament-primary",
                AffixChannel: "drop"),
            successorPotentialCurrent: 75);                          // 25% of a 300 max
        Assert.True(applied.Ok, applied.Reason);

        var pair = _store.GetPotential(successorId);
        Assert.Equal(75, pair.Current);
        // The MAX is still underived on the successor — only the used fraction crosses, exactly as
        // durability's own carry works; the successor derives its own max on first read.
        Assert.Null(pair.Max);

        // No carry parameter (a caller with no potential ceiling): underived, never a fabricated full pair.
        var plainId = "successor-potential-2";
        var plain = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-up-potential-2", Consume(consumed), Carried(plainId),
            new ItemGenerationRow(plainId, DropLogId: 95, BaseTypeId: "item.up-test-base-b-001",
                RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid", Role: "armament-primary",
                AffixChannel: "drop"));
        Assert.True(plain.Ok, plain.Reason);
        Assert.Null(_store.GetPotential(plainId).Current);
        Assert.Null(_store.GetPotential(plainId).Max);
    }

    /// <summary>Both carries ride ONE transaction: a successor can never end up with a spawned
    /// durability fraction and an underived potential (or the reverse) from a single upgrade.</summary>
    [Fact]
    public void Both_head_pairs_cross_in_the_same_upgrade()
    {
        var consumed = _store.SaveInstance(Carried("consumed-both-1"));
        _store.SetDurability(consumed, max: 100, current: 25);
        _store.SetPotential(consumed, max: 400, current: 100);
        var successorId = "successor-both-1";

        var applied = _store.TryUpgradeAndApply(1, "upgrade", Array.Empty<MaterialCostLine>(),
            "corr-up-both-1", Consume(consumed), Carried(successorId),
            new ItemGenerationRow(successorId, DropLogId: 96, BaseTypeId: "item.up-test-base-b-001",
                RarityOrdinal: 1, ItemLevel: 20, Frame: "humanoid", Role: "armament-primary",
                AffixChannel: "drop"),
            successorDurabilityCurrent: 50, successorPotentialCurrent: 75);
        Assert.True(applied.Ok, applied.Reason);

        Assert.Equal(50, _store.GetDurability(successorId).Current);
        Assert.Equal(75, _store.GetPotential(successorId).Current);
    }
}
