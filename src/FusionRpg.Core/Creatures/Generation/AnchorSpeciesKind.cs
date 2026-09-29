using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// R-CS4's `speciesKind` mark as the GENERATION layer reads it: the closed vocabulary that decides
/// which seed-anchor rows are roster members at all — <c>creature</c> (a real creature whose
/// <c>gameTypeId</c> resolves natively), <c>mimic</c> (R-CS2: a real creature deliberately borrowing
/// a lawn type id it does not natively own — still a creature, and a borrowed id keeps every
/// spawn/render/dump join working) and <c>excluded</c> (NOT a creature: never spawn, never draw,
/// never count as roster). It is the same three-member enumeration as `SPECIES_KIND` in
/// `gk-forge/tools/seedsmith/seedsmith/adapters/creatures/anchor/schema.py`, the generator that derives the
/// field.
///
/// <para><b>The field itself is creature-seed's</b> (CS13, commit <c>be3ac8a6</c>): <see
/// cref="AnchorRow.SpeciesKind"/> already carries the raw string, parsed by <see
/// cref="AnchorRowReader"/>, and it must stay a string because its consumers
/// (<c>SpeciesExpander</c> → <c>ConcreteSpecies</c> → <c>CreatureSpeciesDef.SpeciesKind</c>) pass the
/// text through to the runtime record. What the generation layer lacked was a place to READ it: this
/// type is that one place, so the favour measure's population filter — and every consumer of that
/// population (the lean ranks, stage A) — read one rule instead of re-deriving the comparison, and
/// so the <c>excluded</c> literal is still declared exactly once (in <see
/// cref="CreatureAdmission.ExcludedKind"/>, the runtime interpreter of the same mark). A code-owned
/// vocabulary a human changes by review; deliberately NOT the species roster, which is a derived
/// population and must never become an enum.</para>
/// </summary>
public static class AnchorSpeciesKind
{
    /// <summary>A real creature — the default when the anchor omits the mark.</summary>
    public const string Creature = "creature";

    /// <summary>A real creature borrowing a lawn type id (R-CS2). Admitted like <see cref="Creature"/>.</summary>
    public const string Mimic = "mimic";

    /// <summary>Not a creature. The one literal is declared by the runtime interpreter of the same
    /// mark, so the two layers cannot drift; R-CS3 rules the mark exists to remove a row from play.</summary>
    public const string Excluded = CreatureAdmission.ExcludedKind;

    /// <summary>The three members, in declaration order — the whole vocabulary, so a pin or a
    /// validator reads it from one place instead of re-listing the literals.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Creature, Mimic, Excluded };

    /// <summary>Is this anchor row marked out of play? An absent (pre-mark anchor), empty or
    /// unrecognised mark reads as <see cref="Creature"/>: the mark only ever REMOVES a row, so
    /// exclusion is never inferred, and ordinal equality (never a prefix or case-folded match) keeps a
    /// future vocabulary member from excluding species by accident.</summary>
    public static bool IsExcluded(AnchorRow row) => IsExcluded(row?.SpeciesKind);

    /// <inheritdoc cref="IsExcluded(AnchorRow)"/>
    public static bool IsExcluded(string? speciesKind) =>
        string.Equals(speciesKind, Excluded, StringComparison.Ordinal);
}
