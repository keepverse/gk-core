using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// item-ideal.md, `equip-runtime` (module 5) — the DB half of the payoff: module 4's projection
/// actually reaches `effect_binding` at `unique-actor:` scope, and `ResolveBindings` actually
/// surfaces the atom a real caller (`EquipAtomSource.FromResolver`) would read.
/// </summary>
public class EquipRuntimeStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public EquipRuntimeStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.equip-power", "", 1), KindId = "stat.derived",
            FamilyId = "atom.equip-power", Variant = "", Tier = 1, Name = "Equip Power",
            ParamsJson = "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":30}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.equip-power", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.equip-power.t1") },
        }).IsOk);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    string SeedInstance()
    {
        var container = _store.GetContainer("item.equip-power")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    ProjectionResult ProjectFor(string specimenId, int level = 50) =>
        new EquipProjector(new EquipGate(),
            actorOf: _ => new SpecimenActor(specimenId, Frame: null, Level: level, Faction: null),
            itemFactsOf: _ => new EquipItemFacts(Frame: null, LevelReq: null, FactionReq: null))
            .Project(specimenId, _store.ListAssignments(specimenId));

    [Fact]
    public void An_applied_projection_reaches_resolve_bindings_and_a_real_reader_sees_it()
    {
        var instanceId = SeedInstance();
        _store.SaveAssignment("s42", ItemRole.ArmamentPrimary, "rolled", instanceId);

        _store.ApplyEquipProjection("s42", ProjectFor("s42"));

        // Exactly the shape EquipAtomSource.FromResolver's production caller would build.
        var resolution = _store.ResolveBindings(
            new OwnerScope(OwnerKind.UniqueActor, "s42"), new BindContext(RuntimeId.Battle));
        var atoms = resolution.AtomsByBinding!.Values.SelectMany(a => a).ToList();

        var source = EquipAtomSource.FromResolver(_ => atoms);
        var mods = source.DerivedAtomsFor("s42");

        var mod = Assert.Single(mods);
        Assert.Equal("combat.power.fire", mod.Channel);
        Assert.Equal(30, mod.Amount);
    }

    [Fact]
    public void Unequipping_removes_the_binding_with_no_residue()
    {
        var instanceId = SeedInstance();
        // Ownership registered, matching realistic usage -- an equipped item is normally owned too,
        // and this is what lets R1's two-reachability-root sweep keep it after the binding is gone.
        _store.SaveItem(new RpgItemRow { InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveAssignment("s42", ItemRole.ArmamentPrimary, "rolled", instanceId);
        _store.ApplyEquipProjection("s42", ProjectFor("s42"));

        Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "s42")));

        _store.RemoveAssignment("s42", ItemRole.ArmamentPrimary);
        _store.ApplyEquipProjection("s42", ProjectFor("s42"));

        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "s42")));
        // R1, from the equip-runtime side: the OWNED instance survives -- only the binding is gone.
        Assert.NotNull(_store.GetInstance(instanceId));
    }

    [Fact]
    public void An_unowned_unbound_instance_is_swept_after_the_last_binding_is_withdrawn()
    {
        // The other half of the same guarantee, stated as its own test rather than left implicit: an
        // instance with NEITHER an owner NOR a binding is exactly what the orphan sweep exists to
        // collect (module 1, R1) -- equip-runtime's withdraw path must not special-case around that.
        var instanceId = SeedInstance();
        _store.SaveAssignment("s42", ItemRole.ArmamentPrimary, "rolled", instanceId);
        _store.ApplyEquipProjection("s42", ProjectFor("s42"));

        _store.RemoveAssignment("s42", ItemRole.ArmamentPrimary);
        _store.ApplyEquipProjection("s42", ProjectFor("s42"));

        Assert.Null(_store.GetInstance(instanceId));
    }

    [Fact]
    public void Reapplying_an_unchanged_projection_does_not_churn_the_binding()
    {
        var instanceId = SeedInstance();
        _store.SaveAssignment("s42", ItemRole.ArmamentPrimary, "rolled", instanceId);

        _store.ApplyEquipProjection("s42", ProjectFor("s42"));
        var first = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "s42")).Single().BindingId;

        _store.ApplyEquipProjection("s42", ProjectFor("s42"));
        var second = _store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "s42")).Single().BindingId;

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_lapsed_level_req_projection_keeps_the_binding_live()
    {
        var instanceId = SeedInstance();
        _store.SaveAssignment("s42", ItemRole.ArmamentPrimary, "rolled", instanceId);

        var lapsed = new EquipProjector(new EquipGate(),
            actorOf: _ => new SpecimenActor("s42", Frame: null, Level: 1, Faction: null),
            itemFactsOf: _ => new EquipItemFacts(Frame: null, LevelReq: 99, FactionReq: null))
            .Project("s42", _store.ListAssignments("s42"));

        Assert.Single(lapsed.Shortfalls);
        _store.ApplyEquipProjection("s42", lapsed);

        // Never force-unequipped: the binding is still there, and the atom still resolves.
        Assert.Single(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "s42")));
    }
}
