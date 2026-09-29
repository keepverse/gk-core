namespace FusionRpg.Core.Delve.Wild;

/// <summary>The eight talk verbs (spec-wild-room.md §2's own table, plus `fight`/`leave`). The four
/// `offer:*` rows collapse their `{tag}`/rarity payload into the caller's own eligibility facts (see
/// <see cref="WildTalkEligibility"/>) — this enum names only the KIND of offer, never its content.</summary>
public enum WildVerb
{
    Flatter,
    Threaten,
    OfferSouls,
    OfferSpirit,
    OfferSupply,
    OfferContract,
    Fight,
    Leave,
}

/// <summary>
/// Every already-resolved eligibility fact §2's table reads, so <see cref="TalkTree"/> itself stays
/// pure — each field is a caller-supplied read from a store/pack/roster this module does not own
/// (spec citations on each verb's own row). <see cref="CaptureOnly"/> and <see cref="NoFreeSlot"/>
/// gate every `offer:*` before any other eligibility check (spec, verbatim: "capture-only species
/// never `join` by talk… offer no `offer:*`"; "no binding slot refuses every `offer:*` before any
/// soul moves").
/// </summary>
public sealed record WildTalkEligibility(
    bool CaptureOnly,
    bool NoFreeSlot,
    bool DeltaBandIsFarAbove,
    bool UnbankedMeetsSoulsFloor,
    bool SpiritPoolMeetsFloor,
    bool HoldsOfferableSupply,
    bool HasReleasableContractAboveFloor);

/// <summary>
/// D4.2 (spec-wild-room.md §2) — the talk's own step structure: which verbs are legal at a given
/// step, a stance move's band effect, and the closed autopilot rule. Pricing (§3, `OfferFloor` and
/// its four equivalents — D4.3), the outcome draw (§2's `wild.outcome.*Milli` table, `WeightedChoice`
/// — D4.4) and the actual store transaction (debit, mint, room-close — D4.8) are each their own,
/// still-unbuilt, task; this file owns only the tree's own shape.
/// </summary>
public static class TalkTree
{
    /// <summary>
    /// The verbs legal at <paramref name="step"/> (1-based), per §2's own table: `flatter`/
    /// `threaten` are step-1-only (hardcoded by the spec's own table, not derived from
    /// <paramref name="maxSteps"/>); every `offer:*` needs its own eligibility fact true, gated
    /// first by <see cref="WildTalkEligibility.CaptureOnly"/>/<see cref="WildTalkEligibility.NoFreeSlot"/>;
    /// `fight`/`leave` are always legal ("always", verbatim). Past <paramref name="maxSteps"/> —
    /// which the spec's own table never reaches given today's authored `2`, but a future re-author
    /// could — nothing is offered: the talk must already have resolved by then, so this is a
    /// structural refusal surface, not a silent extension of the tree.
    /// </summary>
    public static IReadOnlyList<WildVerb> Offered(int step, int maxSteps, WildTalkEligibility eligibility)
    {
        if (step < 1) throw new ArgumentOutOfRangeException(nameof(step), step, "a talk step is 1-based");
        if (eligibility is null) throw new ArgumentNullException(nameof(eligibility));
        if (step > maxSteps) return Array.Empty<WildVerb>();

        var offered = new List<WildVerb>();

        if (step == 1)
        {
            offered.Add(WildVerb.Flatter);
            if (!eligibility.DeltaBandIsFarAbove) offered.Add(WildVerb.Threaten); // "refused at far-above"
        }

        if (!eligibility.CaptureOnly && !eligibility.NoFreeSlot)
        {
            if (eligibility.UnbankedMeetsSoulsFloor) offered.Add(WildVerb.OfferSouls);
            if (eligibility.SpiritPoolMeetsFloor) offered.Add(WildVerb.OfferSpirit);
            if (eligibility.HoldsOfferableSupply) offered.Add(WildVerb.OfferSupply);
            if (eligibility.HasReleasableContractAboveFloor) offered.Add(WildVerb.OfferContract);
        }

        offered.Add(WildVerb.Fight);
        offered.Add(WildVerb.Leave);
        return offered;
    }

    public static bool IsStance(WildVerb verb) => verb is WildVerb.Flatter or WildVerb.Threaten;

    public static bool IsOffer(WildVerb verb) =>
        verb is WildVerb.OfferSouls or WildVerb.OfferSpirit or WildVerb.OfferSupply or WildVerb.OfferContract;

    /// <summary>
    /// A stance move's own one-step band effect (§2's own "Band effect" column, verbatim): `flatter`
    /// takes the caller's already-rolled coin (`NextPerMille() &lt; wild.talk.flatterMilli`) — this
    /// file owns no RNG stream, matching every other pure decision function in this program;
    /// `threaten` reads the already-banded Δ directly. Calling this with a non-stance verb is a
    /// caller error: every other verb resolves the talk (§2's "Then" column routes it to the outcome
    /// draw or straight to `fight`/`leave`), it never shifts a band through this function.
    /// </summary>
    public static int StanceShift(WildVerb verb, bool flatterCoinUnderThreshold, bool deltaBandAtOrBelowEven) => verb switch
    {
        WildVerb.Flatter => flatterCoinUnderThreshold ? -1 : 1,
        WildVerb.Threaten => deltaBandAtOrBelowEven ? -1 : 1,
        _ => throw new ArgumentException($"{verb} is not a stance move -- it resolves the talk, it does not shift a band.", nameof(verb)),
    };
}

/// <summary>D4.2's own closed autopilot rule (`DungeonTuning.cs` already validates `wild.autopilot.rule`
/// is one of these two at load — this is the runtime side of that same closed set).</summary>
public static class WildAutopilot
{
    public const string RuleFight = "fight";
    public const string RuleLeaveHostile = "leave-hostile";

    public static readonly IReadOnlyList<string> KnownRules = new[] { RuleFight, RuleLeaveHostile };

    /// <summary>
    /// Autopilot's one answer. It "never offers" (spec, verbatim), so it always resolves at step 1
    /// with a terminal verb — there is no second step for it to fail to reach, which is the whole of
    /// D4.2's own verify line ("an autopilot party never opens a talk it cannot finish"):
    /// <see cref="WildVerb.Flatter"/>/<see cref="WildVerb.Threaten"/> are simply never in this
    /// function's range. <paramref name="bandAfterRungShift"/> is the room's disposition band AFTER
    /// the rung's own shift has already been applied ("the rung speaks through its shift, no rung
    /// bool", verbatim) — this function reads no tuning itself, and an unrecognized rule throws
    /// rather than silently defaulting to `fight` (the loader's own closed-set check should already
    /// have caught it long before this call).
    /// </summary>
    public static WildVerb Answer(string rule, string bandAfterRungShift) => rule switch
    {
        RuleFight => WildVerb.Fight,
        RuleLeaveHostile => bandAfterRungShift == "hostile" ? WildVerb.Leave : WildVerb.Fight,
        _ => throw new ArgumentException($"Unknown wild.autopilot.rule '{rule}'.", nameof(rule)),
    };
}
