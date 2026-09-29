using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Loot;

/// <summary>Named content rules this module raises, under its own registered namespace — the same
/// "one code with a namespaced payload" shape `EventRules`/`ConsumableRules` already establish.</summary>
public static class DelvePriceRules
{
    public const string Namespace = "delve";

    /// <summary>Spec §6, verbatim: "the merchant's base price is a wiring gap, named"; refused rather
    /// than defaulted or given a literal, until the item program's own price derivation lands
    /// (`seed-contract.md` §2.1: "price · weight · durability · salvage yield — DERIVED — §8: none
    /// exist yet").</summary>
    public const string PriceUndesigned = "delve.price-undesigned";

    static DelvePriceRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Forces the static constructor above to have run — same empty-body idiom
    /// `EventRules.EnsureRegistered`/`ConsumableRules.EnsureRegistered` already use.</summary>
    public static void EnsureRegistered() { }

    public static AtomRejection Fail(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// `dungeon-loot` D3.13 (spec-dungeon-loot.md §6, "Sinks and prices — one function, three callers") —
/// every in-delve price ends in the SAME `SoulSinkPolicy.Price`, so a sink reads the SAME Θ its faucet
/// reads (spec, verbatim) and no caller re-derives a scale. No price literal exists anywhere in this
/// file — every base amount, markup and multiplier is a plain, caller-supplied parameter (tuning/item
/// program's own future derivation), matching the "no magic numbers on the balance surface" rule.
/// </summary>
public static class DelvePrices
{
    /// <summary>
    /// Spec §6's own table row, verbatim: base = `basePriceSouls(item)` — DERIVED on the item side,
    /// none exist yet, so <paramref name="basePriceSouls"/> arrives `null` until that lands; markup =
    /// `× (1000 + merchant.markupMilli) × rung.merchantMarkupMultMilli / 10⁶` — ONE widen (every factor
    /// already `long`), ONE divide, at the end. A `null` base refuses <see
    /// cref="DelvePriceRules.PriceUndesigned"/> — "a merchant room opens as a sell-nothing rest; no
    /// default, no literal" (spec, verbatim) — never a fabricated number.
    /// </summary>
    public static AtomRejection Merchant(
        long? basePriceSouls, int thetaRoom, long merchantMarkupMilli, long rungMerchantMarkupMultMilli,
        PowerTuning tuning, out long price)
    {
        price = 0;
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (basePriceSouls is not { } b)
            return DelvePriceRules.Fail(DelvePriceRules.PriceUndesigned,
                "no item-side derived merchant price exists yet -- a merchant room opens as a sell-nothing rest");

        var markedUp = checked(b * (1000 + merchantMarkupMilli) * rungMerchantMarkupMultMilli / 1_000_000);
        price = SoulSinkPolicy.Price(markedUp, thetaRoom, tuning);
        return AtomRejection.Ok;
    }

    /// <summary>Spec §6's altar-pull row, verbatim: base = `banners[altar.bannerId].costPerPull`, no
    /// markup, `Θ_room`. Result is at-risk haul (R5) — this function only prices it; the caller owns
    /// spending it from unbanked souls.</summary>
    public static long PullPrice(long costPerPull, int thetaRoom, PowerTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        return SoulSinkPolicy.Price(costPerPull, thetaRoom, tuning);
    }

    /// <summary>Spec §6's recruit-offer-floor row, verbatim: `PullPrice(Θ_room) ×
    /// wild.offer.soulsMilliOfPullPrice / 1000`. The tunable's own "&gt;= 1000‰" floor is a content/
    /// tuning-load validation concern (never below the pull price itself), not something this pure
    /// arithmetic function re-checks.</summary>
    public static long OfferFloor(long costPerPull, int thetaRoom, long offerSoulsMilliOfPullPrice, PowerTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        var pull = PullPrice(costPerPull, thetaRoom, tuning);
        return checked(pull * offerSoulsMilliOfPullPrice / 1000);
    }

    /// <summary>Spec §6's recovery-ritual row, verbatim: base = `risk.recoveryRitualSouls.{rung}`, Θ
    /// read = `theta_run` of the WOUNDING delve (attrition §7 — never the current one). Banked souls,
    /// not unbanked — the caller's own concern, this function only prices it.</summary>
    public static long RecoveryRitual(long recoveryRitualSouls, int woundingDelveThetaRun, PowerTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        return SoulSinkPolicy.Price(recoveryRitualSouls, woundingDelveThetaRun, tuning);
    }

    /// <summary>
    /// D3.30 (spec-supplies-and-objects.md §Objective, "Provisioning as a soul sink") — priced at
    /// `contentScale(Θ_entrance + Wm·bandDelta)`, verbatim. `Wm` is `PowerTuning.Weights.WmMilli`
    /// (already shipped, `power-scale.v1.json:15`, 5000‰) — the SAME weight `PowerIndexComposer`
    /// itself weighs `dangerBand` by, reused here rather than a private re-derivation; `bandDelta` is
    /// the rung's own `DifficultyRungTuning.BandDelta` (`difficulty-ladder`'s own column, can be
    /// negative for an easier rung). One widen (`long × int`), one divide, at the end, matching this
    /// program's own "never twice" rule.
    /// </summary>
    public static long Provisioning(long basePriceSouls, int thetaEntrance, int bandDelta, PowerTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        var wmMilli = tuning.Weights.WmMilli ?? throw new ArgumentException("power tuning has no WmMilli weight configured", nameof(tuning));
        var theta = thetaEntrance + checked((int)(wmMilli * bandDelta / 1000));
        return SoulSinkPolicy.Price(basePriceSouls, theta, tuning);
    }
}

/// <summary>One server-resolved Delve recruit result. The species and every mint field are produced
/// from the persisted delve seed/room coordinates and the configured catalog; no HTTP request field
/// is represented here.</summary>
public sealed record DelveWildJoinResolution(long Price, string SpeciesId, CreatureMintSpec Spec);

/// <summary>
/// Resolves the server-owned half of a wild/cage join. The room's persisted archetype and the
/// delve's sealed seed choose the candidate; the dungeon tuning chooses the altar banner and the
/// offer multiplier; the persisted run depth chooses <c>OfferFloor</c>. The caller cannot replace
/// any of those values.
/// </summary>
public static class DelveWildJoinResolver
{
    public const string CandidateUnavailable = "wild.candidate-unavailable";
    public const string BelowRankFloor = "wild.below-rank-floor";
    public const string BannerUnknown = "altar.banner-unknown";

    // dungeon.v3.json still spells the shipped standard altar `standard`, while the summoning
    // catalog's canonical id is `standard-rift`. Keep the compatibility translation at this
    // boundary; an unknown future id still refuses rather than silently choosing a banner.
    const string LegacyStandardAltarBanner = "standard";

    public static (bool Ok, string Reason, DelveWildJoinResolution? Resolution) Resolve(
        ulong seed,
        int row,
        int col,
        string archetypeId,
        int thetaRoom,
        DungeonTuning dungeonTuning,
        PowerTuning powerTuning)
    {
        if (dungeonTuning is null) throw new ArgumentNullException(nameof(dungeonTuning));
        if (powerTuning is null) throw new ArgumentNullException(nameof(powerTuning));
        if (string.IsNullOrWhiteSpace(archetypeId)) return (false, CandidateUnavailable, null);

        var banner = SummonBannerCatalog.TryGet(dungeonTuning.AltarBannerId)
            ?? (string.Equals(dungeonTuning.AltarBannerId, LegacyStandardAltarBanner, StringComparison.Ordinal)
                ? SummonBannerCatalog.TryGet(SummonBannerCatalog.StandardRift)
                : null);
        if (banner is null) return (false, BannerUnknown, null);
        var price = DelvePrices.OfferFloor(
            banner.CostPerPull,
            thetaRoom,
            dungeonTuning.WildOfferSoulsMilliOfPullPrice,
            powerTuning);

        // Capture-only, event-only, excluded, and the top wild rung never join by talk/cage. The
        // rank floor is deliberately a separate pass so an empty raised floor keeps the established
        // named refusal instead of looking like an empty catalog.
        var structural = CreatureSpeciesCatalog.All
            .Where(s => CreatureAdmission.ForWave(s) && s.BaseRarity != CreatureRarity.Sunwoven)
            .OrderBy(s => s.SpeciesId, StringComparer.Ordinal)
            .ToList();
        var ranked = structural
            .Where(s => CreatureRankFloors.Passes(CreatureRankFloors.CageEligibility, s.Rank))
            .ToList();
        if (ranked.Count == 0)
            return (false, BelowRankFloor, null);

        var eligible = ranked
            .Where(s => Cage.OccupantEligible(
                s.Acquisition == CreatureAcquisition.CaptureOnly,
                s.BaseRarity == CreatureRarity.Sunwoven,
                s.Rank))
            .OrderBy(s => s.SpeciesId, StringComparer.Ordinal)
            .ToList();
        if (eligible.Count == 0) return (false, CandidateUnavailable, null);

        // `seq` is the first talk-step stream. The room coordinates are unique within a delve and
        // the sealed seed is already delve-scoped, so the canonical reserved stream is the whole
        // candidate input; traits use the separate reserved stream below.
        var candidateRng = SeededRng.DeriveStream(seed, DelveStreams.Wild(row, col, "seq"));
        var species = eligible[candidateRng.NextInt(eligible.Count)];
        var traitRng = SeededRng.DeriveStream(seed, $"dungeon:wild:{row}:{col}:traits");
        var traits = SummonRoller.RollTraits(species, species.BaseRarity, traitRng);

        return (true, "", new DelveWildJoinResolution(
            price,
            species.SpeciesId,
            new CreatureMintSpec
            {
                SpeciesId = species.SpeciesId,
                Side = species.Side,
                GameTypeId = species.GameTypeId,
                Rarity = species.BaseRarity.ToId(),
                Variant = "normal",
                ElementPrimary = species.ElementPrimary.ToElementId(),
                ElementSecondary = species.ElementSecondary?.ToElementId(),
                TraitIds = traits.ToList(),
                Origin = "delve",
            }));
    }
}
