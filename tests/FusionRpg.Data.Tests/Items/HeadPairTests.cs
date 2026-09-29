using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// species-gear-chain T10 (potential storage) + T11 (durability-slice a): the two head-field pairs
/// on `effect_instance` — migration, nullability, lazy backfill, and the at-zero filter. In-memory
/// throughout; no population count anywhere.
/// </summary>
public class HeadPairTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public HeadPairTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    string NewInstance() => _store.SaveInstance(new InstanceRow
    {
        ContainerId = "item.blade",
        RollSeed = 12345,
        CatalogRevision = _store.GetCatalogRevision(),
        Origin = InstanceOrigin.Drop,
        Atoms = new[]
        {
            new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}"""),
        },
    });

    // ── migration + nullability ──────────────────────────────────────────────────────

    [Fact]
    public void Fresh_columns_default_null_never_a_fabricated_zero_or_full_value()
    {
        var id = NewInstance();
        // NULL is "not yet derived": a fabricated 0 would read as exhausted/broken.
        Assert.Equal(new InstanceHeadPair(null, null), _store.GetPotential(id));
        Assert.Equal(new InstanceHeadPair(null, null), _store.GetDurability(id));
    }

    [Fact]
    public void Pairs_roundtrip()
    {
        var id = NewInstance();
        _store.SetPotential(id, 120, 119);
        _store.SetDurability(id, 140, 140);
        Assert.Equal(new InstanceHeadPair(120, 119), _store.GetPotential(id));
        Assert.Equal(new InstanceHeadPair(140, 140), _store.GetDurability(id));
    }

    // ── lazy backfill ────────────────────────────────────────────────────────────────

    [Fact]
    public void First_read_derives_and_backfills_max_and_current()
    {
        var id = NewInstance();
        var derived = _store.GetOrDerivePotential(id, deriveMax: () => 111);
        Assert.Equal(new InstanceHeadPair(111, 111), derived);
        // Backfilled: the second read derives nothing.
        var again = _store.GetOrDerivePotential(id, deriveMax: () => throw new Xunit.Sdk.XunitException("re-derived"));
        Assert.Equal(new InstanceHeadPair(111, 111), again);
    }

    [Fact]
    public void Durability_backfills_through_the_same_seam()
    {
        var id = NewInstance();
        Assert.Equal(new InstanceHeadPair(150, 150), _store.GetOrDeriveDurability(id, deriveMax: () => 150));
        Assert.Equal(new InstanceHeadPair(150, 150), _store.GetDurability(id));
    }

    [Fact]
    public void A_pre_slice_database_gains_the_four_columns_with_nulls_not_zeros()
    {
        // The memory equivalent of "a save from before this module shipped": the exact
        // effect_instance shape minus the four head columns, plus one row, before Init runs.
        using var test = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var cmd = seed.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE effect_instance (
                  instance_id TEXT NOT NULL PRIMARY KEY,
                  container_id TEXT NOT NULL,
                  roll_seed INTEGER NOT NULL,
                  catalog_revision INTEGER NOT NULL DEFAULT 0,
                  created_utc TEXT NOT NULL,
                  origin TEXT NOT NULL DEFAULT 'drop',
                  theta_content INTEGER NOT NULL DEFAULT 0,
                  content_scale_milli INTEGER NOT NULL DEFAULT 1000
                );
                INSERT INTO effect_instance
                  (instance_id, container_id, roll_seed, catalog_revision, created_utc)
                VALUES ('pre-slice-1', 'item.blade', 7, 0, '2026-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        });

        Assert.Equal(new InstanceHeadPair(null, null), test.Store.GetPotential("pre-slice-1"));
        Assert.Equal(new InstanceHeadPair(null, null), test.Store.GetDurability("pre-slice-1"));
    }

    // ── the at-zero filter (T11) ─────────────────────────────────────────────────────

    string MintAndAssign(string specimenId)
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.headpair-power", "", 1),
            KindId = "stat.derived",
            FamilyId = "atom.headpair-power",
            Variant = "",
            Tier = 1,
            Name = "Headpair Power",
            ParamsJson = "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":30}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.headpair-power",
            Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.headpair-power.t1") },
        }).IsOk);

        var container = _store.GetContainer("item.headpair-power")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20,
            FusionRpg.Core.Power.PowerTuning.Build(
                1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000),
            out var inst);
        Assert.True(r.IsOk, r.ToString());
        var instanceId = _store.SaveInstance(inst!);
        _store.SaveAssignment(specimenId, FusionRpg.Core.Items.ItemRole.ArmamentPrimary,
            FusionRpg.Core.Items.EquipRefKinds.Rolled, instanceId);
        return instanceId;
    }

    [Fact]
    public void A_zero_Durability_instance_is_excluded_while_an_underived_one_is_admitted()
    {
        var broken = MintAndAssign("specimen-broken");
        _store.SetDurability(broken, 100, 0);
        Assert.Empty(_store.MaterializeRolledEquipRuntime("specimen-broken", level: 50));
        Assert.Empty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-broken")));

        var fresh = MintAndAssign("specimen-fresh");
        Assert.Empty(_store.MaterializeRolledEquipRuntime("specimen-fresh", level: 50));
        Assert.NotEmpty(_store.ListBindings(new OwnerScope(OwnerKind.UniqueActor, "specimen-fresh")));
    }
}
