namespace FusionRpg.Core.Creatures;

/// <summary>Which species a context admits, read PER FLAG (species-gear-chain T5,
/// spec-wild-species-spawn.md "The decided admission rule"). One declaring site: a fourth context
/// is a new member here, never a re-derivation in a fourth file. EventOnly is refused first and
/// unconditionally in every context — events own their own gating, and a plain
/// HasFlag(Summonable) would admit a species carrying Summonable|EventOnly.
///
/// <para><b>R-CS3/R-CS4 (CS13): <c>speciesKind: "excluded"</c> is refused first of all, in every
/// context.</b> The twelve rows that carry the mark ship as <c>Summonable</c> in the corpus, so the
/// flag alone admits them — the mark is the only thing that removes them from play, and it is read
/// here and nowhere else.</para></summary>
public static class CreatureAdmission
{
    /// <summary>The mark's own closed vocabulary — <c>creature</c> (the default) or <c>excluded</c>.</summary>
    public const string ExcludedKind = "excluded";

    static bool Is(CreatureSpeciesDef s, CreatureAcquisition flag) => s.Acquisition.HasFlag(flag);

    /// <summary>The one place the mark is interpreted. An absent or empty kind reads as
    /// <c>creature</c>: the mark exists to REMOVE species from play, so never infer exclusion.</summary>
    public static bool IsExcluded(CreatureSpeciesDef s) =>
        s is not null && string.Equals(s.SpeciesKind, ExcludedKind, StringComparison.Ordinal);

    /// <summary>Wave roster: ordinary summoned species. CaptureOnly is refused — the wild, not the
    /// wave, is its acquisition route.</summary>
    public static bool ForWave(CreatureSpeciesDef s) =>
        !IsExcluded(s) && !Is(s, CreatureAcquisition.EventOnly) && Is(s, CreatureAcquisition.Summonable);

    /// <summary>Wild map: the wild IS CaptureOnly's acquisition route — encountering it is how you
    /// capture it, so refusing it here would strand 29 species entirely.</summary>
    public static bool ForWildMap(CreatureSpeciesDef s) =>
        !IsExcluded(s) && !Is(s, CreatureAcquisition.EventOnly) &&
        (Is(s, CreatureAcquisition.Summonable) || Is(s, CreatureAcquisition.CaptureOnly));

    /// <summary>Delve: decided to match the map (spec-wild-species-spawn.md).</summary>
    public static bool ForDelve(CreatureSpeciesDef s) => ForWildMap(s);
}
