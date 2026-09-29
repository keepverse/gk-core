using System.Text.Json;

namespace FusionRpg.Core.Items;

/// <summary>One `nameWords` row, re-keyed 2026-09-04 (seed-contract.md, `affix-legality` module 8) —
/// either band-keyed (75 families, one word per power band) or variant-keyed (23 families, one word
/// per element). Exactly one of <see cref="Band"/> / <see cref="Variant"/> is set.</summary>
public readonly record struct AffixNameRow(string? Band, string? Variant, string Word, string? WordPlant);

public sealed class AffixNameRejection : Exception
{
    public AffixNameRejection(string message) : base(message) { }
}

/// <summary>
/// The `item_affix_name` PROJECTION — built at import from each family's own `nameWords`, never a
/// second, hand-authored home for the words (spec-affix-legality.md "The data shape — one home, not
/// two"). Keyed by (familyId, slot); <see cref="Parse"/> takes one already-loaded family's JSON
/// `nameWords` object (no file I/O here — the host walks `affix-families/*.json` and calls this once
/// per family).
/// </summary>
public static class AffixNameTable
{
    public static IReadOnlyList<AffixNameRow> ParseSlot(JsonElement nameWordsSlot)
    {
        if (nameWordsSlot.ValueKind != JsonValueKind.Array)
            throw new AffixNameRejection("nameWords slot: expected an array");

        var rows = new List<AffixNameRow>();
        foreach (var row in nameWordsSlot.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new AffixNameRejection(
                    "nameWords row is not an object -- the flat string shape was retired 2026-09-04; "
                    + "every row is now {band|variant, word, wordPlant?}");

            var hasBand = row.TryGetProperty("band", out var bandEl);
            var hasVariant = row.TryGetProperty("variant", out var variantEl);
            if (hasBand == hasVariant)
                throw new AffixNameRejection("nameWords row must carry exactly one of 'band' or 'variant'");

            var word = row.TryGetProperty("word", out var w) ? w.GetString() : null;
            if (string.IsNullOrEmpty(word))
                throw new AffixNameRejection("nameWords row is missing 'word'");

            var wordPlant = row.TryGetProperty("wordPlant", out var wp) ? wp.GetString() : null;

            rows.Add(new AffixNameRow(
                hasBand ? bandEl.GetString() : null,
                hasVariant ? variantEl.GetString() : null,
                word!, wordPlant));
        }

        return rows;
    }

    /// <summary>D29's fixed band split: A = t1-t2, B = t3, C = t4-t5 (not an even split — C is
    /// deliberately the top two tiers).</summary>
    public static string BandOfTier(int tier) => tier switch
    {
        1 or 2 => "A",
        3 => "B",
        4 or 5 => "C",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "tier must be 1..5"),
    };

    /// <summary>
    /// Resolve one word. Band-keyed families look up by the rolled tier's band; variant-keyed
    /// families look up by the rolled variant and fall back to the list's first word for `omni` or
    /// any variant the family did not supply a word for (a family with fewer than six words never
    /// covers all six elements — the fallback is a documented starting choice, not an omission).
    /// `frame == plant` prefers <see cref="AffixNameRow.WordPlant"/> when the row carries one.
    /// </summary>
    public static string Resolve(IReadOnlyList<AffixNameRow> rows, int tier, string? variant, string frame)
    {
        if (rows.Count == 0) throw new AffixNameRejection("nameWords slot has no rows");

        var isVariantKeyed = rows[0].Variant is not null;
        var chosen = isVariantKeyed
            ? rows.FirstOrDefault(r => r.Variant == variant, rows[0])
            : rows.First(r => r.Band == BandOfTier(tier));

        return frame == "plant" && chosen.WordPlant is not null ? chosen.WordPlant : chosen.Word;
    }
}
