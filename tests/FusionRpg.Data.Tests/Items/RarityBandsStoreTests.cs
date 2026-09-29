using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `rarity-bands` (item module 7) — the two shipped-store defects spec-rarity-bands.md names, and the
/// `rarity_budget` KV registry's SC7 enforcement and seed wiring.
/// </summary>
public class RarityBandsStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public RarityBandsStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static RarityRow Chaff => new("chaff", 10, 0, 0, 1, 1);
    static RarityRow Sprout => new("sprout", 20, 0, 1, 1, 1);

    // ---- defect 1: an existing rung's ordinal must never move on upsert -------------------------

    [Fact]
    public void An_existing_rungs_ordinal_cannot_be_changed_on_upsert()
    {
        Assert.True(_store.UpsertRarity(Chaff).Ok);

        var (ok, reason) = _store.UpsertRarity(Chaff with { Ordinal = 15 });

        Assert.False(ok);
        Assert.Contains("ladder-mutated", reason);
        Assert.Equal(10, _store.ListRarities().Single(r => r.RarityId == "chaff").Ordinal);
    }

    [Fact]
    public void A_repeat_upsert_with_the_same_ordinal_still_succeeds()
    {
        Assert.True(_store.UpsertRarity(Chaff).Ok);
        Assert.True(_store.UpsertRarity(Chaff).Ok);
    }

    [Fact]
    public void A_brand_new_rung_is_not_blocked_by_the_self_check()
    {
        Assert.True(_store.UpsertRarity(Chaff).Ok);
        Assert.True(_store.UpsertRarity(Sprout).Ok);
        Assert.Equal(2, _store.ListRarities().Count);
    }

    // ---- defect 2: a container naming an unknown rarity must be rejected -------------------------

    static AtomRow VitalityAtom => new()
    {
        AtomId = AtomRow.DeriveId("atom.vitality", "", 1), KindId = "stat.modify", FamilyId = "atom.vitality", Variant = "", Tier = 1,
        Name = "Vitality", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
    };

    static ContainerRow ItemNaming(string? rarity) => new()
    {
        ContainerId = "item.ember-band",
        Kind = ContainerKind.Item,
        Rarity = rarity,
        Atoms = new List<ContainerAtomRow> { new(1, VitalityAtom.AtomId) },
    };

    [Fact]
    public void A_container_naming_an_unknown_rarity_is_rejected()
    {
        Assert.True(_store.UpsertAtom(VitalityAtom).IsOk);

        var check = _store.UpsertContainer(ItemNaming("does-not-exist"));

        Assert.False(check.IsOk);
        Assert.Contains("rarity", check.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_container_naming_a_seeded_rarity_is_accepted()
    {
        Assert.True(_store.UpsertAtom(VitalityAtom).IsOk);
        Assert.True(_store.UpsertRarity(Chaff).Ok);

        var check = _store.UpsertContainer(ItemNaming("chaff"));

        Assert.True(check.IsOk, check.ToString());
    }

    [Fact]
    public void A_container_naming_no_rarity_is_unaffected_by_the_new_check()
    {
        Assert.True(_store.UpsertAtom(VitalityAtom).IsOk);

        var check = _store.UpsertContainer(ItemNaming(null));

        Assert.True(check.IsOk, check.ToString());
    }

    // ---- rarity_budget: SC7 enforced by the store, not just the C# call site ----------------------

    [Fact]
    public void Setting_a_registered_budget_key_round_trips()
    {
        _store.SetRarityBudget("chaff", "promote_from", 1);
        Assert.Equal(1, _store.GetRarityBudget("chaff", "promote_from"));
    }

    [Fact]
    public void Setting_an_unregistered_budget_key_is_refused()
    {
        Assert.Throws<RarityBudgetKeyRejection>(() => _store.SetRarityBudget("chaff", "set_eligible", 1));
        Assert.Null(_store.GetRarityBudget("chaff", "set_eligible"));
    }

    [Fact]
    public void An_unset_budget_key_reads_as_null()
    {
        Assert.Null(_store.GetRarityBudget("chaff", "power_ceiling"));
    }

    [Fact]
    public void Re_setting_a_budget_key_overwrites_rather_than_duplicates()
    {
        _store.SetRarityBudget("almanac", "enhance_cap", 200);
        _store.SetRarityBudget("almanac", "enhance_cap", 999);
        Assert.Equal(999, _store.GetRarityBudget("almanac", "enhance_cap"));
    }

    // ---- SeedRarityLadder: seeds only rarity_budget, from the real shipped tuning file ------------

    static IReadOnlyDictionary<string, ItemRarityRungTuning> LoadRealTuning()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return ItemRarityTuning.Parse(File.ReadAllText(
                    Path.Combine(dir.FullName, "data", "tuning", "item-rarity.v1.json")));
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public void SeedRarityLadder_writes_all_five_ready_keys_for_every_rung()
    {
        _store.SeedRarityLadder(LoadRealTuning());

        foreach (var id in RarityLadder.RungIds)
        {
            Assert.NotNull(_store.GetRarityBudget(id, "promote_from"));
            Assert.NotNull(_store.GetRarityBudget(id, "pity_guarded"));
            Assert.NotNull(_store.GetRarityBudget(id, "drop_weight_default"));
            Assert.NotNull(_store.GetRarityBudget(id, "enhance_cap"));
            Assert.NotNull(_store.GetRarityBudget(id, "power_ceiling"));
        }
    }

    [Fact]
    public void SeedRarityLadder_does_not_write_rarity_table_rows()
    {
        // The rarity table itself comes only from the standard content.Rarities import path
        // (RpgStore.Import.cs), never from this method -- a second writer would be a driftable
        // second source for the same rows.
        _store.SeedRarityLadder(LoadRealTuning());

        Assert.Empty(_store.ListRarities());
    }

    [Fact]
    public void SeedRarityLadder_is_idempotent()
    {
        var tuning = LoadRealTuning();
        _store.SeedRarityLadder(tuning);
        _store.SeedRarityLadder(tuning);

        Assert.Equal(700, _store.GetRarityBudget("almanac", "drop_weight_default"));
    }

    [Fact]
    public void SeedRarityLadder_marks_only_heirloom_and_sunwoven_as_pity_guarded()
    {
        _store.SeedRarityLadder(LoadRealTuning());

        foreach (var id in RarityLadder.RungIds)
        {
            var expected = id is "heirloom" or "sunwoven" ? 1 : 0;
            Assert.Equal(expected, _store.GetRarityBudget(id, "pity_guarded"));
        }
    }
}
