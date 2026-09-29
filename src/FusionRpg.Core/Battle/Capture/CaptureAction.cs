using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Battle.Capture;

// solid-remediation T4.7 (D12) — relocated from `Core/Delve/Wild/` on 2026-09-17.
//
// The owner's ruling: "any feature that changes battle mechanism is an extension of battle engine and
// must be built on the top of battle engine", and capture is "definitely an extension of battle
// engine". It lived under the delve, which made the delve the owner of a battle-affecting mechanism.
//
// This is a MOVE AND RE-WIRE, not a completion. The refusal below still refuses, with the same reason
// and the same flag, and nothing here became reachable — `CrossProgramLandedFlags.ItemCostRowLanded`
// is another program's to land. Relocating a mechanism and finishing a feature are different work
// with different owners, and conflating them is how a remediation pass turns into a feature pass.
//
// The engine already owned capture's RANDOMNESS before this move: `BattleRunState` derives the
// `"capture"` stream (BattleRunState.cs:284) while the mechanism that draws on it sat in the delve —
// the split that made the ownership wrong rather than merely oddly-filed.

/// <summary>Named rule ids this module raises (§9's own pattern) — refused rather than a fallback.</summary>
public static class CaptureRefusal
{
    /// <summary>Spec §5, verbatim: "the item-cost row is external A3, gating… the action ships
    /// behind the `CrossProgramLandedFlags` shape." A3 is confirmed absent
    /// (`CrossProgramLandedFlags.ItemCostRowLanded = false`, D3.24) — this refuses rather than
    /// pretending a seal-cost action can be authored without it.</summary>
    public const string NotLanded = "capture.not-landed";
}

/// <summary>
/// D4.6 (spec-wild-room.md §5, "Capture action and chance bands") — the pure chance formula, worked
/// exactly as the spec's own text block. Every band table (`hpBand`, `deltaBand`'s edges, seal-tier
/// shift, fail-step ramp, status bonus) arrives as a plain caller-supplied value straight from
/// `DungeonTuning`'s already-shipped `HpBandMilli`/`WildDeltaBands`/`CaptureTuning` fields — this
/// file resolves none of `dungeon.v1.json`'s own object shapes itself, matching `DelvePrices.cs`'s
/// established convention.
///
/// <para><b>Named gap: `countBand(live statuses)`.</b> Every OTHER `countBand` use in this codebase
/// (`slot.countBand`, `quests.countBand`) is an AUTHORED content value, never derived from a live
/// runtime count — there is no existing "raw count → lone/few/several/many" threshold table
/// anywhere in the tree, and the spec names no thresholds either. Inventing one here would be a
/// balance decision with no design input, exactly what "no magic numbers on the balance surface"
/// exists to prevent — so <see cref="Compute"/> takes `countBand` as an already-resolved band name,
/// the same "read model owned elsewhere" shape every other pre-resolved fact in this program uses.
/// </para>
///
/// <para><b>Out of scope, named rather than silently built:</b> the corpus `ActionRow` JSON authoring,
/// its `ConditionsJson` compilation, and the dispatch-table entry that lets a live battle actually
/// invoke this action are NOT part of this file — spec §5's own words: "the runner's id → resolver
/// row is a one-line ask on `action-map.md`," a cross-program coordination point, not a gap this
/// module fills unreviewed. <see cref="CaptureRefusal.NotLanded"/> covers the cost-row half of that
/// same external gate.</para>
/// </summary>
public static class CaptureChance
{
    public static readonly IReadOnlyList<string> HpBandOrder = new[] { "low", "half", "high" };
    public static readonly IReadOnlyList<string> DeltaBandOrder = new[] { "far-below", "below", "even", "above", "far-above" };

    /// <summary>"index of target hp‰ in `bands.hpBand.{low,half,high}.milli`" — each threshold is an
    /// inclusive upper bound (`hpMilli &lt;= low.milli` → `low`, else compare against `half`, then
    /// `high`); a value above the last threshold still lands in the top band, `high`.</summary>
    public static string HpBandOf(long targetHpMilli, IReadOnlyDictionary<string, long> hpBandMilli)
    {
        if (hpBandMilli is null) throw new ArgumentNullException(nameof(hpBandMilli));
        foreach (var name in HpBandOrder)
            if (targetHpMilli <= hpBandMilli[name]) return name;
        return HpBandOrder[^1];
    }

    /// <summary>"index of (θ_target − Θ_caster) in `wild.deltaBands[]`" — N ascending signed edges
    /// make N+1 half-open bands (`(-∞,e0) [e0,e1) … [eN-1,+∞)`), the same edges-to-bands shape
    /// `wild.deltaBands`/`deltaShiftRungs` already use elsewhere in this program (D4.1).</summary>
    public static int DeltaBandOrdinal(int delta, IReadOnlyList<int> deltaBandEdges)
    {
        if (deltaBandEdges is null) throw new ArgumentNullException(nameof(deltaBandEdges));
        var ordinal = 0;
        foreach (var edge in deltaBandEdges)
        {
            if (delta < edge) break;
            ordinal++;
        }
        return ordinal;
    }

    /// <summary>"deltaBand = clamp(deltaBand + `sealTierShiftBands[sealTier]` + attempts(target) ×
    /// `failStepBands`, 0, 4)" — an index rail into the five-member `DeltaBandOrder`, exempt from the
    /// no-silent-clamp rule the same way every other closed-ordinal shift in this program is
    /// (`Disposition.Shift`, `Footprint.cs`, `OutcomeResolver.cs`).</summary>
    public static int AdjustedDeltaBandOrdinal(
        int baseDeltaBandOrdinal, int sealTierIndex, IReadOnlyList<int> sealTierShiftBands, int attempts, int failStepBands)
    {
        if (sealTierShiftBands is null) throw new ArgumentNullException(nameof(sealTierShiftBands));
        if (sealTierIndex < 0 || sealTierIndex >= sealTierShiftBands.Count)
            throw new ArgumentOutOfRangeException(nameof(sealTierIndex), sealTierIndex, "must index into sealTierShiftBands");
        if (attempts < 0) throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "must be non-negative");

        var shifted = baseDeltaBandOrdinal + sealTierShiftBands[sealTierIndex] + checked(attempts * failStepBands);
        return Math.Clamp(shifted, 0, DeltaBandOrder.Count - 1);
    }

    /// <summary>"chance‰ = `capture.chanceMilli[hpBand][deltaBand]` + `capture.statusBonusMilli[countBand]`"
    /// — one widen (both terms already `long`), no divide at all (a straight sum of two already-scaled
    /// per-mille tables, spec's own formula, verbatim).</summary>
    public static long Compute(
        string hpBand, string deltaBand, string countBand,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> chanceMilliByHpBandThenDeltaBand,
        IReadOnlyDictionary<string, long> statusBonusMilliByCountBand)
    {
        if (chanceMilliByHpBandThenDeltaBand is null) throw new ArgumentNullException(nameof(chanceMilliByHpBandThenDeltaBand));
        if (statusBonusMilliByCountBand is null) throw new ArgumentNullException(nameof(statusBonusMilliByCountBand));

        if (!chanceMilliByHpBandThenDeltaBand.TryGetValue(hpBand, out var byDeltaBand))
            throw new ArgumentException($"Unknown hp band '{hpBand}'.", nameof(hpBand));
        if (!byDeltaBand.TryGetValue(deltaBand, out var baseChance))
            throw new ArgumentException($"Unknown delta band '{deltaBand}'.", nameof(deltaBand));
        if (!statusBonusMilliByCountBand.TryGetValue(countBand, out var bonus))
            throw new ArgumentException($"Unknown count band '{countBand}'.", nameof(countBand));

        return checked(baseChance + bonus);
    }
}

/// <summary>The two calls a live resolver needs beyond the pure chance math: the A3 gate, and the
/// roll comparison itself (`success = rolledMilli &lt; chance‰`, spec's own strict `&lt;`). Neither
/// owns the `CaptureRng` this rolls on — `BattleRunState.CaptureRng` derives it, D4.8's own wiring
/// (unbuilt) draws from it and calls these.</summary>
public static class CaptureAction
{
    /// <summary>Refuses <see cref="CaptureRefusal.NotLanded"/> while A3 is absent — reachable today,
    /// by construction, since `CrossProgramLandedFlags.ItemCostRowLanded` is `false` (D3.24).</summary>
    public static bool TryGate(out string? refusalId)
    {
        if (!CrossProgramLandedFlags.ItemCostRowLanded)
        {
            refusalId = CaptureRefusal.NotLanded;
            return false;
        }
        refusalId = null;
        return true;
    }

    /// <summary>"success = (long)CaptureRng.NextPerMille() &lt; chance‰" — the caller has already
    /// drawn <paramref name="rolledMilli"/> off `CaptureRng`; this is the comparison alone.</summary>
    public static bool Resolve(long chanceMilli, long rolledMilli) => rolledMilli < chanceMilli;
}
