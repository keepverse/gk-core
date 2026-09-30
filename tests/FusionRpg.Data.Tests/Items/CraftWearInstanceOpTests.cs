using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// species-gear-chain T24 — the craft-wear decrement at the DAL. It rides the SAME transaction as the
/// op it belongs to, moves <c>durability_current</c> only (never <c>max</c>), floors at zero, and is
/// never applied by a refused attempt. Driven with the REAL shipped recipe corpus and a real temper row.
/// </summary>
public class CraftWearInstanceOpTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    const long Player = 1;
    const string Temper = "recipe.012";

    public CraftWearInstanceOpTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static MaterialTuning Tuning() => MaterialTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Materials)));

    static MaterialRecipeCatalog Catalog() => MaterialRecipeCatalog.Load(
        Directory.EnumerateFiles(Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "recipes"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText),
        Tuning());

    string NewInstance() => _store.SaveInstance(new InstanceRow
    {
        ContainerId = "item.blade",
        RollSeed = 12345,
        CatalogRevision = _store.GetCatalogRevision(),
        Origin = InstanceOrigin.Drop,
        Atoms = new[] { new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}""") },
    });

    /// <summary>The temper row's own resolved price, funded to the unit, imported from the real corpus.</summary>
    IReadOnlyList<MaterialCostLine> FundedTemper()
    {
        var catalog = Catalog();
        _store.ImportRecipeCatalog(catalog);
        var lines = catalog.Resolve(Temper, new RecipeContext(0, 0, 24, "humanoid", 0));
        var souls = lines.Where(l => l.Class == MaterialClass.Souls).Sum(l => l.Qty);
        if (souls > 0) _store.AwardSouls(Player, souls, "t24.seed", Guid.NewGuid().ToString("N"));
        var mats = lines.Where(l => l.Class != MaterialClass.Souls).Select(l => (l.MaterialId, l.Qty)).ToList();
        if (mats.Count > 0) _store.GrantMaterials(Player, mats);
        return lines;
    }

    static WorkbenchMutation Wear(string instanceId, long durabilityCurrent, string originValues = "{}") =>
        new(instanceId, MutationOpKind.Enhance,
            new MutationResult("success", 1, Array.Empty<AtomValueSet>(), Array.Empty<int>(), Array.Empty<AtomAppend>()),
            null, originValues, "2026-09-19T00:00:00Z",
            DurabilityCurrent: durabilityCurrent);

    [Fact]
    public void Craft_wear_moves_durability_in_the_same_transaction_as_the_op()
    {
        var id = NewInstance();
        _store.SetDurability(id, 1000, 1000);
        var lines = FundedTemper();

        var result = _store.TrySpendAndApply(Player, Temper, lines, "wear-1", Wear(id, 950));

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(950, _store.GetDurability(id).Current);
        Assert.Equal(1000, _store.GetDurability(id).Max);   // max never moves on a craft
        // The decrement is attributable: its op row is right there beside it.
        Assert.Single(_store.ReadMutationOps(id));
    }

    [Fact]
    public void A_refused_craft_takes_the_wear_back_with_it()
    {
        var id = NewInstance();
        _store.SetDurability(id, 1000, 1000);
        var lines = FundedTemper();
        var short1 = lines.First(l => l.Class != MaterialClass.Souls);
        _store.GrantMaterials(Player, new[] { (short1.MaterialId, -short1.Qty) });   // one leg short

        var result = _store.TrySpendAndApply(Player, Temper, lines, "wear-2", Wear(id, 10));

        Assert.False(result.Ok);
        Assert.Equal(1000, _store.GetDurability(id).Current);
        Assert.Empty(_store.ReadMutationOps(id));
    }

    [Fact]
    public void An_attempt_with_no_wear_leaves_durability_alone()
    {
        var id = NewInstance();
        _store.SetDurability(id, 1000, 1000);
        var lines = FundedTemper();
        var noWear = new WorkbenchMutation(id, MutationOpKind.Enhance,
            new MutationResult("success", 1, Array.Empty<AtomValueSet>(), Array.Empty<int>(), Array.Empty<AtomAppend>()),
            null, "{}", "2026-09-19T00:00:00Z");

        Assert.True(_store.TrySpendAndApply(Player, Temper, lines, "wear-3", noWear).Ok);
        Assert.Equal(1000, _store.GetDurability(id).Current);
    }

    [Fact]
    public void A_wear_below_zero_is_refused_rather_than_clamped()
    {
        var id = NewInstance();
        _store.SetDurability(id, 1000, 5);
        var lines = FundedTemper();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _store.TrySpendAndApply(Player, Temper, lines, "wear-4", Wear(id, -1)));
        Assert.Equal(5, _store.GetDurability(id).Current);
    }
}
