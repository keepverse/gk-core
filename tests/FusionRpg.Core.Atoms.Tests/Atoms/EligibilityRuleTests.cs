using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T5.2 (`eligibility-tags`, `spec-eligibility-tags.md`): tag-based affix eligibility plus the
/// per-container allow/deny escape hatch — what keeps the affix library shared across features (Q6)
/// instead of forked per feature.
/// </summary>
public class EligibilityRuleTests
{
    static AffixRow Affix(string id, AffixClass cls = AffixClass.Prefix) =>
        new(id, cls, new[] { new AffixRefRow(1, "atom." + id.Replace("affix.", "") + ".t1") });

    static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Catalog = new(StringComparer.Ordinal)
    {
        ["affix.fire-power"] = new Dictionary<string, string> { ["element"] = "fire", ["theme"] = "elemental" },
        ["affix.ice-power"] = new Dictionary<string, string> { ["element"] = "ice", ["theme"] = "elemental" },
        ["affix.plain-strength"] = new Dictionary<string, string> { ["theme"] = "physical" },
        ["affix.hidden-gem"] = new Dictionary<string, string> { ["theme"] = "cosmetic" },
    };

    static IReadOnlyDictionary<string, string> TagsOf(string id) =>
        Catalog.TryGetValue(id, out var t) ? t : new Dictionary<string, string>();

    static readonly IReadOnlyList<AffixRow> AllAffixes = Catalog.Keys.Select(id => Affix(id)).ToArray();

    [Fact]
    public void Tag_match_selects_the_eligible_set()
    {
        var rule = new EligibilityRule(
            RequireTags: new[] { "element" }, AnyOfTags: Array.Empty<string>(),
            Allow: Array.Empty<string>(), Deny: Array.Empty<string>());

        var pool = EligibilityResolver.DrawablePool(AllAffixes, TagsOf, rule);

        Assert.Equal(new[] { "affix.fire-power", "affix.ice-power" }, pool.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Allow_admits_an_affix_the_tags_alone_would_exclude()
    {
        var rule = new EligibilityRule(
            RequireTags: new[] { "element" }, AnyOfTags: Array.Empty<string>(),
            Allow: new[] { "affix.hidden-gem" }, Deny: Array.Empty<string>());

        var pool = EligibilityResolver.DrawablePool(AllAffixes, TagsOf, rule);

        Assert.Contains("affix.hidden-gem", pool); // no "element" tag — tags alone would exclude it
    }

    [Fact]
    public void Deny_excludes_an_affix_the_tags_alone_would_include()
    {
        var rule = new EligibilityRule(
            RequireTags: new[] { "element" }, AnyOfTags: Array.Empty<string>(),
            Allow: Array.Empty<string>(), Deny: new[] { "affix.fire-power" });

        var pool = EligibilityResolver.DrawablePool(AllAffixes, TagsOf, rule);

        Assert.DoesNotContain("affix.fire-power", pool); // has "element" — tags alone would include it
        Assert.Contains("affix.ice-power", pool);
    }

    [Fact]
    public void Deny_wins_over_allow_for_the_same_affix()
    {
        var rule = new EligibilityRule(
            RequireTags: Array.Empty<string>(), AnyOfTags: Array.Empty<string>(),
            Allow: new[] { "affix.hidden-gem" }, Deny: new[] { "affix.hidden-gem" });

        Assert.False(EligibilityResolver.IsEligible("affix.hidden-gem", TagsOf("affix.hidden-gem"), rule));
    }

    [Fact]
    public void An_unsatisfiable_eligible_rule_rejects_at_load()
    {
        // requireTags selects nothing at all — a container that still wants a prefix roll cannot be
        // satisfied, and this must reject at LOAD, not silently under-fill at roll time.
        var rule = new EligibilityRule(
            RequireTags: new[] { "no-such-tag" }, AnyOfTags: Array.Empty<string>(),
            Allow: Array.Empty<string>(), Deny: Array.Empty<string>());

        var r = EligibilityResolver.Validate(rule, AllAffixes, TagsOf, prefixRolls: 1, suffixRolls: 0);

        Assert.Equal(AtomRejectionReason.UnsatisfiablePool, r.Reason);
    }

    [Fact]
    public void A_satisfiable_rule_with_no_roll_budget_never_rejects()
    {
        var rule = new EligibilityRule(
            RequireTags: new[] { "no-such-tag" }, AnyOfTags: Array.Empty<string>(),
            Allow: Array.Empty<string>(), Deny: Array.Empty<string>());

        // Zero rolls in both budgets — an empty eligible set is fine, nothing will ever draw from it.
        var r = EligibilityResolver.Validate(rule, AllAffixes, TagsOf, prefixRolls: 0, suffixRolls: 0);

        Assert.True(r.IsOk, r.ToString());
    }

    [Fact]
    public void An_allow_reference_to_an_unknown_affix_is_rejected()
    {
        var rule = new EligibilityRule(
            RequireTags: Array.Empty<string>(), AnyOfTags: Array.Empty<string>(),
            Allow: new[] { "affix.does-not-exist" }, Deny: Array.Empty<string>());

        var r = EligibilityResolver.Validate(rule, AllAffixes, TagsOf, prefixRolls: 0, suffixRolls: 0);

        Assert.Equal(AtomRejectionReason.UnknownAtom, r.Reason);
    }

    [Fact]
    public void Two_features_declare_independent_eligibility_over_the_same_shared_affix()
    {
        // Q6's own reconciliation: "elemental mastery" (affix.fire-power) is authored ONCE and is
        // eligible for both a feature that wants any "element" affix and one that wants only affixes
        // tagged "physical" — proven by resolving the SAME catalog against two independent rules,
        // neither forking a copy of the affix.
        var elementFeature = new EligibilityRule(
            RequireTags: new[] { "element" }, AnyOfTags: Array.Empty<string>(),
            Allow: Array.Empty<string>(), Deny: Array.Empty<string>());
        var physicalFeature = new EligibilityRule(
            RequireTags: new[] { "theme" }, AnyOfTags: new[] { "theme:physical" },
            Allow: Array.Empty<string>(), Deny: Array.Empty<string>());

        var elementPool = EligibilityResolver.DrawablePool(AllAffixes, TagsOf, elementFeature);
        var physicalPool = EligibilityResolver.DrawablePool(AllAffixes, TagsOf, physicalFeature);

        Assert.Contains("affix.fire-power", elementPool);
        Assert.DoesNotContain("affix.fire-power", physicalPool);
        Assert.Contains("affix.plain-strength", physicalPool);
        // Same catalog entry, same AffixRow instance, resolved twice — never a per-feature fork.
        Assert.Same(AllAffixes.Single(a => a.AffixId == "affix.fire-power"),
            AllAffixes.Single(a => a.AffixId == "affix.fire-power"));
    }

    // ---- AffixTags: the derived tagsOf (T5.8, `spec-eligibility-tags.md`) ----------------------------
    //
    // Everything above resolves eligibility against a HAND-TYPED tag dictionary, the shape the shipped
    // resolver's tests always used. Nothing in production ever supplied one — these tests prove the
    // real supplier `AffixTags` adds: tags derived from the refs' own `AtomRow.TagsJson`, never
    // authored on the affix, exactly as `AffixRow.Class` is already derived by
    // `AffixValidator.ResolveClass`.

    static AtomRow Atom(string atomId, string familyId, string variant, string tagsJson) => new()
    {
        AtomId = atomId,
        KindId = "stat.modify",
        FamilyId = familyId,
        Variant = variant,
        Tier = 1,
        Name = atomId,
        TagsJson = tagsJson,
    };

    [Fact]
    public void Affix_tags_are_the_union_of_its_refs_atom_tags()
    {
        // A real multi-ref bundle (item-ideal.md's "master of fire and ice" shape) — not a hand-typed
        // dictionary. Two concrete refs, each carrying its OWN tags on the atom row; the affix's
        // derived tags are the union, not either ref's tags alone.
        var fireAtom = Atom("atom.fire-rider.t1", "atom.fire-rider", "fire", """{"element":"fire","theme":"elemental"}""");
        var speedAtom = Atom("atom.swift-step.t1", "atom.swift-step", "", """{"category":"mobility"}""");

        var bundle = new AffixRow(
            "affix.fire-and-speed", AffixClass.Prefix,
            new[]
            {
                new AffixRefRow(1, fireAtom.AtomId),
                new AffixRefRow(2, speedAtom.AtomId),
            });

        Func<string, AtomRow?> lookupAtom = id => id switch
        {
            "atom.fire-rider.t1" => fireAtom,
            "atom.swift-step.t1" => speedAtom,
            _ => null,
        };

        var tags = AffixTags.Of(bundle, lookupAtom, _ => null);

        Assert.Equal(
            new Dictionary<string, string> { ["element"] = "fire", ["theme"] = "elemental", ["category"] = "mobility" },
            tags);
    }

    [Fact]
    public void A_slot_ref_with_no_resolvable_pattern_narrows_rather_than_widens()
    {
        // One concrete ref (real tags) plus one slot ref whose family has no atom at all — a pattern
        // that resolves to nothing. The safe direction (spec `:56-60`) means the slot contributes
        // NOTHING: the derived set is exactly the concrete ref's own tags, never more.
        var concreteAtom = Atom("atom.plain-strength.t1", "atom.plain-strength", "", """{"theme":"physical"}""");

        var bundle = new AffixRow(
            "affix.strength-plus-unresolved-slot", AffixClass.Prefix,
            new[]
            {
                new AffixRefRow(1, concreteAtom.AtomId),
                new AffixRefRow(2, AtomId: null, SlotName: "E1", SlotDomain: "element",
                    SlotPick: 1, SlotAtomPattern: "atom.no-such-family.$E1"),
            });

        Func<string, AtomRow?> lookupAtom = id => id == concreteAtom.AtomId ? concreteAtom : null;
        Func<string, AtomRow?> lookupAtomByFamily = _ => null; // the slot's family resolves to nothing

        var tags = AffixTags.Of(bundle, lookupAtom, lookupAtomByFamily);

        // If the slot had accidentally widened the set, this would fail — asserted, not assumed.
        Assert.Equal(new Dictionary<string, string> { ["theme"] = "physical" }, tags);
    }

    [Fact]
    public void The_production_tagsOf_is_wired_and_not_only_a_test_delegate()
    {
        // The whole point of the module: `AffixTags.ProductionSupplier` composed over REAL production
        // pieces — `AffixLibraryGenerator` (module 3, shipped) generating affixes from real atom rows,
        // never a hand-authored `AffixRow`/tag dictionary — reachable and callable by the shipped
        // `EligibilityResolver` exactly as any real caller would use it.
        var fireAtom = Atom("atom.fire-rider.t1", "atom.fire-rider", "fire", """{"element":"fire","theme":"elemental"}""");
        var iceAtom = Atom("atom.ice-rider.t1", "atom.ice-rider", "ice", """{"element":"ice","theme":"elemental"}""");
        var plainAtom = Atom("atom.plain-strength.t1", "atom.plain-strength", "", """{"theme":"physical"}""");
        var atoms = new[] { fireAtom, iceAtom, plainAtom };

        // The real generator (module 3) — one affix per atom, 1:1 — the same call
        // `RpgStore.Import.cs` makes in production, never a fixture built by hand.
        var generated = AffixLibraryGenerator.Generate(atoms).ToDictionary(a => a.AffixId, StringComparer.Ordinal);
        AffixRow? LookupAffix(string id) => generated.TryGetValue(id, out var a) ? a : null;

        var tagsOf = AffixTags.ProductionSupplier(LookupAffix, atoms);

        var rule = new EligibilityRule(
            RequireTags: new[] { "element" }, AnyOfTags: Array.Empty<string>(),
            Allow: Array.Empty<string>(), Deny: Array.Empty<string>());

        var catalog = generated.Values.ToList();
        var pool = EligibilityResolver.DrawablePool(catalog, tagsOf, rule);

        Assert.Equal(new[] { "affix.fire-rider.t1", "affix.ice-rider.t1" }, pool.OrderBy(x => x, StringComparer.Ordinal));

        // And the shipped load-time check runs against it too — the same resolver, the same tagsOf,
        // no separate code path for validation vs. drawing.
        var check = EligibilityResolver.Validate(rule, catalog, tagsOf, prefixRolls: 1, suffixRolls: 0);
        Assert.True(check.IsOk, check.ToString());
    }
}
