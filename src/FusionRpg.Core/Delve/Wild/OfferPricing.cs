using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Wild;

/// <summary>
/// D4.3 (spec-wild-room.md §3, "Offer pricing and the floor") — the four `offer:*` equivalents.
/// Every base amount, rate and divisor is a plain caller-supplied parameter (this program's own
/// "no magic numbers on the balance surface" rule) — this file resolves none of
/// `dungeon.v1.json`/`summoning.v1.json`/`contracts.v1.json`'s own object shapes itself.
///
/// <para><b>Correction to this task's own acceptance line</b> ("all four equivalents call
/// `DelvePrices`"): the spec's own §3 table shows only <see cref="Souls"/> and <see cref="Spirit"/>
/// (derived from `Souls`) actually route through `DelvePrices.OfferFloor`. <see cref="Contract"/>'s
/// base price is <c>ContractPolicy.RitualPrice</c> (`ContractPolicy.cs:161-166`) — the already-shipped,
/// contract-specific wrapper that itself calls the SAME `SoulSinkPolicy.Price` every `DelvePrices`
/// function also wraps, so the underlying pricing engine is identical even though the call does not
/// pass through the `DelvePrices` type; only the loyalty scale on top of it is new arithmetic, and it
/// lives here rather than being re-derived ad hoc by a future caller. <see cref="Supply"/> has no
/// price to compute at all yet — it refuses, via the same <see cref="DelvePriceRules.PriceUndesigned"/>
/// this file's sibling <see cref="DelvePrices.Merchant"/> already uses for the identical gap.</para>
/// </summary>
public static class OfferPricing
{
    /// <summary>
    /// `souls`: the floor **is** the price (spec, verbatim) — one call to the already-shipped
    /// `DelvePrices.OfferFloor`, no private arithmetic in this function at all.
    /// </summary>
    public static long Souls(long costPerPull, int thetaRoom, long offerSoulsMilliOfPullPrice, PowerTuning tuning) =>
        DelvePrices.OfferFloor(costPerPull, thetaRoom, offerSoulsMilliOfPullPrice, tuning);

    /// <summary>
    /// `spirit`: <c>OfferFloor(Θ_room) × 1000 / wild.offer.spiritPerSoulMilli</c> spirit units (spec,
    /// verbatim) — the one divide beyond <see cref="Souls"/>'s own `DelvePrices` call, widened before
    /// the multiply (every factor already `long`), divided exactly once, at the end.
    /// </summary>
    public static long Spirit(
        long costPerPull, int thetaRoom, long offerSoulsMilliOfPullPrice, long spiritPerSoulMilli, PowerTuning tuning)
    {
        if (spiritPerSoulMilli <= 0)
            throw new ArgumentOutOfRangeException(nameof(spiritPerSoulMilli), spiritPerSoulMilli, "must be positive");
        var floor = Souls(costPerPull, thetaRoom, offerSoulsMilliOfPullPrice, tuning);
        return checked(floor * 1000 / spiritPerSoulMilli);
    }

    /// <summary>
    /// `supply:{tag}`: the item-side DERIVED price does not exist yet (`dungeon-loot`'s own named
    /// wiring gap, `spec-dungeon-loot.md:193-195`) — refuses <see cref="DelvePriceRules.PriceUndesigned"/>
    /// in v1, the identical refusal `DelvePrices.Merchant` already uses for the same gap. There is no
    /// success path to model yet, so this always refuses regardless of <paramref name="tag"/> — it is
    /// carried only to name the specific offer in the refusal detail.
    /// </summary>
    public static AtomRejection Supply(string tag) =>
        DelvePriceRules.Fail(DelvePriceRules.PriceUndesigned,
            $"no item-side derived supply price exists yet -- offer:supply:{tag} is unpriceable in v1");

    /// <summary>
    /// `contract`: <c>ContractPolicy.RitualPrice(rarity, Θ_room, power) × loyalty / LoyaltyMax</c>
    /// (spec, verbatim), widened before the multiply, divided exactly once, at the end.
    /// </summary>
    public static long Contract(CreatureRarity rarity, int thetaRoom, int loyalty, int loyaltyMax, PowerTuning tuning)
    {
        if (loyaltyMax <= 0)
            throw new ArgumentOutOfRangeException(nameof(loyaltyMax), loyaltyMax, "must be positive");
        var basePrice = ContractPolicy.RitualPrice(rarity, thetaRoom, tuning);
        return checked(basePrice * loyalty / loyaltyMax);
    }
}
