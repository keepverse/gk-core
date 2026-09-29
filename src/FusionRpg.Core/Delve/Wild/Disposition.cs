using FusionRpg.Core.Dungeon.Registry;

namespace FusionRpg.Core.Delve.Wild;

/// <summary>
/// D4.1 (spec-wild-room.md §1) — the wild room's effective disposition band: the room's own
/// `dispositionBase` (an id into the already-shipped <see cref="DispositionCatalog"/>, ordinal
/// `eager..hostile`, "eager" = 0 BY THE REGISTRY'S OWN ORDER) plus the five one-step-shaped shifts
/// the spec names, verbatim: the rung's own `wildDispositionShiftRungs`; the Δ band's
/// `wild.deltaShiftRungs[band]`; the offer's own preference (§2); `remembers` (§8); the stance
/// verb's own coin/threaten result (§2). Every shift arrives as a plain caller-supplied `int` — this
/// file resolves none of rung, Δ-band, offer-preference, remembers-log or talk-verb logic (that is
/// §2's table, §8's memory, and D4.2-D4.4's own callers, all unbuilt); it only sums and clamps.
///
/// <para>"The disposition is the room's, not the species'" (spec, verbatim) — 0 of 841 species
/// anchors carry a `disposition`/`temperament` field today (the anchor schema has neither), so there
/// is no species-level base to fall back FROM: every room reads its own archetype's
/// `dispositionBase` (`domain-catalog`'s own, unbuilt, content read — D4.15). This file deliberately
/// takes that already-resolved base id as its only "base" input rather than doing a
/// species-vs-archetype lookup itself, so it can never invent a literal default in its own place —
/// an unconfigured or unknown base id throws through <see cref="DispositionCatalog.Get"/>, the same
/// way every other caller of that registry is required to.</para>
/// </summary>
public static class Disposition
{
    /// <summary>Position of <paramref name="id"/> within <see cref="DispositionCatalog.All"/> —
    /// "eager = 0" IS this: the ordinal is the registry's own content order, never a hardcoded
    /// literal, so a future re-vote of the vocabulary's order changes this too, automatically.</summary>
    public static int OrdinalOf(string id)
    {
        DispositionCatalog.Get(id); // throws ArgumentException on an unknown id -- no silent -1
        var all = DispositionCatalog.All;
        for (var i = 0; i < all.Count; i++)
            if (all[i] == id) return i;
        throw new ArgumentException($"Unknown disposition id '{id}'.", nameof(id)); // unreachable after Get()
    }

    /// <summary>
    /// The base ordinal plus all five named shifts, clamped into the registry's own ends. Positive
    /// shifts move toward `hostile` (spec, verbatim: "positive = toward hostile"). Every shift is a
    /// plain signed step count, not itself pre-clamped — only the FINAL sum is clamped, so e.g. two
    /// individually-legal +2 shifts still land exactly at `hostile` rather than a wraparound or an
    /// intermediate value silently escaping the ladder.
    /// </summary>
    public static string Shift(
        string baseId, int rungShift, int deltaBandShift, int offerPreferenceShift, int remembersShift, int stanceShift)
    {
        var all = DispositionCatalog.All;
        var sum = OrdinalOf(baseId) + rungShift + deltaBandShift + offerPreferenceShift + remembersShift + stanceShift;

        // Structural clamp on a closed, content-defined ordinal's own ends -- an index into a
        // four-member registry, exempt from the no-silent-clamp rule the same way
        // Footprint.cs/OutcomeResolver.cs clamp a shape-ladder/drop-band index, never a magnitude.
        var clamped = Math.Clamp(sum, 0, all.Count - 1);
        return all[clamped];
    }
}
