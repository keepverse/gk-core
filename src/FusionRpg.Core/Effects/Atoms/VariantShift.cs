using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// One creature-seed variant's resolution-parameter shift (Q12, `spec-resolution-order.md` "Variant
/// shifts", `ssot-rarity.md` §3.6). A variant nudges <see cref="Resolver.Resolve"/>'s own
/// parameters — it never authors a new container or atom: rarity buys breadth and tier ceiling,
/// never magnitude directly, and letting a variant multiply a magnitude would make rarity dominant
/// and destroy the overlap that rule exists to protect.
/// </summary>
/// <param name="VariantId">The creature-seed anchor's own variant name — <c>"ancient"</c>,
/// <c>"mutated"</c>, etc. (<see cref="Creatures.CreatureSpeciesCatalog.KnownVariants"/>'s vocabulary,
/// minus <c>"normal"</c>, which has no shift row — the caller passes <c>null</c> instead).</param>
/// <param name="TierWindowShift">Added to both <c>MinTier</c> and <c>MaxTier</c> before the t5
/// clamp — a uniform shift, so a valid (non-inverted) window stays valid after shifting.</param>
/// <param name="PrefixRollShift">Added to <c>PrefixRolls</c>, floored at 0.</param>
/// <param name="SuffixRollShift">Added to <c>SuffixRolls</c>, floored at 0.</param>
/// <param name="RerollsOneElementSlot">`corrupted`'s own shift: burns one extra draw on the first
/// `element`-domain slot the resolve encounters, discarding the first pick.</param>
public sealed record VariantShift(
    string VariantId, int TierWindowShift, int PrefixRollShift, int SuffixRollShift,
    bool RerollsOneElementSlot)
{
    /// <summary>
    /// t5 is the highest tier that exists — there is no t6 row to select. This is a <b>structural</b>
    /// limit, not a progression cap (AGENTS.md's no-hard-caps rule governs magnitudes; "which row
    /// exists" is a different question), and is named here so a later overflow/magic-number sweep
    /// does not flag <see cref="ShiftTierWindow"/>'s clamp as an illegal cap.
    /// </summary>
    public const int MaxTier = 5;

    const int MinTier = 1;

    /// <summary>Shifts both ends of the window by the same amount, then clamps each independently to
    /// <c>[1, 5]</c> — the uniform shift means a non-inverted window in stays non-inverted out, so the
    /// clamp can never invert it either. Null in, null out: an unconstrained window has nothing to
    /// shift.</summary>
    public (int? Min, int? Max) ShiftTierWindow(int? minTier, int? maxTier)
    {
        if (TierWindowShift == 0 || (minTier is null && maxTier is null)) return (minTier, maxTier);

        int? Shift(int? t) => t is { } v ? Math.Clamp(v + TierWindowShift, MinTier, MaxTier) : t;
        return (Shift(minTier), Shift(maxTier));
    }

    /// <summary>Floored at 0 — a roll count going negative is a domain-validity floor, not a balance
    /// cap; it is not exempt from anything because it was never a magnitude ceiling to begin with.</summary>
    public int ShiftPrefixRolls(int prefixRolls) => Math.Max(0, prefixRolls + PrefixRollShift);

    /// <summary>See <see cref="ShiftPrefixRolls"/>.</summary>
    public int ShiftSuffixRolls(int suffixRolls) => Math.Max(0, suffixRolls + SuffixRollShift);
}

public sealed class VariantShiftTuningRejection : Exception
{
    public VariantShiftTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2) — loads `gk-core/data/tuning/variant-shifts.v1.json`.</summary>
public static class VariantShiftTable
{
    public static IReadOnlyDictionary<string, VariantShift> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new VariantShiftTuningRejection("variant shifts: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new VariantShiftTuningRejection($"variant shifts: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Object)
                throw new VariantShiftTuningRejection("variant shifts: missing or non-object 'variants'");

            var table = new Dictionary<string, VariantShift>(StringComparer.Ordinal);
            foreach (var prop in variants.EnumerateObject())
            {
                var v = prop.Value;
                table[prop.Name] = new VariantShift(
                    VariantId: prop.Name,
                    TierWindowShift: Int(v, "tierWindowShift", prop.Name),
                    PrefixRollShift: Int(v, "prefixRollShift", prop.Name),
                    SuffixRollShift: Int(v, "suffixRollShift", prop.Name),
                    RerollsOneElementSlot: Bool(v, "rerollsOneElementSlot", prop.Name));
            }
            return table;
        }
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new VariantShiftTuningRejection($"variant shifts: missing or non-integer '{path}.{key}'");
        return v;
    }

    static bool Bool(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el)
            || (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
            throw new VariantShiftTuningRejection($"variant shifts: missing or non-boolean '{path}.{key}'");
        return el.GetBoolean();
    }
}
