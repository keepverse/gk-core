namespace FusionRpg.Core.Items;

/// <summary>One rolled affix, as the naming function needs to see it — content-derived, never an
/// instance/binding id (spec-affix-legality.md: the tiebreak must never use those).</summary>
public readonly record struct NamedAffix(FusionRpg.Core.Effects.Atoms.AffixClass Class, string FamilyId, int Tier, int Seq, string? Variant);

/// <summary>
/// ⛔ THE NAMING FUNCTION (spec-affix-legality.md "Item naming") — nothing owned this before this
/// module; every dropped item was nameless. Pure: a function of `(base type, rolled affix set)`,
/// never stored on the instance, so a reroll (module 15) leaves the name alone for free and
/// `spec-item-card.md:302`'s byte-identical-name assertion is passable.
///
/// <para>Module 10 renders the name; it does not derive it. A unique bypasses this entirely
/// (hand-authored, module 17) — never called for one.</para>
/// </summary>
public static class ItemNameComposer
{
    /// <summary>
    /// `rareNameThreshold`: 3+ affixes get a seeded two-word name rather than being named after two
    /// of (possibly) many affixes, which the spec calls "a lie about what the item does". Balance
    /// surface (tunables-ssot.md T1) — reads through <see cref="ItemsTuningHub"/>, not a bare const.
    /// </summary>
    public static int RareNameThreshold => ItemsTuningHub.Tuning.RareNameThreshold;

    public static string Compose(
        string baseTypeName, IReadOnlyList<NamedAffix> rolled, string frame,
        Func<string, string, int, string?, string> nameWordLookup, // (familyId, slot, tier, variant) -> word
        Func<long, (string Head, string Tail)> rareNameDraw, long rollSeed)
    {
        if (rolled.Count == 0) return baseTypeName;

        if (rolled.Count >= RareNameThreshold)
        {
            var (head, tail) = rareNameDraw(rollSeed);
            return $"{head} {tail}";
        }

        var prefixWord = BestWord(rolled, FusionRpg.Core.Effects.Atoms.AffixClass.Prefix, frame, nameWordLookup);
        var suffixWord = BestWord(rolled, FusionRpg.Core.Effects.Atoms.AffixClass.Suffix, frame, nameWordLookup, excludeFamilySeq: prefixWord?.Seq);

        return (prefixWord, suffixWord) switch
        {
            (null, null) => baseTypeName,
            ({ } p, null) => $"{p.Word} {baseTypeName}",
            (null, { } s) => $"{baseTypeName} {WithConnective(s.Word)}",
            ({ } p, { } s) => $"{p.Word} {baseTypeName} {WithConnective(s.Word)}",
        };
    }

    /// <summary>
    /// ⛔ <b>Defect found 2026-09-06 wiring this function to the real corpus (item-content T1), and
    /// fixed here rather than across 153 seed rows.</b> `ssot-affixes.md` §4.12 contradicts itself: its
    /// grammar line writes the connective as code (<c>[prefix word] [base type] of [suffix word]</c>)
    /// while its own sample word table authors it into the word (<c>of Embers</c>, <c>of Sparks</c>,
    /// <c>of the Wellspring</c>). Every one of the 153 shipped suffix words in
    /// <c>gk-data/packs/fusion/data/seed/items/affix-families/*.json</c> follows the table, so the unconditional
    /// <c>"of "</c> this method used to prepend produced <i>"Sunworn Charm of of Embers"</i> on every
    /// real suffix-bearing item. It was invisible because the only caller was a test whose lookup
    /// returned synthetic words.
    ///
    /// <para>The connective stays owned here — that is what the grammar line says and it is what keeps
    /// a corpus that authors a bare word working — and a word that already carries it is passed
    /// through untouched. Content is not edited to suit code: 153 rows agreeing with each other and
    /// with the SSOT's own table are the authored intent, and re-writing them would also fight the
    /// seedsmith the next time it emits a family.</para>
    /// </summary>
    const string SuffixConnective = "of ";

    static string WithConnective(string word) =>
        word.StartsWith(SuffixConnective, StringComparison.Ordinal) ? word : SuffixConnective + word;

    readonly record struct PickedWord(int Seq, string Word);

    /// <summary>
    /// Highest tier wins; `(tier DESC, seq ASC)` breaks ties — never `instance_id`/`binding_id`, or
    /// two byte-identical items would get two different names. A `Mixed` (hybrid) affix is eligible
    /// for either slot but supplies at most one word total: <paramref name="excludeFamilySeq"/> keeps
    /// the suffix search from reusing the exact affix instance the prefix search already spent.
    /// </summary>
    static PickedWord? BestWord(
        IReadOnlyList<NamedAffix> rolled, FusionRpg.Core.Effects.Atoms.AffixClass slot, string frame,
        Func<string, string, int, string?, string> nameWordLookup, int? excludeFamilySeq = null)
    {
        var slotName = slot == FusionRpg.Core.Effects.Atoms.AffixClass.Prefix ? "prefix" : "suffix";
        var candidates = rolled
            .Where(a => (a.Class == slot || a.Class == FusionRpg.Core.Effects.Atoms.AffixClass.Mixed) && a.Seq != excludeFamilySeq)
            .OrderByDescending(a => a.Tier)
            .ThenBy(a => a.Seq)
            .ToList();

        if (candidates.Count == 0) return null;

        var best = candidates[0];
        var word = nameWordLookup(best.FamilyId, slotName, best.Tier, best.Variant);
        return new PickedWord(best.Seq, word);
    }
}
