using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

public class AssignmentStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AssignmentStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.assign-test", "", 1), KindId = "stat.modify",
            FamilyId = "atom.assign-test", Variant = "", Tier = 1, Name = "Assign Test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.assign-test", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.assign-test.t1") },
        }).IsOk);
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string SeedInstance()
    {
        var container = _store.GetContainer("item.assign-test")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    [Fact]
    public void An_assignment_survives_a_restart()
    {
        var instanceId = SeedInstance();
        _store.SaveAssignment("s7", ItemRole.ArmamentPrimary, "rolled", instanceId);

        var reopened = _testStore.Reopen();

        var rows = reopened.ListAssignments("s7");
        var row = Assert.Single(rows);
        Assert.Equal(ItemRole.ArmamentPrimary, row.Role);
        Assert.Equal(instanceId, row.RefId);
    }

    [Fact]
    public void One_role_one_occupant_reassigning_replaces_rather_than_duplicates()
    {
        var first = SeedInstance();
        var second = SeedInstance();
        _store.SaveAssignment("s7", ItemRole.ArmamentPrimary, "rolled", first);
        _store.SaveAssignment("s7", ItemRole.ArmamentPrimary, "rolled", second);

        var row = Assert.Single(_store.ListAssignments("s7"));
        Assert.Equal(second, row.RefId);
    }

    [Fact]
    public void Unequip_is_one_row_delete_with_no_second_writer()
    {
        var instanceId = SeedInstance();
        _store.SaveAssignment("s7", ItemRole.ArmamentPrimary, "rolled", instanceId);

        var removed = _store.RemoveAssignment("s7", ItemRole.ArmamentPrimary);

        Assert.True(removed);
        Assert.Empty(_store.ListAssignments("s7"));
    }

    [Fact]
    public void Unequip_does_not_destroy_the_item()
    {
        // Module 1's R1, from this side: RemoveAssignment touches only rpg_item_assignment.
        var instanceId = SeedInstance();
        _store.SaveItem(new RpgItemRow { InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveAssignment("s7", ItemRole.ArmamentPrimary, "rolled", instanceId);

        _store.RemoveAssignment("s7", ItemRole.ArmamentPrimary);

        Assert.NotNull(_store.GetInstance(instanceId));
        Assert.NotNull(_store.GetItem(instanceId));
    }

    [Fact]
    public void Bindings_are_rebuilt_as_a_full_projection_and_a_deleted_assignment_disappears()
    {
        var a1 = SeedInstance();
        var a2 = SeedInstance();
        _store.SaveAssignment("s7", ItemRole.ArmamentPrimary, "rolled", a1);
        _store.SaveAssignment("s7", ItemRole.CoreGuard, "rolled", a2);

        var gate = new EquipGate();
        var projector = new EquipProjector(gate,
            actorOf: _ => new SpecimenActor("s7", Frame: null, Level: 99, Faction: null),
            itemFactsOf: _ => new EquipItemFacts(Frame: null, LevelReq: null, FactionReq: null));

        var before = projector.Project("s7", _store.ListAssignments("s7"));
        Assert.Equal(2, before.Bindings.Count);
        Assert.Empty(before.Shortfalls);

        // Delete one assignment OUT OF BAND (not through the projector) and re-project. An
        // append-only implementation that only ever compares the produced list would still show 2
        // bindings here -- this is the assertion "full projection, never a delta" was making.
        _store.RemoveAssignment("s7", ItemRole.CoreGuard);
        var after = projector.Project("s7", _store.ListAssignments("s7"));

        Assert.Single(after.Bindings);
        Assert.Equal(ItemRole.ArmamentPrimary, after.Bindings[0].Role);
    }

    [Fact]
    public void A_content_revision_raising_level_req_strands_nothing()
    {
        var instanceId = SeedInstance();
        _store.SaveAssignment("s7", ItemRole.ArmamentPrimary, "rolled", instanceId);

        var gate = new EquipGate();
        // Simulates a content patch raising level_req above the specimen's own level.
        var projector = new EquipProjector(gate,
            actorOf: _ => new SpecimenActor("s7", Frame: null, Level: 5, Faction: null),
            itemFactsOf: _ => new EquipItemFacts(Frame: null, LevelReq: 50, FactionReq: null));

        var result = projector.Project("s7", _store.ListAssignments("s7"));

        Assert.Single(result.Bindings); // never force-unequipped
        Assert.Single(result.Shortfalls); // but the shortfall is named
        Assert.Equal(EquipRefusalReason.LevelTooLow, result.Shortfalls[0].Reason.Reason);
    }
}
