using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E4's task says its validator wires <b>E1, E2 and E3</b> checks, and its acceptance names D9.
/// The first cut wired only E1 — a predicate tree and a value spec could say anything at all and the
/// row still loaded. These pin the two missing seams.
///
/// <para>Everything here is a load-time refusal: an atom whose condition is nine levels deep or whose
/// range runs backwards is not a runtime surprise to survive, it is a row that must never land.</para>
/// </summary>
public class AtomRowWiringTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AtomRowWiringTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    // Plain strings with escaped quotes: the JSON here is short, and raw-string delimiters around
    // nested quotes cost more clarity than they buy.
    const string TriggerOnly = "{\"trigger\":\"OnDamageDealt\"}";

    static string When(string predicateJson) =>
        "{\"trigger\":\"OnDamageDealt\",\"predicate\":" + predicateJson + "}";

    static string Leaf(string leaf, string subject, string value) =>
        "{\"leaf\":\"" + leaf + "\",\"subject\":\"" + subject + "\",\"value\":" + value + "}";

    static AtomRow Strike(string whenJson, string paramsJson = "{\"amount\":-120}") => new()
    {
        AtomId = "atom.searing-strike.fire.t3",
        KindId = "resource.delta",
        FamilyId = "atom.searing-strike",
        Variant = "fire",
        Tier = 3,
        Name = "Searing Strike",
        WhenJson = whenJson,
        ParamsJson = paramsJson,
    };

    // ---- E3: the predicate tree in when_json ---------------------------------------------------

    [Fact]
    public void A_predicate_deeper_than_four_is_rejected_at_load()
    {
        // and(and(and(and(leaf)))) — depth 5. The depth limit bounds hot-path cost, so a row that
        // evades it at load evades it for good.
        var leaf = Leaf("sideIs", "target", "\"zombie\"");
        var tree = leaf;
        for (var i = 0; i < 4; i++) tree = "{\"op\":\"and\",\"children\":[" + tree + "]}";

        var r = _store.UpsertAtom(Strike(When(tree)));

        Assert.Equal(AtomRejectionReason.DepthExceeded, r.Reason);
    }

    [Fact]
    public void An_unknown_predicate_leaf_is_rejected_at_load()
    {
        var r = _store.UpsertAtom(Strike(When(Leaf("moonPhaseIs", "self", "3"))));

        Assert.Equal(AtomRejectionReason.UnknownLeaf, r.Reason);
    }

    [Fact]
    public void A_leaf_without_a_subject_is_rejected_at_load()
    {
        // OnDamageDealt inverts side and typeId, so an omitted subject silently means the wrong
        // entity. There is no default.
        var r = _store.UpsertAtom(Strike(When("{\"leaf\":\"sideIs\",\"value\":\"zombie\"}")));

        Assert.Equal(AtomRejectionReason.AmbiguousSubject, r.Reason);
    }

    [Fact]
    public void An_and_with_no_children_is_rejected_at_load()
    {
        var r = _store.UpsertAtom(Strike(When("{\"op\":\"and\",\"children\":[]}")));

        Assert.Equal(AtomRejectionReason.EmptyNode, r.Reason);
    }

    [Fact]
    public void An_unknown_predicate_op_is_rejected_rather_than_ignored()
    {
        var tree = "{\"op\":\"xor\",\"children\":[" + Leaf("sideIs", "self", "\"plant\"") + "]}";

        var r = _store.UpsertAtom(Strike(When(tree)));

        Assert.Equal(AtomRejectionReason.UnknownLeaf, r.Reason);
    }

    [Fact]
    public void A_valid_predicate_loads()
    {
        var tree = "{\"op\":\"and\",\"children\":["
                   + Leaf("sideIs", "target", "\"zombie\"") + ","
                   + Leaf("hpBelowMilli", "target", "500") + "]}";

        Assert.True(_store.UpsertAtom(Strike(When(tree))).IsOk);
    }

    [Fact]
    public void An_absent_predicate_is_legal_and_means_always()
    {
        Assert.True(_store.UpsertAtom(Strike(TriggerOnly)).IsOk);
    }

    // ---- E2: value specs in params_json ---------------------------------------------------------

    [Fact]
    public void A_value_spec_with_min_above_max_is_rejected_at_load()
    {
        var r = _store.UpsertAtom(Strike(TriggerOnly,
            "{\"amount\":{\"min\":200,\"max\":100,\"roll\":\"onApply\"}}"));

        Assert.Equal(AtomRejectionReason.BadValueSpec, r.Reason);
    }

    [Fact]
    public void A_fixed_value_spec_carrying_a_range_is_rejected_at_load()
    {
        // "fixed" means one number; a spread would silently resolve to Min forever.
        var r = _store.UpsertAtom(Strike(TriggerOnly,
            "{\"amount\":{\"min\":100,\"max\":200,\"roll\":\"fixed\"}}"));

        Assert.Equal(AtomRejectionReason.BadValueSpec, r.Reason);
    }

    [Fact]
    public void An_unknown_roll_policy_is_rejected()
    {
        var r = _store.UpsertAtom(Strike(TriggerOnly,
            "{\"amount\":{\"min\":1,\"max\":2,\"roll\":\"onEquip\"}}"));

        Assert.Equal(AtomRejectionReason.BadValueSpec, r.Reason);
    }

    [Fact]
    public void A_bare_number_is_still_a_valid_value_spec()
    {
        Assert.True(_store.UpsertAtom(Strike(TriggerOnly, "{\"amount\":-120}")).IsOk);
    }

    [Fact]
    public void An_unknown_curve_is_rejected_at_load()
    {
        // The spec puts this refusal at E4 load: E7's bake only interns an already-valid id to an
        // int, so a curve that does not exist must be caught before it gets that far.
        var r = _store.UpsertAtom(Strike(TriggerOnly,
            "{\"amount\":{\"min\":1,\"max\":2,\"roll\":\"onInstantiate\",\"curve\":\"curve.nope\"}}"));

        Assert.Equal(AtomRejectionReason.BadCurve, r.Reason);
    }

    [Fact]
    public void A_well_formed_range_loads()
    {
        Assert.True(_store.UpsertAtom(Strike(TriggerOnly,
            "{\"amount\":{\"min\":100,\"max\":200,\"roll\":\"onApply\"}}")).IsOk);
    }

    // ---- D9: the curve leak the compile/run split cannot tolerate --------------------------------

    void SeedLevelCurve() => _store.UpsertCurve("curve.atk.level", CurveInput.Level,
        new[] { new CurvePoint(1, 1000), new CurvePoint(10, 2000) });

    [Fact]
    public void A_level_curve_on_an_OnApply_value_is_rejected()
    {
        SeedLevelCurve();
        // D9: rolling this locally would need the curve's points and the actor's level on the
        // injector — a content lookup at trigger time, which E19 forbids outright.
        var r = _store.UpsertAtom(Strike(TriggerOnly,
            "{\"amount\":{\"min\":100,\"max\":200,\"roll\":\"onApply\",\"curve\":\"curve.atk.level\"}}"));

        Assert.Equal(AtomRejectionReason.BadValueSpec, r.Reason);
        Assert.Contains("level", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_level_curve_is_fine_on_an_OnInstantiate_value()
    {
        // It resolves server-side at drop, where the curve table is present.
        SeedLevelCurve();
        Assert.True(_store.UpsertAtom(Strike(TriggerOnly,
            "{\"amount\":{\"min\":100,\"max\":200,\"roll\":\"onInstantiate\",\"curve\":\"curve.atk.level\"}}")).IsOk);
    }

    [Fact]
    public void A_batch_of_curve_scaled_atoms_imports()
    {
        // Probe: UpsertAtoms holds _gate and an open transaction while the validator calls back into
        // GetCurve, which opens a SECOND connection to the same file. Does that survive?
        SeedLevelCurve();

        var rows = Enumerable.Range(1, 20).Select(t => new AtomRow
        {
            AtomId = "atom.scaled.t" + t,
            KindId = "resource.delta",
            FamilyId = "atom.scaled",
            Variant = "",
            Tier = t,
            Name = "Scaled " + t,
            WhenJson = TriggerOnly,
            ParamsJson = "{\"amount\":{\"min\":1,\"max\":2,\"roll\":\"onInstantiate\",\"curve\":\"curve.atk.level\"}}",
        }).ToList();

        var result = _store.UpsertAtoms(rows);

        Assert.Empty(result.Rejected);
        Assert.Equal(20, result.Rows.Count);
    }
}
