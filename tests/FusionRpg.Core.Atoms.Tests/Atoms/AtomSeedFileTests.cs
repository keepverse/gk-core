using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// The authored seed format (E14a). This half touches no database: it turns files into rows and
/// refuses what cannot be turned into rows honestly.
///
/// <para>Two rules carry most of the weight. <b>A duplicate id is refused, never resolved</b> —
/// last-write-wins would make the imported catalog depend on the order the filesystem happened to
/// hand over files. And <b>JSON columns are stored canonically</b>, so re-indenting a seed file is
/// not a content change; the raw text would bump a hashed <c>revision</c> for an edit that changed
/// nothing.</para>
/// </summary>
public class AtomSeedFileTests
{
    static SeedCollectResult Collect(params (string, string)[] files) => AtomSeedFile.Collect(files);

    static string AtomsFile(string entries) =>
        $$"""{ "schemaVersion": 1, "kind": "atom", "entries": [ {{entries}} ] }""";

    const string Vitality = """
        { "kind": "stat.modify", "family": "atom.vitality", "tier": 1, "name": "Vitality",
          "params": { "channel": "maxHp", "op": "flat", "amount": 45 } }
        """;

    // ---- the envelope ---------------------------------------------------------------------------

    [Fact]
    public void A_file_reads_its_entries()
    {
        var r = Collect(("a.json", AtomsFile(Vitality)));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        var atom = Assert.Single(r.Content.Atoms);
        Assert.Equal("atom.vitality.t1", atom.AtomId);
        Assert.Equal("stat.modify", atom.KindId);
        Assert.Equal("a.json", r.Content.SourceOf["atom.vitality.t1"]);
    }

    [Fact]
    public void A_newer_schema_version_is_refused_rather_than_read_optimistically()
    {
        // A format that grew a field would otherwise import with that field silently dropped — a
        // partial row that looks like a complete one.
        var r = Collect(("a.json", """{ "schemaVersion": 2, "kind": "atom", "entries": [] }"""));

        Assert.False(r.IsOk);
        Assert.Contains("schemaVersion 2", r.Errors[0].Detail);
        Assert.Empty(r.Content.Atoms);
    }

    [Fact]
    public void A_missing_schema_version_is_refused_too()
    {
        // Absent reads as 0, not as "the current one": an unversioned file is a file written against
        // a format nobody recorded.
        var r = Collect(("a.json", """{ "kind": "atom", "entries": [] }"""));

        Assert.False(r.IsOk);
    }

    [Fact]
    public void An_unknown_entry_kind_is_refused()
    {
        var r = Collect(("a.json", """{ "schemaVersion": 1, "kind": "spell", "entries": [] }"""));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.UnknownKind, r.Errors[0].Reason);
    }

    [Fact]
    public void Malformed_json_is_a_refusal_naming_the_file_not_an_exception()
    {
        var r = Collect(("broken.json", "{ this is not json"));

        Assert.False(r.IsOk);
        Assert.Equal("broken.json", r.Errors[0].SourcePath);
    }

    [Fact]
    public void All_four_kinds_parse()
    {
        // Curves and rarity bands are hashed content tables with an upsert of their own. Leaving
        // either unauthorable would make a covered table only reachable by hand-editing the database.
        var r = Collect(
            ("a.json", AtomsFile(Vitality)),
            ("c.json", """
                { "schemaVersion": 1, "kind": "curve", "entries": [
                    { "id": "curve.atk.level", "input": "level",
                      "points": [ { "x": 1, "mult": 1000 }, { "x": 10, "mult": 2000 } ] } ] }
                """),
            ("r.json", """
                { "schemaVersion": 1, "kind": "rarity", "entries": [
                    { "id": "rare", "ordinal": 2, "poolRolls": 2, "minTier": 1, "maxTier": 3 } ] }
                """),
            ("k.json", """
                { "schemaVersion": 1, "kind": "container", "entries": [
                    { "id": "item.ring", "kind": "item", "slot": "ring",
                      "atoms": [ { "atom": "atom.vitality.t1" } ] } ] }
                """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        Assert.Single(r.Content.Atoms);
        Assert.Single(r.Content.Curves);
        Assert.Single(r.Content.Rarities);
        Assert.Single(r.Content.Containers);
        Assert.Equal(4, r.Content.Count);
    }

    // ---- duplicates -------------------------------------------------------------------------------

    [Fact]
    public void The_same_id_in_two_files_is_refused_and_both_files_are_named()
    {
        // The whole reason the source path is carried: an author told only "duplicate atom.vitality.t1"
        // has to grep the tree to find the other half.
        var r = Collect(("first.json", AtomsFile(Vitality)), ("second.json", AtomsFile(Vitality)));

        Assert.False(r.IsOk);
        var error = Assert.Single(r.Errors);
        Assert.Equal(AtomRejectionReason.DuplicateKey, error.Reason);
        Assert.Equal("second.json", error.SourcePath);
        Assert.Contains("first.json", error.Detail);
    }

    [Fact]
    public void A_duplicate_is_refused_rather_than_overwriting_the_first()
    {
        var r = Collect(("first.json", AtomsFile(Vitality)), ("second.json", AtomsFile(Vitality)));

        Assert.Single(r.Content.Atoms);
        Assert.Equal("first.json", r.Content.SourceOf["atom.vitality.t1"]);
    }

    [Fact]
    public void Ids_collide_across_kinds_as_well()
    {
        // Nothing stops an author naming a container after an atom, and a shared namespace is the
        // cheaper rule to hold than four namespaces that only overlap by accident.
        var r = Collect(
            ("a.json", AtomsFile(Vitality)),
            ("c.json", """
                { "schemaVersion": 1, "kind": "container", "entries": [
                    { "id": "atom.vitality.t1", "kind": "item" } ] }
                """));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.DuplicateKey, r.Errors[0].Reason);
    }

    [Fact]
    public void An_entry_with_no_id_at_all_is_refused()
    {
        var r = Collect(("r.json",
            """{ "schemaVersion": 1, "kind": "rarity", "entries": [ { "ordinal": 1 } ] }"""));

        Assert.False(r.IsOk);
        Assert.Empty(r.Content.Rarities);
    }

    // ---- ids --------------------------------------------------------------------------------------

    [Fact]
    public void An_absent_id_is_derived_from_the_columns()
    {
        var r = Collect(("a.json", AtomsFile("""
            { "kind": "stat.modify", "family": "atom.ember", "variant": "fire", "tier": 3,
              "params": { "channel": "maxHp", "op": "flat", "amount": 1 } }
            """)));

        Assert.Equal("atom.ember.fire.t3", r.Content.Atoms[0].AtomId);
    }

    [Fact]
    public void An_authored_id_that_disagrees_with_its_columns_is_kept_so_the_validator_can_refuse_it()
    {
        // Quietly rewriting it to the derived form would import content the file does not contain.
        // E4 owns the refusal (IdMismatch); the reader's job is not to hide the disagreement.
        var r = Collect(("a.json", AtomsFile("""
            { "id": "atom.wrong.t9", "kind": "stat.modify", "family": "atom.ember", "tier": 3,
              "params": { "channel": "maxHp", "op": "flat", "amount": 1 } }
            """)));

        var atom = Assert.Single(r.Content.Atoms);
        Assert.Equal("atom.wrong.t9", atom.AtomId);
        Assert.NotEqual(atom.AtomId, atom.DerivedId());
        Assert.Equal(AtomRejectionReason.IdMismatch, AtomRowValidator.Validate(atom).Reason);
    }

    // ---- canonical json ---------------------------------------------------------------------------

    [Fact]
    public void Key_order_and_whitespace_in_an_authored_object_do_not_reach_the_column()
    {
        var tidy = Collect(("a.json", AtomsFile("""
            { "kind": "stat.modify", "family": "atom.v", "tier": 1,
              "params": { "amount": 45, "channel": "maxHp", "op": "flat" } }
            """)));

        var untidy = Collect(("a.json", AtomsFile("""
            { "kind": "stat.modify", "family": "atom.v", "tier": 1,
              "params": {
                    "channel"  :  "maxHp",
                    "op": "flat",
                    "amount": 45
              } }
            """)));

        Assert.Equal(tidy.Content.Atoms[0].ParamsJson, untidy.Content.Atoms[0].ParamsJson);
        Assert.DoesNotContain(" ", tidy.Content.Atoms[0].ParamsJson);
    }

    [Fact]
    public void A_real_value_edit_does_reach_the_column()
    {
        // The other half of the canonical-form claim: it must still notice a change. Without this
        // the previous test agrees about nothing.
        var a = Collect(("a.json", AtomsFile("""
            { "kind": "stat.modify", "family": "atom.v", "tier": 1,
              "params": { "channel": "maxHp", "op": "flat", "amount": 45 } }
            """)));
        var b = Collect(("a.json", AtomsFile("""
            { "kind": "stat.modify", "family": "atom.v", "tier": 1,
              "params": { "channel": "maxHp", "op": "flat", "amount": 46 } }
            """)));

        Assert.NotEqual(a.Content.Atoms[0].ParamsJson, b.Content.Atoms[0].ParamsJson);
    }

    [Fact]
    public void An_absent_optional_object_is_null_not_an_empty_object()
    {
        // `power_json` is nullable and E9 backfills it eleven modules later. Writing "{}" would make
        // every unpriced atom look priced at zero.
        var atom = Collect(("a.json", AtomsFile(Vitality))).Content.Atoms[0];

        Assert.Null(atom.PowerJson);
        Assert.Null(atom.PowerOverrideJson);
        Assert.Equal("{}", atom.WhenJson);
    }

    // ---- containers -------------------------------------------------------------------------------

    [Fact]
    public void A_container_atom_list_numbers_itself_in_authored_order()
    {
        var r = Collect(("c.json", """
            { "schemaVersion": 1, "kind": "container", "entries": [
                { "id": "item.ring", "kind": "item", "atoms": [
                    { "atom": "a.t1" }, { "atom": "b.t1" }, { "atom": "c.t1" } ] } ] }
            """));

        Assert.Equal(new[] { 0, 1, 2 }, r.Content.Containers[0].Atoms.Select(a => a.Seq));
    }

    [Fact]
    public void An_explicit_seq_wins_over_the_authored_position()
    {
        var r = Collect(("c.json", """
            { "schemaVersion": 1, "kind": "container", "entries": [
                { "id": "item.ring", "kind": "item", "atoms": [
                    { "seq": 7, "atom": "a.t1" }, { "atom": "b.t1" } ] } ] }
            """));

        Assert.Equal(new[] { 7, 1 }, r.Content.Containers[0].Atoms.Select(a => a.Seq));
    }

    [Theory]
    [InlineData("item", ContainerKind.Item)]
    [InlineData("trait", ContainerKind.Trait)]
    [InlineData("skill", ContainerKind.Skill)]
    [InlineData("species-passive", ContainerKind.SpeciesPassive)]
    [InlineData("patron", ContainerKind.Patron)]
    [InlineData("world-buff", ContainerKind.WorldBuff)]
    public void Every_container_kind_parses_from_its_authored_spelling(string authored, ContainerKind expected)
    {
        var r = Collect(("c.json", $$"""
            { "schemaVersion": 1, "kind": "container", "entries": [
                { "id": "x.one", "kind": "{{authored}}" } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        Assert.Equal(expected, r.Content.Containers[0].Kind);
    }

    [Fact]
    public void An_unknown_container_kind_is_refused()
    {
        // "consumable" was this test's example until 2026-09-07 (container-kind-expansion, X7) made
        // it a real ContainerKind member -- swapped for a name that stays genuinely outside the enum,
        // since the point of this test is "unknown is refused," not this specific string.
        var r = Collect(("c.json", """
            { "schemaVersion": 1, "kind": "container", "entries": [
                { "id": "x.one", "kind": "bogus-kind" } ] }
            """));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.UnknownKind, r.Errors[0].Reason);
    }

    [Fact]
    public void An_unknown_curve_input_is_refused_rather_than_defaulted_to_level()
    {
        var r = Collect(("c.json", """
            { "schemaVersion": 1, "kind": "curve", "entries": [
                { "id": "curve.x", "input": "wisdom", "points": [ { "x": 1, "mult": 1000 } ] } ] }
            """));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.BadCurve, r.Errors[0].Reason);
        Assert.Empty(r.Content.Curves);
    }

    // ---- affix (module 9, `affix-authoring`'s own consumer — no reader existed before this) ------

    [Fact]
    public void A_concrete_ref_affix_parses()
    {
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.master-of-fire-and-ice", "class": "prefix", "refs": [
                    { "atom": "atom.fire-power.t1" }, { "atom": "atom.ice-power.t1" } ] } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        var affix = Assert.Single(r.Content.Affixes);
        Assert.Equal("affix.master-of-fire-and-ice", affix.AffixId);
        Assert.Equal(AffixClass.Prefix, affix.Class);
        Assert.Equal(2, affix.Refs.Count);
        Assert.Equal("atom.fire-power.t1", affix.Refs[0].AtomId);
        Assert.False(affix.Refs[0].IsSlot);
        Assert.Equal("x.json", r.Content.SourceOf["affix.master-of-fire-and-ice"]);
    }

    [Fact]
    public void A_slot_ref_affix_parses()
    {
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.elemental-adept", "class": "prefix", "refs": [
                    { "slotName": "E1", "slotDomain": "element", "slotPick": 1,
                      "slotAtomPattern": "atom.elemental-power.$E1" } ] } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        var refRow = Assert.Single(r.Content.Affixes[0].Refs);
        Assert.True(refRow.IsSlot);
        Assert.Null(refRow.AtomId);
        Assert.Equal("E1", refRow.SlotName);
        Assert.Equal("element", refRow.SlotDomain);
        Assert.Equal(1, refRow.SlotPick);
        Assert.Equal("atom.elemental-power.$E1", refRow.SlotAtomPattern);
    }

    [Fact]
    public void A_mixed_concrete_and_slot_bundle_parses_both_ref_shapes_in_authored_order()
    {
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.mixed", "class": "mixed", "refs": [
                    { "atom": "atom.fire-power.t1" },
                    { "slotName": "E1", "slotDomain": "element", "slotPick": 1,
                      "slotAtomPattern": "atom.elemental-power.$E1" } ] } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        var refs = r.Content.Affixes[0].Refs;
        Assert.Equal(new[] { 0, 1 }, refs.Select(x => x.Seq));
        Assert.False(refs[0].IsSlot);
        Assert.True(refs[1].IsSlot);
    }

    [Fact]
    public void An_explicit_seq_wins_over_the_authored_position_for_affix_refs()
    {
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.x", "class": "prefix", "refs": [
                    { "seq": 7, "atom": "a.t1" }, { "atom": "b.t1" } ] } ] }
            """));

        Assert.Equal(new[] { 7, 1 }, r.Content.Affixes[0].Refs.Select(x => x.Seq));
    }

    [Theory]
    [InlineData("prefix", AffixClass.Prefix)]
    [InlineData("suffix", AffixClass.Suffix)]
    [InlineData("mixed", AffixClass.Mixed)]
    [InlineData("PREFIX", AffixClass.Prefix)]
    public void Every_affix_class_parses_case_insensitively(string authored, AffixClass expected)
    {
        var r = Collect(("x.json", $$"""
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.x", "class": "{{authored}}", "refs": [ { "atom": "a.t1" } ] } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        Assert.Equal(expected, r.Content.Affixes[0].Class);
    }

    [Fact]
    public void An_unknown_affix_class_is_refused_rather_than_defaulted()
    {
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.x", "class": "legendary", "refs": [ { "atom": "a.t1" } ] } ] }
            """));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Errors[0].Reason);
        Assert.Empty(r.Content.Affixes);
    }

    [Fact]
    public void A_missing_affix_class_parses_as_null_not_defaulted_to_prefix()
    {
        // E32 (spec-affix-import-path.md §3.2, decided 2026-09-03): an absent class is now LEGAL at
        // parse time — "derive it" — the shape a real generator emits. It parses to `null`, never a
        // silent default of Prefix; AffixValidator.Validate/ResolveClass is what fills it in, and
        // only once an atom lookup exists to derive FROM.
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.x", "refs": [ { "atom": "a.t1" } ] } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        var affix = Assert.Single(r.Content.Affixes);
        Assert.Null(affix.Class);
    }

    [Fact]
    public void An_unparseable_affix_class_is_still_refused()
    {
        // Present-but-garbage is still a refusal — only ABSENCE became legal, not "any string goes."
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.x", "class": "nonsense", "refs": [ { "atom": "a.t1" } ] } ] }
            """));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Errors[0].Reason);
    }

    [Fact]
    public void An_affix_with_no_refs_array_parses_as_an_empty_bundle_not_an_error()
    {
        // Matches ReadContainer's own treatment of an absent "atoms"/"pool" array — the shape
        // question ("does an empty bundle make sense") is AffixValidator.Validate's job, not the
        // reader's; this method only parses what is there.
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "affix.x", "class": "prefix" } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        Assert.Empty(r.Content.Affixes[0].Refs);
    }

    [Fact]
    public void An_affix_id_collides_with_an_atom_id_as_a_duplicate_across_kinds()
    {
        var r = Collect(("x.json", """
            { "schemaVersion": 1, "kind": "affix", "entries": [
                { "id": "atom.vitality.t1", "class": "prefix", "refs": [ { "atom": "a.t1" } ] } ] }
            """), ("a.json", AtomsFile(Vitality)));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.DuplicateKey, r.Errors[0].Reason);
    }

    [Fact]
    public void All_five_kinds_parse_affix_included()
    {
        var r = Collect(
            ("a.json", AtomsFile(Vitality)),
            ("aff.json", """
                { "schemaVersion": 1, "kind": "affix", "entries": [
                    { "id": "affix.x", "class": "prefix", "refs": [ { "atom": "atom.vitality.t1" } ] } ] }
                """),
            ("c.json", """
                { "schemaVersion": 1, "kind": "curve", "entries": [
                    { "id": "curve.atk.level", "input": "level",
                      "points": [ { "x": 1, "mult": 1000 } ] } ] }
                """),
            ("r.json", """
                { "schemaVersion": 1, "kind": "rarity", "entries": [
                    { "id": "rare", "ordinal": 2, "poolRolls": 2, "minTier": 1, "maxTier": 3 } ] }
                """),
            ("k.json", """
                { "schemaVersion": 1, "kind": "container", "entries": [
                    { "id": "item.ring", "kind": "item", "slot": "ring",
                      "atoms": [ { "atom": "atom.vitality.t1" } ] } ] }
                """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        Assert.Single(r.Content.Affixes);
        Assert.Equal(5, r.Content.Count);
    }

    // ---- power-coefficient (E44 criterion 0, spec-power-sweep.md §4.1) ----------------------------

    [Fact]
    public void A_coefficient_row_parses()
    {
        var r = Collect(("p.json", """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "kindId": "stat.derived", "channel": "combat.dodge.fire",
                  "coeffMilli": 1000, "referenceScale": 1 } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        var row = Assert.Single(r.Content.Coefficients);
        Assert.Equal("stat.derived", row.KindId);
        Assert.Equal("combat.dodge.fire", row.Channel);
        Assert.Equal(1000, row.CoeffMilli);
        Assert.Equal(1, row.ReferenceScale);
        Assert.Equal("p.json", r.Content.SourceOf["stat.derived/combat.dodge.fire"]);
    }

    [Fact]
    public void An_absent_channel_defaults_to_empty_meaning_priced_the_same_regardless()
    {
        var r = Collect(("p.json", """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "kindId": "shield.grant", "coeffMilli": 1000, "referenceScale": 10 } ] }
            """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        Assert.Equal("", r.Content.Coefficients[0].Channel);
        Assert.Equal("shield.grant/*", r.Content.SourceOf.Keys.Single());
    }

    [Fact]
    public void A_coefficient_with_no_kindId_is_refused()
    {
        var r = Collect(("p.json", """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "channel": "combat.dodge.fire", "coeffMilli": 1000, "referenceScale": 1 } ] }
            """));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.MissingParam, r.Errors[0].Reason);
        Assert.Empty(r.Content.Coefficients);
    }

    [Fact]
    public void A_coefficient_with_no_coeffMilli_is_refused_rather_than_defaulted()
    {
        var r = Collect(("p.json", """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "kindId": "stat.derived", "channel": "combat.dodge.fire", "referenceScale": 1 } ] }
            """));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.MissingParam, r.Errors[0].Reason);
        Assert.Contains("coeffMilli", r.Errors[0].Detail);
    }

    [Fact]
    public void A_coefficient_with_no_referenceScale_is_refused_rather_than_defaulted()
    {
        // A silently-defaulted reference scale would be the same units trap RpgStore.UpsertPowerTables
        // already refuses at the semantic layer — this is the structural half of that same guard.
        var r = Collect(("p.json", """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "kindId": "stat.derived", "channel": "combat.dodge.fire", "coeffMilli": 1000 } ] }
            """));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.MissingParam, r.Errors[0].Reason);
        Assert.Contains("referenceScale", r.Errors[0].Detail);
    }

    [Fact]
    public void The_same_kind_and_channel_pair_in_two_files_is_refused_as_a_duplicate()
    {
        const string row = """
            { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                { "kindId": "stat.derived", "channel": "combat.dodge.fire",
                  "coeffMilli": 1000, "referenceScale": 1 } ] }
            """;
        var r = Collect(("first.json", row), ("second.json", row));

        Assert.False(r.IsOk);
        Assert.Equal(AtomRejectionReason.DuplicateKey, r.Errors[0].Reason);
        Assert.Single(r.Content.Coefficients);
    }

    [Fact]
    public void A_coefficient_key_does_not_collide_with_a_channel_policy_row_naming_the_same_channel()
    {
        // The reason the claim key is "kindId/channel", not the bare channel ReadChannelPolicy claims
        // with: a coefficient's real identity is the (kind, channel) pair the table's own primary key
        // names, and two authors are free to write a channel-policy row and a coefficient row for the
        // same channel string without one refusing the other as a false duplicate.
        var r = Collect(
            ("cp.json", """
                { "schemaVersion": 1, "kind": "channel-policy", "entries": [
                    { "channel": "combat.dodge.fire", "direction": 0 } ] }
                """),
            ("pc.json", """
                { "schemaVersion": 1, "kind": "power-coefficient", "entries": [
                    { "kindId": "stat.derived", "channel": "combat.dodge.fire",
                      "coeffMilli": 1000, "referenceScale": 1 } ] }
                """));

        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        Assert.Single(r.Content.ChannelPolicies);
        Assert.Single(r.Content.Coefficients);
    }
}
