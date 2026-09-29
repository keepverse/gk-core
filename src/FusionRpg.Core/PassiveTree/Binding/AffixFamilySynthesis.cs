using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.PassiveTree.Binding;

/// <summary>
/// Bridges the item program's per-tier `AtomRow`s (E43's `FamilyExpandGen`,
/// `data/seed/atoms/generated/family-expand.&lt;stem&gt;.json`) into the one-`AffixRow`-per-family
/// shape <see cref="AffixComposer"/> needs (task D2, found+decided 2026-09-06 — see
/// spec-tree-binder.md's own filed note for the full investigation).
///
/// <b>Why one row per family, not per tier.</b> A family's own channel/op/kind never change across
/// its tiers — only the item-context numeric amount does (`"Might T1"` .. `"Might T10"`), and this
/// program's own architecture (§3 of spec-tree-binder.md) already prices a node from its
/// `budgetShareMilli`, never from an atom's own amount range. So the numeric band is irrelevant to a
/// passive-tree bind, and the LOWEST tier present is as good a shape-carrier as any other — never a
/// number this method reads.
/// </summary>
public static class AffixFamilySynthesis
{
    /// <summary>Returns a NEW dictionary — `explicitAffixesById` plus one synthesized `AffixRow` per
    /// family in `atomsById` that has no already-authored `AffixRow` of the exact same id. An
    /// explicit, real `AffixRow` for a given id always wins over a synthesized one, never the
    /// reverse — this method never overwrites real, authored content.</summary>
    public static IReadOnlyDictionary<string, AffixRow> WithSynthesizedFamilyAffixes(
        IReadOnlyDictionary<string, AffixRow> explicitAffixesById,
        IReadOnlyDictionary<string, AtomRow> atomsById)
    {
        var result = new Dictionary<string, AffixRow>(explicitAffixesById);
        foreach (var group in atomsById.Values.Where(a => !string.IsNullOrEmpty(a.FamilyId))
                                       .GroupBy(a => a.FamilyId))
        {
            if (result.ContainsKey(group.Key)) continue;
            var canonical = group.OrderBy(a => a.Tier).First();
            result[group.Key] = new AffixRow(group.Key, Class: null,
                Refs: new[] { new AffixRefRow(Seq: 0, AtomId: canonical.AtomId) });
        }
        return result;
    }
}
