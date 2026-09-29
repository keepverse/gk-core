using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items;

/// <summary>
/// One channel's magnitude on each side. <c>Unit</c> labels rather than converts — SC4: magnitudes
/// across channel families are not comparable, so they are never summed across channels.
///
/// <para>⛔ <b><c>Unit</c> is <see cref="ChannelUnits.For"/>'s answer, and only ever that
/// (fixed 2026-09-06).</b> It used to be a free string derived from the atom's OP —
/// <c>"per-mille"</c> for <c>increased</c>/<c>more</c>, <c>"game-units"</c> otherwise — which made
/// two answers to one question: <c>DominancePresentation.GroupByUnitClass</c> puts the same delta
/// under <c>ChannelUnits.For(channel)</c>, so <c>maxHp</c> came back labelled <c>per-mille</c> inside
/// a <c>GameUnits</c> group header. spec-item-card.md's unit ledger settles which is authoritative:
/// <i>"a channel's unit is inseparable from its READER"</i> — the unit belongs to the channel, not to
/// the op, which is exactly how the card already labels its own lines
/// (<c>ItemCard.AtomLines</c> reads <c>ChannelUnits.ForAuthoredChannel</c> and lets the display
/// template carry the word "increased"). Sourcing it here from the same lookup the group header uses
/// makes the two agree BY CONSTRUCTION rather than by a test that notices when they stop.</para>
/// </summary>
/// <param name="Unit">The channel's unit class, or <c>null</c> when no reader resolves one — the same
/// null <c>GroupByUnitClass</c> gives its own group rather than folding into <c>GameUnits</c>.</param>
/// <param name="IncumbentMax">
/// The top of an <c>OnApply</c> BAND, when this channel's magnitude is a band rather than a point.
/// <c>null</c> means a point value and <see cref="Incumbent"/> is the whole answer.
///
/// <para>The shipped corpus authors <c>{min, max, roll: "onApply"}</c> for <b>every</b> generated
/// affix family (<c>FamilyExpansion</c> E43), and <c>Instantiator.Freeze</c> copies such a spec
/// through as authored because the HIT rolls it, not the item. Carrying both bounds is how the delta
/// table reports that content without inventing a scalar the corpus never authored.</para>
/// </param>
/// <param name="CandidateMax">The candidate side of the same band. See <see cref="IncumbentMax"/>.</param>
public sealed record ChannelDelta(
    string Channel, UnitClass? Unit, long Incumbent, long Candidate, long Delta,
    long? IncumbentMax = null, long? CandidateMax = null);

public enum DominanceVerdict
{
    StrictlyBetter,
    StrictlyWorse,
    Sidegrade,
    /// <summary>The two items touch entirely disjoint channel sets — there is nothing to weigh.</summary>
    Incomparable,
}

/// <summary>Where a rolled value sits in its atom's authored <c>[Min, Max]</c>, unit-free. 1000‰ for a
/// <c>Fixed</c> value spec — nothing rolled, so there is nothing to grade against.</summary>
public sealed record RollQualityEntry(string AtomId, int Milli);

public sealed record CompareResult(
    IReadOnlyList<ChannelDelta> Deltas,
    DominanceVerdict Dominance,
    IReadOnlyList<RollQualityEntry> RollQualities,
    int MeanRollQualityMilli);

/// <summary>One item's atom, paired with its frozen roll (<c>InstanceAtomRow.ValuesJson</c>) — the pair
/// this file needs and nothing more, so it stays a Core, DB-free, unit-testable type.</summary>
public readonly record struct CompareAtom(AtomRow Atom, string ValuesJson);

/// <summary>
/// I13 §5.5's three signals: per-channel delta, dominance verdict, roll quality. **No invented
/// scalar** (SC9) — a weighted sum across channel families would be wrong and look authoritative,
/// which is worse than no number. When module 9 lands, <c>power_json</c> becomes a fourth column
/// here, never a replacement: a single number cannot say *what* got better.
///
/// <para><b>Honestly scoped, not fully general.</b> Only <c>stat.modify</c>/<c>stat.derived</c> atoms
/// carry a <c>channel</c>; a <c>flat</c>/<c>replace</c> op labels its magnitude <c>game-units</c>, an
/// <c>increased</c>/<c>more</c> op labels it <c>per-mille</c>. Kinds with no <c>channel</c>
/// (<c>board.action</c>, <c>resource.delta</c>, …) do not contribute a channel delta — an interim
/// simplification, matching <c>item-power-reads</c>'s own precedent of shrinking to what a real
/// consumer needs rather than a fully general reader nobody has asked for yet.</para>
/// </summary>
public static class ArmouryCompare
{
    /// <param name="registry">The derived-channel registry the unit lookup reads. Defaults to
    /// <see cref="DerivedStatRegistry.CreateDefault"/>; a caller that already has one (the Compare
    /// level does) passes it so the deltas and the group header resolve against the same registry.</param>
    public static CompareResult Compare(
        IReadOnlyList<CompareAtom> incumbent, IReadOnlyList<CompareAtom> candidate,
        DerivedStatRegistry? registry = null)
    {
        var resolved = registry ?? DerivedStatRegistry.CreateDefault();

        var incumbentChannels = ChannelMagnitudes(incumbent);
        var candidateChannels = ChannelMagnitudes(candidate);

        var incumbentKeys = incumbentChannels.Keys.ToHashSet(StringComparer.Ordinal);
        var candidateKeys = candidateChannels.Keys.ToHashSet(StringComparer.Ordinal);
        var overlaps = incumbentKeys.Overlaps(candidateKeys);

        var union = incumbentKeys.Union(candidateKeys).OrderBy(k => k, StringComparer.Ordinal);
        var deltas = new List<ChannelDelta>();
        foreach (var channel in union)
        {
            incumbentChannels.TryGetValue(channel, out var i);
            candidateChannels.TryGetValue(channel, out var c);
            // ⛔ ONE producer for the unit -- ChannelUnits.For, the same call GroupByUnitClass makes.
            // See ChannelDelta's own note: the op is not a unit.
            var unit = ChannelUnits.For(channel, resolved);
            var isBand = i.Max != i.Value || c.Max != c.Value;
            deltas.Add(new ChannelDelta(
                channel, unit, i.Value, c.Value, c.Value - i.Value,
                isBand ? i.Max : null, isBand ? c.Max : null));
        }

        // Disjoint channel sets (both non-empty, nothing shared) means there is nothing to weigh --
        // that is the honest "incomparable" case, distinct from a sidegrade (shared channels moving
        // in opposite directions).
        var dominance =
            incumbentKeys.Count > 0 && candidateKeys.Count > 0 && !overlaps
                ? DominanceVerdict.Incomparable
                : VerdictFrom(deltas);

        var rollQualities = candidate.Select(a => new RollQualityEntry(a.Atom.AtomId, RollQualityMilliOf(a))).ToList();
        var mean = rollQualities.Count == 0 ? 1000 : (int)Math.Round(rollQualities.Average(r => r.Milli));

        return new CompareResult(deltas, dominance, rollQualities, mean);
    }

    static DominanceVerdict VerdictFrom(IReadOnlyList<ChannelDelta> deltas)
    {
        var anyPositive = deltas.Any(d => d.Delta > 0);
        var anyNegative = deltas.Any(d => d.Delta < 0);
        if (anyPositive && anyNegative) return DominanceVerdict.Sidegrade;
        if (anyPositive) return DominanceVerdict.StrictlyBetter;
        if (anyNegative) return DominanceVerdict.StrictlyWorse;
        return DominanceVerdict.Sidegrade; // identical on every shared channel -- no change either way
    }

    /// <summary>
    /// Every channel this side touches, summed, as a <c>[Value, Max]</c> pair. <c>Max == Value</c>
    /// means a point; <c>Max &gt; Value</c> means an <c>OnApply</c> band. Bands add bound-wise, which
    /// is the only correct way to add two ranges.
    /// </summary>
    static Dictionary<string, (long Value, long Max)> ChannelMagnitudes(IReadOnlyList<CompareAtom> atoms)
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        foreach (var a in atoms)
        {
            if (a.Atom.KindId is not ("stat.modify" or "stat.derived")) continue;
            if (!TryReadChannelAmount(a.ValuesJson, out var channel, out var min, out var max)) continue;

            map[channel] = map.TryGetValue(channel, out var existing)
                ? (existing.Item1 + min, existing.Item2 + max)
                : (min, max);
        }
        return map;
    }

    /// <summary>
    /// One frozen atom's channel and magnitude bounds.
    ///
    /// <para>⛔ <b>An <c>OnApply</c> band is no longer read as zero (fixed 2026-09-06).</b> This used
    /// to take <c>amount</c> only when it was a JSON <i>number</i> and fall through to <c>0</c>
    /// otherwise — and <c>Instantiator.Freeze</c> deliberately copies an <c>OnApply</c> spec through
    /// as <c>{min, max, roll}</c> because the hit rolls it, which the shipped corpus authors for
    /// <b>every</b> generated affix family. The whole delta table was therefore zeros on real content
    /// while the card beside it rendered the same atoms as <c>125–249 increased attack</c>. It now
    /// reads the band with <see cref="AtomJson.TryReadValueSpec"/> — the same reader
    /// <c>ItemCard.Magnitude</c> uses, so the card and the comparison cannot disagree about what an
    /// atom carries.</para>
    ///
    /// <para><b>The scalar column takes the band's MINIMUM</b>, and that is a read of authored
    /// content rather than a number invented here: it is the same bound <c>ItemCard.Magnitude</c>
    /// already treats as the line's value (with <c>Max</c> as the band top), and it is the
    /// conservative end — a band can only ever over-deliver against it. The full range still reaches
    /// the caller in <see cref="ChannelDelta.IncumbentMax"/> / <see cref="ChannelDelta.CandidateMax"/>,
    /// so nothing the corpus authored is dropped.</para>
    /// </summary>
    static bool TryReadChannelAmount(string json, out string channel, out long min, out long max)
    {
        channel = ""; min = 0; max = 0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (!doc.RootElement.TryGetProperty("channel", out var ch) || ch.ValueKind != JsonValueKind.String)
                return false;
            channel = ch.GetString() ?? "";

            if (!doc.RootElement.TryGetProperty("amount", out var amt)) return false;

            if (amt.ValueKind == JsonValueKind.Number)
            {
                min = max = amt.GetInt64();
                return true;
            }

            if (amt.ValueKind == JsonValueKind.Object && AtomJson.TryReadValueSpec(amt, out var spec).IsOk)
            {
                min = spec.Min;
                max = spec.Max;
                return true;
            }

            // A shape neither this nor the card can read contributes NOTHING rather than a zero: a
            // zero would sit in the table as a real magnitude and read as "this item has none".
            return false;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// One atom's roll quality in ‰, for a caller that has the pair but no <see cref="CompareResult"/>.
    ///
    /// <para><b>Public since item module 10's Card pass (2026-09-06).</b> The card's roll bar and the
    /// comparison's roll-quality column are the same number, and G3 §8.6's one-producer rule applies to
    /// it exactly as it applies to a line: a second position-in-band computation is how "the tooltip
    /// says a good roll and the compare screen says a bad one" happens. Body unchanged — only the
    /// visibility and the parameter shape moved.</para>
    /// </summary>
    public static int RollQualityMilli(AtomRow atom, string valuesJson) =>
        RollQualityMilliOf(new CompareAtom(atom, valuesJson));

    /// <summary>
    /// 1000‰ for a <c>Fixed</c> spec (<c>min == max</c>, or "amount" is a plain number rather than a
    /// <c>{min,max}</c> object) — nothing rolled, so there is nothing to grade against. Otherwise the
    /// rolled value's position in <c>[min, max]</c>, clamped, so a malformed or content-edited bound
    /// never reports outside 0..1000.
    /// </summary>
    static int RollQualityMilliOf(CompareAtom a)
    {
        if (!TryReadAmountBounds(a.Atom.ParamsJson, out var min, out var max)) return 1000;
        if (max <= min) return 1000;
        if (!TryReadRolledAmount(a.ValuesJson, out var rolled)) return 1000;

        var milli = (int)Math.Round((rolled - min) * 1000.0 / (max - min));
        return Math.Clamp(milli, 0, 1000);
    }

    static bool TryReadAmountBounds(string paramsJson, out long min, out long max)
    {
        min = 0; max = 0;
        if (string.IsNullOrWhiteSpace(paramsJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("amount", out var amt)) return false;
            if (amt.ValueKind != JsonValueKind.Object) return false; // a plain number is Fixed -- 1000 is correct
            if (!amt.TryGetProperty("min", out var minEl) || !amt.TryGetProperty("max", out var maxEl)) return false;
            if (minEl.ValueKind != JsonValueKind.Number || maxEl.ValueKind != JsonValueKind.Number) return false;
            min = minEl.GetInt64();
            max = maxEl.GetInt64();
            return true;
        }
        catch (JsonException) { return false; }
    }

    static bool TryReadRolledAmount(string valuesJson, out long rolled)
    {
        rolled = 0;
        if (string.IsNullOrWhiteSpace(valuesJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(valuesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("amount", out var amt) || amt.ValueKind != JsonValueKind.Number)
                return false;
            rolled = amt.GetInt64();
            return true;
        }
        catch (JsonException) { return false; }
    }
}
