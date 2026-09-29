using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// `eligibility-tags` (T5.8, `spec-eligibility-tags.md`): the production <c>tagsOf</c> supplier
/// <see cref="EligibilityResolver"/> has never had (spec `:29` — "nothing in production supplies"
/// one). An affix's tags are DERIVED from its refs' atoms, exactly as <see cref="AffixRow.Class"/>
/// already is by <see cref="AffixValidator.ResolveClass"/> — never authored on the affix itself, and
/// no new <c>effect_affix.tags_json</c> column (spec `:32-64`: it cannot contradict the bundle, it is
/// where the tags already live, and it is reversible).
///
/// <code>
/// tagsOf(affixId) := union over the affix's CONCRETE refs of AtomRow.TagsJson
///                    (a slot ref contributes its slotAtomPattern's family tags, or nothing)
/// </code>
/// </summary>
public static class AffixTags
{
    static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    /// <summary>
    /// The derived tag set for one affix: the union, in <c>Seq</c> order, of every ref's resolved
    /// atom's <see cref="AtomRow.TagsJson"/>. A later ref's key wins on a collision — not expected in
    /// real content, since a bundle does not repeat a tag key across members, so this is a tie-break,
    /// not a rule anyone should author against.
    ///
    /// <para>A concrete ref resolves through <paramref name="lookupAtom"/>, exactly like
    /// <see cref="AffixValidator.ResolveClass"/>'s own concrete-ref walk. A slot ref has no chosen
    /// atom until roll time (module 2, `resolution-order`), so it contributes its
    /// <see cref="AffixRefRow.SlotAtomPattern"/>'s FAMILY tags instead — any atom sharing that family
    /// via <paramref name="lookupAtomByFamily"/>, since a family's tags are stamped once per
    /// generation batch (E43) and shared by every tier/variant in it.</para>
    ///
    /// <para><b>Safe direction (spec `:56-60`, mirrors <c>ContentValidation.OrphanAtoms</c>'s own
    /// "safe direction for a non-blocking lint"):</b> a ref that resolves to nothing — an unknown
    /// concrete atom id, or a slot pattern whose family has no atom at all — contributes nothing. The
    /// derived set can only ever be too narrow, never too wide: a missing affix from a pool is
    /// visible, a wrongly-admitted one is not.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(
        AffixRow affix, Func<string, AtomRow?> lookupAtom, Func<string, AtomRow?> lookupAtomByFamily)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var r in affix.Refs.OrderBy(r => r.Seq))
        {
            var atom = r.AtomId is not null
                ? lookupAtom(r.AtomId)
                : FamilyOf(r.SlotAtomPattern, r.SlotName) is { } family ? lookupAtomByFamily(family) : null;

            if (atom is null) continue; // unresolved ref — safe direction, contributes nothing

            foreach (var (key, value) in ParseTags(atom.TagsJson))
                tags[key] = value;
        }

        return tags;
    }

    /// <summary>
    /// The real production supplier — the exact <c>Func&lt;string, IReadOnlyDictionary&lt;string,
    /// string&gt;&gt;</c> shape <see cref="EligibilityResolver.DrawablePool"/>/<see
    /// cref="EligibilityResolver.Validate"/> take. <paramref name="lookupAffix"/> is the same shape
    /// every real caller already supplies (<see cref="AffixValidator"/>, <see
    /// cref="ContainerValidator"/>, <see cref="Instantiator"/>, <see cref="Resolver"/>, and
    /// <c>RpgStore.Import.cs</c>'s own <c>LookupAffixForImport</c>), so this drops into any of them
    /// with no adaptation. <paramref name="atoms"/> is indexed ONCE, by id and by family, so every
    /// subsequent affix's tag lookup is a dictionary read, never a rescan of the catalog.
    /// </summary>
    public static Func<string, IReadOnlyDictionary<string, string>> ProductionSupplier(
        Func<string, AffixRow?> lookupAffix, IEnumerable<AtomRow> atoms)
    {
        var byId = new Dictionary<string, AtomRow>(StringComparer.Ordinal);
        var byFamily = new Dictionary<string, AtomRow>(StringComparer.Ordinal);

        foreach (var atom in atoms)
        {
            byId[atom.AtomId] = atom;
            // First atom seen for a family wins — family tags are stamped once per generation batch
            // (E43) and shared by every tier/variant in it, so any one member answers for the family.
            if (!byFamily.ContainsKey(atom.FamilyId)) byFamily[atom.FamilyId] = atom;
        }

        return affixId =>
        {
            var affix = lookupAffix(affixId);
            return affix is null
                ? Empty // unknown affix id — same safe direction, contributes nothing
                : Of(affix, id => byId.GetValueOrDefault(id), fam => byFamily.GetValueOrDefault(fam));
        };
    }

    /// <summary>Mirrors <c>AffixValidator.SubstitutePattern</c>/<c>Resolver.SubstitutePatternFamily</c>
    /// exactly — kept local rather than widening either's visibility, the same "kept local" precedent
    /// both of those already set, since this is the only piece of the pattern-splitting logic this
    /// module needs.</summary>
    static string? FamilyOf(string? pattern, string? slotName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(slotName)) return null;
        var placeholder = "$" + slotName;
        var idx = pattern.IndexOf(placeholder, StringComparison.Ordinal);
        return idx < 0 ? null : pattern[..idx].TrimEnd('.');
    }

    static IReadOnlyDictionary<string, string> ParseTags(string? json)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return tags;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return tags;

            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    tags[prop.Name] = prop.Value.GetString()!;
        }
        catch (JsonException) { /* AtomRowValidator already refuses an unparsable tags_json at load */ }

        return tags;
    }
}
