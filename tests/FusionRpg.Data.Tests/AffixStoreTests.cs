using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// T3.1 (affix-schema)'s storage half — round trips, whole-bundle replacement, and the same
/// no-op-on-identical-rewrite / rejected-writes-nothing discipline <c>ContainerStoreTests</c> already
/// proves for containers.
/// </summary>
public class AffixStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AffixStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        SeedAtoms();
    }

    public void Dispose() => _testStore.Dispose();

    void SeedAtoms()
    {
        var rows = new List<(string Family, string Variant, int Tier)>
        {
            ("atom.vitality", "", 1), ("atom.vitality", "", 2), ("atom.might", "", 1),
        };
        // The real "element" domain (RpgStore.Containers.cs's DomainMembers) is all six concrete
        // elements — a slot-ref test needs every one seeded or UpsertAffix genuinely (and correctly)
        // rejects the affix as an unresolvable domain member.
        foreach (var element in new[] { "fire", "ice", "air", "earth", "light", "dark" })
            rows.Add(("atom.elemental-power", element, 1));

        foreach (var (family, variant, tier) in rows)
        {
            var r = _store.UpsertAtom(new AtomRow
            {
                AtomId = AtomRow.DeriveId(family, variant, tier),
                KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
                Name = family, ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
            });
            Assert.True(r.IsOk, r.ToString());
        }
    }

    [Fact]
    public void A_single_ref_affix_round_trips()
    {
        var affix = new AffixRow("affix.vitality", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") });

        Assert.True(_store.UpsertAffix(affix, _store.GetAtom).IsOk);

        var back = _store.GetAffix("affix.vitality");
        Assert.NotNull(back);
        Assert.Equal(AffixClass.Prefix, back!.Class);
        Assert.Single(back.Refs);
        Assert.Equal("atom.vitality.t1", back.Refs[0].AtomId);
    }

    [Fact]
    public void A_multi_ref_bundle_round_trips_in_seq_order()
    {
        var affix = new AffixRow("affix.dual", AffixClass.Prefix, new[]
        {
            new AffixRefRow(2, "atom.might.t1"),
            new AffixRefRow(1, "atom.vitality.t1"),
        });

        Assert.True(_store.UpsertAffix(affix, _store.GetAtom).IsOk);

        var back = _store.GetAffix("affix.dual")!;
        Assert.Equal(new[] { 1, 2 }, back.Refs.Select(r => r.Seq));
        Assert.Equal("atom.vitality.t1", back.Refs[0].AtomId);
    }

    [Fact]
    public void A_slot_ref_round_trips_its_pattern_and_pick_count()
    {
        var affix = new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.elemental-power.$E1"),
        });

        Assert.True(_store.UpsertAffix(affix, _store.GetAtom).IsOk);

        var back = _store.GetAffix("affix.elemental")!;
        var r = back.Refs[0];
        Assert.True(r.IsSlot);
        Assert.Null(r.AtomId);
        Assert.Equal("E1", r.SlotName);
        Assert.Equal("element", r.SlotDomain);
        Assert.Equal(1, r.SlotPick);
        Assert.Equal("atom.elemental-power.$E1", r.SlotAtomPattern);
    }

    [Fact]
    public void Re_upserting_replaces_the_refs_rather_than_merging()
    {
        _store.UpsertAffix(
            new AffixRow("affix.vitality", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") }),
            _store.GetAtom);
        _store.UpsertAffix(
            new AffixRow("affix.vitality", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t2") }),
            _store.GetAtom);

        var back = _store.GetAffix("affix.vitality")!;
        Assert.Single(back.Refs);
        Assert.Equal("atom.vitality.t2", back.Refs[0].AtomId);
    }

    [Fact]
    public void An_identical_rewrite_is_a_no_op_revision_included()
    {
        var affix = new AffixRow("affix.vitality", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") });
        _store.UpsertAffix(affix, _store.GetAtom);
        var beforeHash = _store.ComputeContentHash().Hash;

        _store.UpsertAffix(affix, _store.GetAtom);

        Assert.Equal(beforeHash, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void A_real_edit_moves_the_content_hash()
    {
        _store.UpsertAffix(
            new AffixRow("affix.vitality", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") }),
            _store.GetAtom);
        var before = _store.ComputeContentHash().Hash;

        _store.UpsertAffix(
            new AffixRow("affix.vitality", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t2") }),
            _store.GetAtom);

        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void A_rejected_affix_never_reaches_the_table()
    {
        var affix = new AffixRow("affix.nope", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.nope.t1") });

        var r = _store.UpsertAffix(affix, _store.GetAtom);

        Assert.Equal(AtomRejectionReason.UnknownAtom, r.Reason);
        Assert.Null(_store.GetAffix("affix.nope"));
    }

    [Fact]
    public void An_unknown_affix_id_returns_null_not_a_throw()
    {
        Assert.Null(_store.GetAffix("affix.does-not-exist"));
    }

    [Fact]
    public void Affix_ids_list_in_stable_order()
    {
        foreach (var id in new[] { "affix.zeal", "affix.alacrity", "affix.might" })
            _store.UpsertAffix(
                new AffixRow(id, AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") }),
                _store.GetAtom);

        Assert.Equal(new[] { "affix.alacrity", "affix.might", "affix.zeal" }, _store.ListAffixIds());
    }
}
