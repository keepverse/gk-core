using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Uniques;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// <c>item_unique</c> — ssot-uniques.md §5.2, module 17 — against a real SQLite store, not a mock.
/// </summary>
public class ItemUniqueStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ItemUniqueStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    static UniqueTuning Tuning() =>
        UniqueTuning.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "uniques.v1.json")));

    string NewContainer(string id = "item.kiln-nozzle")
    {
        var check = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = id,
            Kind = ContainerKind.Item,
            Slot = "armament-primary",
        });
        Assert.True(check.IsOk, check.Detail);
        return id;
    }

    static UniqueRow Row(string containerId, string? flavour = "flavour.kiln-nozzle") => new(
        containerId, "item.plant-muzzle-a-001", UniqueCounterPressure.Drawback, 393, "offense",
        UniqueAcquisition.SourceLocked, UniqueEnhanceScope.MagnitudeOnly, flavour);

    [Fact]
    public void A_unique_row_round_trips_with_every_one_of_its_nine_columns()
    {
        var id = NewContainer();
        _store.UpsertItemUnique(Row(id));

        var read = _store.GetItemUnique(id);
        Assert.NotNull(read);
        Assert.Equal("item.plant-muzzle-a-001", read!.DerivedFrom);
        Assert.Equal(UniqueCounterPressure.Drawback, read.CounterPressure);
        Assert.Equal(393, read.BudgetAeHundredths);
        Assert.Equal("offense", read.PowerAxis);
        Assert.Equal(UniqueAcquisition.SourceLocked, read.Acquisition);
        Assert.Equal(UniqueEnhanceScope.MagnitudeOnly, read.EnhanceScope);
        Assert.Equal("flavour.kiln-nozzle", read.FlavourKey);
        Assert.True(read.Enabled);
        Assert.Equal(1, read.Revision);
    }

    /// <summary>32 of the shipped 144 carry no flavour key yet, so NULL must survive the round trip.</summary>
    [Fact]
    public void A_null_flavour_key_round_trips_as_null_and_never_as_an_empty_string()
    {
        var id = NewContainer();
        _store.UpsertItemUnique(Row(id, flavour: null));
        Assert.Null(_store.GetItemUnique(id)!.FlavourKey);
    }

    [Fact]
    public void Upsert_replaces_rather_than_duplicating_and_the_list_is_ordered()
    {
        var a = NewContainer("item.kiln-nozzle");
        var b = NewContainer("item.brainpan-sigil");

        _store.UpsertItemUnique(Row(a));
        _store.UpsertItemUnique(Row(b));
        _store.UpsertItemUnique(Row(a) with { BudgetAeHundredths = 500, Revision = 2 });

        var all = _store.ListItemUniques();
        Assert.Equal(2, all.Count);
        Assert.Equal("item.brainpan-sigil", all[0].ContainerId);
        Assert.Equal(500, all.Single(u => u.ContainerId == a).BudgetAeHundredths);
        Assert.Equal(2, all.Single(u => u.ContainerId == a).Revision);
    }

    /// <summary>
    /// A unique is a FLAG on a container, so it cannot exist without one — the FK says so rather than a
    /// comment. Same constraint <c>item_socket</c> carries against <c>effect_instance</c>.
    /// </summary>
    [Fact]
    public void A_unique_row_cannot_exist_without_its_container()
    {
        Assert.ThrowsAny<Exception>(() => _store.UpsertItemUnique(Row("item.never-authored")));
    }

    /// <summary>§3.8's enforcement as a query: <c>item_set_member</c> may not reference a unique.</summary>
    [Fact]
    public void Set_membership_is_answerable_from_the_store_not_from_a_promise()
    {
        var id = NewContainer();
        _store.UpsertItemUnique(Row(id));
        Assert.False(_store.IsUniqueSetMember(id));

        _store.ImportSetCorpus(new[]
        {
            new FusionRpg.Core.Items.Thresholds.SetDef(
                "kiln-legion", "Kiln Legion",
                new[] { new FusionRpg.Core.Items.Thresholds.SetMemberDef(id, ItemRole.ArmamentPrimary, ItemFrame.Plant) },
                new[] { new FusionRpg.Core.Items.Thresholds.SetTierDef(1, "set.kiln-legion-01", false) }),
        });

        Assert.True(_store.IsUniqueSetMember(id));

        // And the validator turns that fact into the rule.
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.vitality", "", 4),
            KindId = "stat.modify",
            FamilyId = "atom.vitality",
            Tier = 4,
            ParamsJson = """{"channel":"maxHp","op":"Flat","amount":{"min":92,"max":92,"roll":"fixed"}}""",
        };
        var fails = UniqueValidator.Validate(
            Row(id), new ContainerRow
            {
                ContainerId = id,
                Kind = ContainerKind.Item,
                Atoms = new[] { new ContainerAtomRow(1, atom.AtomId) },
            },
            new RarityRungWindow("heirloom", 3, 5, 3), 70, "armament-primary",
            _ => atom, Tuning(), isSetMember: _store.IsUniqueSetMember(id));

        Assert.Contains(fails, f => f.Detail.Contains(UniqueRules.SetMembership, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>unique_eligible</c>, the tenth <c>rarity_budget</c> key: seeded from the ordinal, 0 below the
    /// floor and 1 at or above it, and written through <c>SetRarityBudget</c> so SC7's closed-registry
    /// gate applies to it like every other key.
    /// </summary>
    [Fact]
    public void Unique_eligible_seeds_every_rung_through_the_sc7_gate()
    {
        var ladder = new (string Id, int Ordinal)[]
        {
            ("chaff", 10), ("sprout", 20), ("grafted", 30), ("cultivated", 40), ("fused", 50),
            ("chimeric", 60), ("heirloom", 70), ("firstseed", 80), ("sunwoven", 90), ("almanac", 100),
        };

        foreach (var (id, ordinal) in ladder)
        {
            var (ok, reason) = _store.UpsertRarity(new RarityRow(id, ordinal, 1, 1, 1, 3));
            Assert.True(ok, reason);
        }

        _store.SeedUniqueEligible(Tuning());

        // CONTRACT, not a hardcoded ordinal: the eligibility floor is a tunable
        // (`uniques.v1.json.rungFloorOrdinal`) and D4.23 moved it 30 → 80. Asserting against the
        // TUNING's own floor rather than a literal keeps this true when a balance pass moves the rung
        // — the old `ordinal >= 30` was stale the moment the floor moved.
        var floor = Tuning().RungFloorOrdinal;
        foreach (var (id, ordinal) in ladder)
            Assert.Equal(ordinal >= floor ? 1 : 0,
                _store.GetRarityBudget(id, UniqueLimits.EligibilityBudgetKey));
    }

    /// <summary>
    /// The SC7 mechanism itself, still refusing: a key with no registered consumer cannot be seeded,
    /// however many keys happen to be decided today.
    /// </summary>
    [Fact]
    public void A_key_with_no_registered_consumer_is_still_refused_by_the_store()
    {
        Assert.Throws<RarityBudgetKeyRejection>(() =>
            _store.SetRarityBudget("grafted", "unique_vibes", 1));
    }
}
