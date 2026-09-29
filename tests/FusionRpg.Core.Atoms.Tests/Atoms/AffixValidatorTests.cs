using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T3.1 (affix-schema, `definitions.md` §4a): an affix is a named bundle of atom refs, which may
/// include slots — a parameterised reference naming a domain and a pick count, resolved to a
/// concrete variant at roll time (module 2, not yet built). Same law as
/// <see cref="ContainerValidatorTests"/>: a bad affix is rejected <b>whole</b>, with its id and reason.
/// </summary>
public class AffixValidatorTests
{
    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static AffixValidatorTests()
    {
        void Add(string family, string variant, int tier, string? whenJson = null)
        {
            var id = AtomRow.DeriveId(family, variant, tier);
            Catalog[id] = new AtomRow
            {
                AtomId = id, KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
                ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
                WhenJson = whenJson ?? "{}",
            };
        }

        Add("atom.vitality", "", 1);
        Add("atom.might", "", 1);
        // A triggered atom, for the class-derivation tests — same kind, different when_json.
        Add("atom.surge", "", 1, "{\"trigger\":\"OnHit\"}");

        foreach (var v in new[] { "fire", "ice", "air" })
            Add("atom.elemental-power", v, 1);
        // "earth" deliberately absent — the missing-domain-member fixture.
    }

    static AtomRow? Lookup(string id) => Catalog.TryGetValue(id, out var a) ? a : null;

    static readonly IReadOnlyList<string> ElementDomain = new[] { "fire", "ice", "air", "earth" };
    static IReadOnlyList<string> Domains(string domain) =>
        domain == "element" ? ElementDomain : Array.Empty<string>();

    static bool VariantHasAnyTier(string family, string variant) =>
        Catalog.Values.Any(a => a.FamilyId == family && a.Variant == variant);

    static AtomRejection Check(AffixRow affix, bool withDomains = true) =>
        withDomains
            ? AffixValidator.Validate(affix, Lookup, Domains, VariantHasAnyTier)
            : AffixValidator.Validate(affix, Lookup);

    // ---- single concrete ref (the common case, module 3's own generator shape) --------------------

    [Fact]
    public void A_single_concrete_ref_validates_and_derives_prefix()
    {
        var affix = new AffixRow("affix.vitality", AffixClass.Prefix,
            new[] { new AffixRefRow(1, "atom.vitality.t1") });

        Assert.True(Check(affix).IsOk, Check(affix).ToString());
    }

    [Fact]
    public void A_triggered_atoms_ref_derives_suffix()
    {
        var affix = new AffixRow("affix.surge", AffixClass.Suffix,
            new[] { new AffixRefRow(1, "atom.surge.t1") });

        Assert.True(Check(affix).IsOk, Check(affix).ToString());
    }

    [Fact]
    public void An_authored_class_that_disagrees_with_the_derivation_is_rejected()
    {
        // affixClass is derived, never authored (seed-contract.md §2.1) — the whole point.
        var affix = new AffixRow("affix.vitality", AffixClass.Suffix,
            new[] { new AffixRefRow(1, "atom.vitality.t1") });

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(affix).Reason);
    }

    [Fact]
    public void An_unknown_concrete_atom_rejects_the_affix_whole()
    {
        var affix = new AffixRow("affix.nope", AffixClass.Prefix,
            new[] { new AffixRefRow(1, "atom.nope.t1") });

        Assert.Equal(AtomRejectionReason.UnknownAtom, Check(affix).Reason);
    }

    // ---- multi-ref bundles, mixed class (A1) -------------------------------------------------------

    [Fact]
    public void A_bundle_spanning_both_kinds_derives_mixed()
    {
        var affix = new AffixRow("affix.searing-aegis", AffixClass.Mixed, new[]
        {
            new AffixRefRow(1, "atom.vitality.t1"), // permanent -> prefix
            new AffixRefRow(2, "atom.surge.t1"),    // triggered -> suffix
        });

        Assert.True(Check(affix).IsOk, Check(affix).ToString());
    }

    [Fact]
    public void A_duplicate_atom_in_one_bundle_is_rejected()
    {
        var affix = new AffixRow("affix.dupe", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, "atom.vitality.t1"),
            new AffixRefRow(2, "atom.vitality.t1"),
        });

        Assert.Equal(AtomRejectionReason.DuplicateAtomInContainer, Check(affix).Reason);
    }

    [Fact]
    public void A_duplicate_seq_within_a_bundle_is_rejected()
    {
        var affix = new AffixRow("affix.dupe-seq", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, "atom.vitality.t1"),
            new AffixRefRow(1, "atom.might.t1"),
        });

        Assert.Equal(AtomRejectionReason.DuplicateSeq, Check(affix).Reason);
    }

    // ---- slots --------------------------------------------------------------------------------------

    [Fact]
    public void A_slot_ref_with_every_domain_member_resolvable_is_accepted()
    {
        // Only fire/ice/air are seeded — this fixture deliberately narrows the domain to just
        // those three (excluding "earth", the missing-member case the next test proves) so the
        // ACCEPT path is exercised on its own.
        var affix = new AffixRow("affix.elemental-power-narrow", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.elemental-power.$E1"),
        });

        var r = AffixValidator.Validate(affix, Lookup, _ => new[] { "fire", "ice", "air" }, VariantHasAnyTier);
        Assert.True(r.IsOk, r.ToString());
    }

    [Fact]
    public void A_slot_whose_domain_has_a_missing_member_rejects_at_load()
    {
        // The real fixture: "earth" is in the domain but no atom.elemental-power.earth.* exists —
        // a missing element row is a load-time rejection, never a roll-time surprise.
        var affix = new AffixRow("affix.elemental-power", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.elemental-power.$E1"),
        });

        var r = Check(affix);
        Assert.Equal(AtomRejectionReason.UnknownAtom, r.Reason);
        Assert.Contains("earth", r.Detail);
    }

    [Fact]
    public void A_slot_with_no_domain_check_wired_skips_the_per_member_validation()
    {
        // A caller that omits domainMembers/familyVariantHasAnyTier (e.g. a lighter-weight context)
        // gets a lenient pass on the slot itself — every OTHER check (pattern references the slot
        // name, pick count positive) still runs.
        var affix = new AffixRow("affix.elemental-power", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.elemental-power.$E1"),
        });

        Assert.True(Check(affix, withDomains: false).IsOk);
    }

    [Fact]
    public void An_unknown_slot_domain_is_rejected()
    {
        var affix = new AffixRow("affix.bad-domain", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "X1", "not-a-real-domain", 1, "atom.elemental-power.$X1"),
        });

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(affix).Reason);
    }

    [Fact]
    public void A_slot_pattern_that_does_not_reference_its_own_slot_name_is_rejected()
    {
        var affix = new AffixRow("affix.mismatched-pattern", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 1, "atom.elemental-power.$WrongName"),
        });

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(affix).Reason);
    }

    [Fact]
    public void A_zero_or_negative_pick_count_is_rejected()
    {
        var affix = new AffixRow("affix.zero-pick", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, null, "E1", "element", 0, "atom.elemental-power.$E1"),
        });

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(affix).Reason);
    }

    [Fact]
    public void A_ref_that_sets_both_a_concrete_atom_and_a_slot_is_rejected()
    {
        var affix = new AffixRow("affix.both-set", AffixClass.Prefix, new[]
        {
            new AffixRefRow(1, "atom.vitality.t1", "E1", "element", 1, "atom.elemental-power.$E1"),
        });

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(affix).Reason);
    }

    [Fact]
    public void A_ref_that_sets_neither_a_concrete_atom_nor_a_slot_is_rejected()
    {
        var affix = new AffixRow("affix.neither-set", AffixClass.Prefix, new[] { new AffixRefRow(1, null) });

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(affix).Reason);
    }

    // ---- identity -------------------------------------------------------------------------------

    [Fact]
    public void An_empty_affix_id_is_rejected()
    {
        var affix = new AffixRow("", AffixClass.Prefix, new[] { new AffixRefRow(1, "atom.vitality.t1") });

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(affix).Reason);
    }

    [Fact]
    public void An_affix_with_zero_refs_is_rejected()
    {
        var affix = new AffixRow("affix.empty", AffixClass.Prefix, Array.Empty<AffixRefRow>());

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(affix).Reason);
    }
}
