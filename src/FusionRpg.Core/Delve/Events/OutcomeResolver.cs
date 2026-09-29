using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Roll;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.5 (spec-event-deck.md §5) — PARTIALLY BUILT: the severity band-shift, the weighted
/// `:outcome` draw, and (2026-09-08) the forced-outcome-via-`supplyOverride` path are all built. Every
/// dropBand/weight vocabulary is a plain caller-supplied parameter, never hardcoded — `dropBand` is the
/// ITEM registry's own vocabulary (D3.1's own citation: "this module has no business owning" it), so
/// this class reads it exactly the way `EventCatalog.Load` already does. `TryInstantiate` and the
/// five-way atom-kind dispatch table are NOT built here — see D3.3/D3.5's own todo entries.
///
/// <para><b>The forced-outcome field, re-investigated 2026-09-08: no new seed-contract field is
/// needed, because the resolution rule is already written down, just never implemented.</b> The prior
/// finding ("no field anywhere records which outcome is forced") was correct about the ON-DISK seed
/// JSON — confirmed again by re-reading `EventRow.cs`/`spec-dungeon-seed-contract.md` §1.4's own field
/// table, still no such key. But `spec-event-deck.md:157-158` ("Forced outcome") says literally: *"the
/// outcome is the row `supplyOverride` designates (**the importer records the forced ordinal**...)"* —
/// naming this as an IMPORTER/resolver-computed fact, never an authored one, which is exactly what a
/// pure function does instead of a stored field. `gk-forge/tools/seedsmith/seedsmith/adapters/dungeon/
/// descriptions.py`'s own authoring brief for `supplyOverride` independently confirms which outcome:
/// *"to force this event's **best** outcome"* — and the ordinal vocabulary's own preference reading
/// (`good · mixed · bad · nothing`, the exact tuple order named identically in `schema.py`'s
/// `OUTCOME_ORDINAL`, `spec-dungeon-seed-contract.md`'s own field-table row, and this file's own
/// `ShiftDropBand`, where `good` moves toward stronger bands and `bad` toward weaker ones) gives "best"
/// an unambiguous, already-established total order — never a guess invented here. Spec's own worked
/// example (`spec-event-deck.md:158`, Darkest Dungeon's Iron Maiden curio, *"100% loot with Herbs"*)
/// independently confirms an override always forces a strictly POSITIVE result, matching `good` ranking
/// first. <see cref="TryForcedOutcome"/> is that resolver: it never reads or invents a stored field,
/// and refuses (rather than silently guessing) the one case the total order cannot resolve on its own —
/// two outcomes tied on the same top-ranked available ordinal.</para>
/// </summary>
public static class OutcomeResolver
{
    public const string GoodOrdinal = "good";
    public const string MixedOrdinal = "mixed";
    public const string BadOrdinal = "bad";
    public const string NothingOrdinal = "nothing";
    // mixed / nothing: no shift (spec §5, verbatim: "mixed/nothing stay").

    /// <summary>Best-to-worst, matching the vocabulary's own listed order everywhere it is declared
    /// (`schema.py`'s `OUTCOME_ORDINAL`, `spec-dungeon-seed-contract.md`'s own field-table row,
    /// `EventCatalog.cs`'s validation loop) — never a private ranking invented for this one method.</summary>
    static readonly string[] OrdinalPreferenceOrder = { GoodOrdinal, MixedOrdinal, BadOrdinal, NothingOrdinal };

    static int IndexOfBand(IReadOnlyList<string> dropBandOrder, string dropBand)
    {
        for (var i = 0; i < dropBandOrder.Count; i++)
            if (string.Equals(dropBandOrder[i], dropBand, StringComparison.Ordinal)) return i;
        throw new ArgumentException($"'{dropBand}' is not a member of the supplied dropBand order", nameof(dropBand));
    }

    /// <summary>
    /// Spec §5's "Severity" paragraph, verbatim: for severity tier `t`, a `good` outcome's `dropBand`
    /// moves `t` steps toward the END of <paramref name="dropBandOrder"/> (rarer/stronger — production
    /// order is `staple·frequent·occasional·seldom·exceptional`, but this function never hardcodes it);
    /// a `bad` outcome moves `t` steps toward the START (commoner/weaker); `mixed`/`nothing` never move.
    /// The index is clamped at both ends of the caller's own list — "bounded by the five-member enum,
    /// an ordinal rail" (spec, verbatim): a STRUCTURAL clamp on a closed enum's own ends, exempt from
    /// the no-silent-clamp rule the same way a `PredicateCompiler.MaxDepth` bound is, never a
    /// progression ceiling on a magnitude.
    /// </summary>
    public static string ShiftDropBand(string dropBand, string ordinal, int severityTier, IReadOnlyList<string> dropBandOrder)
    {
        if (dropBand is null) throw new ArgumentNullException(nameof(dropBand));
        if (ordinal is null) throw new ArgumentNullException(nameof(ordinal));
        if (dropBandOrder is null) throw new ArgumentNullException(nameof(dropBandOrder));
        if (dropBandOrder.Count == 0) throw new ArgumentException("dropBandOrder must not be empty", nameof(dropBandOrder));
        if (severityTier < 0) throw new ArgumentOutOfRangeException(nameof(severityTier), severityTier, "severity tier cannot be negative");

        var index = IndexOfBand(dropBandOrder, dropBand);
        var shifted = ordinal switch
        {
            GoodOrdinal => Math.Min(dropBandOrder.Count - 1, index + severityTier),
            BadOrdinal => Math.Max(0, index - severityTier),
            _ => index,
        };
        return dropBandOrder[shifted];
    }

    /// <summary>Spec §5: "Weight = `weightTable[band]`" — the item registry's own `dropBand.weightTable`
    /// (`bands.v1.json`), read as a plain caller-supplied map, never re-derived here.</summary>
    public static int WeightFor(string dropBand, IReadOnlyDictionary<string, int> weightTable)
    {
        if (dropBand is null) throw new ArgumentNullException(nameof(dropBand));
        if (weightTable is null) throw new ArgumentNullException(nameof(weightTable));
        return weightTable.TryGetValue(dropBand, out var weight)
            ? weight
            : throw new ArgumentException($"'{dropBand}' has no entry in the supplied dropBand weight table", nameof(dropBand));
    }

    /// <summary>
    /// The `:outcome` stream (spec §3's own table row: "which outcome | …:outcome | `Pick` over §5's
    /// shifted bands"). Each candidate's weight is its OWN <see cref="ShiftDropBand"/>-shifted band's
    /// weight — never the row's raw authored band — so severity actually changes the odds, not just the
    /// label. Reuses <see cref="EventDeckRefusal"/> (this module's own "the deck has nothing to give"
    /// exception, `EventDraw.cs`) for an exhausted/all-zero-weight outcome set, rather than minting a
    /// second exception for the identical fact.
    /// </summary>
    public static EventOutcomeRow PickOutcome(
        IReadOnlyList<EventOutcomeRow> outcomes, int severityTier, IReadOnlyList<string> dropBandOrder,
        IReadOnlyDictionary<string, int> weightTable, int row, int col, ulong seed)
    {
        if (outcomes is null) throw new ArgumentNullException(nameof(outcomes));
        if (outcomes.Count == 0) throw new ArgumentException("outcomes must not be empty", nameof(outcomes));

        var options = outcomes
            .Select(o => new WeightedOption<EventOutcomeRow>(o,
                WeightFor(ShiftDropBand(o.DropBand, o.Ordinal, severityTier, dropBandOrder), weightTable)))
            .ToList();

        var streamName = DelveStreams.Event(row, col) + ":outcome";
        var stream = SeededRng.DeriveStream(seed, streamName);
        var rollSeed = unchecked((long)stream.NextULong());

        try
        {
            return WeightedChoice.Pick(options, rollSeed, streamName);
        }
        catch (NoDrawableWeightedOptionException ex)
        {
            throw new EventDeckRefusal($"room ({row},{col}): {ex.Message}");
        }
    }

    /// <summary>
    /// Spec §5 "Forced outcome": on `use:{tag}` no `:outcome` draw happens — the outcome is this
    /// event's own best-ranked one (see this class's own doc comment for the citations). Returns
    /// <c>null</c> when <paramref name="holdsOverrideStock"/> is false (the caller's own job to fall
    /// back to <see cref="PickOutcome"/> in that case — this method never draws).
    ///
    /// <para><b>Never silently guesses an ambiguous authoring mistake.</b> `EventCatalog.Load` already
    /// guarantees every outcome's ordinal is a real member of the closed four, and 2-4 outcomes always
    /// exist, so the preference walk below always finds at least one match — but nothing bans two
    /// outcomes sharing the same ordinal (a real gap in the loader's own validation, not this method's
    /// to silently paper over). Two outcomes tied on the top-ranked available ordinal throws by name
    /// rather than picking the first arbitrarily.</para>
    /// </summary>
    public static EventOutcomeRow? TryForcedOutcome(IReadOnlyList<EventOutcomeRow> outcomes, bool holdsOverrideStock)
    {
        if (outcomes is null) throw new ArgumentNullException(nameof(outcomes));
        if (outcomes.Count == 0) throw new ArgumentException("outcomes must not be empty", nameof(outcomes));
        if (!holdsOverrideStock) return null;

        foreach (var ordinal in OrdinalPreferenceOrder)
        {
            var matches = outcomes.Where(o => string.Equals(o.Ordinal, ordinal, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0) continue;
            if (matches.Count > 1)
                throw new EventDeckRefusal(
                    $"forced outcome is ambiguous: {matches.Count} outcomes share the top-ranked available ordinal '{ordinal}'");
            return matches[0];
        }

        // Structurally unreachable given EventCatalog.Load's own validation (every outcome's ordinal is
        // a member of OrdinalPreferenceOrder) — named rather than silently returning null, matching
        // this whole module's "throw by name, never guess" discipline for a hand-built test fixture
        // that skips import validation.
        throw new ArgumentException("outcomes contains no ordinal from the known preference order", nameof(outcomes));
    }

    /// <summary>
    /// The full acceptance line composed into one call: "weights then `TryInstantiate` at the room's
    /// Θ, then a dispatch plan; `supplyOverride` reads `HoldsStock`" — a real override tag combined
    /// with a satisfied `holdsOverrideStock` fact short-circuits the weighted draw entirely, exactly as
    /// spec §5's "Forced outcome" paragraph states ("no `:outcome` draw happens"). <paramref
    /// name="holdsOverrideStock"/> is a plain caller-supplied fact — `HoldsStock`'s own real fact
    /// source (`loot-pack`, D3.18+'s pack ledger) is a separate, already-named gap this method does not
    /// need to wait on, the same "Core decides on a caller-supplied fact" posture this whole program
    /// already uses for `RestResolver`/`NervePolicy`.
    /// </summary>
    public static EventOutcomeRow Resolve(
        EventRow eventRow, bool holdsOverrideStock, int severityTier,
        IReadOnlyList<string> dropBandOrder, IReadOnlyDictionary<string, int> weightTable,
        int row, int col, ulong seed)
    {
        if (eventRow is null) throw new ArgumentNullException(nameof(eventRow));

        if (eventRow.SupplyOverride is not null
            && TryForcedOutcome(eventRow.Outcomes, holdsOverrideStock) is { } forced)
            return forced;

        return PickOutcome(eventRow.Outcomes, severityTier, dropBandOrder, weightTable, row, col, seed);
    }
}
