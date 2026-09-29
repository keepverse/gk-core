using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Delve.Wild;

/// <summary>
/// D4.8 (spec-wild-room.md §7, "The cage") — the structural draw and the occupant's own shape.
/// "Deterministic over `(seed, r, c, tuning)`; no anchor field, no event row, no model" — this file
/// owns no RNG stream and no species-pool lookup itself; every input is the caller's own
/// already-rolled fact, matching this program's "read model owned elsewhere" shape throughout.
/// </summary>
public static class Cage
{
    /// <summary>"at first entry, `NextPerMille() &lt; wild.cageMilli` on `dungeon:wild:{r}:{c}:cage`
    /// makes the room a cage room" — the caller has already rolled <paramref name="rolledMilli"/> on
    /// that named stream; this is the comparison alone.</summary>
    public static bool IsCageRoom(long rolledMilli, long cageMilli) => rolledMilli < cageMilli;

    /// <summary>"the wild pool (`WildBand`'s filter… never `CaptureOnly`, never the top rung)" — a
    /// plain eligibility filter over one already-selected candidate; drawing FROM the pool is the
    /// caller's own job (the species catalog, not owned here).
    ///
    /// <para>spec-species-rank.md §6: the rank floor joins here as a THIRD narrowing condition, never
    /// replacing either of the two structural ones, and it never prices — the cage's contract price
    /// stays rarity-keyed (<see cref="OfferPricing.Contract"/>), which this file's own tests assert. A
    /// candidate with no rank maps to the bottom rung AT THIS GATE, so the ladder never sees a null.
    /// The parameter is REQUIRED (not optional) because there is no production caller yet: the Delve
    /// wild endpoints assemble the occupant spec themselves, so the compiler must force whoever wires
    /// the real cage path to supply the rank rather than silently defaulting it away.</para></summary>
    public static bool OccupantEligible(bool captureOnly, bool isTopRung, CreatureRank? rank) =>
        !captureOnly && !isTopRung && CreatureRankFloors.Passes(CreatureRankFloors.CageEligibility, rank);

    /// <summary>"`dispositionBase` shifted one band toward `eager` — a caged creature wants out; a rule,
    /// not a knob." Reuses <see cref="Disposition.Shift"/> (D4.1) rather than a private clamp — the
    /// cage's own fixed shift has no named source among that function's five (rung/Δ-band/offer/
    /// remembers/stance), so it is carried through the `stanceShift` slot with this comment
    /// explaining why: `Disposition.Shift` is agnostic to WHICH of its five inputs contributes a
    /// given shift, it only sums and clamps.</summary>
    public static string OccupantDispositionBase(string speciesDispositionBase) =>
        Disposition.Shift(speciesDispositionBase,
            rungShift: 0, deltaBandShift: 0, offerPreferenceShift: 0, remembersShift: 0, stanceShift: -1);

    /// <summary>"`open` is §2's tree WITHOUT `fight` and `threaten`, same refusals, same mint, same
    /// memory." A thin filter over the already-shipped <see cref="TalkTree.Offered"/> (D4.2) — no
    /// re-derivation of the eligibility rules themselves.</summary>
    public static IReadOnlyList<WildVerb> Offered(int step, int maxSteps, WildTalkEligibility eligibility)
    {
        var offered = TalkTree.Offered(step, maxSteps, eligibility);
        var filtered = new List<WildVerb>(offered.Count);
        foreach (var verb in offered)
            if (verb != WildVerb.Fight && verb != WildVerb.Threaten)
                filtered.Add(verb);
        return filtered;
    }
}
