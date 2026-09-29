using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E5's storage half. The validator has its own tests in Core; this covers what only exists once a
/// database is involved — round trips, <c>seq</c> ordering, whole-container replacement, and the
/// append-only rarity ordinals.
/// </summary>
public class ContainerStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ContainerStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        SeedAtoms();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    void SeedAtoms()
    {
        foreach (var (family, variant, tier) in new[]
        {
            ("atom.vitality", "", 1), ("atom.vitality", "", 2),
            ("atom.might", "", 1),
            ("atom.elemental-power", "fire", 1), ("atom.elemental-power", "ice", 1),
        })
        {
            var r = _store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId(family, variant, tier),
                KindId = "stat.modify",
                FamilyId = family,
                Variant = variant,
                Tier = tier,
                Name = family,
                ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
            });
            Assert.True(r.IsOk, r.ToString());
        }
    }

    static ContainerRow Trait(params ContainerAtomRow[] atoms) => new()
    {
        ContainerId = "trait.stalwart",
        Kind = ContainerKind.Trait,
        Atoms = atoms,
    };

    /// <summary>T3.1 (affix-schema): a pool row now references an affix, so a pool-testing fixture
    /// must seed one — a single-ref affix under the SAME id, matching what `affix-library` (module
    /// 3, not yet built) will generate 1:1 for real.</summary>
    void SeedSingleRefAffix(string atomId)
    {
        var r = _store.UpsertAffix(new AffixRow(atomId, AffixClass.Prefix, new[] { new AffixRefRow(1, atomId) }), _store.GetAtom);
        Assert.True(r.IsOk, r.ToString());
    }

    // ---- round trip ------------------------------------------------------------------------------

    [Fact]
    public void A_fixed_list_container_round_trips_in_seq_order()
    {
        // seq is AUTHORING order and must be stable. It is not an execution guarantee — execution
        // order belongs to the actor's effect list (definitions §0).
        var c = Trait(
            new ContainerAtomRow(3, "atom.might.t1"),
            new ContainerAtomRow(1, "atom.vitality.t1"),
            new ContainerAtomRow(2, "atom.vitality.t2"));

        Assert.True(_store.UpsertContainer(c).IsOk);

        var back = _store.GetContainer("trait.stalwart");

        Assert.NotNull(back);
        Assert.Equal(ContainerKind.Trait, back!.Kind);
        Assert.Equal(new[] { 1, 2, 3 }, back.Atoms.Select(a => a.Seq));
        Assert.Equal("atom.vitality.t1", back.Atoms[0].AtomId);
    }

    [Fact]
    public void Overrides_survive_the_round_trip()
    {
        const string ov = "{\"amount\":{\"min\":40,\"max\":60,\"roll\":\"onInstantiate\"}}";
        _store.UpsertContainer(Trait(new ContainerAtomRow(1, "atom.vitality.t1", ov)));

        Assert.Equal(ov, _store.GetContainer("trait.stalwart")!.Atoms[0].OverridesJson);
    }

    [Fact]
    public void A_unique_containers_own_frame_and_base_type_round_trip()
    {
        // D3.15/D4.12/D3.11: the real remaining blocker this session found and closed -- a unique's
        // authored Frame/BaseTypeId (carried the identical way Rarity already is) previously had no
        // column at all, so item_generation had no data source for any unique instance. Kind: Item,
        // no Rarity set, mirroring this file's own Trait() helper's minimalism -- this test cares only
        // about the two new columns, not the whole container shape.
        var c = new ContainerRow
        {
            ContainerId = "item.rot-bloom-30-002",
            Kind = ContainerKind.Item,
            Frame = "plant",
            BaseTypeId = "footing.woody-stem",
            Atoms = new[] { new ContainerAtomRow(0, "atom.vitality.t1") },
        };
        Assert.True(_store.UpsertContainer(c).IsOk);

        var back = _store.GetContainer("item.rot-bloom-30-002");

        Assert.NotNull(back);
        Assert.Equal("plant", back!.Frame);
        Assert.Equal("footing.woody-stem", back.BaseTypeId);
    }

    [Fact]
    public void A_non_unique_container_carries_no_frame_or_base_type_by_default()
    {
        // The negative case: an ordinary trait container (no Frame/BaseTypeId ever set) round-trips
        // both as null, never an empty string or a silently-invented default -- the same "most
        // containers don't have one" posture Rarity/Slot/MinTier already establish.
        _store.UpsertContainer(Trait(new ContainerAtomRow(0, "atom.vitality.t1")));
        var back = _store.GetContainer("trait.stalwart");

        Assert.Null(back!.Frame);
        Assert.Null(back.BaseTypeId);
    }

    [Fact]
    public void Editing_only_a_unique_containers_frame_is_a_real_content_change_not_a_no_op()
    {
        // SameContent's own field list had to grow with these two columns, or a re-import that ONLY
        // changed Frame/BaseTypeId would silently look identical and never bump revision or persist.
        var original = new ContainerRow
        {
            ContainerId = "item.rot-bloom-30-002", Kind = ContainerKind.Item,
            Frame = "plant", BaseTypeId = "footing.woody-stem",
        };
        Assert.True(_store.UpsertContainer(original).IsOk);
        var firstRevision = _store.GetContainer("item.rot-bloom-30-002")!.Revision;

        var reclassified = original with { Frame = "humanoid" };
        Assert.True(_store.UpsertContainer(reclassified).IsOk);

        var back = _store.GetContainer("item.rot-bloom-30-002");
        Assert.Equal("humanoid", back!.Frame);
        Assert.True(back.Revision > firstRevision, "changing Frame alone must bump revision, not no-op");
    }

    [Fact]
    public void A_pool_container_round_trips_with_weights_and_groups()
    {
        SeedSingleRefAffix("atom.elemental-power.fire.t1");
        SeedSingleRefAffix("atom.elemental-power.ice.t1");
        SeedSingleRefAffix("atom.vitality.t1");

        var c = new ContainerRow
        {
            ContainerId = "item.ember-band",
            Kind = ContainerKind.Item,
            PrefixRolls = 2,
            Pool = new[]
            {
                new ContainerPoolRow("atom.elemental-power.fire.t1", 10),
                new ContainerPoolRow("atom.elemental-power.ice.t1", 0),
                new ContainerPoolRow("atom.vitality.t1", 5, "defensive"),
            },
        };

        Assert.True(_store.UpsertContainer(c).IsOk, _store.UpsertContainer(c).ToString());

        var back = _store.GetContainer("item.ember-band")!;

        Assert.Equal(3, back.Pool.Count);
        Assert.Equal(0, back.Pool.Single(p => p.AffixId == "atom.elemental-power.ice.t1").Weight);
        Assert.Equal("defensive", back.Pool.Single(p => p.AffixId == "atom.vitality.t1").Group);
    }

    [Fact]
    public void A_zero_weight_row_is_kept_in_the_table_rather_than_dropped()
    {
        SeedSingleRefAffix("atom.vitality.t1");
        SeedSingleRefAffix("atom.might.t1");

        // "Excludes without deleting" — the row stays so an author can re-enable it by editing one
        // number, and so the content hash records that the candidate exists.
        var c = new ContainerRow
        {
            ContainerId = "item.ember-band",
            Kind = ContainerKind.Item,
            PrefixRolls = 1,
            Pool = new[]
            {
                new ContainerPoolRow("atom.vitality.t1", 10),
                new ContainerPoolRow("atom.might.t1", 0),
            },
        };

        _store.UpsertContainer(c);

        Assert.Contains(_store.GetContainer("item.ember-band")!.Pool, p => p.Weight == 0);
    }

    // ---- whole-container replacement ---------------------------------------------------------------

    [Fact]
    public void Re_upserting_replaces_the_contents_rather_than_merging()
    {
        _store.UpsertContainer(Trait(
            new ContainerAtomRow(1, "atom.vitality.t1"),
            new ContainerAtomRow(2, "atom.might.t1")));

        _store.UpsertContainer(Trait(new ContainerAtomRow(1, "atom.vitality.t2")));

        var back = _store.GetContainer("trait.stalwart")!;

        // A stale child row from a previous revision is content nobody wrote.
        Assert.Single(back.Atoms);
        Assert.Equal("atom.vitality.t2", back.Atoms[0].AtomId);
    }

    [Fact]
    public void Revision_bumps_on_edit()
    {
        _store.UpsertContainer(Trait(new ContainerAtomRow(1, "atom.vitality.t1")));
        var first = _store.GetContainer("trait.stalwart")!.Revision;

        _store.UpsertContainer(Trait(new ContainerAtomRow(1, "atom.vitality.t2")));

        Assert.True(_store.GetContainer("trait.stalwart")!.Revision > first);
    }

    [Fact]
    public void A_rejected_container_never_reaches_the_table()
    {
        var r = _store.UpsertContainer(Trait(new ContainerAtomRow(1, "atom.nope.t1")));

        Assert.Equal(AtomRejectionReason.UnknownAtom, r.Reason);
        Assert.Null(_store.GetContainer("trait.stalwart"));
    }

    [Fact]
    public void Containers_list_in_stable_id_order_because_E8_hashes_them()
    {
        foreach (var id in new[] { "trait.zeal", "trait.alacrity", "trait.might" })
            _store.UpsertContainer(Trait(new ContainerAtomRow(1, "atom.vitality.t1")) with { ContainerId = id });

        Assert.Equal(new[] { "trait.alacrity", "trait.might", "trait.zeal" }, _store.ListContainerIds());
    }

    /// <summary>D3.9's own whole-corpus caller (`EventDeckPreflight.CheckNoNerveTargetInAnyContainer`)
    /// needs every container resolved, not just ids — no such query existed before this. Mirrors
    /// <see cref="RpgStore.ListActionContainers"/>'s own composition of `ListContainerIds` +
    /// `GetContainer`, so the round trip (fixed atoms, pool rows, kind, revision) is exactly what
    /// <see cref="RpgStore.GetContainer"/>'s own already-covered tests already prove.</summary>
    [Fact]
    public void ListContainers_returns_every_container_resolved_whole_in_stable_id_order()
    {
        SeedSingleRefAffix("atom.vitality.t1");
        _store.UpsertContainer(Trait(new ContainerAtomRow(1, "atom.might.t1")) with { ContainerId = "trait.zeal" });
        _store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.ember-band",
            Kind = ContainerKind.Item,
            PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("atom.vitality.t1", 10) },
        });

        var all = _store.ListContainers();

        Assert.Equal(new[] { "item.ember-band", "trait.zeal" }, all.Select(c => c.ContainerId));
        var trait = all.Single(c => c.ContainerId == "trait.zeal");
        Assert.Equal(ContainerKind.Trait, trait.Kind);
        Assert.Equal("atom.might.t1", Assert.Single(trait.Atoms).AtomId);
        var item = all.Single(c => c.ContainerId == "item.ember-band");
        Assert.Equal("atom.vitality.t1", Assert.Single(item.Pool).AffixId);
    }

    [Fact]
    public void ListContainers_on_an_empty_store_returns_empty_not_null()
    {
        Assert.Empty(_store.ListContainers());
    }

    // ---- rarity: append-only ordinals ---------------------------------------------------------------

    [Fact]
    public void Rarity_bands_round_trip_in_ordinal_order()
    {
        _store.UpsertRarity(new RarityRow("epic", 3, PrefixRolls: 3, SuffixRolls: 2, MinTier: 2, MaxTier: 4));
        _store.UpsertRarity(new RarityRow("common", 1, PrefixRolls: 1, SuffixRolls: 0, MinTier: 1, MaxTier: 2));
        _store.UpsertRarity(new RarityRow("rare", 2, PrefixRolls: 2, SuffixRolls: 1, MinTier: 1, MaxTier: 3));

        Assert.Equal(new[] { "common", "rare", "epic" }, _store.ListRarities().Select(r => r.RarityId));
    }

    [Fact]
    public void An_ordinal_already_belonging_to_another_band_is_refused()
    {
        // Append-only: a band may be added, never renumbered underneath the content naming it.
        _store.UpsertRarity(new RarityRow("common", 1, 1, 0, 1, 2));

        var (ok, reason) = _store.UpsertRarity(new RarityRow("uncommon", 1, 2, 0, 1, 3));

        Assert.False(ok);
        Assert.Contains("append-only", reason);
    }

    [Fact]
    public void A_band_may_still_be_retuned_in_place()
    {
        _store.UpsertRarity(new RarityRow("common", 1, 1, 0, 1, 2));

        Assert.True(_store.UpsertRarity(new RarityRow("common", 1, PrefixRolls: 2, SuffixRolls: 1, MinTier: 1, MaxTier: 3)).Ok);
        Assert.Equal(2, _store.ListRarities().Single().PrefixRolls);
    }

    [Fact]
    public void An_inverted_tier_window_on_a_band_is_refused()
    {
        var (ok, _) = _store.UpsertRarity(new RarityRow("broken", 9, 1, SuffixRolls: 0, MinTier: 5, MaxTier: 2));

        Assert.False(ok);
        Assert.Empty(_store.ListRarities());
    }
}
