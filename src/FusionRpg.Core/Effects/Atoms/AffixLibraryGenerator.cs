namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// `affix-library` (T3.5, `spec-affix-library.md`): module 1 (`affix-schema`) made
/// <c>effect_container_pool</c> reference affixes, not bare atoms — this generator is what stops that
/// from forcing a hand-authored wrapper for every one of the ~980 generated atom rows. Mirrors
/// `atom-family-library.md` §2's own rule one level up: do not hand-author what a pure function can
/// regenerate from the atom catalog. <b>Zero model calls</b> — every affix this module produces is
/// derived, never judged.
///
/// <para><b>Not every affix is single-atom, and this module does not generate the rest.</b> A
/// correlated bundle or a slot-bearing affix is an authored judgement (module 9, `affix-authoring`) —
/// this generator only ever wraps exactly one atom per affix, and never touches an id it did not
/// generate itself.</para>
/// </summary>
public static class AffixLibraryGenerator
{
    /// <summary>The one prefix this generator recognizes on an atom id — every real atom in the
    /// catalog carries it, but it is a convention, not a grammar the type system enforces, so a
    /// missing prefix falls back to wrapping the id whole rather than mangling a substring.</summary>
    const string AtomIdPrefix = "atom.";

    /// <summary>One single-atom affix per atom row — 1:1, no atom left unwrapped.</summary>
    public static IReadOnlyList<AffixRow> Generate(IEnumerable<AtomRow> atoms) =>
        atoms.Select(SingleAtomAffix).ToArray();

    /// <summary>
    /// <c>affix_class</c> is <b>derived</b> from the wrapped atom's own `kind_id`/trigger presence
    /// (<see cref="AffixValidator.AffixClassOfAtom"/> — the same rule `affix-schema` already
    /// established, `seed-contract.md` §2.1), never authored here.
    /// </summary>
    public static AffixRow SingleAtomAffix(AtomRow atom)
    {
        var affixId = atom.AtomId.StartsWith(AtomIdPrefix, StringComparison.Ordinal)
            ? "affix." + atom.AtomId[AtomIdPrefix.Length..]
            : "affix." + atom.AtomId;

        return new AffixRow(
            AffixId: affixId,
            Class: AffixValidator.AffixClassOfAtom(atom),
            Refs: new[] { new AffixRefRow(1, atom.AtomId) });
    }
}
