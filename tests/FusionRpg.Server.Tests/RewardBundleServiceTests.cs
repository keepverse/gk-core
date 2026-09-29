using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using FusionRpg.Server.Achievements;
using Xunit;

namespace FusionRpg.Server.Tests;

// T3: bundle_ref → title container → the one produce-and-bind path. Determinism,
// undrawable refusal, dangling-ref refusal, owner landing, single-scale proof.
public class RewardBundleServiceTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public RewardBundleServiceTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        Seed();
    }

    public void Dispose() => _testStore.Dispose();

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);
    const int PinTheta = 20;

    void Seed()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.vitality.t1", KindId = "stat.modify", FamilyId = "atom.vitality", Tier = 1,
            Name = "Vitality", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        }).IsOk);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = "atom.focus.t1", KindId = "stat.modify", FamilyId = "atom.focus", Tier = 1,
            Name = "Focus", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":5}",
        }).IsOk);
        var affixResult = _store.UpsertAffix(
            new AffixRow("affix.focus", AffixClass.Prefix,
                new[] { new AffixRefRow(1, "atom.focus.t1") }),
            id => _store.GetAtom(id));
        Assert.True(affixResult.IsOk, affixResult.ToString());
        foreach (var (id, kind, atoms, prefix, pool) in new[]
        {
            ("empire-title.first-blood", ContainerKind.EmpireTitle,
                new[] { new ContainerAtomRow(1, "atom.vitality.t1") }, 1,
                new List<ContainerPoolRow> { new("affix.focus", 100, "g1") }),
            ("actor-title.first-blood", ContainerKind.ActorTitle,
                new[] { new ContainerAtomRow(1, "atom.vitality.t1") }, 1,
                new List<ContainerPoolRow> { new("affix.focus", 100, "g1") }),
        })
        {
            var cr = _store.UpsertContainer(new ContainerRow
            {
                ContainerId = id, Kind = kind, Atoms = atoms,
                PrefixRolls = prefix, Pool = pool,
            });
            Assert.True(cr.IsOk, $"{id}: {cr}");
        }
    }

    RewardBundleService Svc(PowerTuning? tuning = null) =>
        new(_store, tuning ?? Tuning, catalogRevision: 7);

    [Fact]
    public void Same_inputs_grant_byte_identical_instances()
    {
        var a = Svc().Grant("bundle.first-blood", "empire",
            new OwnerScope(OwnerKind.Player, "1"), 99, PinTheta, out var ga);
        var b = Svc().Grant("bundle.first-blood", "empire",
            new OwnerScope(OwnerKind.Player, "2"), 99, PinTheta, out var gb);
        Assert.True(a.IsOk, a.ToString());
        Assert.True(b.IsOk, b.ToString());
        Assert.Equal(ga!.Fingerprint, gb!.Fingerprint);
    }

    [Fact]
    public void Theta_sensitivity_changes_fingerprint()
    {
        // Magnitudes flow from the passed tuning inputs: same container+seed at two thetas
        // freeze differently (the single-scale-site proof pairs with Fanout_never_rescales —
        // fan-out itself never re-applies a scale).
        var a = Svc().Grant("bundle.first-blood", "empire",
            new OwnerScope(OwnerKind.Player, "3"), 99, PinTheta, out var ga);
        var b = Svc().Grant("bundle.first-blood", "empire",
            new OwnerScope(OwnerKind.Player, "4"), 99, PinTheta + 1, out var gb);
        Assert.True(a.IsOk, a.ToString());
        Assert.True(b.IsOk, b.ToString());
        Assert.NotEqual(ga!.Fingerprint, gb!.Fingerprint);
    }

    [Fact]
    public void Undrawable_pool_rejected_at_authoring_naming_group()
    {
        // An undrawable bundle can never reach the tables: UpsertContainer applies the
        // existing validator whole-row, so the grant path below can never promise it.
        var r = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = "empire-title.greedy", Kind = ContainerKind.EmpireTitle,
            PrefixRolls = 2,
            Pool = new List<ContainerPoolRow>
            {
                new("affix.focus", 100, "g1"),
                new("affix.focus", 100, "g1"),
            },
        });
        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.PoolRollsExceedGroups, r.Reason);
        Assert.Contains("prefix_rolls 2", r.ToString());
        Assert.Null(_store.GetContainer("empire-title.greedy"));
    }

    [Fact]
    public void Dangling_bundle_ref_refuses_naming_row()
    {
        var r = Svc().Grant("bundle.nope", "empire",
            new OwnerScope(OwnerKind.Player, "6"), 1, PinTheta, out var grant);
        Assert.False(r.IsOk);
        Assert.Null(grant);
        Assert.Contains("empire-title.nope", r.ToString());
    }

    [Fact]
    public void Grant_lands_bound_to_owner_scope()
    {
        var owner = new OwnerScope(OwnerKind.UniqueActor, "first-blood");
        var r = Svc().Grant("bundle.first-blood", "unique-actor",
            owner, 7, PinTheta, out var grant);
        Assert.True(r.IsOk, r.ToString());
        Assert.NotNull(grant);
        var resolved = _store.ResolveBindings(owner, new BindContext(RuntimeId.Lawn));
        Assert.NotEmpty(resolved.Bindings);
    }

    [Fact]
    public void Fanout_never_rescales_fixed_core()
    {
        // Pin theta reads at the pin (scale 1.0): the fixed maxHp flat stays authored 45
        // through grant, proving fan-out applies no second scale on top of TryInstantiate.
        var r = Svc().Grant("bundle.first-blood", "empire",
            new OwnerScope(OwnerKind.Player, "8"), 11, PinTheta, out var grant);
        Assert.True(r.IsOk, r.ToString());
        var instance = _store.GetInstance(grant!.InstanceId)!;
        var fixed_ = instance.Atoms[0];
        Assert.Contains("45", fixed_.ValuesJson);
    }
}
