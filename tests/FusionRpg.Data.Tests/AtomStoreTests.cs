using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E4 acceptance (spec-atom-schema.md). The recurring theme: <b>whole-row rejection with a typed
/// reason</b>. There is no disabled-on-error state, and one bad row never takes its file down with it.
/// </summary>
public class AtomStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AtomStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>A valid `stat.modify` atom — a permanent modifier, so it carries no trigger.</summary>
    static AtomRow Vitality(int tier = 1, string variant = "") => new()
    {
        AtomId = AtomRow.DeriveId("atom.vitality", variant, tier),
        KindId = "stat.modify",
        FamilyId = "atom.vitality",
        Variant = variant,
        Tier = tier,
        Name = $"Vitality t{tier}",
        WhenJson = "{}",
        ParamsJson = """{"channel":"maxHp","op":"flat","amount":45}""",
        TagsJson = """{"category":"survivability"}""",
    };

    /// <summary>A valid event-triggered atom.</summary>
    static AtomRow SearingStrike(string variant = "fire") => new()
    {
        AtomId = AtomRow.DeriveId("atom.searing-strike", variant, 3),
        KindId = "resource.delta",
        FamilyId = "atom.searing-strike",
        Variant = variant,
        Tier = 3,
        Name = "Searing Strike",
        WhenJson = """{"trigger":"OnDamageDealt","chance":250,"icd_ms":500}""",
        ParamsJson = """{"amount":-120,"element":"fire"}""",
    };

    // ---- round trip ---------------------------------------------------------------------------

    [Fact]
    public void An_atom_round_trips_byte_identically()
    {
        var row = SearingStrike();
        Assert.True(_store.UpsertAtom(row).IsOk);

        var back = _store.GetAtom(row.AtomId);

        Assert.NotNull(back);
        Assert.Equal(row.KindId, back!.KindId);
        Assert.Equal(row.FamilyId, back.FamilyId);
        Assert.Equal(row.Variant, back.Variant);
        Assert.Equal(row.Tier, back.Tier);
        Assert.Equal(row.WhenJson, back.WhenJson);
        Assert.Equal(row.ParamsJson, back.ParamsJson);
        Assert.Null(back.PowerJson); // E9 lands eleven positions later and backfills
    }

    [Fact]
    public void Revision_bumps_on_edit()
    {
        var row = Vitality();
        _store.UpsertAtom(row);
        var first = _store.GetAtom(row.AtomId)!.Revision;

        _store.UpsertAtom(row with { Name = "Vitality, renamed" });
        var second = _store.GetAtom(row.AtomId)!.Revision;

        Assert.True(second > first, $"{second} should exceed {first}");
    }

    // ---- the unique key that makes generated families possible ---------------------------------

    [Fact]
    public void Variant_is_part_of_the_key_so_one_family_holds_all_seven_element_slots()
    {
        // The old (family_id, tier) key rejected 30 of elemental_power's 35 rows.
        foreach (var element in new[] { "fire", "ice", "air", "earth", "light", "dark", "omni" })
            Assert.True(_store.UpsertAtom(SearingStrike(element)).IsOk, element);

        Assert.Equal(7, _store.ListAtoms().Count);
    }

    [Fact]
    public void Empty_variant_is_stored_as_empty_string_not_null()
    {
        // NULL does not compare equal to itself in a SQLite unique index, so two "no variant" rows
        // would both slip through. The column is '' and never NULL for exactly that reason.
        _store.UpsertAtom(Vitality(tier: 1));
        var back = _store.GetAtom("atom.vitality.t1");

        Assert.NotNull(back);
        Assert.Equal("", back!.Variant);
    }

    // ---- rejection, each with its own reason ----------------------------------------------------

    [Fact]
    public void An_unknown_kind_is_rejected()
    {
        var r = _store.UpsertAtom(Vitality() with { KindId = "stat.teleport" });
        Assert.Equal(AtomRejectionReason.UnknownKind, r.Reason);
    }

    [Fact]
    public void An_atom_id_that_disagrees_with_its_columns_is_rejected()
    {
        // atom_id is derived; storing it is a denormalisation, so it must agree or there are two
        // sources of truth for the same fact.
        var r = _store.UpsertAtom(Vitality() with { AtomId = "atom.vitality.t9" });
        Assert.Equal(AtomRejectionReason.IdMismatch, r.Reason);
    }

    [Theory]
    [InlineData("vitality")]          // no atom. prefix
    [InlineData("atom.Vitality")]     // not lower case
    [InlineData("atom.vita_lity")]    // underscore, not kebab
    public void A_family_id_breaking_the_grammar_is_rejected(string familyId)
    {
        var r = _store.UpsertAtom(Vitality() with
        {
            FamilyId = familyId,
            AtomId = AtomRow.DeriveId(familyId, "", 1),
        });
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void Tier_zero_is_rejected_there_is_no_parking_spot()
    {
        var r = _store.UpsertAtom(Vitality() with { Tier = 0, AtomId = "atom.vitality.t0" });
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void A_missing_required_param_is_rejected()
    {
        var r = _store.UpsertAtom(Vitality() with { ParamsJson = """{"op":"flat","amount":45}""" });
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    [Fact]
    public void Malformed_json_is_rejected_rather_than_swallowed()
    {
        var r = _store.UpsertAtom(Vitality() with { ParamsJson = "{not json" });
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    [Fact]
    public void A_power_override_without_a_note_is_rejected()
    {
        var r = _store.UpsertAtom(Vitality() with { PowerOverrideJson = """{"survivability":90}""" });
        Assert.Equal(AtomRejectionReason.MissingPowerNote, r.Reason);
    }

    [Fact]
    public void Chance_outside_per_mille_is_rejected()
    {
        var r = _store.UpsertAtom(SearingStrike() with
        {
            WhenJson = """{"trigger":"OnDamageDealt","chance":2500}""",
        });
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
    }

    // ---- triggers: absent for permanent modifiers, required for event kinds --------------------

    [Fact]
    public void A_permanent_modifier_carries_no_trigger_and_that_is_the_normal_case()
    {
        Assert.True(_store.UpsertAtom(Vitality()).IsOk);
        Assert.Empty(_store.ListAtomsByTrigger("OnGranted"));
    }

    [Fact]
    public void A_trigger_on_a_permanent_modifier_is_rejected()
    {
        // OnGranted/OnRemoved are runtime lifecycle states, not authorable triggers (§14.2).
        var r = _store.UpsertAtom(Vitality() with { WhenJson = """{"trigger":"OnGranted"}""" });
        Assert.Equal(AtomRejectionReason.TriggerNotAllowed, r.Reason);
    }

    [Fact]
    public void An_event_kind_without_a_trigger_is_rejected()
    {
        var r = _store.UpsertAtom(SearingStrike() with { WhenJson = "{}" });
        Assert.Equal(AtomRejectionReason.MissingParam, r.Reason);
    }

    [Fact]
    public void An_unknown_trigger_is_rejected()
    {
        // ⚠️ The fixture used "OnWave" until 2026-09-04, when the atom stream ADDED that trigger to
        // the vocabulary — so this test started reporting `TriggerNotAllowed` (known, but illegal for
        // this kind) and looked like a regression when it was a stale fixture. `ValidateTrigger`
        // checks unknown-first, then allowed, and `DropTableValidator.cs:262` is explicit that the two
        // codes are "different author mistakes" worth telling apart — so the intent is preserved and
        // only the value changed, to one that cannot join the vocabulary by accident.
        var r = _store.UpsertAtom(SearingStrike() with { WhenJson = """{"trigger":"OnNotARealTrigger"}""" });
        Assert.Equal(AtomRejectionReason.UnknownTrigger, r.Reason);
    }

    [Fact]
    public void The_trigger_is_extracted_into_its_own_index()
    {
        _store.UpsertAtom(SearingStrike());
        _store.UpsertAtom(Vitality());

        var byTrigger = _store.ListAtomsByTrigger("OnDamageDealt");

        Assert.Single(byTrigger);
        Assert.Equal("atom.searing-strike.fire.t3", byTrigger[0].AtomId);
    }

    // ---- the acceptance row the spec names ------------------------------------------------------

    [Fact]
    public void One_bad_row_in_fifty_loads_forty_nine()
    {
        var rows = new List<AtomRow>();
        for (var tier = 1; tier <= 50; tier++) rows.Add(Vitality(tier));
        rows[17] = rows[17] with { KindId = "stat.nonsense" };

        var result = _store.UpsertAtoms(rows);

        Assert.Single(result.Rejected);
        Assert.Equal(AtomRejectionReason.UnknownKind, result.Rejected[0].Reason);
        // Self-referential (population-pin SE3.4, 2026-09-20): this test built rows.Count rows and
        // corrupted exactly one, so it proves the rest loaded, not a fact about shipped content.
        Assert.Equal(rows.Count - result.Rejected.Count, result.Rows.Count);
        Assert.Equal(rows.Count - result.Rejected.Count, _store.ListAtoms().Count);
    }

    // ---- ordering and revision ------------------------------------------------------------------

    [Fact]
    public void Atoms_come_back_in_stable_id_order_because_E8_hashes_them()
    {
        foreach (var v in new[] { "omni", "fire", "dark", "ice" })
            _store.UpsertAtom(SearingStrike(v));

        var ids = _store.ListAtoms().Select(a => a.AtomId).ToList();

        Assert.Equal(ids.OrderBy(i => i, StringComparer.Ordinal).ToList(), ids);
    }

    [Fact]
    public void Catalog_revision_starts_at_zero_and_bumps_once_per_call()
    {
        Assert.Equal(0, _store.GetCatalogRevision());
        Assert.Equal(1, _store.BumpCatalogRevision());
        Assert.Equal(1, _store.GetCatalogRevision());
    }

    [Fact]
    public void Icd_key_defaults_to_the_atom_id_and_survives_a_round_trip()
    {
        var grouped = SearingStrike() with { IcdKey = "shield-grant" };
        _store.UpsertAtom(grouped);
        _store.UpsertAtom(Vitality());

        Assert.Equal("shield-grant", _store.GetAtom(grouped.AtomId)!.EffectiveIcdKey());
        Assert.Equal("atom.vitality.t1", _store.GetAtom("atom.vitality.t1")!.EffectiveIcdKey());
    }
}
