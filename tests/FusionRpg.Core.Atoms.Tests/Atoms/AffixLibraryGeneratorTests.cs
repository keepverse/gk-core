using System.IO;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T3.5 (`affix-library`, `spec-affix-library.md`): a pure rule — one single-atom affix per generated
/// atom row, `affix_class` derived, zero model calls, and never touching an authored (module 9) affix.
/// </summary>
public class AffixLibraryGeneratorTests
{
    static AtomRow Atom(string family, string variant, int tier, string? whenJson = null) => new()
    {
        AtomId = AtomRow.DeriveId(family, variant, tier),
        KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        WhenJson = whenJson ?? "{}",
    };

    [Fact]
    public void Every_generated_atom_gets_exactly_one_single_atom_affix()
    {
        var atoms = new[]
        {
            Atom("atom.elemental-power", "fire", 1), Atom("atom.elemental-power", "fire", 2),
            Atom("atom.elemental-power", "ice", 1), Atom("atom.vitality", "", 1),
        };

        var affixes = AffixLibraryGenerator.Generate(atoms);

        Assert.Equal(atoms.Length, affixes.Count);
        Assert.Equal(atoms.Select(a => a.AtomId).OrderBy(x => x, StringComparer.Ordinal),
            affixes.Select(a => a.Refs.Single().AtomId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void The_generated_affix_id_strips_the_atom_prefix()
    {
        var affix = AffixLibraryGenerator.SingleAtomAffix(Atom("atom.elemental-power", "fire", 3));

        Assert.Equal("affix.elemental-power.fire.t3", affix.AffixId);
    }

    [Fact]
    public void A_missing_atom_prefix_wraps_the_id_whole_rather_than_mangling_it()
    {
        var atom = new AtomRow
        {
            AtomId = "weird.family.t1", KindId = "stat.modify", FamilyId = "weird.family", Tier = 1,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        };

        var affix = AffixLibraryGenerator.SingleAtomAffix(atom);

        Assert.Equal("affix.weird.family.t1", affix.AffixId);
    }

    [Fact]
    public void The_generated_affix_wraps_exactly_one_ref_with_no_slot()
    {
        var affix = AffixLibraryGenerator.SingleAtomAffix(Atom("atom.vitality", "", 1));

        var r = Assert.Single(affix.Refs);
        Assert.False(r.IsSlot);
        Assert.Equal("atom.vitality.t1", r.AtomId);
        Assert.Equal(1, r.Seq);
    }

    [Fact]
    public void Single_atom_affix_class_matches_the_atoms_own_derivation()
    {
        var permanent = Atom("atom.vitality", "", 1); // no trigger -> Prefix
        var triggered = Atom("atom.surge", "", 1, "{\"trigger\":\"OnHit\"}"); // trigger -> Suffix

        Assert.Equal(AffixClass.Prefix, AffixLibraryGenerator.SingleAtomAffix(permanent).Class);
        Assert.Equal(AffixClass.Suffix, AffixLibraryGenerator.SingleAtomAffix(triggered).Class);
    }

    [Fact]
    public void Every_generated_affix_passes_AffixValidator_on_its_own_terms()
    {
        // The generator's own output is never hand-checked against the validator elsewhere — prove it
        // directly, since a generated affix that the real validator would reject is a defect no
        // "1:1 count" assertion alone would catch.
        var atoms = new[] { Atom("atom.vitality", "", 1), Atom("atom.surge", "", 1, "{\"trigger\":\"OnHit\"}") };
        var lookup = atoms.ToDictionary(a => a.AtomId, a => a);

        foreach (var affix in AffixLibraryGenerator.Generate(atoms))
        {
            var r = AffixValidator.Validate(affix, id => lookup.TryGetValue(id, out var a) ? a : null);
            Assert.True(r.IsOk, r.ToString());
        }
    }

    [Fact]
    public void Adding_a_new_element_variant_regenerates_without_touching_authored_affixes()
    {
        // The regeneration property, proven not asserted: running the SAME generator over a catalog
        // that has grown a new variant produces the OLD affixes unchanged plus one new one — no
        // authored file is ever read or written by this generator to make that true.
        var before = new[] { Atom("atom.elemental-power", "fire", 1), Atom("atom.elemental-power", "ice", 1) };
        var after = before.Append(Atom("atom.elemental-power", "air", 1)).ToArray();

        var beforeAffixes = AffixLibraryGenerator.Generate(before).ToDictionary(a => a.AffixId);
        var afterAffixes = AffixLibraryGenerator.Generate(after).ToDictionary(a => a.AffixId);

        // byte-identical, same discipline as species-generator's --check — compared field-by-field,
        // since AffixRow.Refs is an array and record equality on an array is reference identity, not
        // content equality.
        foreach (var (id, affix) in beforeAffixes)
        {
            var other = afterAffixes[id];
            Assert.Equal(affix.AffixId, other.AffixId);
            Assert.Equal(affix.Class, other.Class);
            Assert.Equal(affix.Refs, other.Refs);
        }
        Assert.True(afterAffixes.ContainsKey("affix.elemental-power.air.t1"));
        Assert.Equal(before.Length + 1, afterAffixes.Count);
    }

    [Fact]
    public void Regenerating_over_an_unchanged_catalog_is_byte_identical()
    {
        var atoms = new[] { Atom("atom.vitality", "", 1), Atom("atom.might", "", 2) };

        var first = AffixLibraryGenerator.Generate(atoms);
        var second = AffixLibraryGenerator.Generate(atoms);

        // Compared field-by-field (see the same note above) — an AffixRow's Refs is an array, and
        // record equality on an array is reference identity, not content equality.
        Assert.Equal(first.Select(a => a.AffixId), second.Select(a => a.AffixId));
        Assert.Equal(first.Select(a => a.Class), second.Select(a => a.Class));
        for (var i = 0; i < first.Count; i++) Assert.Equal(first[i].Refs, second[i].Refs);
    }

    [Fact]
    public void An_authored_multi_ref_affix_is_never_overwritten_by_this_generator()
    {
        // This generator only ever produces ids of the shape "affix." + <stripped atom id> — an
        // authored multi-ref bundle names its OWN id (module 9's job), and this generator never reads
        // or writes an id it did not derive itself. Proven here by construction: an authored affix
        // with an unrelated id is simply absent from this generator's output, never touched.
        var atoms = new[] { Atom("atom.vitality", "", 1) };
        var authored = new AffixRow("affix.searing-aegis", AffixClass.Mixed, new[]
        {
            new AffixRefRow(1, "atom.vitality.t1"), new AffixRefRow(2, "atom.surge.t1"),
        });

        var generated = AffixLibraryGenerator.Generate(atoms);

        Assert.DoesNotContain(generated, a => a.AffixId == authored.AffixId);
    }

    [Fact]
    public void Zero_model_calls_anywhere_in_this_module()
    {
        // Same zero-call convention this repo already checks elsewhere (commander_effect.py) —
        // grepped against the source, not merely asserted by design intent.
        var path = FindSource("AffixLibraryGenerator.cs");
        var text = File.ReadAllText(path);

        foreach (var forbidden in new[] { "HttpClient", "call_model", "openai", "anthropic", "OpenAI", "Anthropic" })
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
    }

    static string FindSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FusionRpg.Core", "Effects", "Atoms", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(fileName + " not found walking up from " + AppContext.BaseDirectory);
    }
}
