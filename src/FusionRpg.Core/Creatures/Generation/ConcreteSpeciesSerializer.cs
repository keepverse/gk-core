using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// The generated tree's canonical form (T4.5, spec-species-generator.md §7: "committed, canonically
/// serialised, and regenerating over unchanged seeds produces byte-identical files"). Sorted keys
/// throughout — `SortedDictionary` for both the row's own fields and its magnitudes map — so the
/// same species always serialises to the same bytes regardless of dictionary insertion order.
/// </summary>
public static class ConcreteSpeciesSerializer
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Canonical(ConcreteSpecies species)
    {
        var obj = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["speciesId"] = species.SpeciesId,
            ["rarity"] = species.Rarity.ToString(),
            ["theta"] = species.Theta,
            ["pTheta"] = species.PTheta,
            ["attackIntervalMs"] = species.AttackIntervalMs,
            ["attackIntervalSource"] = species.AttackIntervalSource,
            ["rangeCells"] = species.RangeCells,
            ["variantCount"] = species.VariantCount,
            // catalog-runtime pass-through (T4.8's own real precondition, resolved 2026-09-02) —
            // copied from the anchor, not derived; committed here so the generated tree stays the
            // full, diffable, reviewable picture of what species-import actually writes.
            ["side"] = species.Side,
            ["gameTypeId"] = species.GameTypeId,
            ["elementPrimary"] = species.ElementPrimary.ToString(),
            ["elementSecondary"] = species.ElementSecondary?.ToString(),
            ["deployMode"] = species.DeployMode.ToString(),
            ["acquisition"] = species.Acquisition.ToString(),
            ["variants"] = species.Variants.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
            ["traitPool"] = species.TraitPool.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
            ["magnitudes"] = new SortedDictionary<string, long>(
                species.Magnitudes.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
        };
        // rank (spec-species-rank.md §1, §4): written only when the species HAS one, so a rank the
        // pipeline skipped (an unresolved threatBand/rarity) leaves the key absent rather than
        // fabricating a bottom rung — the same shape `speciesKind` uses below, and what the reader's
        // own optional-read expects. An absent key sorts into place on its own.
        if (species.Rank is { } rank) obj["rank"] = rank.ToString();
        // R-CS3/R-CS4's mark (CS13): written only when it is NOT the default, so the generated tree's
        // diff is exactly the species whose kind is something other than `creature` — the twelve
        // `excluded` rows — rather than a `null` line on all 904. Absent reads back as the default on
        // both sides, and the key sorts into place on its own.
        if (species.SpeciesKind is { Length: > 0 } kind &&
            !string.Equals(kind, "creature", StringComparison.Ordinal))
            obj["speciesKind"] = kind;
        // LF, never Environment.NewLine: the serializer indents with the host's newline, and the tree is
        // checked out as LF (`* text=auto eol=lf`), so a CRLF write makes every file stale on a clean
        // checkout (CanonicalEol's own doc comment).
        return JsonSerializer.Serialize(obj, Options).ToLf() + "\n";
    }
}
