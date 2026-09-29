using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items;

/// <summary>
/// One family's authored naming row set, as the card reads it: the slot the family authored its words
/// under, plus the rows themselves.
///
/// <para><b><see cref="Slot"/> is content, not a second derivation of the affix budget class.</b> Every
/// shipped family authors exactly one of <c>nameWords.prefix</c> / <c>nameWords.suffix</c>, and that
/// choice IS the family's naming side — <c>freezing</c> authors <c>of Killing Frost</c> under
/// <c>suffix</c>, <c>warding</c> authors <c>Mossbound</c> under <c>prefix</c>. Reading it here rather
/// than re-deriving it is what keeps the corpus the one home for the words AND for which half of the
/// grammar they belong to (spec-affix-legality.md's "one home, not two").</para>
///
/// <para>⛔ <b>Named defect, 2026-09-06 — why this field exists at all.</b>
/// <c>AffixValidator.AffixClassOfAtom</c> is the SSOT for the affix BUDGET class and derives it from the
/// atom's own <c>when_json</c> trigger. <c>FamilyExpansion.Expand</c> writes
/// <c>WhenJson = "{}"</c> unconditionally for every one of the 109 generated families
/// (<c>FamilyExpansion.cs:208</c>), so that derivation currently answers <c>Prefix</c> for all of them —
/// including the 51 families whose whole authored vocabulary is suffix words. Feeding that answer to
/// <see cref="ItemNameComposer"/> would mean no item ever gets an <c>of …</c> clause and every
/// suffix-only family gets asked for a prefix word it does not have. That is a real generator gap
/// (the trigger is never emitted), not a naming decision, and it is filed rather than patched here:
/// this type reads the authored side instead, and falls back to the derivation for a family that
/// authored no words at all.</para>
/// </summary>
public readonly record struct AffixNameSlot(AffixClass Slot, IReadOnlyList<AffixNameRow> Rows);

/// <summary>
/// The wiring between a real rolled instance and <see cref="ItemNameComposer"/> — item module 8's
/// missing production caller, and nothing more.
///
/// <para>⛔ <b>It owns no naming logic.</b> The grammar, the selection rule and the rare-name threshold
/// are all <see cref="ItemNameComposer"/>'s; the word resolution is <see cref="AffixNameTable"/>'s.
/// This class does exactly two things neither of them can: it says which of an instance's frozen atoms
/// are the ROLLED ones, and it turns a family-keyed corpus into the two delegates
/// <see cref="ItemNameComposer.Compose"/> takes.</para>
/// </summary>
public static class ItemNameAssembly
{
    /// <summary>
    /// The rolled affixes of one instance, in the shape the naming function needs.
    ///
    /// <para>The drawn/authored split is the container's own fixed-core <c>seq</c> set — the SAME rule
    /// <c>ItemCardRenderer.Classify</c> reads for the affix block, so the name can never be composed
    /// from a line the card files under base stats. Nothing here re-reads the pool or the affix
    /// library: an instance's atoms and its container's fixed core are the whole input.</para>
    ///
    /// <para>An atom the catalog no longer carries is SKIPPED rather than throwing: the card renderer
    /// already refuses that instance by name (<c>DisplayTemplateRejection</c>), and a second, earlier
    /// refusal here would only change which message the player sees.</para>
    /// </summary>
    public static IReadOnlyList<NamedAffix> RolledAffixes(
        InstanceRow instance, ContainerRow container, Func<string, AtomRow?> lookupAtom,
        Func<string, AffixNameSlot?>? lookupNameWords = null)
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        if (container is null) throw new ArgumentNullException(nameof(container));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));

        var coreSeqs = container.Atoms.Select(a => a.Seq).ToHashSet();
        var rolled = new List<NamedAffix>();

        foreach (var row in instance.Atoms.OrderBy(a => a.Seq))
        {
            if (coreSeqs.Contains(row.Seq)) continue;   // authored, not drawn — never names the item

            var atom = lookupAtom(row.AtomId);
            if (atom is null) continue;

            rolled.Add(new NamedAffix(
                lookupNameWords?.Invoke(atom.FamilyId)?.Slot ?? AffixValidator.AffixClassOfAtom(atom),
                atom.FamilyId,
                atom.Tier,
                row.Seq,
                // `AtomRow.Variant` is "" and never NULL by design; the naming table's variant key is
                // nullable, and "" would match no element row and read as an authored blank.
                atom.Variant.Length == 0 ? null : atom.Variant));
        }

        return rolled;
    }

    /// <summary>
    /// The composed name of one rolled instance — <see cref="ItemNameComposer.Compose"/>, called once,
    /// against the real <c>nameWords</c> corpus.
    ///
    /// <para>A family the corpus does not carry throws <see cref="InvalidOperationException"/> by name.
    /// That is deliberate and it matches <c>DisplayRules.MissingDisplayTemplate</c>'s own posture: the
    /// alternative is putting a family id into a player-facing name. The card route turns it into the
    /// named 409 it already turns a missing base type into, never a 500. Every one of the 109 shipped
    /// families authors words, so nothing fires this today.</para>
    /// </summary>
    /// <param name="baseTypeName">The base type's authored NAME, never its <c>nameKey</c> — the grammar
    /// glues words onto a real noun ("Sturdy Bark Helm of Embers"), and a key would read
    /// "Sturdy base.bark-helm of Embers".</param>
    public static string Compose(
        string baseTypeName, string frame, long rollSeed,
        IReadOnlyList<NamedAffix> rolled,
        Func<string, AffixNameSlot?> lookupNameWords,
        Func<long, (string Head, string Tail)> rareNameDraw)
    {
        if (lookupNameWords is null) throw new ArgumentNullException(nameof(lookupNameWords));
        if (rareNameDraw is null) throw new ArgumentNullException(nameof(rareNameDraw));

        return ItemNameComposer.Compose(
            baseTypeName, rolled, frame,
            // The composer's `slot` argument is deliberately unused: `RolledAffixes` set every
            // NamedAffix's class FROM the family's own authored slot, so the slot the composer asks
            // for is always the slot the corpus holds, and looking it up a second time by name could
            // only ever disagree with itself.
            (familyId, _, tier, variant) =>
            {
                var slot = lookupNameWords(familyId)
                    ?? throw new InvalidOperationException(
                        $"affix family '{familyId}' has no authored nameWords, so it cannot supply a "
                        + "name word — naming it after its family id would put a raw id in front of the "
                        + "player (ssot-presentation.md §2.4)");

                return AffixNameTable.Resolve(slot.Rows, tier, variant, frame);
            },
            rareNameDraw, rollSeed);
    }
}
